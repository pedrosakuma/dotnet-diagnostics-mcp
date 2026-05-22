# AWS ECS / Fargate recipe

On-demand diagnostics for .NET applications running on **AWS ECS with the
Fargate launch type**. The recipe deploys `dotnet-diagnostics-mcp` as a
**sidecar container** alongside your application in the same ECS task. The
sidecar attaches to the app via the .NET runtime's diagnostic IPC socket
(created in `/tmp`), so the target app needs **no code changes**.

This is the AWS-native counterpart to the
[Azure Container Apps recipe](../../azure/container-apps/). It implements
[`docs/cloud-recipes-design.md`](../../../docs/cloud-recipes-design.md)
Phase 1.

| Artifact | Purpose |
|---|---|
| [`main.yaml`](main.yaml) | CloudFormation template: log group, IAM roles, task definition (`app` + `diag`), ECS service. |
| [`parameters.example.json`](parameters.example.json) | Placeholder values; copy + edit, then pass via `--parameters file://...`. |

For Kubernetes on EKS (or any other cluster), use the generic recipes under
[`../../k8s/`](../../k8s/) instead.

> **Fargate platform version 1.4.0 is required.** The template pins it
> explicitly. Earlier platform versions silently drop `LinuxParameters` and
> the diag container cannot use `SYS_PTRACE`-backed tools
> (`collect_thread_snapshot`, `inspect_live_heap`, `inspect_dump`,
> `collect_process_dump`).
>
> **AWS Lambda is out of scope** for this recipe — Lambda's freeze-between-
> invocations model breaks long-running diagnostic sessions. See
> [`docs/cloud-integrations-design.md`](../../../docs/cloud-integrations-design.md)
> for the deferred discussion.

---

## Prerequisites

1. **AWS CLI v2** authenticated against the target account.
2. **An existing ECS cluster** that supports Fargate. The recipe targets an
   existing cluster on purpose — clusters are commonly shared.
   ```bash
   aws ecs create-cluster --cluster-name diag-cluster
   ```
3. **Subnets and a security group** for the task ENI:
   - Prefer **private subnets with a NAT gateway**; the diag container needs
     egress for ECR pulls, CloudWatch Logs, and Secrets Manager.
   - The security group must allow **inbound TCP 18887** (default `DiagPort`)
     **only from your operator network or an internal ALB**. Do not expose
     the MCP endpoint to the public internet.
4. **Container images reachable from the task**:
   - Your application image (any registry the task role/execution role can
     pull from; ECR is the simplest path).
   - The diagnostics sidecar image — by default
     `ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:0.3.1`. If your account
     blocks anonymous GHCR pulls, mirror it to ECR first.
5. **A bearer token in AWS Secrets Manager** for the MCP HTTP transport:
   ```bash
   TOKEN=$(openssl rand -hex 32)
   aws secretsmanager create-secret \
     --name diag-mcp-bearer-token \
     --secret-string "$TOKEN"
   ```
   The template injects this secret into the diag container as
   `MCP_BEARER_TOKEN`.
6. **cfn-lint** for static validation (optional but recommended):
   ```bash
   python3 -m venv ~/.cfnlint && ~/.cfnlint/bin/pip install cfn-lint
   ~/.cfnlint/bin/cfn-lint deploy/aws/ecs-fargate/main.yaml
   ```

## UID matching (read this first)

Both containers must agree on the UID that creates and reads
`/tmp/dotnet-diagnostic-<pid>-<unique>-socket`. The default
`dotnet-diagnostics-mcp` image runs as UID **10001**. Most Microsoft
`mcr.microsoft.com/dotnet/aspnet:*` images run as **root**. The template
forces the diag container to **`User: "0"`** because that is the path with
the fewest surprises for the reference recipe.

Two alternatives, if running diag as root is not acceptable in your
environment:

- **Most secure**: rebuild your app image with `USER 10001` and ensure all
  files under `/tmp` are writable by that UID. Then drop the `User: "0"`
  override on the diag container.
- **Easiest, fully controlled**: rebuild the diag image without the
  non-root user (`USER root` in your fork of `deploy/Dockerfile`). The
  template's `User` field becomes unnecessary.

If the UIDs do not match, you'll see `Permission denied` when the diag
container tries to open the diagnostic socket — even though networking and
storage look healthy. See `AGENTS.md` → "Diagnostic socket UID" for the
underlying invariant.

## Capability matrix

