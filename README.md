# genesis-vault

Encrypted Genesis credential vault — AES-256-GCM, PBKDF2-SHA512, passphrase protected.

## Cold Session Bootstrap

Nothing installed except `gcc` and OpenSSL? One command:

```bash
curl -sL https://raw.githubusercontent.com/wzbuck01/genesis-vault/main/vault-decrypt.c \
  | gcc -O2 -x c - -lssl -lcrypto -o vault-decrypt

VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --env > .env && . .env
```

Then proceed with session-start:

```bash
VAULT_PASSPHRASE=<passphrase> ./vault-decrypt ZAC_TOKEN   # get one key
VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --list      # list all keys
VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --env       # export all
```

## vault-decrypt.c

Self-contained C tool. No Node.js, no pre-existing credentials.

- Fetches `vault.json` from this public repo (no token required)
- Falls back to monorepo vault via `ZAC_TOKEN` if set
- Falls back to ESB vault via `VAULT_FETCH_CREDENTIAL` if set
- Decrypts AES-256-GCM entries with PBKDF2-SHA512 (210k rounds)
- Proxy intentionally not honoured — key material transport security

Build deps: `gcc`, `libssl-dev` (OpenSSL)

## vault.json

AES-256-GCM encrypted entries. Token format: `P⟨<base64url>⟩`
Salt: `pv-vault-v1:<key_name>` — key-specific, prevents cross-entry attacks.
