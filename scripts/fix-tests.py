"""Inject TestPrincipalAccessors.Root into test call sites for B5.2-modified tools.
Uses proper paren-balance to find the matching ')' for each call."""
import re
from pathlib import Path

INSERTIONS = {
    "CollectCpuSample":      5,
    "CollectOffCpuSample":   4,
    "CollectEventSource":    5,
    "InspectDump":           3,
    "InspectLiveHeap":       4,
    "QueryHeapSnapshot":     4,
    "CollectThreadSnapshot": 4,
    "DetachFromPod":         4,
    "ListActiveInvestigations": 2,
}
PRINCIPAL = "TestPrincipalAccessors.Root"

def find_matching(text, start):
    depth = 1
    i = start
    while i < len(text) and depth > 0:
        c = text[i]
        if c in "([{": depth += 1
        elif c in ")]}": depth -= 1
        if depth == 0: return i
        i += 1
    return -1

def split_args(body):
    depth = 0
    parts = []
    last = 0
    for i, ch in enumerate(body):
        if ch in "([{": depth += 1
        elif ch in ")]}": depth -= 1
        elif ch == "," and depth == 0:
            parts.append(body[last:i])
            last = i + 1
    parts.append(body[last:])
    return parts

def process(text, method, pos):
    pattern = re.compile(rf"\b(?:DiagnosticTools|OrchestratorTools)\.{method}\(")
    out = []
    last = 0
    changes = 0
    for m in pattern.finditer(text):
        end_paren = find_matching(text, m.end())
        if end_paren < 0:
            continue
        body = text[m.end():end_paren]
        parts = split_args(body)
        if any("TestPrincipalAccessors" in p or "IPrincipalAccessor" in p for p in parts):
            continue
        if len(parts) < pos:
            continue
        # Detect indent from first arg line (after the opening paren may be on same line).
        if "\n" in body:
            # find indent of first arg
            first = parts[0]
            mws = re.match(r"\s*\n(\s*)", first)
            if mws:
                indent = mws.group(1)
            else:
                # First arg on same line, use indent of second line of body
                mws2 = re.search(r"\n(\s*)", body)
                indent = mws2.group(1) if mws2 else "            "
            new_arg = f"\n{indent}{PRINCIPAL}"
        else:
            new_arg = f" {PRINCIPAL}"
        new_parts = parts[:pos] + [new_arg] + parts[pos:]
        new_body = ",".join(new_parts)
        out.append(text[last:m.end()])
        out.append(new_body)
        last = end_paren
        changes += 1
    out.append(text[last:])
    return "".join(out), changes

# Revert prior partial changes first by running git checkout, then re-apply.
import subprocess
subprocess.run(["git", "checkout", "--", "tests/DotnetDiagnosticsMcp.Server.IntegrationTests/"], check=True)

root = Path("tests/DotnetDiagnosticsMcp.Server.IntegrationTests")
total = 0
for path in root.rglob("*.cs"):
    text = path.read_text()
    file_changes = 0
    for method, pos in INSERTIONS.items():
        text, n = process(text, method, pos)
        file_changes += n
    if file_changes:
        path.write_text(text)
        total += file_changes
        print(f"  {path.name}: +{file_changes}")
print(f"Total: {total}")
