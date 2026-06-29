# write-handoff.ps1  — trigger A/B swap (T2-authorized operator tool)
# Usage: write-handoff.ps1 [-Epoch <n>]
# Writes seed_handoff with HMAC-SHA256(signing_secret, "handoff:<epoch>")
# Standby detects within 1s and promotes to primary.
param([int]$Epoch = 0)

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

# Read epoch from lease if not supplied
if ($Epoch -eq 0) {
    $lease = Get-Content "$ScriptDir\genesis.lease" -ErrorAction SilentlyContinue | ConvertFrom-Json
    $Epoch = if ($lease.epoch) { $lease.epoch } else { 1 }
}

# Load signing secret from vault.json
$vault = Get-Content "$ScriptDir\vault.json" | ConvertFrom-Json
$Secret = $vault.SEED_SIGNING_SECRET
if (-not $Secret) { Write-Error "SEED_SIGNING_SECRET not found in vault.json"; exit 1 }

# HMAC-SHA256(secret, "handoff:<epoch>")
$msg   = [Text.Encoding]::UTF8.GetBytes("handoff:$Epoch")
$key   = [Text.Encoding]::UTF8.GetBytes($Secret)
$hmac  = New-Object System.Security.Cryptography.HMACSHA256
$hmac.Key = $key
$hash  = $hmac.ComputeHash($msg)
$hex   = ($hash | ForEach-Object { $_.ToString("x2") }) -join ""

Set-Content -Path "$ScriptDir\seed_handoff" -Value $hex -NoNewline
Write-Host "[handoff] written: handoff:$Epoch => $($hex.Substring(0,16))..." -ForegroundColor Cyan
Write-Host "[handoff] standby will promote within 1s"
