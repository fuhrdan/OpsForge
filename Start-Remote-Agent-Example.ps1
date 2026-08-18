$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Write-Host 'OpsForge v0.6 remote agent example' -ForegroundColor Cyan
Write-Host '1. Configure OpsForge.Agent\appsettings.remote-example.json with your HTTPS server URL.'
Write-Host '2. On first run, set OPSFORGE_AGENT_ENROLLMENT_TOKEN to the server enrollment token.'
Write-Host '3. The agent creates a local client certificate, enrolls, stores its API key, and uses both when server mTLS is enabled.'
$env:OPSFORGE_AGENT_CONFIG = Join-Path $PSScriptRoot 'OpsForge.Agent\appsettings.remote-example.json'
dotnet run --project .\OpsForge.Agent\OpsForge.Agent.csproj
