@echo off
REM Seed-B: standby slot, port 2225
REM seed-b.exponential-systems.net routes here
REM Deploy new version here first, then flip seed.exponential-systems.net to 2225
SET SEED_PORT=2225
SET VAULT_PASSPHRASE=bc3942bc
SET SEED_LEASE_PATH=C:\share\seed\seed_b.lease
SET SEED_TOMBSTONE_PATH=C:\share\seed\seed_b.tombstone
SET SEED_SENTRY_DETACH=0
echo [%DATE% %TIME%] Seed-B starting (port 2225) >> C:\share\seed\seed_b.log
C:\share\seed\seed_next_v339.exe bc3942bc >> C:\share\seed\seed_b.log 2>&1
echo [%DATE% %TIME%] Seed-B exit %ERRORLEVEL% >> C:\share\seed\seed_b.log
