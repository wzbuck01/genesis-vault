# install-scout-gmktec.ps1 v2
# Deploy genesis-scout-coordinator + genesis-scout-worker to GMKtec.
# No inline tokens — ZAC_TOKEN inherited from seed process env (vault-loaded).
# Run via: seed_exec, or manually as Administrator from C:\share\seed

param([string]$SeedDir = "C:\share\seed")
Set-Location $SeedDir

Write-Host "[install-scout] downloading scout-host.exe from genesis-vault..." -ForegroundColor Cyan
# genesis-vault is PUBLIC — no auth required
Invoke-WebRequest `
  -Uri "https://raw.githubusercontent.com/wzbuck01/genesis-vault/main/bin/scout-host.exe" `
  -OutFile "$SeedDir\scout-host.exe"
$size = [math]::Round((Get-Item "$SeedDir\scout-host.exe").Length/1MB, 1)
Write-Host "[install-scout] scout-host.exe: ${size}MB" -ForegroundColor Green

Write-Host "[install-scout] downloading start scripts..." -ForegroundColor Cyan
$ZAC = $env:ZAC_TOKEN
$Headers = if ($ZAC) { @{ Authorization = "token $ZAC" } } else { @{} }
$base = "https://raw.githubusercontent.com/wzbuck01/genesis-monorepo/main/cftunnel/ab"
foreach ($f in @("start-scout-coordinator.ps1","start-scout-worker.ps1")) {
    Invoke-WebRequest -Headers $Headers -Uri "$base/$f" -OutFile "$SeedDir\$f"
    Write-Host "  $f" -ForegroundColor Gray
}

Write-Host "[install-scout] registering tasks..." -ForegroundColor Cyan
$PS       = "powershell.exe"
$Bypass   = "-ExecutionPolicy Bypass -WindowStyle Hidden"
$Settings = New-ScheduledTaskSettingsSet `
    -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0)
$Principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$Trigger   = New-ScheduledTaskTrigger -AtStartup

function Register-ScoutTask {
    param($Name, $Script)
    $action = New-ScheduledTaskAction -Execute $PS `
        -Argument "$Bypass -File `"$SeedDir\$Script`""
    Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue
    try {
        Register-ScheduledTask -TaskName $Name -Action $action -Trigger $Trigger `
            -Settings $Settings -Principal $Principal -Force -ErrorAction Stop | Out-Null
        Write-Host "[install-scout] registered: $Name" -ForegroundColor Green
    } catch {
        Write-Host "[install-scout] FAILED: $Name -- $($_.Exception.Message)" -ForegroundColor Red
        throw
    }
}

Register-ScoutTask "genesis-scout-coordinator" "start-scout-coordinator.ps1"
Register-ScoutTask "genesis-scout-worker"      "start-scout-worker.ps1"

Write-Host "[install-scout] starting tasks..." -ForegroundColor Cyan
foreach ($t in @("genesis-scout-coordinator","genesis-scout-worker")) {
    Start-ScheduledTask -TaskName $t
    Start-Sleep -Seconds 2
    $s = (Get-ScheduledTask -TaskName $t).State
    Write-Host "  $t : $s"
}

Write-Host ""
Write-Host "[install-scout] done." -ForegroundColor Green
schtasks /query /fo TABLE | Select-String genesis
