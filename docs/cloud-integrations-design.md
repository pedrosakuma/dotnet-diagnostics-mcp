# Cloud platform integrations design

_Status: Phase 1 spike for [issue #16](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/16)._
This document answers one question: **which managed cloud platforms can host `dotnet-diagnostics-mcp` close enough to a target .NET process to make the existing MCP server useful, and what is the right first implementation path on each platform?**
Short answer:
- **AWS ECS / Fargate is the strongest near-term fit.**
- **Azure App Service Windows is plausible only as a Windows-specific follow-up, likely via a Kudu/site-extension style package.**
- **Azure App Service Linux and Azure Container Apps look attractive on paper, but both appear blocked by the absence of a documented shared PID namespace.**
- **AWS Lambda should not be treated as a normal sidecar port. It is a different product shape with a different lifecycle.**
This is a design document only. It does **not** add code, manifests, tests, or platform recipes. Implementation should happen later, one follow-up PR per platform.

## 1. Context and goals

Today the repository has a clear deployment story for Kubernetes-style co-location:
- an always-on sidecar next to the target process,
- or an on-demand attach flow for central Kubernetes,
- both built around the same runtime fact: the diagnostics process must be close enough to the target process to open the .NET diagnostics transport with compatible permissions.
Issue #16 asks what happens when the customer does **not** run plain Kubernetes, or when they want a more opinionated managed-cloud story.
For this repository, "cloud integration" does **not** mean rewriting the MCP server into a cloud-native control plane. The server itself is already platform-agnostic at the HTTP/MCP layer. The real deployment question is narrower and more concrete:
- how does the diagnostics server get **co-located** with the target process,
- how does it see the target process identifier,
- how does it see the same diagnostics transport endpoint,
- and how does it obtain any extra privilege needed for ClrMD-backed tools?
That co-location problem is easy in a Kubernetes Pod. It is much less obvious in managed platforms that intentionally hide Pod, namespace, or host-level details.

### 1.1 Goals

This spike exists to make follow-up implementation work cheaper. Its goals are:
- Evaluate feasibility per platform honestly.
- Pick one recommended architecture per platform.
- Identify what platform features we would need from the host.
- Surface blockers early, especially blockers caused by the current Linux-first runtime assumptions.
- Recommend an implementation order for follow-up PRs.
- Keep scope limited to design only.

### 1.2 Non-goals

This document does **not**:
- duplicate the central-topology recommendation from [issue
  #15](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/15),
- define a new MCP tool surface,
- ship deployable recipes,
- close issue #16,
- or promise support for platforms whose runtime model is incompatible with the current server.

### 1.3 Relation to other infrastructure work

Three nearby issues matter:
- [#15](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/15): central Kubernetes topology without a permanent sidecar in every Pod.
- [#20](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/20): a central orchestrator that can manage many target Pods.
- [#22](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/22): cloud recipes for ECS/Fargate and Cloud Run.
The cloud recipes in this document should be read as **consumers** of that broader topology work, not as replacements for it. Where possible, managed-cloud deployment should converge on the same mental model:
- one MCP server per investigation surface,
- bearer-token auth,
- no target app code changes,
- and clear separation between the control plane and the co-located data plane.

## 2. Universal constraints

These constraints are the part the cloud platform cannot wish away. If a host cannot satisfy them, it is not a good match for the current server shape.

### 2.1 Same-process-neighborhood requirement

`dotnet-diagnostics-mcp` is useful only when it runs **in the same process neighborhood** as the target .NET runtime. In practice that means:
- same machine or sandbox,
- same diagnostics transport namespace,
- and enough visibility to map target process to transport endpoint.
For Linux hosts, this is usually a shared PID namespace plus a shared filesystem path for `/tmp/dotnet-diagnostic-<pid>`.
For Windows hosts, the equivalent is access to the runtime's diagnostics named pipe and whatever host debugging privileges the platform allows.

### 2.2 Diagnostic socket / transport locality

Per `AGENTS.md`, on Linux the runtime creates a Unix domain socket at:
- `/tmp/dotnet-diagnostic-<pid>`
Implications:
- the diagnostics process must see the same `/tmp`,
- it must know or infer the target PID,
- and it must be allowed to open the socket.
This is the single biggest reason that sidecar-like co-location matters. A remote control plane is not enough by itself.

### 2.3 UID compatibility on Linux

Per `AGENTS.md`, the Linux diagnostics socket inherits the target process UID. A diagnostics process running with a different UID can hit `Permission denied`.
Therefore every Linux platform integration needs a credible answer for:
- same UID between app and diagnostics container,
- or a platform-sanctioned equivalent that still makes the socket readable.
If the platform abstracts away UID control, that is a real product risk, not a documentation gap.

### 2.4 Extra privilege for ClrMD-backed tools

Per `AGENTS.md`, the following tools attach via `ptrace(2)` on Linux:
- `collect_thread_snapshot`
- `inspect_heap(source="live")`
- `inspect_heap(source="dump")` against a live PID
- `collect_process_dump`
On hosts with `kernel.yama.ptrace_scope=1`, matching UID alone is not enough. The diagnostics container also needs `CAP_SYS_PTRACE`.
For non-Linux platforms, the equivalent question is: can the hosting environment allow an auxiliary process to perform dump, thread, or heap inspection against the app process?
A platform that permits EventPipe-like collection but blocks deeper attach is still useful, but it should be treated as a **reduced-capability platform** rather than full parity.

### 2.5 Current server is Linux-first

The current repo and docs are explicitly Linux-first for deployment topology:
- the documented transport is the Unix diagnostics socket,
- the documented permission story is UID + `CAP_SYS_PTRACE`,
- the documented local and Kubernetes walkthroughs are Linux container-based.
That does **not** mean Windows can never work. It does mean Windows-hosted platforms need an explicit follow-up design for:
- Windows process discovery,
- Windows diagnostics transport handling,
- and Windows-friendly packaging.
Any recommendation that ignores that gap is not credible.

### 2.6 Bearer token distribution

The server reads `MCP_BEARER_TOKEN` from the environment. If the environment variable is absent, it generates an ephemeral token at startup.
For managed-cloud deployment we should assume:
- production setups supply a platform secret explicitly,
- the token is rotated by the platform's secret distribution system,
- and the MCP server remains inbound-only.
This matters because these integrations do **not** require outbound control-plane access to do diagnostics. The network question is mostly:
- who can reach the MCP endpoint,
- and how the bearer token arrives.

### 2.7 Unix-only path assumption matters

Because the repo's current operational guidance is centered on the Linux socket path, platforms that host only Windows processes, or platforms that obscure local filesystem/process boundaries, start from a disadvantage.
This is why App Service Windows and Lambda are not symmetrical with Fargate, even though all four are managed compute platforms.

## 3. Platform evaluations

Each section uses the same shape:
- architecture options,
- recommendation,
- required platform features,
- known blockers / open questions,
- phased implementation.

### 3.1 Azure App Service (Linux + Windows)

Azure App Service is really two different design problems:
- **Windows App Service** with Kudu and site extensions,
- **Linux App Service / Web App for Containers** with the newer sidecar story.
Those two should not be merged into one implementation PR.

#### 3.1.1 Architecture options

##### Option A: Windows App Service via Kudu / site extension

Shape:
- Package `dotnet-diagnostics-mcp` as a Kudu/site-extension style artifact.
- Run it inside the SCM/Kudu environment for the site, not as a customer-managed sidecar container.
- Expose the MCP endpoint through the SCM site, similar to the historical `dotnet-monitor` precedent on App Service.
Why it is attractive:
- Kudu/site extensions are the native App Service extension mechanism on Windows.
- The extension already lives close to the app worker process.
- This avoids inventing a fake sidecar story on a platform that does not really sell sidecars for Windows workloads.
Why it is risky:
- The repo does not yet document or implement a Windows diagnostics transport path.
- The packaging and lifecycle model is platform-specific.
- Access to deeper debugging privileges may be constrained by App Service.

##### Option B: Linux App Service custom container with App Service sidecars

Shape:
- Use App Service's sidecar-enabled Linux custom container support.
- Run the app as the main container and `dotnet-diagnostics-mcp` as a sidecar.
- Attempt to share whatever filesystem surface is needed for the diagnostics socket.
Why it is attractive:
- The platform now has a first-class sidecar feature for Linux apps.
- It matches the mental model of the existing Kubernetes sidecar recipe.
- Operationally it looks friendly to App Service customers already using custom containers.
Why it is risky:
- Microsoft documents shared network namespace, but not shared PID namespace.
- Without shared PID visibility, `inspect_process(view="list")` and ClrMD attach become suspect.
- The docs are not explicit that sidecars can mount the same replica-local filesystem path in a way that preserves socket visibility and UID semantics.

##### Option C: Linux App Service single custom container with MCP in-process or
same image
Shape:
- Package the MCP server into the same custom image as the application.
- Start both processes in the same container or bootstrap wrapper.
Why it is attractive:
- It sidesteps the missing shared-PID problem because both processes are in the same container.
- It preserves the Linux socket and UID assumptions.
Why it is a poor fit:
- It violates the sidecar/separation principle this repo has used so far.
- It effectively asks the customer to mutate the target image, which is the exact integration burden we have been avoiding.
- Operationally it is closer to "bring your own custom supervision model" than to a clean platform integration.

##### Option D: Web App for Containers multi-container / legacy compose-style path

Shape:
- Treat older multi-container App Service configuration as the equivalent of a sidecar host.
Assessment:
- This is legacy-shaped and weaker than the newer sidecar model.
- It should not be our recommended path for a fresh implementation.

#### 3.1.2 Recommendation

**Recommendation: split App Service into two separate follow-up tracks, and only one is viable in the near term.**
- **Windows App Service:** if we decide App Service is strategically important, the right architecture is **Kudu/site-extension style packaging**.
- **Linux App Service:** do **not** promise support yet. Treat sidecar-enabled Linux App Service as an **investigation-only spike** until PID visibility and shared socket access are proven on the platform.
Stated more bluntly:
- The **Windows** story is platform-native but requires Windows runtime work in this repo.
- The **Linux** story looks container-friendly but is likely blocked by missing namespace guarantees.
That means App Service is **not** the first platform I would ship, though Windows App Service is a reasonable second-wave candidate if a customer specifically wants it.

#### 3.1.3 Required platform features

For Windows App Service:
- Kudu / SCM extension hosting surface.
- Stable way to deploy and update an extension package.
- Access from the extension process to the site's .NET worker process.
- Enough debugging privilege for at least EventPipe, and ideally for dump / heap / thread tooling.
- Secure bearer-token injection into the extension process.
For Linux App Service:
- Sidecar-enabled custom containers.
- Shared local filesystem path for the diagnostics socket.
- Documented or proven shared PID visibility between main and sidecar containers.
- Ability to align effective UID between main and sidecar containers, or a host guarantee that makes the socket readable anyway.
- Some answer for `CAP_SYS_PTRACE` if full ClrMD capability is expected.

#### 3.1.4 Known blockers / open questions

Windows App Service blockers:
- The repo currently assumes Linux transport semantics.
- We do not yet have a Windows deployment or transport design for the MCP server.
- It is unclear how much of App Service's worker environment allows dump / heap attach from an extension process.
- Site-extension packaging adds a platform-specific distribution story.
Linux App Service blockers:
- App Service sidecar docs clearly describe shared networking, but I have not found equivalent documentation for shared PID namespace.
- Without shared PID namespace, the sidecar might see the socket file only partially or not have a reliable way to map target process to socket.
- UID alignment is not well documented in the sidecar model.
- `CAP_SYS_PTRACE` support for sidecars is unclear.
Strategic blocker:
- App Service splits naturally into two implementations. Treating "Azure App Service" as one item on a roadmap hides that complexity.

#### 3.1.5 Phased implementation

Phase A1: Windows feasibility spike
- Validate a minimal Kudu/site-extension packaging shape.
- Prove process discovery against a .NET App Service workload.
- Prove EventPipe-only collection first.
- Defer full ClrMD parity until platform limits are understood.
Phase A2: Windows MCP packaging follow-up
- Add a Windows-friendly runtime packaging story.
- Document bearer-token injection and SCM endpoint exposure.
- Decide whether the endpoint lives only on the SCM hostname.
Phase A3: Linux App Service feasibility spike
- Build a disposable proof on sidecar-enabled Linux custom containers.
- Verify whether sidecar and main container share PID visibility.
- Verify shared socket path access and UID behavior.
- Kill the track quickly if those guarantees do not hold.
Phase A4: Linux recipe follow-up
- Only proceed if A3 proves namespace and permission behavior are good enough.
- Otherwise explicitly mark Linux App Service unsupported and direct customers toward AKS or ECS/Fargate, with ACA remaining design-only until its own feasibility spike succeeds.

### 3.2 Azure Container Apps (ACA)

ACA is attractive because it already embraces sidecars, Dapr, and managed ingress. Unfortunately, its ergonomics for helper containers are not the same as Kubernetes Pod control.

#### 3.2.1 Architecture options

##### Option A: Standard ACA sidecar in the same replica

Shape:
- Run the target app and `dotnet-diagnostics-mcp` as containers in the same Container App revision.
- Use replica-scoped `EmptyDir` storage to share `/tmp`.
- Expose the MCP server on an internal port or separate ingress.
Why it is attractive:
- ACA supports multiple containers per replica.
- ACA supports replica-scoped `EmptyDir` storage that multiple containers can mount.
- It is the cleanest conceptual port of the Kubernetes sidecar design.
Why it is risky:
- Microsoft documentation covers shared storage and sidecars, but not shared PID namespace.
- Public guidance strongly suggests ACA does **not** expose Kubernetes-style `shareProcessNamespace`.
- Without shared PID visibility, the diagnostics server may not be able to discover or attach to the target process correctly.

##### Option B: Sidecar with reduced capability set

Shape:
- Accept that ACA cannot offer full attach parity.
- Support only the subset of tools that can work without deeper process visibility.
Why it is attractive:
- Could still provide a counters / trace-oriented story if the runtime transport proves reachable.
Why it is risky:
- The current server does not advertise per-platform degraded capability based on deployment topology.
- Users would expect the normal tool set and hit confusing runtime failures.
- This would be a new product shape, not just a recipe.

##### Option C: Co-package the diagnostics server in the app container image

Assessment:
- Technically the same escape hatch as App Service Linux.
- Strategically the same bad trade: customer image mutation instead of a clean sidecar integration.

#### 3.2.2 Recommendation

**Recommendation: do not ship ACA in the first wave.**
ACA is promising only if we can prove one of these is true:
- containers in the same replica share enough PID visibility for the current diagnostics model, or
- the server can be intentionally reduced to a smaller capability set without breaking user expectations.
Today I would not bet a first-wave implementation on either assumption.
The practical recommendation is:
- keep ACA as a documented design target,
- but defer implementation until namespace behavior is proven,
- and do not market it as "basically Kubernetes" for this use case.

#### 3.2.3 Required platform features

Minimum features we would need:
- multiple containers per replica,
- replica-scoped shared `EmptyDir` storage,
- internal networking between sidecar and app,
- explicit process visibility between containers,
- a way to keep diagnostics ingress internal or separately authenticated,
- and a clear story for revision-based rollout without breaking existing MCP sessions.
Nice-to-have features:
- secret injection from Key Vault references,
- ability to disable public ingress for the diagnostics container,
- and predictable cold-start behavior when `minReplicas = 0`.

#### 3.2.4 Known blockers / open questions

- No documented shared PID namespace equivalent.
- Unknown UID handling between containers for the Linux diagnostics socket.
- Unknown `CAP_SYS_PTRACE` support or equivalent for full ClrMD-based attach.
- Dapr coexistence complicates port allocation and resource budgeting.
- Scale-to-zero means the diagnostics surface disappears with the app. That is fine for on-demand diagnostics, but it eliminates any expectation of a permanently reachable MCP endpoint.
- If the app already uses Dapr, our diagnostics sidecar becomes the **second** auxiliary container, increasing cold-start and resource overhead.

#### 3.2.5 Phased implementation

Phase B1: namespace and permission experiment
- Stand up a trivial ACA app with main + diagnostics sidecar + shared EmptyDir.
- Prove or disprove PID visibility.
- Prove or disprove socket readability.
- Attempt EventPipe-only calls and one ClrMD-backed call.
Phase B2: capability decision
- If B1 fails on PID visibility, stop.
- If B1 supports only a subset, decide whether ACA deserves a reduced-capability support tier.
Phase B3: recipe implementation
- Only after B1 and B2 confirm a coherent product story.
- Keep Dapr coexistence explicitly in scope for the recipe.

### 3.3 AWS ECS / Fargate

ECS / Fargate is the cleanest managed-cloud analogue of the Kubernetes sidecar model for the current server.

#### 3.3.1 Architecture options

##### Option A: Sidecar in the same task with `pidMode: task`

Shape:
- Run the target app container and `dotnet-diagnostics-mcp` in one ECS task.
- Set `pidMode: task` so containers share the PID namespace.
- Mount a shared task volume at `/tmp` for both containers.
- Expose the MCP endpoint through the task's network surface, ideally with private ingress.
Why it fits:
- Fargate supports task-level shared PID namespace.
- Containers in the same task already share the same ENI under `awsvpc`.
- Shared task volumes map naturally to the diagnostics socket requirement.
- IAM task roles give the MCP server a clean AWS-native identity if it ever needs to read secrets or emit logs.
This is the closest thing to a drop-in cloud equivalent of our Kubernetes sidecar story.

##### Option B: ECS on EC2 with a privileged DaemonSet-like agent

Assessment:
- Feasible, but off strategy.
- The point of this issue is managed-cloud ergonomics, not host-level cluster management.
- If we go this route we have recreated the node-agent debate from central Kubernetes.

##### Option C: One-off debug task per investigation

Assessment:
- Conceptually possible, but worse than a steady sidecar per task.
- Startup latency would be higher, networking would be awkward, and the task would still need to join the same process / filesystem surface.

#### 3.3.2 Recommendation

**Recommendation: make ECS / Fargate the first platform we implement.**
Why:
1. It matches the current Linux-first diagnostics model almost exactly.
2. The host exposes the two most important primitives directly: shared PID namespace and shared task storage.
3. The packaging story stays container-native.
4. IAM task roles and Secrets Manager / Parameter Store fit our bearer-token model cleanly.
5. It produces a real cloud-managed recipe without forcing a redesign of the server.
This is the least speculative platform in issue #16.

#### 3.3.3 Required platform features

- ECS task definition with two containers.
- `pidMode: task` on Linux tasks.
- Shared task volume mounted at `/tmp` in both containers.
- Same effective Linux UID between app and diagnostics containers, or a tested host guarantee that the socket remains readable.
- Decision on whether Fargate allows the equivalent of `CAP_SYS_PTRACE` needed for full ClrMD parity.
- Private secret injection for `MCP_BEARER_TOKEN`.
- Security group / load balancer / service-connect rules that keep the MCP surface internal unless explicitly exposed.

#### 3.3.4 Known blockers / open questions

- We still need to prove the Linux UID story in Fargate, not just assume it from Docker/Kubernetes precedent.
- We need to verify whether the sidecar can obtain the attach privileges needed for dump / heap / thread tooling under Fargate.
- We need to decide whether the MCP endpoint belongs behind an internal ALB, ECS Service Connect, or no ingress at all with port-forward-only access.
- Windows ECS tasks exist, but they should be explicitly out of scope for the first Fargate recipe.
These are real questions, but none of them look like architectural deal-breakers.

#### 3.3.5 Phased implementation

Phase C1: design-to-recipe conversion
- Author the ECS / Fargate reference deployment.
- Include shared `/tmp`, `pidMode: task`, and bearer-token secret wiring.
- Scope to Linux Fargate only.
Phase C2: validation
- Smoke-test `inspect_process(view="list")`, `collect_events(kind="counters")`, and `collect_sample(kind="cpu")`.
- Attempt one ClrMD-backed call to see whether extra privilege is available.
- Document any capability downgrade explicitly if needed.
Phase C3: hardening
- Add internal-only ingress guidance.
- Add IAM task-role guidance.
- Add log and health-check guidance.

### 3.4 AWS Lambda

Lambda deserves a different framing. The question is not "can we run a sidecar?" The question is whether the Lambda Extensions model can host a useful, interactive, co-located diagnostics surface at all.

#### 3.4.1 Architecture options

##### Option A: External Lambda extension layer

Shape:
- Package `dotnet-diagnostics-mcp` as an external extension.
- Register with the Lambda Extensions API.
- Run as a sibling process inside the execution environment.
- Attempt diagnostics during `Init`, `Invoke`, and `Shutdown` windows.
Why it is attractive:
- It is the only truly native extension mechanism Lambda offers.
- Extensions already run as separate processes alongside the function runtime.
- It avoids mutating user code.
Why it is risky:
- Lambda freezes the execution environment between invocations once runtime and extensions are idle.
- A normal long-lived inbound MCP session does not map cleanly onto that model.
- Function lifetimes are short, ingress is nonstandard, and cold/warm reuse is intentionally opaque.

##### Option B: Function-integrated diagnostics helper

Shape:
- Link diagnostics behavior directly into the function or its bootstrap path.
Assessment:
- This violates the no-target-modification goal more than any other platform.
- It should not be the repository's preferred answer.

##### Option C: Lambda-specific batch collector,
not a normal MCP endpoint
Shape:
- Accept that Lambda is not a stable inbound MCP host.
- Reframe the integration as: "collect a short diagnostics artifact during invocation and export it elsewhere."
Assessment:
- This is strategically coherent, but it is not a port of the current server.
- It is a separate product mode, closer to an extension-based capture agent than to `dotnet-diagnostics-mcp` as deployed today.

#### 3.4.2 Recommendation

**Recommendation: do not implement Lambda as part of the normal cloud-recipes wave.**
If Lambda support is ever pursued, it should be tracked as a distinct design effort with a separate success criterion:
- not "run the existing interactive MCP server unchanged,"
- but "derive a Lambda-native diagnostics capture mode from the same core primitives."
In other words, Lambda is feasible only if we are willing to change the product shape. That makes it the **last** platform I would prioritize from issue #16.

#### 3.4.3 Required platform features

To make even a limited Lambda story useful we would need:
- external extension packaging,
- access to the .NET runtime process from the extension process,
- enough time during invocation or shutdown to collect data,
- a way to flush captured artifacts before freeze/shutdown,
- and a clear external destination for those artifacts.
For the current server shape, we would additionally need:
- some stable inbound network surface,
- long enough runtime lifetime for an MCP session,
- and no freeze during the investigation window.
Those extra requirements are exactly where Lambda stops being a good fit.

#### 3.4.4 Known blockers / open questions

- The execution environment freezes between invocations once the runtime and extensions have completed.
- There is no credible always-on MCP endpoint in the common case.
- Provisioned concurrency reduces cold-start pain but does not change the fundamental freeze/thaw lifecycle.
- Diagnostics collected only during active invocation windows are useful, but they are a different UX than live interactive diagnosis.
- Exposing an inbound diagnostics endpoint from Lambda would be unnatural and operationally odd.
- Cost and latency tradeoffs may be worse than simply reproducing the workload on ECS/Fargate for diagnosis.

#### 3.4.5 Phased implementation

Phase D1: decide whether Lambda is in-scope as a product
- If we require an interactive MCP endpoint, stop here.
- If we accept a capture-agent model, open a separate design issue.
Phase D2: extension-only proof
- Validate whether an external extension can collect useful .NET artifacts during `Init`, `Invoke`, and `Shutdown`.
- Do not attempt full MCP parity.
Phase D3: separate productization track
- Only if D2 proves the capture-agent model worthwhile.
- Keep it explicitly out of the normal sidecar/cloud-recipe roadmap.

## 4. Comparative matrix

| Platform | Can co-locate with target? | Shared PID support story | Extension mechanism | Expected attach latency | Blocker severity | Verdict |
| --- | --- | --- | --- | --- | --- | --- |
| Azure App Service Windows | Yes, via SCM/Kudu neighborhood rather than sidecar | Unknown for full debugging; not a Linux PID-namespace story | Yes: Kudu / site-extension style model | Medium | High | Plausible, but needs Windows-specific design |
| Azure App Service Linux | Maybe, via App Service sidecars | **Unproven / likely missing** from docs | Yes: App Service sidecars for Linux custom containers | Medium | High | Do not ship until namespace behavior is proven |
| Azure Container Apps | Yes, same replica | **No documented shared PID namespace** | Not really an extension model; native sidecar containers instead | Medium | High | Defer |
| AWS ECS / Fargate | Yes, same task | **Yes**, via `pidMode: task` on Linux tasks | Native sidecar via task definition | Low to medium | Medium | **Ship first** |
| AWS Lambda | Yes, same execution environment for an extension process | N/A in sidecar sense; sibling process lifecycle only during active phases | Yes: Lambda Extensions API | High and bursty | Very high | Separate product mode, not normal recipe |

A few clarifications behind the table:
- "Can co-locate" means the platform gives us **some** way to run next to the workload, not that the current server will work unchanged.
- "Shared PID support story" is the critical differentiator for Linux sidecar parity.
- "Extension mechanism" matters most for Windows App Service and Lambda, where a normal sidecar is not the right mental model.

## 5. Recommended order of implementation

### 5.1 First platform: AWS ECS / Fargate

This should be the first follow-up PR after the design doc.
Reasons:
- best match to the current Linux container assumptions,
- native sidecar/task-definition model,
- explicit shared PID support,
- explicit task-scoped secret and IAM story,
- and lowest risk that the implementation turns into a product redesign.
If we want a proof that issue #16 is worth pursuing, ECS / Fargate is the proof.

### 5.2 Second platform: Azure App Service Windows,
not Linux App Service or ACA
This is the opinionated part of the recommendation. I would choose **Windows App Service** as the second-wave exploration, not because it is lower risk than ACA, but because it exercises a **different strategic path**:
- a platform-native extension host,
- rather than another Linux sidecar host that may or may not expose the right namespaces.
Why not Linux App Service second?
- because the missing shared-PID story makes it too likely we burn time on an attractive dead end.
Why not ACA second?
- for the same namespace reason, plus Dapr/scale-to-zero complexity.
Why Windows App Service second?
- because if it works, we learn how to package the server for a cloud-managed extension surface, which is a genuinely new capability.
- because the dotnet-monitor precedent suggests App Service customers already understand the SCM/extension mental model.
Important caveat:
- This second-wave recommendation assumes we are willing to do Windows-specific design work.
- If the project is intentionally staying Linux-only for the foreseeable future, then there is no honest second platform yet; in that case, ship ECS/Fargate first and keep the rest in design status.

## 6. Cross-cutting concerns

### 6.1 Secret and bearer-token distribution

The bearer-token story is straightforward across clouds, and should remain boring.
Recommended mapping:
- **Azure App Service / ACA:** Key Vault references or App Service / Container Apps secrets.
- **AWS ECS / Fargate:** Secrets Manager or SSM Parameter Store injected into the task.
- **AWS Lambda:** environment variables or Secrets Manager fetch during extension init, if Lambda is ever pursued.
Principles:
- always set `MCP_BEARER_TOKEN` explicitly in managed environments,
- do not rely on the server's ephemeral auto-generated token except in local dev,
- and keep the token scoped per deployment surface, not shared globally across unrelated workloads.

### 6.2 Ingress and egress

Ingress:
- The MCP endpoint is an **inbound** operational surface.
- It should default to private reachability.
- Public exposure should be deliberate and rare.
Egress:
- The server does **not** need outbound internet access to do diagnostics.
- If later deployments add outbound calls, they should be limited to secret retrieval, logging, or orchestrator communication.
This matters because it keeps the threat model small:
- we are exposing a diagnostics API, not a cloud agent that must continuously talk back to a SaaS control plane.

### 6.3 Capability tiers may differ by platform

The repo currently presents one main tool surface. Managed-cloud integrations may force a more nuanced reality:
- some platforms may allow full EventPipe + ClrMD parity,
- some may allow EventPipe-only,
- some may only support short-lived capture windows.
That is acceptable **only if it is documented explicitly**. What we should avoid is a recipe that looks fully supported but fails at runtime for half the tools.

### 6.4 Relationship to #15 and #20

Issue #15 and issue #20 matter because several cloud integrations could be implemented in two layers:
- a **co-located data plane** that touches the runtime,
- and a **central orchestration plane** that decides where to attach.
Examples:
- ECS/Fargate could later plug into a central orchestrator that knows about many services or tasks.
- App Service or ACA recipes could eventually feed a central deployment that manages diagnostics entrypoints consistently across environments.
So these cloud recipes should not each invent their own custom control plane. They should be thought of as **targets that a future central orchestrator can consume**.

### 6.5 Resource overhead and noisy-neighbor effects

All of these integrations introduce some amount of overhead:
- extra container or extension process,
- extra ports,
- extra memory footprint,
- and potential attach-time CPU spikes.
The design guidance should stay consistent with the rest of the repo:
- prefer low-impact collectors first,
- keep heavy capture operations explicit,
- and document when a platform's guardrails make the heaviest tools risky.

### 6.6 Support policy should stay honest

After this spike, we should avoid blanket language like:
- "Azure supported"
- or "AWS supported"
The support unit should be more precise:
- **ECS/Fargate on Linux**
- **App Service Windows via extension model**
- **ACA experimental / not yet implemented**
- **Lambda not supported as interactive MCP**
That precision will save support and review time later.

## 7. Open questions

The next PRs should answer these explicitly.

### 7.1 Windows platform questions

- What Windows diagnostics transport assumptions must `dotnet-diagnostics-mcp` add before any App Service Windows work is credible?
- Can a Kudu/site-extension hosted process perform only EventPipe collection, or full dump / heap / thread attach as well?
- Should a Windows App Service integration expose the MCP endpoint only through the SCM hostname, or through a separate internal reverse proxy?

### 7.2 Linux namespace and permission questions

- Does App Service Linux sidecar hosting expose shared PID visibility between main and sidecar containers?
- If not, is there any supported App Service Linux pattern that still makes the runtime socket usable without mutating the customer image?
- Does ACA expose any hidden or future process-sharing primitive that would make a full sidecar recipe viable?
- How much Linux capability control does Fargate actually allow for the ClrMD-backed tool set?

### 7.3 Product-shape questions

- Do we want to formalize platform capability tiers in docs and possibly in the tool responses?
- Is Lambda in scope only if it becomes a separate capture-agent product mode?
- Should the central orchestrator work from #20 be designed from the start to target non-Kubernetes clouds too, or should it stay Kubernetes-specific first?

### 7.4 Operational questions

- Which clouds should get internal-only ingress guidance by default?
- Should follow-up recipes standardize on port-forward / bastion access rather than load-balancer exposure for the MCP endpoint?
- How do we want to document token rotation in environments where the platform rolls instances independently from the app?

## 8. References

Repository-local references:
- [`AGENTS.md`](../AGENTS.md)
  - "Diagnostic socket UID — same UID, every time"
  - "CAP_SYS_PTRACE for ClrMD-backed tools"
  - "Shell escapes when driving gh / git"
- [`deploy/k8s/sample-sidecar.yaml`](../deploy/k8s/sample-sidecar.yaml)
- [`docs/local-docker-sidecar.md`](./local-docker-sidecar.md)
- [Issue #15: central K8s topology](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/15)
- [Issue #16: cloud platform integrations](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/16)
- [Issue #20: central MCP orchestrator](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/20)
- [Issue #22: cloud recipes for ECS/Fargate and Cloud Run](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/22)
- [PR #137 central K8s design doc](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/pull/137)
Platform references used for this spike:
- Azure App Service sidecars overview: <https://learn.microsoft.com/en-us/azure/app-service/overview-sidecar>
- Azure App Service sidecar configuration: <https://learn.microsoft.com/en-us/azure/app-service/configure-sidecar>
- Azure App Service custom containers: <https://learn.microsoft.com/en-us/azure/app-service/configure-custom-container>
- Azure Container Apps containers: <https://learn.microsoft.com/en-us/azure/container-apps/containers>
- Azure Container Apps storage mounts: <https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts>
- Amazon ECS task definition parameters for Fargate: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html>
- AWS Lambda Extensions API: <https://docs.aws.amazon.com/lambda/latest/dg/runtimes-extensions-api.html>
- AWS Lambda execution environment lifecycle: <https://docs.aws.amazon.com/lambda/latest/dg/lambda-runtime-environment.html>

## 9. Final recommendation

If we want to turn issue #16 into shipped value rather than endless exploration, the sequence should be:
1. **ECS / Fargate first**.
2. **Windows App Service second only if Windows support becomes an explicit project goal**.
3. Keep **ACA** and **App Service Linux** in feasibility mode until PID and permission behavior are proven.
4. Treat **Lambda** as a separate product-shape decision, not as another sidecar recipe.
That keeps the roadmap aligned with the current diagnostics model, keeps the first implementation low-risk, and avoids pretending that every managed platform is equally sidecar-friendly.
