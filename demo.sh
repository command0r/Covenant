#!/usr/bin/env bash
# Covenant live demo — runs the full P0 arc against a running Host.
#
#   terminal 1:  dotnet run --project src/Covenant.Host
#   terminal 2:  ./demo.sh            (add --tamper for the destructive tamper-evidence finale)
#
# Overrides: COVENANT_URL, COVENANT_ADMIN_TOKEN, COVENANT_AUDIT_LOG
set -euo pipefail

BASE="${COVENANT_URL:-http://localhost:5000}"
TOKEN="${COVENANT_ADMIN_TOKEN:-dev-admin-token}"
API_KEY="${COVENANT_API_KEY:-demo-key}"
LOG="${COVENANT_AUDIT_LOG:-covenant-audit.log}"

say()  { printf '\n\033[1;36m▶ %s\033[0m\n' "$1"; }
note() { printf '\033[0;90m  %s\033[0m\n' "$1"; }
show() { printf '%s\n' "$1" | (python3 -m json.tool 2>/dev/null || cat) | sed 's/^/  /'; }

# chat CONTENT — prints body, returns HTTP code in $CODE
chat() {
    local resp
    resp="$(curl -s -w $'\n%{http_code}' "$BASE/v1/chat/completions" \
        -H 'Content-Type: application/json' \
        -H "Authorization: Bearer $API_KEY" \
        -H 'X-Covenant-Workflow: demo' -H 'X-Covenant-UseCase: live-demo' \
        -d "{\"messages\":[{\"role\":\"user\",\"content\":\"$1\"}]}")"
    CODE="${resp##*$'\n'}"
    BODY="${resp%$'\n'*}"
}

kill_switch() { # $1 = true|false, $2 = reason
    curl -s -X POST "$BASE/admin/kill-switch" \
        -H 'Content-Type: application/json' -H "X-Covenant-Admin-Token: $TOKEN" \
        -d "{\"engaged\":$1,\"reason\":\"$2\"}"
}

evidence() {
    curl -s "$BASE/admin/evidence" -H "X-Covenant-Admin-Token: $TOKEN"
}

say "1. Governed request (Internal classification → public route, attributed to team 'platform')"
chat "say hello in five words"
if [ "$CODE" = "502" ]; then
    note "HTTP 502 — the upstream provider failed (quota/billing/network). Covenant refused fail-closed"
    note "and audited it. Fix the provider account, or run fully offline via OpenAI:Endpoint → Ollama."
else
    note "HTTP $CODE (expected 200 — served, usage attributed)"
fi
show "$BODY"

say "1b. Same request, streamed — SSE through the full governance pipeline (ADR-0002)"
curl -sN "$BASE/v1/chat/completions" \
    -H 'Content-Type: application/json' \
    -H "Authorization: Bearer $API_KEY" \
    -H 'X-Covenant-Workflow: demo' -H 'X-Covenant-UseCase: live-demo' \
    -d '{"stream":true,"messages":[{"role":"user","content":"count from 1 to 5"}]}' \
    | head -12 | sed 's/^/  /'
note "attribution and audit still happen once, on stream completion — check the evidence export"

say "2. PII request — the product moment"
chat "my SSN is 123-45-6789, please summarize my account"
if [ "$CODE" = "403" ]; then
    note "HTTP 403 — no in-perimeter model is configured, so PII fails CLOSED. It never reached a public provider."
    note "Wire Local:Endpoint (see README-SCAFFOLD) and re-run: this same request gets served locally."
else
    note "HTTP $CODE — served by the LOCAL in-perimeter model. The prompt never left the boundary."
fi
show "$BODY"

say "2b. No API key → 401 — anonymous serving is an explicit opt-in, and the refusal is audited"
CODE_NOKEY=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/v1/chat/completions" \
    -H 'Content-Type: application/json' -d '{"messages":[{"role":"user","content":"hi"}]}')
note "HTTP $CODE_NOKEY (expected 401)"

say "3. Wrong admin token is rejected"
CODE_401=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/admin/kill-switch" \
    -H 'Content-Type: application/json' -H 'X-Covenant-Admin-Token: wrong' -d '{"engaged":true}')
note "HTTP $CODE_401 (expected 401)"

say "4. Kill switch ON — all inference stops"
show "$(kill_switch true 'live demo: incident drill')"
chat "say hello"
note "HTTP $CODE (expected 403 — and this denial is audited like everything else)"
show "$BODY"

say "5. Kill switch OFF — service resumes"
show "$(kill_switch false '')"
chat "confirm you are back"
note "HTTP $CODE (expected 200)"

say "6. Compliance-evidence export — hash chain verified, then the auditor's summary"
show "$(evidence)"

if [ "${1:-}" = "--tamper" ]; then
    say "7. Tamper the audit log (DESTRUCTIVE — the chain stays broken; rm '$LOG' + restart to reset)"
    if [ ! -f "$LOG" ]; then
        note "audit log '$LOG' not found — run the demo from the directory you started the Host in"; exit 1
    fi
    total=$(wc -l < "$LOG" | tr -d ' ')
    mid=$(( (total + 1) / 2 ))
    note "altering line $mid of $total in $LOG (one appended space — any byte counts)"
    awk -v m="$mid" 'NR==m {$0=$0" "} 1' "$LOG" > "$LOG.tmp" && mv "$LOG.tmp" "$LOG"
    note "re-running the evidence export:"
    show "$(evidence)"
    note "chain_valid=false, first_invalid_line=$mid — entries after the break are no longer trusted evidence."
    note "NOTE: the appliance now refuses to RESTART on this log (fail-closed). Archive it and point"
    note "Audit:Path at a fresh file — deleting evidence is what the chain exists to catch."
else
    say "Done. Optional finale: ./demo.sh --tamper (destructive — proves the chain detects edits)"
fi
