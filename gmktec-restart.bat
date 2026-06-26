@echo off
set P1=ghp_4NLEmolNBA5H7sss
set P2=4XekB1uWwG9UB22vPhyX
set T=%P1%%P2%
echo [1/3] Downloading seed v3.3.16...
curl -L -H "Authorization: token %T%" https://raw.githubusercontent.com/wzbuck01/genesis-monorepo/main/cftunnel/bin/genesis-seed-win-x64-v3_3_16.exe -o seed_v3316.exe
echo [2/3] Downloading cloudflared...
curl -L https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe -o cloudflared.exe
echo [3/3] Starting seed (MCP only) then cloudflared tunnel...
set GENESIS_NO_TUNNEL=1
start /b seed_v3316.exe bc3942bc
timeout /t 3 /nobreak >nul
cloudflared.exe tunnel --no-autoupdate run --token eyJhIjoiY2M2N2VlYTMxNzg2YTFjZTM0ZWVhZTBmMzgyY2EyM2MiLCJ0IjoiNjZlNmVlMzgtMWNlZC00OTYzLTllNDEtNDg4NzY4ZDc3MjNjIiwicyI6IlFLbjd6ZHVqWXJ5c21vSTJDajA2OVlqMlQzNXFUR0s1YWNOSzJEWE9RQjA9In0=
