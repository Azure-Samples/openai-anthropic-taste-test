#!/usr/bin/env bash

set -euo pipefail

warn() {
  printf 'Preflight: %s\n' "$*" >&2
}

CLAUDE_MODEL="${CLAUDE_MODEL_NAME:-claude-opus-5}"
CLAUDE_CAPACITY="${CLAUDE_MODEL_CAPACITY:-25}"
AOAI_MODEL="${AOAI_MODEL_NAME:-gpt-5.6-sol}"
AOAI_CAPACITY="${AOAI_MODEL_CAPACITY:-10}"

if [ -z "${CLAUDE_ORGANIZATION_NAME:-}" ]; then
  warn "CLAUDE_ORGANIZATION_NAME is not set. azd will prompt for it. Set it first for unattended runs: azd env set CLAUDE_ORGANIZATION_NAME 'Your organization'"
fi

if [ -z "${AZURE_LOCATION:-}" ]; then
  warn "AZURE_LOCATION is not set. azd will prompt for a supported region; proactive quota checks are skipped."
  exit 0
fi

if ! command -v az >/dev/null 2>&1; then
  warn "Azure CLI is not installed. Skipping proactive Marketplace and quota checks; ARM validation still runs during azd provision."
  exit 0
fi

SUBSCRIPTION_ID="$(az account show --query id -o tsv 2>/dev/null || true)"
if [ -z "$SUBSCRIPTION_ID" ]; then
  warn "Azure CLI is not signed in. Skipping proactive Marketplace and quota checks."
  exit 0
fi

echo "Preflight: subscription $SUBSCRIPTION_ID, location $AZURE_LOCATION"
echo "Preflight: Claude $CLAUDE_MODEL@$CLAUDE_CAPACITY; OpenAI $AOAI_MODEL@$AOAI_CAPACITY"
echo "Preflight: Claude Hosted on Azure requires a paid Marketplace-eligible subscription. modelProviderData will auto-accept the offer during deployment."

PERMISSIONS_URI="https://management.azure.com/subscriptions/$SUBSCRIPTION_ID/providers/Microsoft.Authorization/permissions?api-version=2015-07-01"
ROLE_ASSIGNMENT_GRANTS="$(az rest --method get --url "$PERMISSIONS_URI" --query "length(value[?contains(actions, '*') || contains(actions, 'Microsoft.Authorization/roleAssignments/write')])" -o tsv 2>/dev/null || true)"
if [ "$ROLE_ASSIGNMENT_GRANTS" = "0" ]; then
  warn "The signed-in principal does not appear to have roleAssignments/write. Owner or User Access Administrator is required for the Entra-only app and ACR pull assignments."
fi

check_quota() {
  local model="$1"
  local capacity="$2"
  local sku="AIServices.GlobalStandard.$model"
  local limit current available

  limit="$(az cognitiveservices usage list --location "$AZURE_LOCATION" --query "[?name.value=='$sku'].limit | [0]" -o tsv 2>/dev/null || true)"
  current="$(az cognitiveservices usage list --location "$AZURE_LOCATION" --query "[?name.value=='$sku'].currentValue | [0]" -o tsv 2>/dev/null || true)"

  if [ -z "$limit" ]; then
    warn "No quota row was returned for $sku. Verify model availability in the Foundry portal before a live demo."
    return
  fi

  current="${current%%.*}"
  current="${current:-0}"
  limit="${limit%%.*}"
  available=$((limit - current))

  if [ "$available" -lt "$capacity" ]; then
    warn "$model requests $capacity but only $available quota units appear available. ARM preflight will confirm and offer model/region adjustments."
  else
    echo "Preflight: $model has $available quota units available."
  fi
}

check_quota "$CLAUDE_MODEL" "$CLAUDE_CAPACITY"
check_quota "$AOAI_MODEL" "$AOAI_CAPACITY"
