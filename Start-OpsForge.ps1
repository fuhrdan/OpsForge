$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Write-Host ''
Write-Host '============================================================' -ForegroundColor DarkCyan
Write-Host ' OPSFORGE v0.7.2 - RELIABILITY COMMAND CENTER - FULL BUILD' -ForegroundColor Cyan
Write-Host '============================================================' -ForegroundColor DarkCyan
Write-Host ''
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Host 'ERROR: dotnet was not found in PATH.' -ForegroundColor Red; Write-Host 'Install the .NET 8 SDK, then run START-HERE.cmd again.' -ForegroundColor Yellow; exit 1 }
Write-Host "Found .NET SDK: $(dotnet --version)" -ForegroundColor Green
Write-Host 'Restoring packages and building OpsForge...' -ForegroundColor Cyan
dotnet build .\OpsForge.sln
if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed. Review the compiler output above.' -ForegroundColor Red; exit $LASTEXITCODE }

$env:OPSFORGE_ROOT = $PSScriptRoot
$securityDir = Join-Path $PSScriptRoot 'data\security'
New-Item -ItemType Directory -Force -Path $securityDir | Out-Null
function New-OpsForgeToken([string]$prefix) { $bytes = New-Object byte[] 32; $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }; return $prefix + [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_') }
$enrollmentPath = Join-Path $securityDir 'enrollment-token.txt'
if (-not (Test-Path $enrollmentPath)) { Set-Content -Path $enrollmentPath -Value (New-OpsForgeToken 'ofe_') -NoNewline }
$env:OPSFORGE_ENROLLMENT_TOKEN = (Get-Content $enrollmentPath -Raw).Trim()

$escapedRoot = $PSScriptRoot.Replace("'", "''")
$enrollEscaped = $env:OPSFORGE_ENROLLMENT_TOKEN.Replace("'", "''")
$common = "`$env:OPSFORGE_ROOT='$escapedRoot'; Set-Location '$escapedRoot';"
Write-Host 'Starting OpsForge Server...' -ForegroundColor Cyan
Start-Process powershell.exe -ArgumentList '-NoExit', '-Command', "$common `$env:OPSFORGE_ENROLLMENT_TOKEN='$enrollEscaped'; dotnet run --project .\OpsForge.Server\OpsForge.Server.csproj --no-build"
Start-Sleep -Seconds 3
Write-Host 'Starting Demo HTTP Service on port 5091...' -ForegroundColor Cyan
Start-Process powershell.exe -ArgumentList '-NoExit', '-Command', "$common dotnet run --project .\OpsForge.DemoService\OpsForge.DemoService.csproj --no-build"
Start-Sleep -Seconds 2
Write-Host 'Starting authenticated Windows Agent...' -ForegroundColor Cyan
$agentCredentials = (Join-Path $PSScriptRoot 'data\agents\local-agent.credentials.json').Replace("'", "''")
Start-Process powershell.exe -ArgumentList '-NoExit', '-Command', "$common `$env:OPSFORGE_AGENT_ENROLLMENT_TOKEN='$enrollEscaped'; `$env:OPSFORGE_AGENT_CREDENTIALS='$agentCredentials'; dotnet run --project .\OpsForge.Agent\OpsForge.Agent.csproj --no-build"
Start-Sleep -Seconds 4
Write-Host ''
Write-Host 'OpsForge is running.' -ForegroundColor Green
Write-Host 'Dashboard:             http://localhost:5080' -ForegroundColor White
Write-Host 'Demo health:           http://localhost:5091/health' -ForegroundColor White
Write-Host 'Persistent database:   .\data\opsforge.db' -ForegroundColor White
Write-Host 'Enrollment token:      .\data\security\enrollment-token.txt' -ForegroundColor White
$bootstrapPath = Join-Path $securityDir 'admin-bootstrap.txt'
if (Test-Path $bootstrapPath) {
    Write-Host 'Bootstrap admin file:  .\data\security\admin-bootstrap.txt' -ForegroundColor White
    Write-Host ''
    Write-Host 'FIRST RUN: sign in as admin using the temporary password in admin-bootstrap.txt, then change it immediately.' -ForegroundColor Yellow
} else {
    Write-Host 'Bootstrap admin:       already consumed / normal login required' -ForegroundColor White
    Write-Host ''
}
Write-Host 'Remote agents should use HTTPS. Set OPSFORGE_AGENT_MTLS=1 on an HTTPS Kestrel deployment to require bound client certificates on agent endpoints.' -ForegroundColor Yellow
Write-Host ''
Start-Process 'http://localhost:5080'
