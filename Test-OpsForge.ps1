$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Write-Host 'OpsForge v1.0.0 full-build smoke test' -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Host 'dotnet SDK not found; build portion cannot run.' -ForegroundColor Yellow; exit 2 }

dotnet build .\OpsForge.sln
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

$testRoot = Join-Path $env:TEMP ('opsforge-v100-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$env:OPSFORGE_ROOT = $testRoot
$env:OPSFORGE_LISTEN_URL = 'http://127.0.0.1:5180'
$env:OPSFORGE_ENROLLMENT_TOKEN = 'ofe_test_enrollment_token_1234567890'
$server = Start-Process dotnet -ArgumentList '.\OpsForge.Server\bin\Debug\net8.0\OpsForge.Server.dll' -PassThru -WindowStyle Hidden

try {
  $base='http://127.0.0.1:5180'
  $ready=$false
  for($i=0;$i -lt 30;$i++){
    try { $health=Invoke-RestMethod "$base/api/health"; $ready=$true; break }
    catch { Start-Sleep -Milliseconds 500 }
  }
  if(-not $ready){ throw 'OpsForge.Server did not become ready.' }
  if($health.version -ne '1.0.0' -or $health.schemaVersion -ne '9.0'){ throw "Unexpected version/schema: $($health.version) / $($health.schemaVersion)" }

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
  $traceId='1234567890abcdef1234567890abcdef'
  $tracedAgentHeaders=@{'X-OpsForge-Agent-Key'=$enrolled.apiKey;'traceparent'="00-$traceId-1234567890abcdef-01"}
  Invoke-RestMethod -Method Post -Uri "$base/api/agents/heartbeat" -Headers $tracedAgentHeaders -ContentType 'application/json' -Body $failedHeartbeat | Out-Null

  $primaries=@(Invoke-RestMethod -Uri "$base/api/primary-incidents" -WebSession $operatorSession)
  $primary=$primaries | Where-Object { $_.active -eq $true -and $_.agentId -eq 'smoke-01' } | Select-Object -First 1
  if(-not $primary){ throw 'Correlated primary incident was not created.' }
  if($primary.correlationKey -ne 'smoke-01:primary:demo-application' -or @($primary.signals).Count -ne 3){ throw 'Unexpected correlation key or evidence.' }
  if($primary.traceId -ne $traceId){ throw "Incoming W3C trace context did not reach the persisted incident: $($primary.traceId)" }
  $primaryReport=Invoke-RestMethod -Uri "$base/api/primary-incidents/$($primary.id)/report" -WebSession $operatorSession
  if($primaryReport -notlike "*Trace ID: *$traceId*"){ throw 'Primary report did not include the incident trace ID.' }
  $reassessed=$failedHeartbeat | ConvertFrom-Json
  $reassessed.timestampUtc=[DateTime]::UtcNow.AddSeconds(1).ToString('o')
  foreach($probe in $reassessed.probes){ $probe.checkedUtc=$reassessed.timestampUtc }
  $reassessmentHeaders=@{'X-OpsForge-Agent-Key'=$enrolled.apiKey;'traceparent'='00-abcdef1234567890abcdef1234567890-1234567890abcdef-01'}
  Invoke-RestMethod -Method Post -Uri "$base/api/agents/heartbeat" -Headers $reassessmentHeaders -ContentType 'application/json' -Body ($reassessed|ConvertTo-Json -Depth 10) | Out-Null
  $reassessedPrimary=@(Invoke-RestMethod -Uri "$base/api/primary-incidents" -WebSession $operatorSession) | Where-Object { $_.id -eq $primary.id } | Select-Object -First 1
  if($reassessedPrimary.traceId -ne $traceId){ throw 'Reassessment replaced the opening trace ID.' }

  # Incident workflow: acknowledge and take ownership.
  Invoke-RestMethod -Method Post -Uri "$base/api/primary-incidents/$($primary.id)/acknowledge" -Headers $operatorHeaders -WebSession $operatorSession -ContentType 'application/json' -Body (@{note='Smoke-test acknowledgement'}|ConvertTo-Json) | Out-Null
  Invoke-RestMethod -Method Post -Uri "$base/api/primary-incidents/$($primary.id)/assign" -Headers $operatorHeaders -WebSession $operatorSession -ContentType 'application/json' -Body (@{ownerUsername='operator01';note='Taking smoke-test ownership'}|ConvertTo-Json) | Out-Null
  $updatedRows=Invoke-RestMethod -Uri "$base/api/primary-incidents" -WebSession $operatorSession
  $updated=$updatedRows | Where-Object { $_.id -eq $primary.id } | Select-Object -First 1
  if(-not $updated){ throw "Primary incident $($primary.id) was missing during acknowledgement/ownership readback." }
  if(-not $updated.acknowledged -or $updated.ownerUsername -ne 'operator01'){
    throw "Incident acknowledgement/ownership did not persist. acknowledged=$($updated.acknowledged) owner=$($updated.ownerUsername)"
  }

  # Maintenance window suppresses the active incident and is excluded from reliability accounting.
  $start=(Get-Date).ToUniversalTime().AddMinutes(-1)
  $end=$start.AddMinutes(20)
  $maintenanceBody=@{name='Smoke maintenance';agentId='smoke-01';reason='v0.7 test';startUtc=$start.ToString('o');endUtc=$end.ToString('o')}|ConvertTo-Json
  $maintenance=Invoke-RestMethod -Method Post -Uri "$base/api/maintenance" -Headers $operatorHeaders -WebSession $operatorSession -ContentType 'application/json' -Body $maintenanceBody
  if(-not $maintenance.activeNow){ throw 'Created maintenance window was not active.' }
  $mutedRows=Invoke-RestMethod -Uri "$base/api/primary-incidents" -WebSession $operatorSession
  $muted=$mutedRows | Where-Object { $_.id -eq $primary.id } | Select-Object -First 1
  if(-not $muted){ throw "Primary incident $($primary.id) was missing during maintenance readback." }
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

  # One source and two observers report the same service; an unrelated warning stays separate.
  $fleetNodes=@()
  for($i=1;$i -le 4;$i++){
    $id='fleet-smoke-{0:d2}' -f $i
    $created=Invoke-RestMethod -Method Post -Uri "$base/api/enrollment/agents" -Headers $enrollHeaders -ContentType 'application/json' -Body (@{agentId=$id;displayName=$id;site='Test';environmentName='lab'}|ConvertTo-Json)
    $fleetNodes+=@{Id=$id;Key=$created.apiKey;Index=$i}
  }
  function Send-FleetHeartbeat($node,[bool]$failed){
    $stamp=[DateTime]::UtcNow.ToString('o')
    $affected=$failed -and $node.Index -le 3
    $body=@{
      agentId=$node.Id;machineName=$node.Id;displayName=$node.Id;site='Test';environmentName='lab'
      operatingSystem='Windows';agentVersion='1.0.0';timestampUtc=$stamp
      fleetServiceId=$(if($node.Index -eq 4){'smoke-unrelated'}else{'smoke-fleet-01'})
      fleetRuleId='demo-application';fleetRole=$(if($node.Index -eq 1){'source'}else{'observer'})
      cpuPercent=5;memoryUsedPercent=$(if($failed -and $node.Index -eq 4){95}else{20});uptimeSeconds=200
      drives=@();networkAdapters=@();monitoredProcesses=@();monitoredServices=@()
      probes=@(
        @{id='demo-tcp';type='TCP';target='service:5091';success=(-not $affected);checkedUtc=$stamp},
        @{id='demo-http';type='HTTP';target='http://service/health';success=(-not $affected);checkedUtc=$stamp}
      )
    }
    if($node.Index -eq 1){$body.monitoredProcesses=@(@{name='OpsForge.DemoService';running=(-not $affected);processId=$(if($affected){$null}else{4321})})}
    $trace=('000000000000000000000000000000'+$node.Index.ToString('x2'))
    $headers=@{'X-OpsForge-Agent-Key'=$node.Key;'traceparent'="00-$trace-1234567890abcdef-01"}
    Invoke-RestMethod -Method Post -Uri "$base/api/agents/heartbeat" -Headers $headers -ContentType 'application/json' -Body ($body|ConvertTo-Json -Depth 10) | Out-Null
  }
  foreach($node in $fleetNodes){Send-FleetHeartbeat $node $false}
  foreach($node in $fleetNodes){Send-FleetHeartbeat $node $true}
  $fleetRows=@(Invoke-RestMethod -Uri "$base/api/primary-incidents" -WebSession $operatorSession)
  $fleet=$fleetRows|Where-Object {$_.active -and $_.fleetServiceId -eq 'smoke-fleet-01'}|Select-Object -First 1
  if(-not $fleet -or @($fleet.fleetEvidence).Count -ne 3){throw 'One fleet incident did not collect all three affected agents.'}
  if(@($fleetRows|Where-Object {$_.active -and $_.agentId -like 'fleet-smoke-*'}).Count -ne 1){throw 'Fleet outage opened duplicate primary incidents.'}
  if(@($fleet.fleetEvidence|Select-Object -ExpandProperty traceId -Unique).Count -ne 3){throw 'Fleet evidence lost distinct agent traces.'}
  $fleetReport=Invoke-RestMethod -Uri "$base/api/primary-incidents/$($fleet.id)/report" -WebSession $operatorSession
  if($fleetReport -notlike '*Affected agents and traces*'){throw 'Fleet report omitted per-agent evidence.'}
  $independentRows=@(Invoke-RestMethod -Uri "$base/api/incidents" -WebSession $operatorSession)
  if(-not ($independentRows|Where-Object {$_.agentId -eq 'fleet-smoke-04' -and $_.active -and $_.category -eq 'performance'})){throw 'Independent memory warning was not visible.'}

  # Persisted snapshots and incident identity survive restart; silence from one observer cannot prove recovery.
  Stop-Process -Id $server.Id -Force
  $server=Start-Process dotnet -ArgumentList '.\OpsForge.Server\bin\Debug\net8.0\OpsForge.Server.dll' -PassThru -WindowStyle Hidden
  $ready=$false
  for($i=0;$i -lt 30;$i++){
    try{$health=Invoke-RestMethod "$base/api/health";$ready=$true;break}
    catch{Start-Sleep -Milliseconds 500}
  }
  if(-not $ready -or $health.schemaVersion -ne '9.0'){throw 'Fleet server did not restart on schema 9.'}
  $afterRestart=@(Invoke-RestMethod -Uri "$base/api/primary-incidents" -WebSession $operatorSession)|Where-Object {$_.id -eq $fleet.id}|Select-Object -First 1
  if(-not $afterRestart.active -or @($afterRestart.fleetEvidence).Count -ne 3){throw 'Fleet incident or evidence was lost on restart.'}
  Send-FleetHeartbeat $fleetNodes[0] $false
  Send-FleetHeartbeat $fleetNodes[1] $false
  $pending=@(Invoke-RestMethod -Uri "$base/api/primary-incidents" -WebSession $operatorSession)|Where-Object {$_.id -eq $fleet.id}|Select-Object -First 1
  if(-not $pending.active -or @($pending.fleetEvidence|Where-Object {$_.active}).Count -ne 1){throw 'Missing observer was incorrectly treated as recovered.'}
  Send-FleetHeartbeat $fleetNodes[2] $false
  $closed=@(Invoke-RestMethod -Uri "$base/api/primary-incidents" -WebSession $operatorSession)|Where-Object {$_.id -eq $fleet.id}|Select-Object -First 1
  if($closed.active -or @($closed.fleetEvidence|Where-Object {$_.active}).Count -ne 0){throw 'Fresh recovery did not resolve the fleet incident.'}

  Write-Host 'PASS: build, schema 9, bootstrap/RBAC, agent auth, fleet correlation, trace context, restart/recovery, acknowledgement, ownership, maintenance suppression, SLA analytics, telemetry history, and audit.' -ForegroundColor Green
}
finally {
  if($server -and -not $server.HasExited){ Stop-Process -Id $server.Id -Force }
  Remove-Item -Recurse -Force $testRoot -ErrorAction SilentlyContinue
}
