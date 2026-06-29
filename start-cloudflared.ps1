# genesis-cloudflared — independent CF tunnel service
# Runs standalone so seed restarts never affect tunnel continuity
param([string]$Token = "eyJhIjoiY2M2N2VlYTMxNzg2YTFjZTM0ZWVhZTBmMzgyY2EyM2MiLCJ0IjoiNjdjZDViYjctMDQwZS00Yjc0LWE5MzMtNjE1ZmYwM2QzYzE0IiwicyI6InVWcmd1cTNHV3JCWW50VnE1enZzc2xnK1dSTkNqNTdPemM5aDJENmdsbG5kZGFJSWpHa01NTE5BcW9NWXpzWkpFL01yeFpVNWpKODA0elJCVDlhNTF3PT0ifQ==")
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir
Write-Host "[cloudflared] starting tunnel..." -ForegroundColor DarkGray
& "$ScriptDir\cloudflared.exe" tunnel --no-autoupdate run --token $Token
