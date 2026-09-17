#!/usr/bin/env bash
# Starts a manual Container Apps job and waits for its execution to finish.
#
#   run-job.sh <resource group> <job name> [timeout seconds, default 1800]
#
# Exits 0 only when the execution reports Succeeded. On failure it prints the job's recent
# console logs, when Log Analytics has them.
set -euo pipefail

rg="${1:?resource group}"
job="${2:?job name}"
timeout="${3:-1800}"

az config set extension.use_dynamic_install=yes_without_prompt -o none

execution=$(az containerapp job start -g "$rg" -n "$job" --query name -o tsv)
echo "Started $job execution $execution"

deadline=$(( $(date +%s) + timeout ))
while :; do
  status=$(az containerapp job execution show -g "$rg" -n "$job" \
    --job-execution-name "$execution" --query properties.status -o tsv)
  echo "$(date -u +%H:%M:%S) $execution: $status"

  case "$status" in
    Succeeded)
      exit 0
      ;;
    Failed|Stopped|Degraded)
      echo "::error::Job $job execution $execution ended with status $status."
      az containerapp job logs show -g "$rg" -n "$job" --execution "$execution" \
        --container "$(az containerapp job show -g "$rg" -n "$job" \
          --query 'properties.template.containers[0].name' -o tsv)" 2>/dev/null || true
      exit 1
      ;;
  esac

  if [ "$(date +%s)" -ge "$deadline" ]; then
    echo "::error::Timed out after ${timeout}s waiting for $job execution $execution."
    exit 1
  fi
  sleep 15
done
