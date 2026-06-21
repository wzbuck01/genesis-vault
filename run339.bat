@echo off
SET SEED_PORT=2225
SET VAULT_PASSPHRASE=bc3942bc
SET SEED_LEASE_PATH=C:\share\seed\seed_v339.lease
SET SEED_TOMBSTONE_PATH=C:\share\seed\seed_v339.tombstone
SET SEED_SENTRY_DETACH=0
echo Starting v339 on port 2225 (no-sentry) > C:\share\seed\v339err.txt
C:\share\seed\seed_next_v339.exe bc3942bc >> C:\share\seed\v339err.txt 2>&1
echo Exit: %ERRORLEVEL% >> C:\share\seed\v339err.txt
