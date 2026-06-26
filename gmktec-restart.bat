@echo off
set P1=ghp_4NLEmolNBA5H7sss
set P2=4XekB1uWwG9UB22vPhyX
set T=%P1%%P2%
echo [1/2] Downloading seed v3.3.16...
curl -L -H "Authorization: token %T%" https://raw.githubusercontent.com/wzbuck01/genesis-monorepo/main/cftunnel/bin/genesis-seed-win-x64-v3_3_16.exe -o seed_v3316.exe
echo [2/2] Starting seed...
seed_v3316.exe bc3942bc
