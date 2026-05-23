#!/usr/bin/env bash
# Helm render smoke test for B5.5 — scoped bearer-token wiring (issue #186,
# RFC docs/rfcs/0001-per-tool-authorization-scopes.md §6).
#
# Confirms `helm template` renders cleanly for the three supported auth shapes
# and that each shape produces the expected env wiring:
#
#   (a) legacy single-bearer only       — `bearerToken.value` set, no `bearerTokens`
#   (b) new scoped list only            — `bearerTokens` set, `bearerToken` at defaults
#   (c) both set (back-compat overlap)  — both shapes; chart renders both; server
#                                         logs a runtime warning that the scoped
#                                         registry wins (RFC §7.1)
#
# Plus a (d) defaults-only render that MUST fail loudly (H1 placeholder guard).
#
# Invoked from CI (.github/workflows/ci.yml → "Helm render smoke (B5.5)"). Run
# locally with: tests/helm/render-smoke.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CHART="${REPO_ROOT}/deploy/helm/dotnet-diagnostics-orchestrator"
FIXTURES="${REPO_ROOT}/tests/helm"
HELM="${HELM:-helm}"

if ! command -v "${HELM}" >/dev/null 2>&1; then
  echo "ERROR: helm not found on PATH (HELM=${HELM}); install Helm 3.x first." >&2
  exit 2
fi

pass=0
fail=0

# Each row: <label> <values-file> <expected-secret-count> <expected-mcp-env-count> <expected-scoped-entries>
declare -a CASES=(
  "legacy-only  values-legacy-only.yaml  1 1 0"
  "scoped-only  values-scoped-only.yaml  0 0 2"
  "both         values-both.yaml         1 1 2"
)

run_case() {
  local label="$1" values="$2" want_secrets="$3" want_mcp_env="$4" want_scoped="$5"
  local out
  if ! out="$("${HELM}" template diag "${CHART}" -f "${FIXTURES}/${values}" 2>&1)"; then
    echo "FAIL [${label}] helm template exited non-zero"
    echo "${out}" | tail -20
    fail=$((fail + 1))
    return
  fi

  local got_secrets got_mcp_env got_scoped
  got_secrets=$(printf '%s\n' "${out}" | grep -c '^kind: Secret' || true)
  got_mcp_env=$(printf '%s\n' "${out}" | grep -c 'name: MCP_BEARER_TOKEN' || true)
  got_scoped=$(printf '%s\n' "${out}" | grep -c 'Auth__BearerTokens__.*__Name' || true)

  if [[ "${got_secrets}" -ne "${want_secrets}" || "${got_mcp_env}" -ne "${want_mcp_env}" || "${got_scoped}" -ne "${want_scoped}" ]]; then
    echo "FAIL [${label}] expected secrets=${want_secrets} mcp_env=${want_mcp_env} scoped=${want_scoped}; got secrets=${got_secrets} mcp_env=${got_mcp_env} scoped=${got_scoped}"
    fail=$((fail + 1))
    return
  fi

  echo "PASS [${label}] secrets=${got_secrets} mcp_env=${got_mcp_env} scoped=${got_scoped}"
  pass=$((pass + 1))
}

for row in "${CASES[@]}"; do
  # shellcheck disable=SC2086
  run_case ${row}
done

# (d) Defaults-only — must fail with the H1 placeholder guard. We invert the exit code.
if "${HELM}" template diag "${CHART}" >/dev/null 2>&1; then
  echo "FAIL [defaults-only] helm template succeeded; expected H1 placeholder guard to abort"
  fail=$((fail + 1))
else
  echo "PASS [defaults-only] H1 placeholder guard correctly aborted render"
  pass=$((pass + 1))
fi

echo
echo "helm render smoke: ${pass} passed, ${fail} failed"
exit $(( fail == 0 ? 0 : 1 ))
