# Cloud recipes design

_Status: design doc for [issue #22](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/22)._  
_Parent design: [PR #138 — cloud platform integrations design](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/pull/138)._  
_This file is design only. It adds no templates under `deploy/aws/` or `deploy/gcp/`._

## Context

Issue #22 asks for the next two cloud-host deployment recipes after the Azure
examples already in the repo: AWS ECS / Fargate and GCP Cloud Run. This
rewrite is intentionally narrower than the previous draft. It is not trying to
re-run the broad cloud survey from PR #138. That parent design already made the
important portfolio decision: ECS / Fargate is the best next recipe, while
Cloud Run is worth documenting only if the limitations are explicit.

This child document should therefore do three things well:

- decide the first IaC format for each platform
- define the minimum sidecar shape each recipe must implement
- give later PRs a clear validation and smoke-test contract

It should not try to be an AWS or GCP handbook. It should not speculate about
future discovery services, remote control planes, or platform-specific product
expansions. Those belong to issue #16 at the portfolio level or to issue #20 if
and when a central orchestrator is added.

The style target is the existing Azure material under `deploy/azure/`.
Those recipes are useful because they are small, provider-native,
review-friendly, and honest about prerequisites. The next cloud recipes should
preserve that tone. They should look like reference deployments for the current
sidecar model, not like a new product layer.

That current model is already clear across `AGENTS.md`, the generic Kubernetes
sample, and the local Docker walkthrough:

- one application container
- one diagnostics sidecar
- shared `/tmp` so the .NET diagnostics socket is visible
- a bearer-protected MCP HTTP endpoint
- no changes to target app code

The AWS and GCP recipes should stay close to that shape. When a platform cannot
match it fully, the recipe should say so directly instead of smoothing it over.

## Universal constraints

These rules apply to both recipe families.

### Locality beats control-plane cleverness

`dotnet-diagnostics-mcp` only works when it sits close enough to the target
runtime to reach the diagnostics transport. For Linux-hosted sidecars that means
shared task or instance locality, shared filesystem visibility for the socket,
and a privilege model compatible with the tool being requested. A recipe that
only explains networking but not attach locality is incomplete.

### Shared `/tmp` is non-negotiable

The repo already assumes the .NET diagnostics socket lives under `/tmp`. Every
cloud recipe therefore needs an explicit answer for how the app and diag
containers share `/tmp`, whether the share is task-local or instance-local, and
how the smoke test proves socket visibility. The design should optimize for the
smallest correct answer. Persistent storage is irrelevant here; shared socket
visibility is the point.

### UID alignment still matters

`AGENTS.md` is explicit that the diagnostics socket inherits the target process
UID. If the sidecar and target run as different effective UIDs, the attach path
can fail with `Permission denied` even when networking and storage look fine.
Both new recipe families must therefore say, early and plainly, that the two
images need to agree on a UID. The docs may suggest easy ways to get there, but
the invariant matters more than any one preferred UID value.

### Secret-managed bearer tokens only

The server reads `MCP_BEARER_TOKEN` from the environment. That is fine, but the
reference cloud story should not normalize inline secrets. The default guidance
must be AWS Secrets Manager for ECS and GCP Secret Manager for Cloud Run.
Generated fallback tokens are acceptable for local experiments, not for the
reference production-style recipes.

### Diagnostics ingress defaults private

The MCP endpoint is an operator surface. The default examples should prefer
private ingress, internal networking, and clearly identified callers.
Public exposure can exist as an override, but it should not be the first thing a
reader sees.

### Static validation belongs in the contract

Issue #22 explicitly asks for validation without a real deploy. That is a good
constraint. It keeps the first recipe PRs reviewable and forces each platform
choice to stay declarative. The design should therefore prefer IaC formats with
a straightforward native validation command and a small runtime smoke test.

### Do not over-design for future orchestration

Issue #20 may eventually add a central orchestrator, but that does not change
what these recipes need to solve today. The recipes should remain data-plane
focused: how to run the sidecar next to the app, how to secure it, and how to
prove the deploy is alive. They should not try to define a fleet control plane.

## AWS ECS/Fargate

### Decision and rationale

**Use CloudFormation YAML as the first AWS IaC.**

That is the right answer for this repo. CloudFormation YAML is provider-native,
directly reviewable in git, easy to validate with `cfn-lint`, and close in
spirit to the existing Azure Bicep and Kubernetes YAML artifacts. It keeps the
focus on the infrastructure shape instead of introducing another program that
then synthesizes the real deployable artifact.

I do not recommend CDK first. CDK is a perfectly respectable option for teams
building bigger internal platforms, but it is the wrong first reference recipe
here because reviewers then have to reason about both the source program and the
synthesized template AWS actually receives. That is unnecessary ceremony for a
small, opinionated example.

I also do not recommend Terraform first. Terraform may become a useful alternate
dialect later, especially for users standardizing across clouds, but this repo's
current reference style is provider-native. The first AWS recipe should answer a
simple question: what is the smallest correct AWS-native sidecar deployment for
this server? CloudFormation YAML answers that more directly than Terraform.

### Recommended task shape

The first AWS recipe should describe one ECS service on Fargate running a single
task definition with exactly two Linux containers:

1. `app`
2. `diag`

The task definition should make the diagnostics-critical decisions explicit:

- `networkMode: awsvpc`
- `pidMode: task`
- one shared task-scoped volume mounted at `/tmp` in both containers
- diagnostics container port `18887`
- `awslogs` for both containers
- ECR-friendly image parameters for both images
- `linuxParameters.capabilities.add: ["SYS_PTRACE"]` on `diag`
- bearer token injection from Secrets Manager
- minimal IAM

That is the whole point of shipping AWS first. ECS / Fargate can express the two
Linux properties that matter most for this project: shared PID visibility via
`pidMode: task`, and a real capability path for `SYS_PTRACE`. Those two facts
make AWS materially closer to the existing Kubernetes and Docker references than
Cloud Run.

### Container expectations

The `diag` container should listen on port `18887`, set
`ASPNETCORE_URLS=http://0.0.0.0:18887`, mount the shared `/tmp` volume, use
`awslogs`, and receive `MCP_BEARER_TOKEN` through ECS secret injection from
Secrets Manager. It should also carry
`linuxParameters.capabilities.add: ["SYS_PTRACE"]` by default because the whole
reason to prefer ECS is that it can support the ptrace-backed tools.

That still should **not** be documented as blanket support for every Linux tool.
`collect_sample(kind="off_cpu")` depends on `perf` plus `CAP_PERFMON` (or equivalent
host configuration), and this design does not claim that Fargate provides that
path. The first AWS recipe should therefore aim for near-parity on the socket,
EventPipe, and ptrace-backed flows while treating perf-based sampling as an open
question.

The `app` container should mount the same shared `/tmp` volume, set
`DOTNET_EnableDiagnostics=1` explicitly in the reference example, and otherwise
stay application-agnostic. The recipe should not assume app-specific filesystem
paths or entrypoint conventions beyond what is needed for the shared sidecar
shape.

### Shared storage choice

The simplest correct answer is one shared task-scoped volume mounted at `/tmp`
in both containers. That is enough for the diagnostics socket. Issue #22 names
`bind` or `tmpfs` style ideas, but the design should not start there. The first
recipe is about shared socket visibility, not storage micro-optimization. If a
more specialized storage option later proves materially better, it can be added
later.

### Networking and logs

The service should standardize on `awsvpc`. The README should lead with private
access: private subnets when practical, security groups that allow port `18887`
only from operator networks or an internal ALB, and no public internet exposure
by default. An internal ALB is worth mentioning as the optional front door for
stable DNS or centralized TLS, but it should not dominate the first template.

Both containers should use `awslogs`. That is enough for the reference recipe.
The goal is not to solve observability architecture. The goal is to give
operators an obvious place to inspect app startup failures, diag startup
failures, UID or socket-permission issues, and bearer-token wiring mistakes.

### IAM, images, and secrets

The reference recipe should assume both images are pulled from ECR. That keeps
the example simple and entirely native to AWS. It does not need to forbid other
registries, but it should optimize for the least surprising path.

IAM should stay intentionally small. The task execution role only needs enough
privilege to pull from ECR, write to CloudWatch Logs, and read the bearer-token
secret from Secrets Manager. The task role should be minimal by default, likely
empty or nearly empty, because the MCP server does not need AWS API access just
to collect diagnostics from a colocated .NET process.

The default bearer-token wiring should therefore be one Secrets Manager secret,
injected into the `diag` container environment and mapped to
`MCP_BEARER_TOKEN`. Parameter Store can be mentioned as an alternative, but it
should not replace Secrets Manager as the primary recommendation.

### Smoke test

The later AWS README should include a short smoke test with five steps:

1. deploy the stack
2. wait for the ECS service to stabilize
3. inspect the `diag` container logs in CloudWatch
4. verify a `/tmp/dotnet-diagnostic-*` socket is visible from the diag container
5. hit the MCP health endpoint on port `18887` with the bearer token

That smoke test is intentionally small. It proves the task came up, the sidecar
can see the socket path, and the MCP endpoint is reachable. It should not try to
be a full acceptance suite.

### Open questions

The AWS follow-up PR should answer these directly:

1. Should the example pin a specific Fargate platform version for the clearest
   `SYS_PTRACE` story, or rely on `LATEST`.
2. Should the README recommend rebuilding the app image, rebuilding the diag
   image, or simply require same-UID alignment without preferring which image
   changes.
3. Should the first implementation include an internal ALB path in-template, or
   keep the initial stack limited to direct service networking plus README
   guidance.
4. Should the example create a CloudWatch log group with a short default
   retention period, or defer retention entirely to operators.
5. Is the simplest shared task volume enough on all Fargate variants we care
   about, or do we need tighter wording around storage behavior.

## GCP Cloud Run

### Decision and rationale

**Use Cloud Run service YAML applied with `gcloud run services replace` as the
first GCP IaC.**

That is the clearest way to document Cloud Run for this repo. The service YAML
is close to the actual Cloud Run resource shape, easy to review in git, and a
natural fit for the dry-run validation story from issue #22.

I do not recommend Terraform first. Terraform may become a useful alternate
option later, but the first GCP recipe needs to show the platform's actual
container, volume, probe, ingress, and secret model directly. Service YAML is
better than a provider abstraction when the main job of the recipe is to explain
what Cloud Run can and cannot do.

### Recommended service shape

The first Cloud Run recipe should be one multi-container Cloud Run service with
exactly two containers:

1. `diag`
2. `app`

The service should mount one shared in-memory volume at `/tmp` in both
containers. Cloud Run's single-ingress model is the biggest design mismatch
versus the Azure and Kubernetes recipes, because only one container can receive
service traffic. This design does **not** treat that mismatch as solved. It
chooses one explicit phase-1 posture instead:

- the first Cloud Run recipe represents a **dedicated diagnostics service**,
  not an in-place retrofit of an existing public app service;
- in that dedicated service, `diag` is the ingress container and owns port
  `18887`;
- if preserving normal app ingress is a hard requirement, the follow-up PR must
  treat that as a separate design problem rather than pretending the limitation
  does not exist.

The `diag` container should set `ASPNETCORE_URLS=http://0.0.0.0:18887`, mount
the shared in-memory `/tmp` volume, and receive `MCP_BEARER_TOKEN` from Secret
Manager. The `app` container should mount the same `/tmp` volume, set
`DOTNET_EnableDiagnostics=1` in the reference example, and define a
`startupProbe`.

That `startupProbe` belongs on the app container, not the diag container. The
question the smoke test really cares about is whether the target runtime has
started far enough to create its diagnostics socket and become meaningful to
inspect. A healthy HTTP listener in the sidecar is not enough.

### Shared in-memory volume

Cloud Run's shared in-memory volume is the right equivalent to `emptyDir` for
this recipe family. It should be mounted at `/tmp` in both containers. The README
should be explicit that the volume is instance-local, memory-backed, and not
persistent. The example should also set an explicit size limit, even if that
limit is small, because the design is about socket sharing rather than generic
scratch storage.

### gVisor and unsupported MCP tools

This is the most important Cloud Run section.

Cloud Run runs inside gVisor and does not expose a capability knob equivalent to
`linuxParameters.capabilities.add: ["SYS_PTRACE"]`. That means Cloud Run should
be documented as a **reduced-capability** host for this project. The docs should
name the unsupported MCP tools directly:

- `collect_thread_snapshot`
- `inspect_heap(source="live")`
- `inspect_heap(source="dump")` against a live PID
- `collect_process_dump`

The README should not use vague language like “some advanced tools may be
unavailable.” It should explain that these tools depend on ptrace-style access
that Cloud Run does not provide.

The same caution applies to perf-based sampling. `collect_sample(kind="off_cpu")` and
other perf-dependent flows should be treated as unsupported on Cloud Run unless a
future proof says otherwise. gVisor is the wrong environment for promising those
Linux capabilities.

### Process visibility caveat

Ptrace is not the only limitation. Cloud Run also does not document a
Kubernetes-style shared PID namespace for multi-container services. That creates
additional uncertainty even for the EventPipe-oriented tools. Shared `/tmp` may
be enough for some scenarios, but the attach path is still less trustworthy than
on ECS or Kubernetes. The design should therefore avoid parity claims. The right
tone is: likely useful for some workflows, clearly weaker than ECS, and not a
full-parity sidecar host.

### Identity, secrets, and ingress

The Cloud Run service should use a minimal service account. The identity only
needs enough privilege for the service's runtime wiring and any Secret Manager
access required by the chosen secret reference path. The design should not imply
that the diagnostics server needs broad GCP API access. It does not.

The default bearer-token path should be one Secret Manager secret, referenced by
the Cloud Run service manifest and exposed to the `diag` container as
`MCP_BEARER_TOKEN`. That keeps the GCP story parallel to AWS and Azure.

Ingress should default to **internal**. That is the better starting point
because the endpoint is operational, the platform is already reduced-capability,
and the likely callers are operator workstations, internal automation, or a
future orchestrator from issue #20. External ingress can be described as an
explicit override, but it should not be the default example.

### `min-instances = 1` caveat

Cloud Run scales to zero by default. That is a bad default for a diagnostics
endpoint. The reference recipe should therefore require `min-instances = 1`.
The README should explain the tradeoff clearly: without a warm instance the
endpoint may disappear between uses; with one warm instance the operator
workflow is more practical; and even then, Cloud Run remains request-driven and
may restart instances at any time.

That caveat matters because it is part of the overall ship-order decision. Cloud
Run is second not because it lacks value, but because it is a more constrained
fit for long-lived diagnostics workflows.

### Smoke test

The later Cloud Run README should include a short smoke test with these steps:

1. deploy the service
2. wait for the revision to become ready
3. verify the app container `startupProbe` passes
4. hit the MCP health endpoint with the bearer token
5. restate that a passing health check does **not** imply ptrace-backed tools
   work

That is the right scale for the first recipe. It proves the service is alive and
reachable without pretending the platform supports the full MCP tool surface.

### Open questions

The Cloud Run follow-up PR should answer these directly:

1. With shared `/tmp` but no documented shared PID namespace, is process
   discovery reliable enough to recommend EventPipe-oriented workflows without
   stronger caveats.
2. What is the smallest app-level `startupProbe` that is generic enough for the
   example while still meaningfully proving the runtime is alive.
3. Do we need stronger image-build guidance to keep both containers on the same
   effective UID, or is the invariant-only wording enough.
4. Should the first implementation stay a dedicated diagnostics service with
   `diag` as ingress, or do we need a separate design for teams that must keep
   the app container as the public ingress surface.
5. Should the first implementation show both internal and external ingress
   variants, or keep one internal-first manifest plus README toggles.
6. Does `min-instances = 1` need additional wording around cost and restarts to
   prevent false expectations about always-on diagnostics sessions.

## Comparative matrix

| Dimension | AWS ECS / Fargate | GCP Cloud Run |
| --- | --- | --- |
| Primary IaC | **CloudFormation YAML** | **Service YAML via `gcloud run services replace`** |
| Why this IaC first | Native, declarative, direct git diffs, simple `cfn-lint` story | Native manifest, direct review of containers and probes, simple dry-run story |
| First recipe posture | Near-parity Linux sidecar host for socket, EventPipe, and ptrace-backed flows; perf-based sampling still unproven | Reduced-capability diagnostics host |
| Container shape | Two containers in one ECS task | Two containers in one Cloud Run service instance |
| Shared `/tmp` | Shared task-scoped volume mounted at `/tmp` | Shared in-memory volume mounted at `/tmp` |
| Shared PID story | **Yes** via `pidMode: task` | No documented equivalent |
| `SYS_PTRACE` story | **Yes** via `linuxParameters.capabilities.add` | No exposed capability knob under gVisor |
| Diag port | `18887` | `18887` |
| Logs | `awslogs` for both containers | Cloud Run service logs |
| Image source in reference recipe | ECR | Cloud Run-compatible container images |
| Secret source | AWS Secrets Manager | GCP Secret Manager |
| IAM / identity | Minimal execution role, minimal task role | Minimal service account |
| Networking default | `awsvpc` with private access or internal ALB | Internal ingress |
| Public exposure guidance | Optional override, not default | Optional override, not default |
| Lifecycle fit | Stronger for warm diagnostics access | Requires `min-instances = 1` and is still request-driven |
| Unsupported / unproven MCP tools | `collect_sample(kind="off_cpu")` remains unproven because the design does not assume a perf + `CAP_PERFMON` path on Fargate | `collect_thread_snapshot`, `inspect_heap(source="live")`, live `inspect_heap(source="dump")`, `collect_process_dump`, `collect_sample(kind="off_cpu")` |
| Smoke test emphasis | Socket visibility plus health endpoint | Health endpoint plus explicit caveat about unsupported tools |
| Ship order | **First** | **Second** |
| Overall verdict | Best next recipe | Useful but clearly constrained |

The matrix should not be read as “both platforms are basically equivalent.”
They are not. AWS is first because it matches the current sidecar assumptions
far better. Cloud Run is second because it is still worth documenting, but only
with sharp limits and with its single-ingress tradeoff stated explicitly.

## Recommended ship order

### Ship ECS / Fargate first

This should remain the official recommendation from PR #138. ECS / Fargate can
express the shape the repo already understands: two containers, shared `/tmp`,
`awsvpc`, `pidMode: task`, `awslogs`, ECR images, Secrets Manager wiring, and a
real `SYS_PTRACE` path. Most importantly, its smoke test can prove socket
visibility and endpoint reachability in a way that maps cleanly to the existing
Linux sidecar story.

Shipping AWS first gives the repo one strong cloud recipe before it documents a
weaker one. That is the right way to build confidence.

### Ship Cloud Run second

Cloud Run should follow AWS in a separate PR. It is multi-container and therefore
worth supporting, but it is not parity-equivalent. gVisor blocks the ptrace path
that several MCP tools need, the service lifecycle is request-driven unless
`min-instances = 1` is set, and ingress selection is more constrained. Cloud Run
is still useful; it just should not be the first recipe people see.

### Do not combine both implementations in one PR

A single implementation PR would mix one near-parity host, one reduced-capability
host, two validation commands, and two different ingress stories. The review
would get muddy quickly. One platform per implementation PR is the cleaner path.

## Phased implementation plan

This section is a handoff for later recipe PRs. It is not permission to add the
actual templates in this PR.

### Phase 0 — this design PR

Deliverables:

- rewrite `docs/cloud-recipes-design.md`
- reference PR #138 instead of reproducing its survey
- keep the doc opinionated and design-only
- add no `deploy/aws` or `deploy/gcp` templates

Exit criteria:

- the AWS and Cloud Run decisions are explicit
- the line between parity and reduced-capability is explicit
- the later recipe PRs have clear validation and smoke-test contracts

### Phase 1 — AWS ECS / Fargate recipe PR

Recommended scope:

- `deploy/aws/README.md` if a top-level AWS index is useful
- `deploy/aws/ecs-fargate/README.md`
- one primary CloudFormation YAML template
- one example parameter file with placeholders only

Required design elements to implement:

- two containers: `app` and `diag`
- `networkMode: awsvpc`
- `pidMode: task`
- shared `/tmp` volume
- port `18887` on `diag`
- `awslogs` for both containers
- ECR-friendly image parameters
- `linuxParameters.capabilities.add: ["SYS_PTRACE"]` on `diag`
- bearer token from Secrets Manager
- minimal execution role
- minimal task role

Required static validation:

```bash
cfn-lint deploy/aws/ecs-fargate/*.yaml
```

Required smoke test shape:

- deploy stack
- wait for service stabilization
- inspect diag logs
- confirm `/tmp/dotnet-diagnostic-*` is visible
- hit `/health` on port `18887` with the bearer token

Non-goals for that PR:

- Terraform
- CDK
- public-by-default ingress
- discovery tooling that talks to AWS APIs

### Phase 2 — Cloud Run recipe PR

Recommended scope:

- `deploy/gcp/README.md` if a top-level GCP index is useful
- `deploy/gcp/cloud-run/README.md`
- one primary service YAML manifest
- one example values file with placeholders only

Required design elements to implement:

- multi-container Cloud Run service
- `diag` as ingress container
- `app` as sidecar container
- shared in-memory volume mounted at `/tmp`
- `startupProbe` on `app`
- bearer token from Secret Manager
- minimal service account
- internal ingress by default
- `min-instances = 1`
- explicit reduced-capability note naming unsupported MCP tools

Required static validation:

```bash
gcloud run services replace deploy/gcp/cloud-run/service.yaml --dry-run
```

Required smoke test shape:

- deploy service
- wait for revision readiness
- verify the app startup probe passes
- hit `/health` with the bearer token
- restate unsupported MCP tools in the success path

Non-goals for that PR:

- Terraform
- parity claims for ptrace-backed tools
- public-by-default ingress
- combining the AWS recipe in the same change

### Phase 3 — optional CI follow-up

Only after both recipe families exist:

- add `cfn-lint` coverage for AWS templates
- add Cloud Run dry-run validation coverage for GCP manifests

The CI follow-up should stay static. It should not turn the repo into a real
deploy pipeline.

### Phase 4 — optional alternate dialects

Only after the native reference recipes prove useful:

- consider CDK for AWS if contributors explicitly want it
- consider Terraform for AWS or GCP if users need a cross-cloud workflow

The default answer should remain native declarative recipes first. Alternate IaC
is a follow-up choice, not part of the initial ship.

## References

Repository references:

- [`AGENTS.md`](../AGENTS.md)
- [Issue #16 — cloud platform integrations](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/16)
- [Issue #20 — Central MCP orchestrator](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/20)
- [Issue #22 — Cloud recipes: AWS ECS/Fargate + GCP Cloud Run](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/22)
- [PR #138 — cloud platform integrations design](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/pull/138)
- [`deploy/azure/README.md`](../deploy/azure/README.md)
- [`deploy/azure/container-apps/main.bicep`](../deploy/azure/container-apps/main.bicep)
- [`deploy/azure/app-service/main.bicep`](../deploy/azure/app-service/main.bicep)
- [`deploy/k8s/sample-sidecar.yaml`](../deploy/k8s/sample-sidecar.yaml)
- [`docs/local-docker-sidecar.md`](./local-docker-sidecar.md)

Platform references for later implementation PRs:

- AWS ECS task definition parameters: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html>
- AWS ECS `LinuxParameters`: <https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_LinuxParameters.html>
- AWS Fargate platform versions: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/platform-fargate.html>
- AWS `awslogs` log driver: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/using_awslogs.html>
- AWS Secrets Manager with ECS: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/specifying-sensitive-data.html>
- Cloud Run multi-container services: <https://cloud.google.com/run/docs/deploying#sidecars>
- Cloud Run in-memory volumes: <https://cloud.google.com/run/docs/configuring/services/in-memory-volume-mounts>
- Cloud Run health checks: <https://cloud.google.com/run/docs/configuring/healthchecks>
- Cloud Run ingress settings: <https://cloud.google.com/run/docs/securing/ingress>
- Cloud Run minimum instances: <https://cloud.google.com/run/docs/configuring/min-instances>
- Cloud Run secrets: <https://cloud.google.com/run/docs/configuring/services/secrets>
