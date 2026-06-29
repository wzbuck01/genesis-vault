# genesis-conduit-broker — PUREI LP-frame relay on :5641
# Runs standalone; Task Scheduler restarts on failure
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir
Write-Host "[broker] starting conduit-broker on :5641..." -ForegroundColor DarkGray
& "$ScriptDir\conduit-broker.exe"
