param(
  [ValidateRange(2,50)][int]$Agents = 10,
  [ValidateSet('CascadingFailure')][string]$Scenario = 'CascadingFailure',
  [ValidateRange(0,30)][int]$HoldSeconds = 5,
  [string]$ServerUrl = 'http://localhost:5080'
)

$ErrorActionPreference = 'Stop'
$uri = [Uri]$ServerUrl
if ($uri.Scheme -ne 'http' -or $uri.Host -notin @('localhost','127.0.0.1','::1')) {
  throw 'ChaosLab only sends synthetic data to a local HTTP OpsForge server.'
}
$root = $PSScriptRoot
$tokenPath = Join-Path $root 'data\security\enrollment-token.txt'
$enrollmentToken = $env:OPSFORGE_ENROLLMENT_TOKEN
if ([string]::IsNullOrWhiteSpace($enrollmentToken)) {
  if (-not (Test-Path $tokenPath)) { throw "Start OpsForge first; enrollment token missing at $tokenPath" }
  $enrollmentToken = (Get-Content $tokenPath -Raw).Trim()
}
$base = $ServerUrl.TrimEnd('/')
$health = Invoke-RestMethod "$base/api/health"
if ($health.version -ne '1.0.0') { throw "Expected OpsForge v1.0.0, found $($health.version)." }
$runId = [Guid]::NewGuid().ToString('N').Substring(0,8)
$serviceId = "chaos-demo-$runId"
$nodes = @()
for ($i=1; $i -le $Agents; $i++) {
  $agentId = 'lab-{0}-{1:d2}' -f $runId,$i
  $enroll = @{
    agentId=$agentId;displayName=('ChaosLab Node {0:d2}' -f $i);site='Synthetic Lab';environmentName='lab'
  } | ConvertTo-Json
  $response = Invoke-RestMethod -Method Post -Uri "$base/api/enrollment/agents" -Headers @{'X-OpsForge-Enrollment-Token'=$enrollmentToken} -ContentType 'application/json' -Body $enroll
  $nodes += [pscustomobject]@{Id=$agentId;Key=$response.apiKey;Index=$i}
}

function Send-LabHeartbeats([bool]$failed) {
  foreach ($node in $nodes) {
    # One source failure and two observing nodes share an explicit service identity.
    $outage = $failed -and $node.Index -le [Math]::Min(3,$Agents)
    $stamp = (Get-Date).ToUniversalTime().ToString('o')
    $body = @{
      agentId=$node.Id;machineName=$node.Id;displayName=('ChaosLab Node {0:d2}' -f $node.Index)
      site='Synthetic Lab';environmentName='lab';operatingSystem='Synthetic';agentVersion='1.0.0'
      fleetServiceId=$serviceId;fleetRuleId='demo-application';fleetRole=$(if($node.Index -eq 1){'source'}else{'observer'})
      timestampUtc=$stamp;cpuPercent=12;memoryUsedPercent=$(if ($failed -and $node.Index -eq $Agents) { 94 } else { 24 });uptimeSeconds=200
      drives=@();networkAdapters=@();monitoredServices=@()
      monitoredProcesses=@()
      probes=@(
        @{id='demo-tcp';type='TCP';target='synthetic:5091';success=(-not $outage);latencyMs=4;detail='synthetic';checkedUtc=$stamp},
        @{id='demo-http';type='HTTP';target='http://synthetic/health';success=(-not $outage);latencyMs=5;detail='synthetic';checkedUtc=$stamp}
      )
    }
    if ($node.Index -eq 1) {
      $body.monitoredProcesses = @(@{name='OpsForge.DemoService';running=(-not $outage);processId=$(if($outage){$null}else{4321})})
    }
    $body = $body | ConvertTo-Json -Depth 10
    Invoke-RestMethod -Method Post -Uri "$base/api/agents/heartbeat" -Headers @{'X-OpsForge-Agent-Key'=$node.Key} -ContentType 'application/json' -Body $body | Out-Null
  }
}

Send-LabHeartbeats $false
Send-LabHeartbeats $true
Write-Host "Injected $Scenario for $Agents synthetic agents ($runId). One fleet incident across three nodes and one separate memory warning." -ForegroundColor Yellow
Write-Host 'Open the dashboard now to inspect primary incidents and suppressed signals.'
if ($HoldSeconds -gt 0) { Start-Sleep -Seconds $HoldSeconds }
Send-LabHeartbeats $false
Write-Host 'Recovery telemetry sent. Lab nodes will later show offline because synthetic agents do not keep running.' -ForegroundColor Green
