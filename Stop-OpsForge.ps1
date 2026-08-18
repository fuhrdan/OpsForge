$names = @('OpsForge.Agent', 'OpsForge.DemoService', 'OpsForge.Server')
foreach ($name in $names) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Write-Host 'Stopped OpsForge application processes.' -ForegroundColor Green
Write-Host 'The PowerShell host windows opened by START-HERE may now be closed.' -ForegroundColor DarkGray
