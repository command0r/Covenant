#!/usr/bin/env bash
# Covenant v2 demo launcher — isolated, reproducible, no terminal needed after it opens the console.
#
#   scripts/run-local-demo.sh                         # Local-only profile (Ollama only, no cloud credential)
#   PROFILE=hybrid scripts/run-local-demo.sh          # Hybrid: Public → cloud (+ local fallback), everything else local
#   MODEL=qwen3.8:27b-mlx PORT=5212 scripts/run-local-demo.sh
#   SKIP_PUBLISH=1 scripts/run-local-demo.sh          # reuse the last publish
#   FRESH=1 scripts/run-local-demo.sh                 # start this profile's isolated state from scratch
#
# Isolation: Production environment (user-secrets NOT loaded), its own port and its own state directory
# (/tmp/covenant-v2-demo-<profile>/: audit log, configuration repository, budget ledger). It never touches
# another running instance (e.g. :5100 or :5201) or its files. State persists across runs of the same profile
# so configuration versions, spend and evidence survive restarts; FRESH=1 moves the old state aside (renamed,
# never deleted) and starts again from the seed.
#
# Hybrid needs, in the ENVIRONMENT (never printed, never written to disk by this script):
#   OPENAI_API_KEY         the cloud credential (server-side only; the console only sees "available")
#   CLOUD_PRICE_IN_1M      USD per 1M input tokens for CLOUD_MODEL, from the provider's CURRENT price list
#   CLOUD_PRICE_OUT_1M     USD per 1M output tokens
#   CLOUD_MODEL            default gpt-4o-mini;  CLOUD_PRICE_DATE (yyyy-MM-dd) optional
# Covenant refuses to start the hybrid seed without explicit prices — no default price is ever assumed.
set -euo pipefail

PROFILE="${PROFILE:-local-only}"
PORT="${PORT:-5210}"
OLLAMA="${OLLAMA:-http://localhost:11434/v1}"
MODEL="${MODEL:-qwen2.5-coder:7b}"
CAP="${CAP:-5}"
OUT="${OUT:-/tmp/covenant-v2-pub}"
STATE="${STATE:-/tmp/covenant-v2-demo-$PROFILE}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

case "$PROFILE" in local-only) SEED=demo-local-only ;; hybrid) SEED=demo-hybrid ;;
  *) echo "PROFILE must be local-only or hybrid"; exit 1 ;; esac
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) RID=osx-arm64 ;; Darwin-x86_64) RID=osx-x64 ;;
  Linux-x86_64) RID=linux-x64 ;; Linux-aarch64) RID=linux-arm64 ;;
  *) echo "unsupported platform $(uname -sm)"; exit 1 ;;
esac
say() { printf '\n\033[1m%s\033[0m\n' "$*"; }

say "1/5 Checking Ollama at $OLLAMA"
models="$(curl -sS --max-time 5 "$OLLAMA/models")" || { echo "Ollama is not reachable at $OLLAMA (ollama serve, or set OLLAMA=...)"; exit 1; }
if ! grep -q "\"id\":\"$MODEL\"" <<<"$models"; then
  echo "Model '$MODEL' is not installed. Installed tags:"; grep -o '"id":"[^"]*"' <<<"$models" | cut -d'"' -f4 | sed 's/^/  /'
  echo "Re-run with MODEL=<one of the above>."; exit 1
fi
echo "ok — $MODEL"

if [[ "$PROFILE" == hybrid ]]; then
  : "${OPENAI_API_KEY:?Hybrid needs OPENAI_API_KEY in the environment}"
  : "${CLOUD_PRICE_IN_1M:?Hybrid needs CLOUD_PRICE_IN_1M (USD per 1M input tokens, current provider list price)}"
  : "${CLOUD_PRICE_OUT_1M:?Hybrid needs CLOUD_PRICE_OUT_1M (USD per 1M output tokens)}"
fi
CLOUD_MODEL="${CLOUD_MODEL:-gpt-4o-mini}"

if lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then
  echo "Port $PORT is in use — pick another with PORT=..."; lsof -nP -iTCP:"$PORT" -sTCP:LISTEN; exit 1
fi

