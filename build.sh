#!/usr/bin/env bash
# build.sh — vault-decrypt zero-dep static build
# Source: wzbuck01/genesis-vault/
# Requires: gcc, curl in PATH. No OpenSSL.
set -euo pipefail
cd "$(dirname "$0")"

echo "[build] vault-decrypt zero-dep static build"

# Fetch sources if not present
BASE="https://raw.githubusercontent.com/wzbuck01/genesis-vault/main"
for f in vault-decrypt.c sha512.c sha512.h aes256gcm.c aes256gcm.h genesis_secure.h; do
  [ -f "$f" ] || {
    echo "[build] fetching $f"
    curl -fsSL "$BASE/$f" -o "$f"
  }
done

gcc -O2 -D_POSIX_C_SOURCE=200809L -static \
  vault-decrypt.c sha512.c aes256gcm.c \
  -o vault-decrypt

echo "[build] OpenSSL creep check"
ldd vault-decrypt 2>&1 | grep -E 'ssl|crypto' && { echo "FAIL: OpenSSL present"; exit 1; } || true

SHA=$(sha256sum vault-decrypt | cut -d' ' -f1)
echo "[build] SHA-256: $SHA"
echo "[build] OK"
