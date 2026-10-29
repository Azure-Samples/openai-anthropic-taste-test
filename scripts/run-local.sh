#!/usr/bin/env bash

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
values="$(azd env get-values)"
telemetry_console_override="${TASTE_TEST_TELEMETRY_CONSOLE+x}"
otlp_endpoint_override="${OTEL_EXPORTER_OTLP_ENDPOINT+x}"
legacy_endpoint=''
set_config_default() {
  if ! declare -p "$1" >/dev/null 2>&1; then
    export "$1=$2"
  fi
}
while IFS='=' read -r name value; do
  value="${value%$'\r'}"
  value="${value#\"}"
  value="${value%\"}"
  case "$name" in
    TasteTest__*)
      set_config_default "$name" "$value"
      ;;
    TASTE_TEST_MAX_OUTPUT_TOKENS|TASTE_TEST_TELEMETRY_CONSOLE|OTEL_EXPORTER_OTLP_ENDPOINT)
      if [[ "$name" == TASTE_TEST_TELEMETRY_CONSOLE && "$telemetry_console_override" == x ]] ||
         [[ "$name" == OTEL_EXPORTER_OTLP_ENDPOINT && "$otlp_endpoint_override" == x ]]; then
        continue
      fi
      export "$name=$value"
      ;;
  esac
done <<< "$values"
# Apply output aliases only after explicit configuration, regardless of azd's output ordering.
while IFS='=' read -r name value; do
  value="${value%$'\r'}"
  value="${value#\"}"
  value="${value%\"}"
  case "$name" in
    GPT_ENDPOINT) set_config_default TasteTest__Gpt__Options__Endpoint "$value" ;;
    CLAUDE_ENDPOINT) set_config_default TasteTest__Claude__Options__Endpoint "$value" ;;
    AOAI_DEPLOYMENT_NAME) set_config_default TasteTest__Gpt__Model "$value" ;;
    CLAUDE_DEPLOYMENT_NAME) set_config_default TasteTest__Claude__Model "$value" ;;
    AZURE_TENANT_ID) set_config_default TasteTest__Credential__TenantId "$value" ;;
    AZURE_FOUNDRY_ENDPOINT) legacy_endpoint="$value" ;;
  esac
done <<< "$values"
if [[ -n "$legacy_endpoint" ]]; then
  set_config_default TasteTest__Gpt__Options__Endpoint "${legacy_endpoint%/}/openai/v1/"
  set_config_default TasteTest__Claude__Options__Endpoint "${legacy_endpoint%/}/anthropic/"
fi
export TASTE_TEST_USE_SAMPLE_RESPONSES=false
exec dotnet run --project ./src/TasteTest --launch-profile http
