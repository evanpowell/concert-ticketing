#!/usr/bin/env bash
# Ampere A1 capacity in OCI Always Free regions is frequently exhausted. Oracle
# does not queue requests, so the only remedy is to retry until a slot frees up.
# This rotates every availability domain and both viable shapes, preferring the
# larger one, and stops at the first success.
#
# Usage:  ./retry-capacity.sh                    (5 min between cycles, 24h cap)
#         INTERVAL=120 MAX_HOURS=8 ./retry-capacity.sh
#
# Safe to run detached overnight: it stops at the first success, on any
# non-capacity error, or when MAX_HOURS elapses.
set -uo pipefail
cd "$(dirname "$0")"

ADS=(
  "NahR:US-CHICAGO-1-AD-1"
  "NahR:US-CHICAGO-1-AD-2"
  "NahR:US-CHICAGO-1-AD-3"
)
# "ocpus memory_gbs", best first.
SHAPES=("2 12" "1 6")
INTERVAL="${INTERVAL:-300}"
MAX_HOURS="${MAX_HOURS:-24}"
DEADLINE=$(( $(date +%s) + MAX_HOURS * 3600 ))
LOG=/tmp/tf-retry-last.log

attempt=0
cycle=0
while true; do
  cycle=$((cycle + 1))
  for shape in "${SHAPES[@]}"; do
    set -- $shape
    ocpus=$1
    mem=$2
    for ad in "${ADS[@]}"; do
      attempt=$((attempt + 1))
      printf '[%s] #%d %s %sOCPU/%sGB ... ' \
        "$(date '+%m-%d %H:%M:%S')" "$attempt" "${ad##*-CHICAGO-1-}" "$ocpus" "$mem"

      if terraform apply -auto-approve -no-color \
           -var "availability_domain=$ad" \
           -var "instance_ocpus=$ocpus" \
           -var "instance_memory_gbs=$mem" > "$LOG" 2>&1; then
        ip=$(terraform output -raw public_ip 2>/dev/null)
        echo "SUCCESS"
        echo
        echo "Instance is up: $ip  ($ad, ${ocpus} OCPU / ${mem} GB)"
        echo "Took $attempt attempts over $cycle cycles."
        exit 0
      fi

      if grep -q 'Out of host capacity' "$LOG"; then
        echo "no capacity"
      else
        # Anything that is not a capacity error is a real problem; stop rather
        # than hammer the API with a request that can never succeed.
        echo "FAILED (not a capacity error)"
        echo
        grep -E '^Error:|Suggestion:' "$LOG" | head -10
        exit 1
      fi
    done
  done
  if [ "$(date +%s)" -ge "$DEADLINE" ]; then
    echo "  reached the ${MAX_HOURS}h limit after $attempt attempts; giving up."
    exit 2
  fi
  echo "  cycle $cycle exhausted; sleeping ${INTERVAL}s"
  sleep "$INTERVAL"
done
