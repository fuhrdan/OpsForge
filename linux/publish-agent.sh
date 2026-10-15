#!/usr/bin/env bash
set -euo pipefail

RID="${1:-linux-x64}"
OUTPUT="${2:-publish/${RID}}"

dotnet restore OpsForge.sln
dotnet build OpsForge.sln -c Release --no-restore
dotnet publish OpsForge.Agent/OpsForge.Agent.csproj \
  -c Release \
  -r "${RID}" \
  --self-contained false \
  -o "${OUTPUT}"

cp OpsForge.Agent/appsettings.linux-example.json "${OUTPUT}/appsettings.linux-example.json"

echo "Published OpsForge Agent for ${RID} to ${OUTPUT}"
