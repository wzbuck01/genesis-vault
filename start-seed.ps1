# genesis-seed — primary or standby role (seed_survivability_init decides)
# Same binary, same passphrase for both tasks; lease file determines role
param([string]$VaultPassphrase = "bc3942bc")
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir
$env:SEED_PORT         = "2226"
$env:SEED_MCP_NAME     = "genesis-seed-skynet"
$env:SEED_EXTERNAL_URL = "https://skynet.exponential-systems.net"
$env:VAULT_PASSPHRASE  = $VaultPassphrase
Write-Host "[seed] starting (role determined by lease)..." -ForegroundColor Cyan
& "$ScriptDir\seed_skynet.exe" $VaultPassphrase