| MCP tool family | Works on Fargate 1.4.0+? | Notes |
|---|---|---|
| EventPipe (`snapshot_counters`, `collect_cpu_sample`, `collect_gc_events`, …) | ✅ Yes | Only needs socket access + UID match. |
| ClrMD / `ptrace` (`collect_thread_snapshot`, `inspect_live_heap`, `inspect_dump`, `collect_process_dump`) | ✅ Yes | Requires `LinuxParameters.Capabilities.Add: [SYS_PTRACE]` (template adds it). |
| `perf`-based off-CPU sampling (`collect_off_cpu_sample`) | ❌ Not validated | Fargate does not expose `CAP_PERFMON` or paranoia tuning. Tracked as an open question in [`docs/cloud-recipes-design.md`](../../../docs/cloud-recipes-design.md#open-questions). |

## Deploy

The example parameters file uses the `aws cloudformation create-stack
--parameters file://...` JSON shape (an object with a `Parameters` array of
`{ParameterKey, ParameterValue}` entries). That format does not work with
`aws cloudformation deploy --parameter-overrides file://...`, so the
commands below use `create-stack` / `update-stack`.

```bash
cp deploy/aws/ecs-fargate/parameters.example.json /tmp/diag-params.json
# Edit /tmp/diag-params.json with your cluster ARN, subnets, security group,
# app image, and Secrets Manager ARN.

# First deploy:
aws cloudformation create-stack \
  --stack-name dotnet-diagnostics-mcp \
  --template-body file://deploy/aws/ecs-fargate/main.yaml \
  --capabilities CAPABILITY_IAM \
  --parameters file:///tmp/diag-params.json

aws cloudformation wait stack-create-complete --stack-name dotnet-diagnostics-mcp

# Subsequent updates:
aws cloudformation update-stack \
  --stack-name dotnet-diagnostics-mcp \
  --template-body file://deploy/aws/ecs-fargate/main.yaml \
  --capabilities CAPABILITY_IAM \
  --parameters file:///tmp/diag-params.json
```

The stack creates:

- a CloudWatch log group `/ecs/dotnet-diagnostics-mcp` (7-day retention)
- a task execution role (ECR pull + CloudWatch Logs + read the bearer secret)
- a task role (empty unless ECS Exec is enabled; then SSM channel rights)
- one task definition with two containers (`app` + `diag`) sharing `/tmp`
- one ECS service running on Fargate platform `1.4.0`

## Smoke test

```bash
# 1. Wait for the service to stabilize.
aws ecs wait services-stable --cluster diag-cluster --services dotnet-diagnostics-mcp

# 2. Find the running task ARN.
TASK_ARN=$(aws ecs list-tasks --cluster diag-cluster \
  --service-name dotnet-diagnostics-mcp --query 'taskArns[0]' --output text)

# 3. Tail the diag container logs.
aws logs tail /ecs/dotnet-diagnostics-mcp --since 5m --filter-pattern '"[diag]"'

# 4. Shell into the diag container (requires EnableEcsExec=true) and
#    confirm the .NET diagnostic socket from the app is visible.
aws ecs execute-command \
  --cluster diag-cluster \
  --task "$TASK_ARN" \
  --container diag \
  --interactive \
  --command "/bin/sh"
# inside the container:
ls /tmp/dotnet-diagnostic-*    # must list the app process socket
```

If `/tmp/dotnet-diagnostic-*` is missing inside `diag`, the UID is mismatched
or `pidMode: task` is not in effect — re-check the prerequisites above.

## MCP client snippet

Once the service is healthy and reachable from your operator network
(internal ALB, port-forward, VPN, etc.), wire it into your MCP client:

```json
{
  "mcpServers": {
    "dotnet-diag-aws": {
      "url": "http://<service-endpoint>:18887/mcp",
      "headers": {
        "Authorization": "Bearer <value of MCP_BEARER_TOKEN>"
      }
    }
  }
}
```

For Claude Code / Copilot CLI / Cursor, replace `<service-endpoint>` with
whatever address resolves to the task ENI (commonly an internal ALB DNS name
or a `localhost` port set up via `aws ssm start-session` port forwarding).

## Out of scope

- **Public-by-default ingress.** This template does not provision a public
  ALB. Front the service with an **internal** ALB if you need stable DNS;
  see [`docs/cloud-recipes-design.md`](../../../docs/cloud-recipes-design.md)
  for the rationale.
- **AWS-API discovery tools.** A future `list_ecs_tasks` style tool that
  hits the ECS API to enumerate candidate workloads lives in a separate
  issue and is intentionally not part of this recipe.
- **CDK / Terraform variants.** Native CloudFormation is the first
  reference. Alternate IaC dialects are noted as Phase 4 in the design doc.
- **`collect_off_cpu_sample`.** Fargate does not currently advertise
  `CAP_PERFMON`. The recipe leaves this tool documented as not-validated.

## Reference

- [`docs/cloud-recipes-design.md`](../../../docs/cloud-recipes-design.md) — the design that drives this recipe
- [`docs/cloud-integrations-design.md`](../../../docs/cloud-integrations-design.md) — parent portfolio decision
- [`AGENTS.md`](../../../AGENTS.md) — diagnostic socket UID and `CAP_SYS_PTRACE` invariants
- [Issue #22](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/22) — acceptance criteria

## Production: pin to a digest

The defaults above use a released version tag
(`ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:0.3.1`) rather than `:latest`, so a
new upstream push cannot silently re-deploy under your stack. For production
workloads go one step further and pin to a **content-addressable digest** so the
exact image bytes are immutable across replicas, rollbacks, and pull retries:

```bash
# Resolve the current digest for the version tag you trust:
docker buildx imagetools inspect \
  ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:0.3.1 \
  --format '{{json .Manifest}}' | jq -r .digest
# -> sha256:...

# Use the digest form in your parameters / Bicep / service.yaml / template:
ghcr.io/pedrosakuma/dotnet-diagnostics-mcp@sha256:<digest>
```

Mirror the digest into your private registry (Artifact Registry, ECR, ACR) for
air-gapped or pull-quota-limited environments. Bump the pinned digest on the
same cadence as your other base images; the SLSA build provenance attestation
published by [`.github/workflows/publish-container.yml`](../../../.github/workflows/publish-container.yml)
lets you verify the bytes before promoting.
