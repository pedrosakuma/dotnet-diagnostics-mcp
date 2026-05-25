#!/usr/bin/env python3
"""B5.2 — insert [RequireScope]/[RequireAnyScope] before each [McpServerTool(
Name = "...") block in the tool surface files. Idempotent: skips lines that
already carry a scope attribute. Mapping comes from RFC 0001 §2 / task body."""

import re
import sys
from pathlib import Path

# tool_name -> ("scope", [scopes]) or ("any", [scopes])
MAPPING = {
    # read-counters
    "inspect_process(view="list")":        ("scope", ["read-counters"]),
    "inspect_process(view="info")":             ("scope", ["read-counters"]),
    "inspect_process(view="capabilities")":  ("scope", ["read-counters"]),
    "inspect_process(view="container")":        ("scope", ["read-counters"]),
    "inspect_process(view="memory_trend")":             ("scope", ["read-counters"]),
    "collect_events(kind="counters")":            ("scope", ["read-counters"]),
    # eventpipe
    "collect_sample(kind="cpu")":           ("scope", ["eventpipe"]),
    "collect_sample(kind="allocation")":    ("scope", ["eventpipe"]),
    "collect_sample(kind="off_cpu")":       ("scope", ["eventpipe"]),
    "collect_events(kind="exceptions")":           ("scope", ["eventpipe"]),
    "collect_events(kind="gc")":            ("scope", ["eventpipe"]),
    "collect_events(kind="activities")":           ("scope", ["eventpipe"]),
    "collect_events(kind="event_source")":         ("scope", ["eventpipe"]),
    "query_snapshot":       ("scope", ["eventpipe"]),
    # heap-read
    "inspect_heap(source="dump")":                 ("scope", ["heap-read"]),
    "query_snapshot":          ("scope", ["heap-read"]),
    "inspect_heap(source="live")":            ("scope", ["heap-read", "ptrace"]),
    # dump-write
    "collect_process_dump":         ("scope", ["dump-write", "ptrace"]),
    # ptrace
    "collect_thread_snapshot":      ("scope", ["ptrace"]),
    "capture_method_bytes":         ("scope", ["ptrace"]),
    "query_snapshot":        ("scope", ["ptrace"]),
    # query_snapshot drills into both Counters and EventPipe handles (§2.12).
    "query_snapshot":             ("any", ["read-counters", "eventpipe"]),
    # investigation-export — task body explicitly groups status/cancel/call_tree
    # under this scope (supersedes RFC §2.10/§2.11 for B5.2).
    "start_investigation":          ("scope", ["investigation-export"]),
    "export_investigation_summary": ("scope", ["investigation-export"]),
    "compare_to_baseline":          ("scope", ["investigation-export"]),
    "query_snapshot(view="call-tree")":                ("scope", ["investigation-export"]),
    "get_collection_status":        ("scope", ["investigation-export"]),
    "cancel_collection":            ("scope", ["investigation-export"]),
    # orchestrator
    "list_orchestrator(kind="pods")":                    ("scope", ["orchestrator-list"]),
    "attach_to_pod":                ("scope", ["orchestrator-attach"]),
    "detach_from_pod":              ("scope", ["orchestrator-attach"]),
    "list_orchestrator(kind="investigations")":   ("scope", ["orchestrator-attach"]),
}

TOOL_RE = re.compile(r'^(?P<indent>[ \t]*)\[McpServerTool\(\s*$')
NAME_RE = re.compile(r'^[ \t]*Name = "(?P<name>[^"]+)",')
ALREADY_RE = re.compile(r'\[(RequireScope|RequireAnyScope)\(')

def render_attr(kind, scopes, indent):
    args = ", ".join(f'"{s}"' for s in scopes)
    name = "RequireScope" if kind == "scope" else "RequireAnyScope"
    return f"{indent}[{name}({args})]\n"

def process(path: Path):
    src = path.read_text().splitlines(keepends=True)
    out = []
    i = 0
    seen = set()
    while i < len(src):
        line = src[i]
        m = TOOL_RE.match(line)
        if m:
            indent = m.group("indent")
            # Look at the previous non-blank source line for an existing scope attribute.
            j = len(out) - 1
            while j >= 0 and out[j].strip() == "":
                j -= 1
            already = j >= 0 and ALREADY_RE.search(out[j]) is not None
            # Find the tool name on the next few lines.
            tool_name = None
            for k in range(i + 1, min(i + 6, len(src))):
                nm = NAME_RE.match(src[k])
                if nm:
                    tool_name = nm.group("name")
                    break
            if tool_name is None:
                print(f"!! cannot find Name= for [McpServerTool( at {path}:{i+1}", file=sys.stderr)
                sys.exit(1)
            if tool_name not in MAPPING:
                print(f"!! no scope mapping for tool '{tool_name}' at {path}:{i+1}", file=sys.stderr)
                sys.exit(1)
            seen.add(tool_name)
            if not already:
                kind, scopes = MAPPING[tool_name]
                out.append(render_attr(kind, scopes, indent))
        out.append(line)
        i += 1
    path.write_text("".join(out))
    return seen

root = Path(sys.argv[1] if len(sys.argv) > 1 else ".")
files = [
    root / "src/DotnetDiagnosticsMcp.Server/Tools/DiagnosticTools.cs",
    root / "src/DotnetDiagnosticsMcp.Server/Tools/OrchestratorTools.cs",
]
all_seen = set()
for f in files:
    s = process(f)
    all_seen |= s
    print(f"{f}: decorated {len(s)} tool(s)")

missing = set(MAPPING) - all_seen
if missing:
    print(f"!! mapping entries not applied (tool not found): {sorted(missing)}", file=sys.stderr)
    sys.exit(1)
