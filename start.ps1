# Genesis Seed — SkyNet node startup
# WAN deployment | Port 2226 | skynet.exponential-systems.net
param([string]$VaultPassphrase = "bc3942bc")

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

Write-Host "[skynet] Starting..." -ForegroundColor Cyan

$env:SEED_PORT         = "2226"
$env:SEED_MCP_NAME     = "genesis-seed-skynet"
$env:SEED_EXTERNAL_URL = "https://skynet.exponential-systems.net"
$env:VAULT_PASSPHRASE  = $VaultPassphrase
$env:CF_CREDS          = "$ScriptDir\__no__"

# cloudflared reads tunnel config from CF (ingress: skynet.exponential-systems.net -> localhost:2226)
$Token = "eyJhIjoiY2M2N2VlYTMxNzg2YTFjZTM0ZWVhZTBmMzgyY2EyM2MiLCJ0IjoiNjdjZDViYjctMDQwZS00Yjc0LWE5MzMtNjE1ZmYwM2QzYzE0IiwicyI6InVWcmd1cTNHV3JCWW50VnE1enZzc2xnK1dSTkNqNTdPemM5aDJENmdsbG5kZGFJSWpHa01NTE5BcW9NWXpzWkpFL01yeFpVNWpKODA0elJCVDlhNTF3PT0ifQ=="

Write-Host "[skynet] Starting cloudflared tunnel..." -ForegroundColor DarkGray
$tunnel = Start-Process -FilePath "$ScriptDir\cloudflared.exe" `
    -ArgumentList "tunnel --no-autoupdate run --token `"$Token`"" `
    -PassThru -NoNewWindow

Start-Sleep -Seconds 3

Write-Host "[skynet] Starting conduit-broker on :5641..." -ForegroundColor DarkGray
$broker = Start-Process -FilePath "$ScriptDir\conduit-broker.exe" `
    -PassThru -NoNewWindow
Start-Sleep -Seconds 1

Write-Host "[skynet] Starting seed on port 2226..." -ForegroundColor Cyan
& "$ScriptDir\seed_skynet.exe" $VaultPassphrase

if ($broker -and !$broker.HasExited) { Stop-Process -Id $broker.Id -Force }
if ($tunnel -and !$tunnel.HasExited) { Stop-Process -Id $tunnel.Id -Force }
