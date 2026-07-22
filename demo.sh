#!/usr/bin/env bash
# Covenant live demo — runs the full P0 arc against a running Host.
#
#   terminal 1:  dotnet run --project src/Covenant.Host
#   terminal 2:  ./demo.sh                       the scripted walk-through
#                ./demo.sh --traffic [N] [MIN]   randomized traffic: N requests (default 40)
#                                                spread over MIN minutes (default 5) — fills the
#                                                activity chart with a diverse allow/deny mix
#                ./demo.sh --tamper              destructive tamper-evidence finale
#
# Overrides: COVENANT_URL, COVENANT_ADMIN_TOKEN, COVENANT_API_KEY, COVENANT_AUDIT_LOG
set -euo pipefail

# 5100, not 5000: macOS AirPlay Receiver occupies 5000 on the IPv6 loopback and answers 403s.
BASE="${COVENANT_URL:-http://localhost:5100}"
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

# --- Traffic generator: randomized mix so the dashboard shows realistic, diverse data ---
if [ "${1:-}" = "--traffic" ]; then
    N="${2:-40}"
    MINUTES="${3:-5}"
    PAUSE=$(python3 -c "print(max(0.2, ($MINUTES*60)/$N))" 2>/dev/null || echo 2)
    say "Traffic: $N randomized requests over ~$MINUTES minute(s) (pause ${PAUSE}s)"
    note "Mix: short/long prompts (router), PII/PHI (fail-closed), missing/wrong keys (401s), bad model asks (403s)"

    SHORT=("say hello" "what is 2 plus 2" "name three colors" "give me a haiku about audits" "what day is it" "summarize: cash is king")
    for i in $(seq 1 "$N"); do
        roll=$((RANDOM % 100))
        if   [ $roll -lt 45 ]; then    # plain internal → allowed, cheap model
            chat "${SHORT[$((RANDOM % ${#SHORT[@]}))]}"
            tag="allow/cheap"
        elif [ $roll -lt 60 ]; then    # long prompt → complexity router escalates
            chat "analyze thoroughly: $(printf 'lorem ipsum dolor sit amet %.0s' $(seq 1 80))"
            tag="allow/strong"
        elif [ $roll -lt 72 ]; then    # PII → fail-closed (or local if configured)
            chat "customer SSN is 123-45-6789, check the account"
            tag="pii"
        elif [ $roll -lt 80 ]; then    # PHI → fail-closed (or local if configured)
            chat "patient id 1023, MRN 55-70, diagnosis summary please"
            tag="phi"
        elif [ $roll -lt 88 ]; then    # no key → 401
            CODE=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/v1/chat/completions" \
                -H 'Content-Type: application/json' -d '{"messages":[{"role":"user","content":"hi"}]}')
            tag="no-key"
        elif [ $roll -lt 94 ]; then    # wrong key → 401
            CODE=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/v1/chat/completions" \
                -H 'Content-Type: application/json' -H 'Authorization: Bearer wrong-key-123' \
                -d '{"messages":[{"role":"user","content":"hi"}]}')
            tag="bad-key"
        else                           # not-permitted model → policy 403
            CODE=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/v1/chat/completions" \
                -H 'Content-Type: application/json' -H "Authorization: Bearer $API_KEY" \
                -d '{"model":"claude-3-opus","messages":[{"role":"user","content":"hi"}]}')
            tag="bad-model"
        fi
        printf '  %2d/%d  HTTP %s  %s\n' "$i" "$N" "$CODE" "$tag"
        sleep "$PAUSE"
    done
    say "Done — watch the chart and request history fill in."
    exit 0
fi

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
    | head -12 | sed 's/^/  /' || true   # head closing early SIGPIPEs curl; must not abort the script
note "attribution and audit still happen once, on stream completion — check the evidence export"

say "1c. Long prompt — the complexity router escalates to the strong model"
chat "analyze this in depth: $(printf 'lorem ipsum dolor sit amet consectetur adipiscing %.0s' $(seq 1 60))"
note "HTTP $CODE — check the live feed: served by the STRONG model, reason 'complexity-routed'"

say "2. PII request — the product moment"
chat "my SSN is 123-45-6789, please summarize my account"
if [ "$CODE" = "403" ]; then
    note "HTTP 403 — no in-perimeter model is configured, so PII fails CLOSED. It never reached a public provider."
    note "Wire Local:Endpoint (see README-SCAFFOLD) and re-run: this same request gets served locally."
else
    note "HTTP $CODE — served by the LOCAL in-perimeter model. The prompt never left the boundary."
fi
show "$BODY"

say "2c. PHI request (MRN / patient id) — same fail-closed guarantee, distinct classification"
chat "patient id 8842, MRN 129-44: summarize the diagnosis history"
note "HTTP $CODE — classified PHI; check the feed: red PHI pill, denied or served locally"
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
