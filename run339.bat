@echo off
SET SEED_PORT=2225
SET VAULT_PASSPHRASE=bc3942bc
echo Starting v339 on port 2225 > C:\share\seed\v339err.txt
C:\share\seed\seed_next_v339.exe bc3942bc >> C:\share\seed\v339err.txt 2>&1
echo Exit code: %ERRORLEVEL% >> C:\share\seed\v339err.txt
