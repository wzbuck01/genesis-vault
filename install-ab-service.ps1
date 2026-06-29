# install-ab-service.ps1
# Registers 4 independent scheduled tasks for A/B survivability.
# Run as Administrator from C:\skynet\
#
# Task topology:
#   genesis-cloudflared      — CF tunnel (independent, never dies with seed)
#   genesis-conduit-broker   — PUREI relay on :5641
#   genesis-seed-primary     — seed instance A (claims primary via lease)
#   genesis-seed-standby     — seed instance B (registers as sentry, promotes on primary death)
#
# Recovery: each task restarts independently on failure (5x, 1min interval)
# A/B guarantee: if primary dies, standby promotes within 30s; cloudflared unaffected

param([string]$VaultPassphrase = "bc3942bc")

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PS = "powershell.exe"
$BypassArgs = "-ExecutionPolicy Bypass -WindowStyle Hidden"
$RestartSettings = New-ScheduledTaskSettingsSet `
    -RestartCount 10 `
    -RestartInterval (New-TimeSpan -Seconds 30) `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0)
$Principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$Trigger   = New-ScheduledTaskTrigger -AtStartup

function Register-GenesisTask {
    param($Name, $Script, $Args)
    $action = New-ScheduledTaskAction -Execute $PS `
        -Argument "$BypassArgs -File `"$ScriptDir\$Script`" $Args"
    Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue
    Register-ScheduledTask -TaskName $Name -Action $action `
        -Trigger $Trigger -Settings $RestartSettings `
        -Principal $Principal -Force | Out-Null
    Write-Host "[install] registered: $Name" -ForegroundColor Green
}

Register-GenesisTask "genesis-cloudflared"    "start-cloudflared.ps1" ""
Register-GenesisTask "genesis-conduit-broker" "start-broker.ps1"      ""
Register-GenesisTask "genesis-seed-primary"   "start-seed.ps1"        $VaultPassphrase
Register-GenesisTask "genesis-seed-standby"   "start-seed.ps1"        $VaultPassphrase

# Remove old monolithic task if present
Unregister-ScheduledTask -TaskName "genesis-seed-skynet" -Confirm:$false -ErrorAction SilentlyContinue
Write-Host "[install] removed: genesis-seed-skynet (monolithic)" -ForegroundColor Yellow

# Start all four now
Write-Host ""
Write-Host "[install] starting all tasks..." -ForegroundColor Cyan
foreach ($t in @("genesis-cloudflared","genesis-conduit-broker","genesis-seed-primary","genesis-seed-standby")) {
    Start-ScheduledTask -TaskName $t
    Start-Sleep -Seconds 2
    $s = (Get-ScheduledTask -TaskName $t).State
    Write-Host "  $t : $s"
}

Write-Host ""
Write-Host "[install] A/B setup complete." -ForegroundColor Green
Write-Host "  Verify: schtasks /query /fo TABLE | findstr genesis"
