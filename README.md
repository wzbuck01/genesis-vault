# genesis-vault

Encrypted Genesis credential vault — AES-256-GCM, PBKDF2-SHA512, passphrase protected.

## Cold Session Bootstrap

### Option A — Binary (preferred)

Pull the pre-built binary and validate its hash before executing.
Hash mismatch means drift or tampering — fall back to Option B.

```bash
BASE=https://raw.githubusercontent.com/wzbuck01/genesis-vault/main
curl -sLO $BASE/vault-decrypt
curl -sLO $BASE/vault-decrypt.sha256
sha256sum -c vault-decrypt.sha256          # must print: vault-decrypt: OK
chmod +x vault-decrypt

VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --env > .env && . .env
```

Expected hash: `3d1698f581db52986196a335e0fe38174813349fd7b7458a9d72409f1cdcedd1`

Platform: Linux x86_64, dynamically linked (glibc 2.39+, OpenSSL 3.x).
Matches the claude.ai bash_tool container environment exactly.

### Option B — Compile from source

If the binary hash fails, or you need a different platform:

```bash
curl -sL https://raw.githubusercontent.com/wzbuck01/genesis-vault/main/vault-decrypt.c \
  | gcc -O2 -x c - -lssl -lcrypto -o vault-decrypt
VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --env > .env && . .env
```

Requires: `gcc`, `libssl-dev` (OpenSSL headers).

## Usage

```bash
VAULT_PASSPHRASE=<pp> ./vault-decrypt ZAC_TOKEN       # single key -> stdout
VAULT_PASSPHRASE=<pp> ./vault-decrypt --list          # list key names
VAULT_PASSPHRASE=<pp> ./vault-decrypt --all           # all key=val pairs
VAULT_PASSPHRASE=<pp> ./vault-decrypt --env           # export KEY='val' lines
```

## Files

| File | Purpose |
|------|---------|
| `vault.json` | Encrypted vault entries |
| `vault-decrypt` | Pre-built binary (Linux x86_64) |
| `vault-decrypt.sha256` | Binary hash manifest — validate before running |
| `vault-decrypt.c` | Source — compile if binary hash fails or wrong platform |

## vault.json format

Token format: `P⟨<base64url>⟩`
Crypto: AES-256-GCM, key derived via PBKDF2-SHA512 (210k rounds)
Salt: `pv-vault-v1:<key_name>` — key-specific, prevents cross-entry attacks
