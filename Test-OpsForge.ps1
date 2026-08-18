$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Write-Host 'OpsForge v0.7.2 full-build smoke test' -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Host 'dotnet SDK not found; build portion cannot run.' -ForegroundColor Yellow; exit 2 }

dotnet build .\OpsForge.sln
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

$testRoot = Join-Path $env:TEMP ('opsforge-v070-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$env:OPSFORGE_ROOT = $testRoot
$env:OPSFORGE_LISTEN_URL = 'http://127.0.0.1:5180'
$env:OPSFORGE_ENROLLMENT_TOKEN = 'ofe_test_enrollment_token_1234567890'
$server = Start-Process dotnet -ArgumentList 'run','--project','.\OpsForge.Server\OpsForge.Server.csproj','--no-build' -PassThru -WindowStyle Hidden

try {
  $base='http://127.0.0.1:5180'
  $ready=$false
  for($i=0;$i -lt 30;$i++){
    try { $health=Invoke-RestMethod "$base/api/health"; $ready=$true; break }
    catch { Start-Sleep -Milliseconds 500 }
  }
  if(-not $ready){ throw 'OpsForge.Server did not become ready.' }
  if($health.version -ne '0.7.2' -or $health.schemaVersion -ne '7.0'){ throw "Unexpected version/schema: $($health.version) / $($health.schemaVersion)" }

  # Bootstrap administrator and remove the temporary credential file.
  $bootstrapPath=Join-Path $testRoot 'data\security\admin-bootstrap.txt'
  $bootstrap = Get-Content $bootstrapPath
  $username = (($bootstrap | Where-Object { $_ -like 'Username:*' }) -split ':',2)[1].Trim()
  $password = (($bootstrap | Where-Object { $_ -like 'Temporary password:*' }) -split ':',2)[1].Trim()
  $loginBody=@{username=$username;password=$password}|ConvertTo-Json
  $login=Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' -Body $loginBody -SessionVariable adminSession
  if(-not $login.csrfToken){ throw 'Admin login did not return CSRF token.' }
  $adminHeaders=@{'X-OpsForge-CSRF'=$login.csrfToken}
  $adminPassword='OpsForge!SmokeAdmin2026'
  $change=@{currentPassword=$password;newPassword=$adminPassword}|ConvertTo-Json
  Invoke-RestMethod -Method Post -Uri "$base/api/auth/change-password" -Headers $adminHeaders -WebSession $adminSession -ContentType 'application/json' -Body $change | Out-Null
  if(Test-Path $bootstrapPath){ throw 'Bootstrap administrator file was not removed after password change.' }

  # Create Viewer and Operator users.
  $viewerCreated=Invoke-RestMethod -Method Post -Uri "$base/api/auth/users" -Headers $adminHeaders -WebSession $adminSession -ContentType 'application/json' -Body (@{username='viewer01';displayName='Smoke Viewer';role='viewer'}|ConvertTo-Json)
  $operatorCreated=Invoke-RestMethod -Method Post -Uri "$base/api/auth/users" -Headers $adminHeaders -WebSession $adminSession -ContentType 'application/json' -Body (@{username='operator01';displayName='Smoke Operator';role='operator'}|ConvertTo-Json)

  $viewerLogin=Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' -Body (@{username='viewer01';password=$viewerCreated.temporaryPassword}|ConvertTo-Json) -SessionVariable viewerSession
  $viewerHeaders=@{'X-OpsForge-CSRF'=$viewerLogin.csrfToken}
  Invoke-RestMethod -Method Post -Uri "$base/api/auth/change-password" -Headers $viewerHeaders -WebSession $viewerSession -ContentType 'application/json' -Body (@{currentPassword=$viewerCreated.temporaryPassword;newPassword='OpsForge!ViewerSmoke2026'}|ConvertTo-Json) | Out-Null

  $operatorLogin=Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' -Body (@{username='operator01';password=$operatorCreated.temporaryPassword}|ConvertTo-Json) -SessionVariable operatorSession
  $operatorHeaders=@{'X-OpsForge-CSRF'=$operatorLogin.csrfToken}
  Invoke-RestMethod -Method Post -Uri "$base/api/auth/change-password" -Headers $operatorHeaders -WebSession $operatorSession -ContentType 'application/json' -Body (@{currentPassword=$operatorCreated.temporaryPassword;newPassword='OpsForge!OperatorSmoke2026'}|ConvertTo-Json) | Out-Null

  $viewerDenied=$false
  try { Invoke-RestMethod -Uri "$base/api/auth/users" -WebSession $viewerSession | Out-Null }
  catch { if($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 403){ $viewerDenied=$true } }
  if(-not $viewerDenied){ throw 'Viewer unexpectedly accessed administrator user management.' }

  # Enroll an agent and submit healthy telemetry.
  $enrollHeaders=@{'X-OpsForge-Enrollment-Token'=$env:OPSFORGE_ENROLLMENT_TOKEN}
  $enrollBody=@{agentId='smoke-01';displayName='Smoke Node';site='Test';environmentName='lab';clientCertificateThumbprint='ABCDEF123456'}|ConvertTo-Json
  $enrolled=Invoke-RestMethod -Method Post -Uri "$base/api/enrollment/agents" -Headers $enrollHeaders -ContentType 'application/json' -Body $enrollBody
  if(-not $enrolled.apiKey){ throw 'Agent enrollment did not return an API key.' }
  $agentHeaders=@{'X-OpsForge-Agent-Key'=$enrolled.apiKey}

  $healthyHeartbeat=@{
    agentId='smoke-01';machineName='SMOKE';displayName='Smoke Node';site='Test';environmentName='lab';operatingSystem='Windows';agentVersion='0.7.2';timestampUtc=(Get-Date).ToUniversalTime().ToString('o');
    cpuPercent=5;memoryUsedPercent=25;uptimeSeconds=100;drives=@();networkAdapters=@();
    monitoredProcesses=@(@{name='OpsForge.DemoService';running=$true;processId=4321});monitoredServices=@();
    probes=@(
      @{id='demo-tcp';type='TCP';target='localhost:5091';success=$true;latencyMs=3;detail='ok';checkedUtc=(Get-Date).ToUniversalTime().ToString('o')},
      @{id='demo-http';type='HTTP';target='http://localhost:5091/health';success=$true;latencyMs=5;detail='ok';checkedUtc=(Get-Date).ToUniversalTime().ToString('o')}
    )
  }|ConvertTo-Json -Depth 10
  Invoke-RestMethod -Method Post -Uri "$base/api/agents/heartbeat" -Headers $agentHeaders -ContentType 'application/json' -Body $healthyHeartbeat | Out-Null

  # Submit a correlated outage: process + TCP + HTTP all fail.
  $failedHeartbeat=@{
    agentId='smoke-01';machineName='SMOKE';displayName='Smoke Node';site='Test';environmentName='lab';operatingSystem='Windows';agentVersion='0.7.2';timestampUtc=(Get-Date).ToUniversalTime().ToString('o');
    cpuPercent=8;memoryUsedPercent=28;uptimeSeconds=110;drives=@();networkAdapters=@();
    monitoredProcesses=@(@{name='OpsForge.DemoService';running=$false;processId=$null});monitoredServices=@();
    probes=@(
      @{id='demo-tcp';type='TCP';target='localhost:5091';success=$false;latencyMs=10;detail='connection refused';checkedUtc=(Get-Date).ToUniversalTime().ToString('o')},
      @{id='demo-http';type='HTTP';target='http://localhost:5091/health';success=$false;latencyMs=12;detail='unavailable';checkedUtc=(Get-Date).ToUniversalTime().ToString('o')}
    )
  }|ConvertTo-Json -Depth 10
  Invoke-RestMethod -Method Post -Uri "$base/api/agents/heartbeat" -Headers $agentHeaders -ContentType 'application/json' -Body $failedHeartbeat | Out-Null

  $primaries=@(Invoke-RestMethod -Uri "$base/api/primary-incidents" -WebSession $operatorSession)
  $primary=$primaries | Where-Object { $_.active -eq $true -and $_.agentId -eq 'smoke-01' } | Select-Object -First 1
  if(-not $primary){ throw 'Correlated primary incident was not created.' }

  # Incident workflow: acknowledge and take ownership.
  Invoke-RestMethod -Method Post -Uri "$base/api/primary-incidents/$($primary.id)/acknowledge" -Headers $operatorHeaders -WebSession $operatorSession -ContentType 'application/json' -Body (@{note='Smoke-test acknowledgement'}|ConvertTo-Json) | Out-Null
  Invoke-RestMethod -Method Post -Uri "$base/api/primary-incidents/$($primary.id)/assign" -Headers $operatorHeaders -WebSession $operatorSession -ContentType 'application/json' -Body (@{ownerUsername='operator01';note='Taking smoke-test ownership'}|ConvertTo-Json) | Out-Null
  $updated=@(Invoke-RestMethod -Uri "$base/api/primary-incidents" -WebSession $operatorSession) | Where-Object id -eq $primary.id
  if(-not $updated.acknowledged -or $updated.ownerUsername -ne 'operator01'){ throw 'Incident acknowledgement/ownership did not persist.' }

  # Maintenance window suppresses the active incident and is excluded from reliability accounting.
  $start=(Get-Date).ToUniversalTime().AddMinutes(-1)
  $end=$start.AddMinutes(20)
  $maintenanceBody=@{name='Smoke maintenance';agentId='smoke-01';reason='v0.7 test';startUtc=$start.ToString('o');endUtc=$end.ToString('o')}|ConvertTo-Json
  $maintenance=Invoke-RestMethod -Method Post -Uri "$base/api/maintenance" -Headers $operatorHeaders -WebSession $operatorSession -ContentType 'application/json' -Body $maintenanceBody
  if(-not $maintenance.activeNow){ throw 'Created maintenance window was not active.' }
  $muted=@(Invoke-RestMethod -Uri "$base/api/primary-incidents" -WebSession $operatorSession) | Where-Object id -eq $primary.id
  if(-not $muted.maintenanceSuppressed){ throw 'Active primary incident was not maintenance-muted.' }

  $viewerMutationDenied=$false
  try { Invoke-RestMethod -Method Post -Uri "$base/api/maintenance" -Headers $viewerHeaders -WebSession $viewerSession -ContentType 'application/json' -Body $maintenanceBody | Out-Null }
  catch { if($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 403){ $viewerMutationDenied=$true } }
  if(-not $viewerMutationDenied){ throw 'Viewer unexpectedly created a maintenance window.' }

  # Historical analytics and node history are available from the fresh install.
  $reliability=Invoke-RestMethod -Uri "$base/api/reliability?hours=24&slaTarget=99.9" -WebSession $viewerSession
  if($null -eq $reliability.fleetAvailabilityPercent){ throw 'Reliability dashboard missing fleet availability.' }
  if(-not (@($reliability.agents) | Where-Object agentId -eq 'smoke-01')){ throw 'Reliability dashboard missing smoke agent.' }
  $history=Invoke-RestMethod -Uri "$base/api/agents/smoke-01/history?hours=24" -WebSession $viewerSession
  if(@($history.points).Count -lt 1){ throw 'Historical telemetry endpoint did not return the initial sample.' }

  Invoke-RestMethod -Method Post -Uri "$base/api/maintenance/$($maintenance.maintenanceId)/cancel" -Headers $operatorHeaders -WebSession $operatorSession | Out-Null
  $maintenanceRows=@(Invoke-RestMethod -Uri "$base/api/maintenance" -WebSession $viewerSession)
  if(-not ($maintenanceRows | Where-Object { $_.maintenanceId -eq $maintenance.maintenanceId -and $_.cancelled -eq $true })){ throw 'Maintenance cancellation did not persist.' }

  $inventory=Invoke-RestMethod -Uri "$base/api/agent-inventory" -WebSession $adminSession
  if(-not ($inventory | Where-Object agentId -eq 'smoke-01')){ throw 'Authenticated inventory missing agent.' }
  $audit=Invoke-RestMethod -Uri "$base/api/audit" -WebSession $adminSession
  foreach($expected in @('user.create','incident.acknowledge','incident.assign','maintenance.create','maintenance.cancel')){
    if(-not ($audit | Where-Object action -eq $expected)){ throw "Audit log missing $expected event." }
  }

  Write-Host 'PASS: build, schema 7, bootstrap/RBAC, agent auth, correlation, acknowledgement, ownership, maintenance suppression, SLA analytics, telemetry history, and audit.' -ForegroundColor Green
}
finally {
  if($server -and -not $server.HasExited){ Stop-Process -Id $server.Id -Force }
  Remove-Item -Recurse -Force $testRoot -ErrorAction SilentlyContinue
}
