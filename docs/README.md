# Documentation

| File | What it covers |
|---|---|
| [`tool-reference.md`](./tool-reference.md) | Every MCP tool: parameters, returns, runtime requirements, examples |
| [`investigation-playbooks.md`](./investigation-playbooks.md) | Step-by-step recipes for common symptoms (slow, leaking, 5xx, slow HTTP, NativeAOT) |
| [`client-setup.md`](./client-setup.md) | Connecting to the server from the C# SDK, GUI MCP clients, and `curl` smoke tests |
| [`local-docker-sidecar.md`](./local-docker-sidecar.md) | Reproducing the K8s sidecar topology locally with plain Docker (`--pid=container:` + shared `/tmp`) |
| [`bad-code-scenarios.md`](./bad-code-scenarios.md) | The 7 anti-patterns in `samples/BadCodeSample/` and the MCP investigation flow each one exercises |
| [`../deploy/k8s/README.md`](../deploy/k8s/README.md) | Sidecar topology for Kubernetes, including the required pod-level settings |
| [`rfcs/`](./rfcs/README.md) | Numbered design documents for cross-cutting changes (e.g. [RFC 0001 — per-tool authorization scopes](./rfcs/0001-per-tool-authorization-scopes.md)) |

Planned but not yet written:

- `architecture.md` — high-level component map (Core vs Server, EventPipe pipeline)
- `nativeaot-support.md` — capability matrix and limitations (currently summarized inside `tool-reference.md` and `investigation-playbooks.md`)

