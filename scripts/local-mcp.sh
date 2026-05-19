#!/usr/bin/env bash
# Idempotent lifecycle wrapper for a single shared local MCP server instance.
#
# Why this exists:
#   The HTTP transport is multi-client, so one shared 127.0.0.1:8787 instance
#   serves N concurrent MCP clients (multiple `gh copilot` / `gh-cli` sessions,
#   editors, etc). This wrapper guarantees:
#     1. Exactly one running instance per dev box (pidfile interlock).
#     2. Deterministic URL + bearer — independent of launchSettings drift.
#     3. Clean start / stop / status / restart / logs without per-shell `nohup`.
#
# Pair with ~/.copilot/mcp-config.json:
#   {"dotnet-diagnostics":{"type":"http","url":"http://127.0.0.1:8787/mcp",
#    "headers":{"Authorization":"Bearer <same as MCP_BEARER_TOKEN below>"}}}
#
# Usage:
#   scripts/local-mcp.sh start    # build (if needed) + start in background
#   scripts/local-mcp.sh stop
#   scripts/local-mcp.sh restart
#   scripts/local-mcp.sh status
#   scripts/local-mcp.sh logs [-f]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="${REPO_ROOT}/src/DotnetDiagnosticsMcp.Server"
PIDFILE="${LOCAL_MCP_PIDFILE:-/tmp/dotnet-diagnostics-mcp.pid}"
LOGFILE="${LOCAL_MCP_LOGFILE:-/tmp/dotnet-diagnostics-mcp.log}"
URL="${LOCAL_MCP_URL:-http://127.0.0.1:8787}"
TOKEN="${MCP_BEARER_TOKEN:-demo-local-token-2026}"

is_alive() {
    local pid="$1"
    [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null
}

read_pid() {
    [[ -f "$PIDFILE" ]] && cat "$PIDFILE" 2>/dev/null || true
}

cmd_status() {
    local pid; pid="$(read_pid)"
    if is_alive "$pid"; then
        echo "running pid=$pid url=$URL log=$LOGFILE"
        return 0
    fi
    echo "stopped"
    return 1
}

cmd_start() {
    local pid; pid="$(read_pid)"
    if is_alive "$pid"; then
        echo "already running pid=$pid url=$URL"
        return 0
    fi
    rm -f "$PIDFILE"

    # Ensure a fresh Release build exists — idempotent.
    echo "building (Release) ..."
    dotnet build "${REPO_ROOT}/DotnetDiagnosticsMcp.slnx" -c Release --nologo -v q >/dev/null

    echo "starting on $URL ..."
    # setsid + nohup so the process survives this shell. --no-launch-profile
    # bypasses launchSettings entirely so the URL is whatever --urls says.
    MCP_BEARER_TOKEN="$TOKEN" \
        setsid nohup dotnet run \
            --project "$PROJECT" \
            -c Release \
            --no-build \
            --no-launch-profile \
            --urls "$URL" \
        > "$LOGFILE" 2>&1 < /dev/null &
    local launched=$!
    echo "$launched" > "$PIDFILE"
    # Wait up to ~10s for the listener to come up.
    for _ in {1..20}; do
        sleep 0.5
        if curl -fsS -o /dev/null -m 1 \
            -H "Authorization: Bearer $TOKEN" \
            -H "Accept: application/json, text/event-stream" \
            -H "Content-Type: application/json" \
            -X POST "${URL}/mcp" \
            -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"local-mcp.sh","version":"1"}}}'
        then
            echo "ready pid=$launched url=$URL log=$LOGFILE"
            return 0
        fi
    done
    echo "FAILED to become ready in 10s — tail of $LOGFILE:" >&2
    tail -n 30 "$LOGFILE" >&2 || true
    return 1
}

cmd_stop() {
    local pid; pid="$(read_pid)"
    if ! is_alive "$pid"; then
        echo "not running"
        rm -f "$PIDFILE"
        return 0
    fi
    echo "stopping pid=$pid ..."
    kill "$pid" 2>/dev/null || true
    for _ in {1..20}; do
        sleep 0.5
        is_alive "$pid" || break
    done
    if is_alive "$pid"; then
        echo "force-killing pid=$pid"
        kill -9 "$pid" 2>/dev/null || true
    fi
    rm -f "$PIDFILE"
    echo "stopped"
}

cmd_restart() {
    cmd_stop || true
    cmd_start
}

cmd_logs() {
    if [[ "${1:-}" == "-f" ]]; then
        tail -n 100 -f "$LOGFILE"
    else
        tail -n 100 "$LOGFILE"
    fi
}

case "${1:-status}" in
    start)   cmd_start ;;
    stop)    cmd_stop ;;
    restart) cmd_restart ;;
    status)  cmd_status ;;
    logs)    shift; cmd_logs "${1:-}" ;;
    *) echo "usage: $0 {start|stop|restart|status|logs [-f]}" >&2; exit 2 ;;
esac
