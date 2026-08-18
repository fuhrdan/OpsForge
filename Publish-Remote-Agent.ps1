$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK not found in PATH.' }
$out = Join-Path $PSScriptRoot 'dist\OpsForge.Agent'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null
Write-Host 'Publishing framework-dependent .NET 8 remote agent package...' -ForegroundColor Cyan
dotnet publish .\OpsForge.Agent\OpsForge.Agent.csproj -c Release --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
Copy-Item .\OpsForge.Agent\appsettings.remote-example.json (Join-Path $out 'agent.json') -Force
@'
@echo off
setlocal
cd /d "%~dp0"
set "OPSFORGE_AGENT_CONFIG=%~dp0agent.json"
dotnet OpsForge.Agent.dll
endlocal
'@ | Set-Content (Join-Path $out 'RUN-AGENT.cmd') -Encoding ASCII
@'
OpsForge Remote Agent v0.7.2

1. Edit agent.json. Give the agent a unique agentId and set serverUrl to your HTTPS OpsForge server.
2. On first run only, set OPSFORGE_AGENT_ENROLLMENT_TOKEN in the shell to the server enrollment token.
3. Run RUN-AGENT.cmd.
4. The agent creates/persists a client certificate, enrolls, stores its API key locally, and can authenticate with API key + mTLS when enabled on the server.
5. Remove the enrollment token from the shell after the first successful enrollment.

The remote machine needs the .NET 8 runtime. Credential files contain the API key and client-certificate password; do not commit or share them.
'@ | Set-Content (Join-Path $out 'REMOTE-AGENT-README.txt') -Encoding UTF8
Write-Host "Remote agent package created at: $out" -ForegroundColor Green
