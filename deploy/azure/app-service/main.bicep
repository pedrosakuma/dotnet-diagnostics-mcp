// dotnet-diagnostics-mcp — Azure App Service (Linux containers) recipe.
//
// Provisions an App Service Plan + a Linux Web App that runs the user application
// alongside the diagnostics MCP server as a sidecar container (GA feature on
// App Service Linux as of 2024). Both containers share a writable filesystem at
// /tmp via the App Service "mounted" path, which lets the diag container read the
// runtime diagnostic socket created by the app.
//
// Validate without deploying:
//   az bicep build --file deploy/azure/app-service/main.bicep
//   az deployment group validate \
//     --resource-group <rg> \
//     --template-file deploy/azure/app-service/main.bicep \
//     --parameters siteName=<name> appImage=<your-app-image> diagBearerToken=<token>
//
// Notes on Windows containers: App Service for Windows does NOT support the
// sitecontainers sidecar feature today. Run the MCP server out-of-process on a
// dedicated VM or use Azure Container Apps (see ../container-apps/main.bicep).

@description('App Service site name. Must be globally unique.')
param siteName string

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('App Service Plan name. Created in this deployment.')
param planName string = '${siteName}-plan'

@description('App Service Plan SKU. P0v3+ recommended for production diag workloads (the sidecar adds ~256-512 MiB working set).')
param planSku string = 'P0v3'

@description('Image for the application container. Should be a Linux container; on private registries you will also need registryServer/Username/Password.')
param appImage string

@description('Image for the dotnet-diagnostics-mcp sidecar.')
param diagImage string = 'ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest'

@description('TCP port the application container listens on.')
param appPort int = 8080

@description('TCP port the diagnostics MCP container listens on. Reachable from the app via http://localhost:<port> after `az webapp ssh`.')
param diagPort int = 8787

@description('Bearer token enforced by the diagnostics MCP server. Provide a long random value; rotate on demand.')
@secure()
param diagBearerToken string

@description('Optional ACR-style registry to authenticate against (e.g. "myregistry.azurecr.io"). Leave empty for anonymous pulls.')
param registryServer string = ''

@description('Username for `registryServer`. Ignored when registryServer is empty.')
param registryUsername string = ''

@description('Password for `registryServer`. Ignored when registryServer is empty.')
@secure()
param registryPassword string = ''

var hasRegistry = !empty(registryServer)

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  sku: {
    name: planSku
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: siteName
  location: location
  kind: 'app,linux,container'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      // Disable the legacy single-container linuxFxVersion path — sidecar containers
      // require the "multi-container via siteContainers" model where the main image
      // is set on the main siteContainer (below), not on linuxFxVersion.
      linuxFxVersion: ''
      acrUseManagedIdentityCreds: false
      alwaysOn: true
      ftpsState: 'Disabled'
      appSettings: [
        // App Service exposes the public HTTP port via WEBSITES_PORT.
        {
          name: 'WEBSITES_PORT'
          value: string(appPort)
        }
        // Ensure the runtime IPC sockets stay enabled so the diag container can attach.
        {
          name: 'DOTNET_EnableDiagnostics'
          value: '1'
        }
      ]
    }
  }
}

// Main app container — must use isMain=true exactly once.
resource appContainer 'Microsoft.Web/sites/sitecontainers@2023-12-01' = {
  parent: site
  name: 'app'
  properties: {
    image: appImage
    isMain: true
    targetPort: string(appPort)
    authType: hasRegistry ? 'UserCredentials' : 'Anonymous'
    userName: hasRegistry ? registryUsername : null
    passwordSecret: hasRegistry ? registryPassword : null
    environmentVariables: [
      {
        name: 'DOTNET_EnableDiagnostics'
        value: '1'
      }
    ]
  }
}

// Sidecar — same registry credentials, attaches to the same Pod-equivalent and
// shares /tmp with the main container (sitecontainers share the writable root by
// design on App Service Linux).
resource diagContainer 'Microsoft.Web/sites/sitecontainers@2023-12-01' = {
  parent: site
  name: 'diag'
  properties: {
    image: diagImage
    isMain: false
    targetPort: string(diagPort)
    authType: hasRegistry ? 'UserCredentials' : 'Anonymous'
    userName: hasRegistry ? registryUsername : null
    passwordSecret: hasRegistry ? registryPassword : null
    environmentVariables: [
      {
        name: 'ASPNETCORE_URLS'
        value: 'http://0.0.0.0:${diagPort}'
      }
      {
        name: 'MCP_BEARER_TOKEN'
        value: diagBearerToken
      }
    ]
  }
  dependsOn: [
    appContainer
  ]
}

output siteDefaultHostName string = site.properties.defaultHostName
output diagPortInternal int = diagPort
output diagAccessHint string = 'Diag endpoint is NOT exposed publicly. Reach it via `az webapp ssh -n ${site.name} -g ${resourceGroup().name}` then `curl http://localhost:${diagPort}/health`.'