if [[ "${FRESH:-}" == "1" && -d "$STATE" ]]; then
  mv "$STATE" "$STATE.$(date -u +%Y%m%dT%H%M%SZ).previous"
  echo "previous state kept at $STATE.*.previous (not deleted)"
fi
mkdir -p "$STATE"

if [[ "${SKIP_PUBLISH:-}" != "1" ]]; then
  say "2/5 Publishing ($RID, Release, self-contained single file) → $OUT"
  dotnet publish "$ROOT/src/Covenant.Host" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true --disable-build-servers -o "$OUT" -v:minimal -nologo
else
  say "2/5 Skipping publish (SKIP_PUBLISH=1)"
fi
[[ -x "$OUT/Covenant.Host" ]] || { echo "No binary at $OUT/Covenant.Host — the publish step failed above."; exit 1; }

# Credentials for THIS run only: the admin token (console) and one application key for /v1 clients
# (Open WebUI, curl). The console never needs the application key — it runs requests as authorized test
# applications (ADR-0012).
ADMIN="$(openssl rand -hex 16)"; APPKEY="$(openssl rand -hex 16)"
LOCAL_AUTHORITY="$(sed -E 's#^https?://([^/]+).*#\1#' <<<"$OLLAMA")"

say "3/5 Starting Covenant ($PROFILE) on http://localhost:$PORT  (state: $STATE)"
env_args=(
  ASPNETCORE_ENVIRONMENT=Production
  ASPNETCORE_URLS="http://localhost:$PORT"
  Covenant__Environment="demo-$PROFILE"
  Config__Seed="$SEED"
  Local__Endpoint="$OLLAMA"
  Local__ModelId="$MODEL"
  Providers__LocalEndpoints="$LOCAL_AUTHORITY"
  Admin__Token="$ADMIN"
  Budget__GlobalCapUsd="$CAP"
  Auth__Keys__0__Key="$APPKEY"
  Auth__Keys__0__Principal=demo-service
  Auth__Keys__0__Team=platform
  Auth__Keys__0__Application=demo-app
  Audit__Path="$STATE/audit.log"
)
if [[ "$PROFILE" == hybrid ]]; then
  env_args+=(
    OpenAI__ApiKey="$OPENAI_API_KEY"
    OpenAI__ModelId="$CLOUD_MODEL"
    "Pricing__${CLOUD_MODEL}__InPer1M=$CLOUD_PRICE_IN_1M"
    "Pricing__${CLOUD_MODEL}__OutPer1M=$CLOUD_PRICE_OUT_1M"
    "Pricing__${CLOUD_MODEL}__Basis=provider list price (operator-supplied)"
  )
  [[ -n "${CLOUD_PRICE_DATE:-}" ]] && env_args+=("Pricing__${CLOUD_MODEL}__EffectiveDate=$CLOUD_PRICE_DATE")
fi
LOG="$STATE/server.log"
env "${env_args[@]}" "$OUT/Covenant.Host" >"$LOG" 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null || true' INT TERM

say "4/5 Waiting for the console"
code=""
for _ in $(seq 1 60); do
  kill -0 "$PID" 2>/dev/null || { echo "Covenant exited during startup:"; echo "------"; cat "$LOG"; echo "------"; exit 1; }
  code="$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$PORT/ui/" || true)"
  [[ "$code" == "200" ]] && break
  sleep 0.5
done
[[ "$code" == "200" ]] || { echo "No answer on /ui/ (last HTTP '$code'). Log:"; cat "$LOG"; kill $PID; exit 1; }

LAUNCH="http://localhost:$PORT/ui/connect#token=$ADMIN"
say "5/5 Running — opening the console (already connected)"
command -v open >/dev/null && open "$LAUNCH" || true
cat <<EOF
  Console:        $LAUNCH
  Profile:        $PROFILE  (seed $SEED; Local-only never calls a cloud provider)
  Local model:    ollama/$MODEL
  Run requests:   Playground → "Run as" demo-app → profile "Public demo" or "Internal" (no key needed)
  /v1 app key:    $APPKEY   (only for Open WebUI / curl: base URL http://localhost:$PORT/v1)
  State:          $STATE    (audit.log, audit.log.config/, audit.log.ledger.jsonl, server.log)

  Ctrl+C stops Covenant. Credentials are new on every run; state persists per profile (FRESH=1 to restart it).
EOF
wait $PID
