# AWS deployment recipes

On-demand diagnostics for .NET applications running on AWS-managed container
hosts. Recipes here deploy `dotnet-diagnostics-mcp` as a **sidecar container**
alongside your application; the sidecar attaches to the app via the .NET
runtime's diagnostic IPC socket (created in `/tmp`), so the target app needs
**no code changes**.

| Recipe | Target host | Multi-container model | External MCP ingress? |
|---|---|---|---|
| [`ecs-fargate/`](ecs-fargate/) | ECS on Fargate (1.4.0+) | One task, two containers, shared task-scoped volume on `/tmp` | No — reach via internal ALB or `aws ssm start-session` port forward |

For Kubernetes on EKS (or any other cluster), use the generic recipes under
[`../k8s/`](../k8s/) instead. For Azure-managed container hosts, see
[`../azure/`](../azure/).

> **AWS Lambda** is intentionally not covered. Lambda's freeze-between-
> invocations execution model breaks long-running EventPipe sessions and
> `ptrace` attach paths. See
> [`../../docs/cloud-integrations-design.md`](../../docs/cloud-integrations-design.md)
> for the rationale.

## What lives here

- [`ecs-fargate/`](ecs-fargate/) — single-task CloudFormation recipe
  (`main.yaml` + `parameters.example.json` + README with smoke test).

## Future additions

- An **EC2 launch type** variant of the ECS recipe (with `--cap-add SYS_PTRACE`
  on the container instance) is a likely follow-up. It is not part of the
  initial ship because the Fargate template already covers the most common
  Linux deployment shape.
- **CDK or Terraform alternate dialects** are deliberately deferred; the
  initial reference is CloudFormation YAML for parity with the Azure Bicep
  and Kubernetes YAML recipes already in this repo.

See [`../../docs/cloud-recipes-design.md`](../../docs/cloud-recipes-design.md)
for the design that drives these recipes.
