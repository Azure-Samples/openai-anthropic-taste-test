#!/usr/bin/env bash

set -euo pipefail

eval "$(azd env get-values)"
dotnet run --project ./src/TasteTest --no-launch-profile
