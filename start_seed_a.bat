@echo off
REM Seed-A: primary slot, port 2224
REM seed-a.exponential-systems.net routes here
REM Deploy: update seed-b first, verify, flip seed.exponential-systems.net
SET SEED_PORT=2224
SET VAULT_PASSPHRASE=bc3942bc
SET SEED_LEASE_PATH=C:\share\seed\seed_a.lease
SET SEED_TOMBSTONE_PATH=C:\share\seed\seed_a.tombstone
SET SEED_SENTRY_DETACH=0
echo [%DATE% %TIME%] Seed-A starting (port 2224) >> C:\share\seed\seed_a.log
C:\share\seed\seed_next_v339.exe bc3942bc >> C:\share\seed\seed_a.log 2>&1
echo [%DATE% %TIME%] Seed-A exit %ERRORLEVEL% >> C:\share\seed\seed_a.log
