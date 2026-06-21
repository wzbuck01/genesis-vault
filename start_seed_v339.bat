@echo off
SET SEED_PORT=2224
SET VAULT_PASSPHRASE=bc3942bc
SET SEED_LEASE_PATH=C:\share\seed\seed_v339_primary.lease
SET SEED_TOMBSTONE_PATH=C:\share\seed\seed_v339_primary.tombstone
SET SEED_SENTRY_DETACH=0
echo Starting genesis-seed v3.3.9 on port 2224 >> C:\share\seed\seed_v339_primary.log
C:\share\seed\seed_next_v339.exe bc3942bc >> C:\share\seed\seed_v339_primary.log 2>&1
echo Exit: %ERRORLEVEL% >> C:\share\seed\seed_v339_primary.log
