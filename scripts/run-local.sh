#!/usr/bin/env bash

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
values="$(azd env get-values)"
telemetry_console_override="${TASTE_TEST_TELEMETRY_CONSOLE+x}"
otlp_endpoint_override="${OTEL_EXPORTER_OTLP_ENDPOINT+x}"
while IFS='=' read -r name value; do
  case "$name" in
    AZURE_FOUNDRY_ENDPOINT|AZURE_FOUNDRY_RESOURCE_NAME|AOAI_DEPLOYMENT_NAME|CLAUDE_DEPLOYMENT_NAME|AZURE_TENANT_ID|TASTE_TEST_MAX_OUTPUT_TOKENS|TASTE_TEST_TELEMETRY_CONSOLE|OTEL_EXPORTER_OTLP_ENDPOINT)
      if [[ "$name" == TASTE_TEST_TELEMETRY_CONSOLE && "$telemetry_console_override" == x ]] ||
         [[ "$name" == OTEL_EXPORTER_OTLP_ENDPOINT && "$otlp_endpoint_override" == x ]]; then
        continue
      fi
      value="${value%$'\r'}"
      value="${value#\"}"
      value="${value%\"}"
      export "$name=$value"
      ;;
  esac
done <<< "$values"
export TASTE_TEST_USE_SAMPLE_RESPONSES=false
exec dotnet run --project ./src/TasteTest --launch-profile http
