#!/usr/bin/env bash
# Run the Covenant appliance + console locally in local-only mode against an existing Ollama.
# Isolated: Production environment (your user-secrets are NOT loaded), its own port and audit file,
# throwaway credentials generated per run. Your service on :5100 and your Ollama are not touched.
#
#   scripts/run-local-demo.sh                      # defaults below
#   MODEL=qwen3.8:27b-mlx PORT=5202 scripts/run-local-demo.sh
#   SKIP_PUBLISH=1 scripts/run-local-demo.sh       # reuse the last publish
set -euo pipefail

PORT="${PORT:-5201}"
OLLAMA="${OLLAMA:-http://localhost:11434/v1}"
MODEL="${MODEL:-qwen2.5-coder:7b}"
OUT="${OUT:-/tmp/covenant-pub}"
AUDIT="${AUDIT:-/tmp/covenant-demo-audit.log}"
LOG="${LOG:-/tmp/covenant-demo.log}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) RID=osx-arm64 ;; Darwin-x86_64) RID=osx-x64 ;;
  Linux-x86_64) RID=linux-x64 ;; Linux-aarch64) RID=linux-arm64 ;;
  *) echo "unsupported platform $(uname -sm)"; exit 1 ;;
esac

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }

say "1/4 Checking Ollama at $OLLAMA"
if ! models="$(curl -sS --max-time 5 "$OLLAMA/models")"; then
  echo "Ollama is not reachable at $OLLAMA — start it (ollama serve) or set OLLAMA=..."; exit 1
fi
if ! grep -q "\"id\":\"$MODEL\"" <<<"$models"; then
  echo "Model '$MODEL' is not installed. Installed tags:"
  grep -o '"id":"[^"]*"' <<<"$models" | cut -d'"' -f4 | sed 's/^/  /'
  echo "Re-run with MODEL=<one of the above>."; exit 1
fi
echo "ok — $MODEL is installed"

if lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then
  echo "Port $PORT is already in use:"; lsof -nP -iTCP:"$PORT" -sTCP:LISTEN; echo "Stop it or set PORT=..."; exit 1
fi

if [[ "${SKIP_PUBLISH:-}" != "1" ]]; then
  say "2/4 Publishing ($RID, Release) → $OUT   (first run takes a few minutes)"
  dotnet publish "$ROOT/src/Covenant.Host" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true --disable-build-servers -o "$OUT" -v:minimal -nologo
else
  say "2/4 Skipping publish (SKIP_PUBLISH=1)"
fi
[[ -x "$OUT/Covenant.Host" ]] || { echo "No binary at $OUT/Covenant.Host — the publish step failed above."; exit 1; }

ADMIN="$(openssl rand -hex 16)"; APPKEY="$(openssl rand -hex 16)"

say "3/4 Starting Covenant on http://localhost:$PORT (log: $LOG)"
ASPNETCORE_ENVIRONMENT=Production \
ASPNETCORE_URLS="http://localhost:$PORT" \
Covenant__Environment=local-demo \
Providers__Mode=local-only \
Local__Endpoint="$OLLAMA" \
Local__ModelId="$MODEL" \
Admin__Token="$ADMIN" \
Budget__GlobalCapUsd=5 \
Auth__Keys__0__Key="$APPKEY" \
Auth__Keys__0__Principal=demo-user \
Auth__Keys__0__Team=platform \
Audit__Path="$AUDIT" \
  "$OUT/Covenant.Host" >"$LOG" 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null || true' INT TERM

for _ in $(seq 1 40); do
  if ! kill -0 "$PID" 2>/dev/null; then
    echo "Covenant exited during startup. Its output:"; echo "------"; cat "$LOG"; echo "------"; exit 1
  fi
  code="$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$PORT/ui/" || true)"
  [[ "$code" == "200" ]] && break
  sleep 0.5
done
[[ "${code:-}" == "200" ]] || { echo "Covenant did not answer on /ui/ (last HTTP '$code'). Log:"; cat "$LOG"; kill $PID; exit 1; }

LAUNCH="http://localhost:$PORT/ui/connect#token=$ADMIN&appkey=$APPKEY"
say "4/4 Running — opening the console, already connected"
command -v open >/dev/null && open "$LAUNCH" || true
cat <<EOF
  One-click:    $LAUNCH
  Console:      http://localhost:$PORT/ui/
  Admin token:  $ADMIN        (Connect screen)
  App key:      $APPKEY        (Playground → Application key)
  Mode:         local-only → local:$MODEL   (no cloud credentials)
  Audit file:   $AUDIT
  Server log:   $LOG

  Ctrl+C stops it. Credentials are regenerated on every run.
EOF
wait $PID
