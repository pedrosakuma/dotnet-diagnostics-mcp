// dotnet-diagnostics-mcp — Azure Container Apps recipe.
//
// Deploys a single Container App with two co-located containers sharing /tmp via an
// EmptyDir volume: the user application (`app`) and the diagnostics MCP server
// (`diag`). The diag container attaches to the app via the runtime diagnostic IPC
// socket created in the shared /tmp.
//
// Validate without deploying:
//   az bicep build --file deploy/azure/container-apps/main.bicep
//   az deployment group validate \
//     --resource-group <rg> \
//     --template-file deploy/azure/container-apps/main.bicep \
//     --parameters appImage=<your-app-image> diagImage=<your-diag-image> environmentId=<env-id>
//
// Smoke after a real deploy:
//   az containerapp exec -n <name> -g <rg> --container diag -- /bin/sh
//   # inside diag:
//   ls /tmp/dotnet-diagnostic-*  # should show the app's socket

@description('Container App name.')
param name string

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Resource ID of an existing Microsoft.App/managedEnvironments instance.')
param environmentId string

@description('Image for the application container. Must be reachable by the Container App.')
param appImage string

@description('Image for the dotnet-diagnostics-mcp sidecar.')
param diagImage string = 'ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest'

@description('TCP port the application container listens on. Exposed via ingress when ingressTarget=app.')
param appPort int = 8080

@description('TCP port the diagnostics MCP container listens on.')
param diagPort int = 8787

@description('Which container ingress should target. Set to "diag" to expose the MCP endpoint externally; defaults to "app".')
@allowed([
  'app'
  'diag'
])
param ingressTarget string = 'app'

@description('Make ingress externally reachable. When false, the Container App is only reachable from within its environment.')
param externalIngress bool = false

@description('Bearer token enforced by the diagnostics MCP server. Provide a long random value; rotate on demand.')
@secure()
param diagBearerToken string

@description('Minimum number of replicas.')
@minValue(0)
param minReplicas int = 1

@description('Maximum number of replicas.')
@minValue(1)
param maxReplicas int = 1

@description('Optional ACR-style registry to authenticate against, e.g. "myregistry.azurecr.io". Leave empty when images are anonymously pullable.')
param registryServer string = ''

@description('Username for `registryServer`. Ignored when registryServer is empty.')
param registryUsername string = ''

@description('Password for `registryServer`. Ignored when registryServer is empty.')
@secure()
param registryPassword string = ''

var hasRegistry = !empty(registryServer)
var ingressPort = ingressTarget == 'diag' ? diagPort : appPort
var sharedVolumeName = 'diag-tmp'

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    environmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: externalIngress
        targetPort: ingressPort
        transport: 'auto'
        allowInsecure: false
      }
      secrets: concat([
        {
          name: 'diag-bearer-token'
          value: diagBearerToken
        }
      ], hasRegistry ? [
        {
          name: 'registry-password'
          value: registryPassword
        }
      ] : [])
      registries: hasRegistry ? [
        {
          server: registryServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ] : []
    }
    template: {
      containers: [
        {
          name: 'app'
          image: appImage
          // The diagnostic socket is created at /tmp/dotnet-diagnostic-<pid>-<unique>-socket
          // by the runtime when DOTNET_EnableDiagnostics != 0 (default). Mounting an
          // EmptyDir over /tmp shares that socket with the diag sidecar.
          env: [
            {
              name: 'DOTNET_EnableDiagnostics'
              value: '1'
            }
          ]
          volumeMounts: [
            {
              volumeName: sharedVolumeName
              mountPath: '/tmp'
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
        {
          name: 'diag'
          image: diagImage
          // The diag image's USER must match the app's UID so the socket created by the
          // app is readable. The default dotnet-diagnostics-mcp image runs as 10001; if
          // your app runs as a different UID (root by default for aspnet:* images), rebuild
          // the diag image with USER root or change the app's USER directive.
          command: [
            'dotnet'
            'DotnetDiagnosticsMcp.Server.dll'
          ]
          env: [
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://0.0.0.0:${diagPort}'
            }
            {
              name: 'MCP_BEARER_TOKEN'
              secretRef: 'diag-bearer-token'
            }
          ]
          volumeMounts: [
            {
              volumeName: sharedVolumeName
              mountPath: '/tmp'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      volumes: [
        {
          name: sharedVolumeName
          storageType: 'EmptyDir'
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
output containerAppName string = containerApp.name
output ingressContainer string = ingressTarget
output diagMcpEndpoint string = ingressTarget == 'diag'
  ? 'https://${containerApp.properties.configuration.ingress.fqdn}/mcp'
  : 'Run `az containerapp exec -n ${containerApp.name} -g ${resourceGroup().name} --container diag -- curl http://localhost:${diagPort}/health` to reach the diag endpoint from inside the app.'
