#!/usr/bin/env bash
# Resolves the values main.bicep needs for one deployment and exports them for the
# readEnvironmentVariable() calls in infra/main.<region>.bicepparam.
#
# Inputs (environment): TAG, REGION, ENVIRONMENT, RESOURCE_GROUP, ACR_NAME, NAME_PREFIX
# Outputs (GITHUB_OUTPUT): resource_group, web_app, sync_app, candidate_revision, new_sync_image
# Exports (GITHUB_ENV):    MLCP_WEB_IMAGE, MLCP_SYNC_IMAGE, MLCP_MIGRATE_IMAGE,
#                          MLCP_WEB_REVISION_SUFFIX, MLCP_WEB_STABLE_REVISION
set -euo pipefail

: "${TAG:?}" "${REGION:?}" "${ENVIRONMENT:?}" "${RESOURCE_GROUP:?}" "${ACR_NAME:?}"
prefix="${NAME_PREFIX:-mlcp}"

az config set extension.use_dynamic_install=yes_without_prompt -o none
az extension add --name containerapp --upgrade --only-show-errors

registry="${ACR_NAME}.azurecr.io"
web_app="${prefix}-${ENVIRONMENT}-${REGION}-web"
sync_app="${prefix}-${ENVIRONMENT}-${REGION}-sync"

# Unique per run and attempt, so a re-run never collides with a revision created by a failed
# attempt. Lower-case letters, digits and hyphens only (Container Apps revision suffix rules).
suffix="r${GITHUB_RUN_NUMBER}-${GITHUB_RUN_ATTEMPT}-${TAG:0:7}"
suffix="$(tr '[:upper:]' '[:lower:]' <<<"$suffix")"
candidate="${web_app}--${suffix}"

new_sync_image="${registry}/mlcp-sync:${TAG}"
stable=""
current_sync_image="$new_sync_image"

if az containerapp show -g "$RESOURCE_GROUP" -n "$web_app" -o none 2>/dev/null; then
  # The revision currently serving 100%. A traffic entry with latestRevision=true has no name,
  # so fall back to the latest ready revision.
  stable=$(az containerapp ingress traffic show -g "$RESOURCE_GROUP" -n "$web_app" \
    --query "[?weight==\`100\`].revisionName | [0]" -o tsv 2>/dev/null || true)
  if [ -z "$stable" ] || [ "$stable" = "None" ]; then
    stable=$(az containerapp show -g "$RESOURCE_GROUP" -n "$web_app" \
      --query properties.latestReadyRevisionName -o tsv)
  fi
fi

if az containerapp show -g "$RESOURCE_GROUP" -n "$sync_app" -o none 2>/dev/null; then
  # Keep the running worker image during the infrastructure deployment, so the worker never runs
  # new code against the old schema. The workflow updates it after the migrate job.
  current_sync_image=$(az containerapp show -g "$RESOURCE_GROUP" -n "$sync_app" \
    --query "properties.template.containers[0].image" -o tsv)
fi

echo "Stable web revision: ${stable:-<none: first deployment>}"
echo "Candidate web revision: $candidate"
echo "Worker image during infrastructure deployment: $current_sync_image"

{
  echo "resource_group=$RESOURCE_GROUP"
  echo "web_app=$web_app"
  echo "sync_app=$sync_app"
  echo "candidate_revision=$candidate"
  echo "new_sync_image=$new_sync_image"
} >> "$GITHUB_OUTPUT"

{
  echo "MLCP_WEB_IMAGE=${registry}/mlcp-web:${TAG}"
  echo "MLCP_SYNC_IMAGE=$current_sync_image"
  echo "MLCP_MIGRATE_IMAGE=${registry}/mlcp-migrate:${TAG}"
  echo "MLCP_WEB_REVISION_SUFFIX=$suffix"
  echo "MLCP_WEB_STABLE_REVISION=$stable"
} >> "$GITHUB_ENV"
