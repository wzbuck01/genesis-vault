/*
 * vault-decrypt.c — Genesis vault bootstrap tool
 *
 * Self-contained. No Node.js. No OpenSSL. Just gcc + curl.
 *
 * Fetches vault.json from the public genesis-vault repo, decrypts
 * entries with AES-256-GCM + PBKDF2-SHA512, and prints values to stdout.
 *
 * Build:
 *   gcc -O2 -D_POSIX_C_SOURCE=200809L vault-decrypt.c sha512.c aes256gcm.c -o vault-decrypt
 *
 * Runtime dependency: curl in PATH (for vault fetch). No OpenSSL.
 *
 * Usage:
 *   VAULT_PASSPHRASE=<passphrase> ./vault-decrypt <KEY>
 *   VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --all
 *   VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --env   # export KEY=val lines
 *   VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --list  # key names only
 *
 * Multi-file bootstrap (cold session):
 *   for f in vault-decrypt.c sha512.c sha512.h aes256gcm.c aes256gcm.h genesis_secure.h; do
 *     curl -fsSL https://raw.githubusercontent.com/wzbuck01/genesis-vault/main/$f -o $f
 *   done
 *   gcc -O2 -D_POSIX_C_SOURCE=200809L vault-decrypt.c sha512.c aes256gcm.c -o vault-decrypt
 *   VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --env > .env && . .env
 *
 * Vault tiers attempted in order:
 *   1. Public genesis-vault (no token required)  -- always attempted first
 *   2. ZAC_TOKEN -> monorepo vault               -- canonical source; if ZAC_TOKEN in env
 *   3. VAULT_FETCH_CREDENTIAL -> ESB vault       -- fallback when ZAC dead; if VFC in env
 *   4. Stale local cache                         -- $VAULT_CDN_PATH/vault/vault.json
 *                                                   or $HOME/.genesis/vault.json
 *
 * Transport policy:
 *   Vault fetches MUST NOT honor HTTPS_PROXY.
 *   curl --noproxy '*' enforces this unconditionally.
 *   Key material must not route through an untrusted intermediary.
 *
 * Cache: encrypted vault.json written to disk on every successful network load.
 * Requires VAULT_PASSPHRASE to decrypt — safe to persist.
 *
 * Intentional gaps vs Node vault.mjs:
 *   - No Anthropic Files API tier (no inference context in cold bootstrap)
 *   - No write path (read-only bootstrap tool by design)
 *   - No proxy support (key material must not route through untrusted intermediary)
 *     Node vault.mjs allows proxy for credentialed tiers (operability trade-off).
 *
 * (c) 2026 Brandon Clark / Genesis Systems. All Rights Reserved.
 */

#include "sha512.h"
#include "aes256gcm.h"
#include "genesis_secure.h"

#include <stdio.h>
#include <unistd.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>

/* ── Constants ───────────────────────────────────────────────────────────── */

#define VAULT_URL_PUBLIC  "https://raw.githubusercontent.com/wzbuck01/genesis-vault/main/vault.json"
#define VAULT_URL_MONO    "https://api.github.com/repos/wzbuck01/genesis-monorepo/contents/vault/vault.json"
#define VAULT_URL_ESB     "https://api.github.com/repos/Prime-Velocity/exponential-session-bootstrap/contents/vault/vault.json"

#define PBKDF2_ROUNDS  210000
#define KEY_LEN        32
#define TAG_LEN        16
#define NONCE_LEN      12
#define MAX_VAULT      (1024 * 1024)
#define MAX_ENTRIES    128          /* raised from 64 — warn at 96 */
#define MAX_ENTRIES_WARN 96
#define MAX_KEY        128
#define MAX_VAL        (64 * 1024)

/* ── In-memory store ─────────────────────────────────────────────────────── */

typedef struct { char key[MAX_KEY]; char *val; } entry_t;
static entry_t  g_entries[MAX_ENTRIES];
static int      g_count = 0;

/* ── Growable buffer ─────────────────────────────────────────────────────── */

typedef struct { char *data; size_t len; size_t cap; } buf_t;

static int buf_append(buf_t *b, const char *d, size_t n) {
    if (b->len + n + 1 > b->cap) {
        size_t nc = b->cap * 2 + n + 1;
        char *t = realloc(b->data, nc);
        if (!t) return -1;
        b->data = t; b->cap = nc;
    }
    memcpy(b->data + b->len, d, n);
    b->len += n;
    b->data[b->len] = '\0';
    return 0;
}

/* ── Shell argument safety check ─────────────────────────────────────────── */
/*
 * Restricts token and Accept header values to characters safe for
 * single-quoted shell arguments (no single quotes, no backticks).
 */
static int shell_safe(const char *s) {
    if (!s) return 1;
    for (; *s; s++) {
        unsigned char c = (unsigned char)*s;
        if (!( (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
               (c >= '0' && c <= '9') ||
                c == '_' || c == '-' || c == '.' || c == '/' ||
                c == ':' || c == '+' || c == '=' || c == '@' ))
            return 0;
    }
    return 1;
}

/* ── HTTPS GET via curl --noproxy '*' ────────────────────────────────────── */
/*
 * Replaces the OpenSSL BIO transport.
 * --noproxy '*' is unconditional: vault fetches must not route through proxy.
 * -w '\n%{http_code}' appends the HTTP status as the last line of output.
 * Status is parsed and body is trimmed before returning.
 */
static int https_get(const char *url, const char *token,
                     const char *accept, buf_t *out) {
    if (!shell_safe(token))  { fprintf(stderr, "[vault-decrypt] unsafe token\n");  return -1; }
    if (!shell_safe(accept)) { fprintf(stderr, "[vault-decrypt] unsafe accept\n"); return -1; }

    char cmd[2048];
    int n;
    if (token && token[0] && accept && accept[0]) {
        n = snprintf(cmd, sizeof(cmd),
            "curl -s --noproxy '*' --max-time 30 "
            "-H 'Authorization: token %s' "
            "-H 'Accept: %s' "
            "-H 'User-Agent: genesis/vault-decrypt' "
            "-w '\\n%%{http_code}' '%s' 2>/dev/null",
            token, accept, url);
    } else if (token && token[0]) {
        n = snprintf(cmd, sizeof(cmd),
            "curl -s --noproxy '*' --max-time 30 "
            "-H 'Authorization: token %s' "
            "-H 'User-Agent: genesis/vault-decrypt' "
            "-w '\\n%%{http_code}' '%s' 2>/dev/null",
            token, url);
    } else {
        n = snprintf(cmd, sizeof(cmd),
            "curl -s --noproxy '*' --max-time 30 "
            "-H 'User-Agent: genesis/vault-decrypt' "
            "-w '\\n%%{http_code}' '%s' 2>/dev/null",
            url);
    }
    if (n <= 0 || (size_t)n >= sizeof(cmd)) return -1;

    FILE *fp = popen(cmd, "r");
    if (!fp) return -1;

    buf_t raw = { malloc(131072), 0, 131072 };
    if (!raw.data) { pclose(fp); return -1; }
    char rbuf[8192]; size_t nr;
    while ((nr = fread(rbuf, 1, sizeof(rbuf), fp)) > 0)
        buf_append(&raw, rbuf, nr);
    pclose(fp);

    /* Last line is HTTP status appended by -w '\n%{http_code}' */
    if (raw.len < 3) { free(raw.data); return -1; }
    char *last_nl = raw.data + raw.len;
    while (last_nl > raw.data && *last_nl != '\n') last_nl--;
    long code = strtol(last_nl + 1, NULL, 10);
    *last_nl = '\0';
    raw.len = (size_t)(last_nl - raw.data);

    if (code != 200) {
        fprintf(stderr, "[vault-decrypt] HTTP %ld from %s\n", code, url);
        free(raw.data); return -1;
    }

    buf_append(out, raw.data, raw.len);
    free(raw.data);
    return 0;
}

/* ── Base64url decode ────────────────────────────────────────────────────── */
/*
 * Replaces the OpenSSL BIO base64 decoder.
 * Handles both base64url ('-','_') and standard base64 ('+','/').
 * Padding is optional.
 */
static int b64url_decode(const char *in, size_t inlen,
                         unsigned char *out, size_t *outlen) {
    static const unsigned char T[256] = {
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0x3E,0xFF,0x3E,0xFF,0x3F,
        0x34,0x35,0x36,0x37,0x38,0x39,0x3A,0x3B,0x3C,0x3D,0xFF,0xFF,0xFF,0x40,0xFF,0xFF,
        0xFF,0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0A,0x0B,0x0C,0x0D,0x0E,
        0x0F,0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0xFF,0xFF,0xFF,0xFF,0x3F,
        0xFF,0x1A,0x1B,0x1C,0x1D,0x1E,0x1F,0x20,0x21,0x22,0x23,0x24,0x25,0x26,0x27,0x28,
        0x29,0x2A,0x2B,0x2C,0x2D,0x2E,0x2F,0x30,0x31,0x32,0x33,0xFF,0xFF,0xFF,0xFF,0xFF,
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
        0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
    };
    size_t oi = 0;
    uint32_t acc = 0;
    int bits = 0;
    for (size_t i = 0; i < inlen; i++) {
        unsigned char c = (unsigned char)in[i];
        unsigned char v = T[c];
        if (v == 0xFF) return -1;
        if (v == 0x40) break;   /* padding '=': stop */
        acc  = (acc << 6) | v;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out[oi++] = (unsigned char)((acc >> bits) & 0xFF);
        }
    }
    *outlen = oi;
    return 0;
}

/* ── PBKDF2-SHA512 + AES-256-GCM decrypt ────────────────────────────────── */
/*
 * Replaces PKCS5_PBKDF2_HMAC + EVP_aes_256_gcm.
 * Uses pbkdf2_sha512() from sha512.c and aes256gcm_decrypt() from aes256gcm.c.
 */
static int decrypt_entry(const char *passphrase, const char *label,
                         const char *b64token, char *plain, size_t *plen) {
    /* Strip P⟨ prefix and ⟩ suffix (raw UTF-8 or JSON-escaped forms) */
    const char *b64 = b64token;
    size_t b64len   = strlen(b64token);

    if (b64len > 7 && b64[0] == 'P' &&
        (unsigned char)b64[1] == 0xE2 && (unsigned char)b64[2] == 0x9F &&
        (unsigned char)b64[3] == 0xA8) {
        b64 += 4; b64len -= 4;
        if (b64len >= 3 &&
            (unsigned char)b64[b64len-3] == 0xE2 &&
            (unsigned char)b64[b64len-2] == 0x9F &&
            (unsigned char)b64[b64len-1] == 0xA9)
            b64len -= 3;
    } else if (b64len > 13 && b64[0] == 'P' && b64[1] == '\\' &&
               b64[2] == 'u' && b64[3] == '2' && b64[4] == '7' &&
               b64[5] == 'e' && b64[6] == '8') {
        b64 += 7; b64len -= 7;
        if (b64len >= 6 && b64[b64len-6] == '\\' &&
            b64[b64len-5] == 'u' && b64[b64len-4] == '2' &&
            b64[b64len-3] == '7' && b64[b64len-2] == 'e' &&
            b64[b64len-1] == '9')
            b64len -= 6;
    }

    unsigned char blob[MAX_VAL + 64]; size_t blen = 0;
    if (b64url_decode(b64, b64len, blob, &blen) != 0) return -1;
    if (blen < (size_t)(NONCE_LEN + TAG_LEN + 1)) return -1;

    char salt[256];
    snprintf(salt, sizeof(salt), "pv-vault-v1:%s", label);
    unsigned char key[KEY_LEN];
    pbkdf2_sha512((const uint8_t *)passphrase, strlen(passphrase),
                  (const uint8_t *)salt,       strlen(salt),
                  PBKDF2_ROUNDS, key, KEY_LEN);

    const unsigned char *nonce  = blob;
    const unsigned char *ct     = blob + NONCE_LEN;
    size_t               ct_len = blen - NONCE_LEN - TAG_LEN;
    const unsigned char *tag    = blob + blen - TAG_LEN;

    int rc = aes256gcm_decrypt(key, nonce, NONCE_LEN,
                               ct, ct_len,
                               NULL, 0,
                               tag,
                               (unsigned char *)plain);

    secure_zero(key,  sizeof(key));
    secure_zero(blob, blen);
    secure_zero(salt, sizeof(salt));

    if (rc != 0) return -1;
    *plen = ct_len;
    plain[ct_len] = '\0';
    return 0;
}

/* ── JSON vault walker ───────────────────────────────────────────────────── */
/*
 * Walks raw vault JSON looking for sealed P⟨⟩ token values.
 * Handles both raw UTF-8 and JSON-escaped \u27e8/\u27e9 bracket forms.
 *
 * NOTE: GitHub Contents API wrapper (base64-encoded "content" field) is not
 * handled here because all active vault tiers return raw JSON:
 *   - Public tier: raw.githubusercontent.com returns raw bytes
 *   - ZAC/VFC tiers: application/vnd.github.raw+json returns raw bytes
 * The wrapper format is only returned by the default Contents API Accept
 * header, which this tool never uses.
 */
static int walk_vault(const char *src, const char *passphrase) {
    static char plain[MAX_VAL + 1];
    const char *p = src;
    int count = 0;

    while (*p && g_count < MAX_ENTRIES) {
        if (*p != '"') { p++; continue; }
        p++;
        const char *ks = p;
        while (*p && *p != '"') p++;
        size_t kl = (size_t)(p - ks);
        if (!*p || kl == 0 || kl >= MAX_KEY) { if (*p) p++; continue; }
        char key[MAX_KEY]; memcpy(key, ks, kl); key[kl] = '\0';
        p++;
        while (*p && *p != ':' && *p != '"' && *p != '}') p++;
        if (*p != ':') continue;
        p++;
        while (*p == ' ') p++;
        if (*p != '"') continue;
        p++;

        /* Detect sealed token: P⟨ (UTF-8) or P\u27e8 (JSON-escaped) */
        int sealed = 0;
        if (p[0] == 'P' && (unsigned char)p[1] == 0xE2 &&
            (unsigned char)p[2] == 0x9F && (unsigned char)p[3] == 0xA8)
            sealed = 1;
        else if (p[0] == 'P' && p[1] == '\\' && p[2] == 'u' &&
                 p[3] == '2' && p[4] == '7' && p[5] == 'e' && p[6] == '8')
            sealed = 1;

        if (sealed) {
            const char *tok_start = p;
            const char *scan = p;
            while (*scan && *scan != '"') scan++;
            size_t tok_len = (size_t)(scan - tok_start);

            char *tokbuf = malloc(tok_len + 1);
            if (tokbuf) {
                memcpy(tokbuf, tok_start, tok_len); tokbuf[tok_len] = '\0';
                size_t pl = 0;
                if (decrypt_entry(passphrase, key, tokbuf, plain, &pl) == 0
                    && pl > 0 && g_count < MAX_ENTRIES) {
                    char *v = malloc(pl + 1);
                    if (v) {
                        memcpy(v, plain, pl + 1);
                        memcpy(g_entries[g_count].key, key, kl + 1);
                        g_entries[g_count].val = v;
                        g_count++;
                        count++;
                        if (g_count == MAX_ENTRIES_WARN)
                            fprintf(stderr, "[vault-decrypt] WARNING: %d entries loaded"
                                    " — approaching limit of %d; consider bumping MAX_ENTRIES\n",
                                    g_count, MAX_ENTRIES);
                    }
                }
                free(tokbuf);
            }
            p = scan;
        }

        while (*p && *p != '"') p++;
        if (*p) p++;
    }
    return count;
}

/* ── Local vault cache ───────────────────────────────────────────────────── */

static char *cache_path(void) {
    const char *cdp = getenv("VAULT_CDN_PATH");
    char *buf = malloc(1024);
    if (!buf) return NULL;
    if (cdp && cdp[0])
        snprintf(buf, 1024, "%s/vault/vault.json", cdp);
    else {
        const char *home = getenv("HOME");
        if (!home || !home[0]) { free(buf); return NULL; }
        snprintf(buf, 1024, "%s/.genesis/vault.json", home);
    }
    return buf;
}

static void write_cache(const char *data, size_t len) {
    char *cp = cache_path();
    if (!cp) return;
    char dir[1024]; snprintf(dir, sizeof(dir), "%s", cp);
    char *slash = strrchr(dir, '/');
    if (slash) {
        *slash = '\0';
        char cmd[1100]; snprintf(cmd, sizeof(cmd), "mkdir -p '%s' 2>/dev/null", dir);
        int _r = system(cmd); (void)_r;
    }
    FILE *f = fopen(cp, "w");
    if (f) { fwrite(data, 1, len, f); fclose(f); }
    free(cp);
}

static int read_cache(buf_t *out) {
    char *cp = cache_path();
    if (!cp) return -1;
    FILE *f = fopen(cp, "r");
    if (!f) { free(cp); return -1; }
    char rbuf[8192]; int n;
    while ((n = (int)fread(rbuf, 1, sizeof(rbuf), f)) > 0)
        buf_append(out, rbuf, (size_t)n);
    fclose(f); free(cp);
    return out->len > 0 ? 0 : -1;
}

/* ── Vault loader ────────────────────────────────────────────────────────── */

static int load_vault(const char *passphrase) {
    const char *zac = getenv("ZAC_TOKEN");
    const char *vfc = getenv("VAULT_FETCH_CREDENTIAL");

    buf_t buf = { malloc(131072), 0, 131072 };
    if (!buf.data) return -1;
    int ok = 0;

    /* Tier 1: public genesis-vault — no token, always first */
    fprintf(stderr, "[vault-decrypt] trying public vault...\n");
    ok = (https_get(VAULT_URL_PUBLIC, NULL, NULL, &buf) == 0);

    /* Tier 2: monorepo via ZAC */
    if (!ok && zac && zac[0]) {
        fprintf(stderr, "[vault-decrypt] trying monorepo vault (ZAC)...\n");
        buf.len = 0;
        ok = (https_get(VAULT_URL_MONO, zac,
                        "application/vnd.github.raw+json", &buf) == 0);
    }

    /* Tier 3: ESB via VFC */
    if (!ok && vfc && vfc[0]) {
        fprintf(stderr, "[vault-decrypt] trying ESB vault (VFC)...\n");
        buf.len = 0;
        ok = (https_get(VAULT_URL_ESB, vfc,
                        "application/vnd.github.raw+json", &buf) == 0);
    }

    /* Tier 4: stale local cache */
    if (!ok) {
        fprintf(stderr, "[vault-decrypt] network tiers exhausted — trying stale cache...\n");
        buf.len = 0;
        ok = (read_cache(&buf) == 0);
        if (ok) fprintf(stderr, "[vault-decrypt] loaded from stale local cache\n");
    }

    if (!ok || buf.len == 0) {
        fprintf(stderr, "[vault-decrypt] all vault tiers failed\n");
        free(buf.data); return -1;
    }

    fprintf(stderr, "[vault-decrypt] fetched %zu bytes, decrypting...\n", buf.len);
    int n = walk_vault(buf.data, passphrase);
    if (n > 0) write_cache(buf.data, buf.len);
    free(buf.data);

    if (n == 0) {
        fprintf(stderr, "[vault-decrypt] no entries decrypted — wrong passphrase?\n");
        return -1;
    }
    fprintf(stderr, "[vault-decrypt] decrypted %d entries\n", n);
    return 0;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * VAULT SET — write path (--set KEY VALUE)
 * Seals VALUE for KEY with AES-256-GCM + PBKDF2-SHA512, updates the vault
 * JSON in memory, and PUTs to all three canonical vault locations:
 *   1. wzbuck01/genesis-monorepo/vault/vault.json  (ZAC_TOKEN)
 *   2. wzbuck01/genesis-vault/vault.json            (ZAC_TOKEN)
 *   3. Prime-Velocity/exponential-session-bootstrap/vault/vault.json  (PV_TOKEN)
 *
 * Env: VAULT_PASSPHRASE + ZAC_TOKEN required. PV_TOKEN optional (ESB skipped if absent).
 * Runtime dep: curl in PATH (same as read path).
 * ═══════════════════════════════════════════════════════════════════════════ */

#include <stdint.h>
#include <time.h>

/* ── Base64url encode ────────────────────────────────────────────────────── */

static const char B64URL[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

/* Returns number of chars written (no null terminator).
 * out must have capacity ceil(inlen * 4/3) + 1 bytes. */
static size_t b64url_encode(const uint8_t *in, size_t inlen, char *out) {
    size_t oi = 0;
    for (size_t i = 0; i < inlen; i += 3) {
        uint32_t v  = (uint32_t)in[i] << 16;
        if (i+1 < inlen) v |= (uint32_t)in[i+1] << 8;
        if (i+2 < inlen) v |= (uint32_t)in[i+2];
        out[oi++] = B64URL[(v >> 18) & 0x3F];
        out[oi++] = B64URL[(v >> 12) & 0x3F];
        if (i+1 < inlen) out[oi++] = B64URL[(v >>  6) & 0x3F];
        if (i+2 < inlen) out[oi++] = B64URL[(v      ) & 0x3F];
    }
    out[oi] = '\0';
    return oi;
}

/* ── Seal a plaintext value → P⟨base64url⟩ token ────────────────────────── */

/* out must be at least MAX_VAL + 128 bytes.
 * Returns 0 on success. */
static int vault_seal(const char *passphrase, const char *label,
                      const char *plaintext,  char *out, size_t outmax) {
    /* Key derivation */
    char salt[300];
    snprintf(salt, sizeof(salt), "pv-vault-v1:%s", label);
    uint8_t key[32];
    pbkdf2_sha512((const uint8_t *)passphrase, strlen(passphrase),
                  (const uint8_t *)salt,       strlen(salt),
                  PBKDF2_ROUNDS, key, 32);

    /* Random nonce */
    uint8_t nonce[NONCE_LEN];
#if defined(_WIN32)
    /* On Windows: use BCryptGenRandom via CryptGenRandom */
    HCRYPTPROV hProv;
    CryptAcquireContext(&hProv, NULL, NULL, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT);
    CryptGenRandom(hProv, NONCE_LEN, nonce);
    CryptReleaseContext(hProv, 0);
#else
    FILE *urnd = fopen("/dev/urandom", "rb");
    if (!urnd || fread(nonce, 1, NONCE_LEN, urnd) != NONCE_LEN) {
        if (urnd) fclose(urnd);
        /* Fallback: time-based (weak, log a warning) */
        uint64_t ts = (uint64_t)time(NULL);
        memcpy(nonce, &ts, 8);
        fprintf(stderr, "[vault-set] WARNING: /dev/urandom unavailable — weak nonce\n");
    } else {
        fclose(urnd);
    }
#endif

    /* Encrypt */
    size_t ptlen = strlen(plaintext);
    if (ptlen + NONCE_LEN + TAG_LEN + 2 > outmax) return -1;

    uint8_t *blob     = (uint8_t *)malloc(NONCE_LEN + ptlen + TAG_LEN + 4);
    if (!blob) return -1;
    uint8_t *ct       = blob + NONCE_LEN;
    uint8_t *tag_dst  = ct + ptlen;
    memcpy(blob, nonce, NONCE_LEN);

    aes256gcm_encrypt(key, nonce, NONCE_LEN,
                      (const uint8_t *)plaintext, ptlen,
                      NULL, 0,
                      ct, tag_dst);
    memset(key, 0, sizeof(key)); __asm__ volatile("" ::: "memory");

    /* Encode blob to base64url */
    size_t bloblen = NONCE_LEN + ptlen + TAG_LEN;
    size_t enclen  = (bloblen * 4 + 2) / 3 + 4;
    char *encbuf   = (char *)malloc(enclen);
    if (!encbuf) { free(blob); return -1; }
    b64url_encode(blob, bloblen, encbuf);
    free(blob);

    /* Format: P⟨base64url⟩  (⟨ = U+27E8, ⟩ = U+27E9, UTF-8) */
    int r = snprintf(out, outmax, "P\xe2\x9f\xa8%s\xe2\x9f\xa9", encbuf);
    free(encbuf);
    return (r > 0 && (size_t)r < outmax) ? 0 : -1;
}

/* ── JSON entry patcher ───────────────────────────────────────────────────── */

/* Updates or inserts entries["key"] = token in raw vault JSON.
 * Returns newly allocated string (caller must free), or NULL on error.
 * Simple string-level patch — vault format is controlled, not arbitrary JSON. */
static char *json_patch_entry(const char *json, const char *key, const char *token) {
    /* Build the search pattern: "KEY": "  */
    char search[MAX_KEY + 8];
    snprintf(search, sizeof(search), "\"%s\":", key);

    char *out      = (char *)malloc(strlen(json) + strlen(token) + 256);
    if (!out) return NULL;

    const char *found = strstr(json, search);
    if (found) {
        /* Key exists — replace token: skip to opening " of value, find closing " */
        const char *vs = found + strlen(search);
        while (*vs == ' ' || *vs == '\n' || *vs == '\r') vs++;
        if (*vs != '"') { free(out); return NULL; }    /* unexpected format */
        vs++;                                           /* past opening " */
        const char *ve = vs;
        /* Find end of P⟨...⟩ sealed token — scan to closing ⟩ then " */
        while (*ve && *ve != '"') {
            /* skip UTF-8 sequences */
            if ((unsigned char)*ve >= 0x80) { ve++; continue; }
            ve++;
        }
        /* Copy: before opening ", insert new token, copy rest */
        size_t pre = (size_t)((vs - 1) - json);  /* up to and incl the " */
        memcpy(out, json, pre);
        size_t oi = pre;
        oi += (size_t)sprintf(out + oi, "%s", token);
        strcpy(out + oi, ve);
    } else {
        /* Key not found — insert before closing } of "entries" */
        const char *entries = strstr(json, "\"entries\"");
        if (!entries) { free(out); return NULL; }
        const char *brace = strchr(entries, '{');
        if (!brace) { free(out); return NULL; }

        /* Find the closing } of entries — scan forward counting braces */
        const char *p = brace + 1;
        int depth = 1;
        while (*p && depth > 0) {
            if (*p == '{') depth++;
            else if (*p == '}') depth--;
            if (depth > 0) p++;
        }
        /* p now points to the closing } of entries */
        /* Find the last real entry before } to know if we need a comma */
        const char *prev = p - 1;
        while (prev > brace && (*prev == ' ' || *prev == '\n' || *prev == '\r')) prev--;
        int need_comma = (*prev != '{');  /* empty entries block gets no comma */

        size_t pre = (size_t)(p - json);
        memcpy(out, json, pre);
        size_t oi = pre;
        if (need_comma) out[oi++] = ',';
        oi += (size_t)sprintf(out + oi, "\n    \"%s\": \"%s\"", key, token);
        strcpy(out + oi, p);
    }
    return out;
}

/* ── Update _updated timestamp ───────────────────────────────────────────── */

static char *json_set_updated(const char *json) {
    time_t now = time(NULL);
    struct tm *utc = gmtime(&now);
    char ts[32];
    strftime(ts, sizeof(ts), "%Y-%m-%dT%H:%M:%SZ", utc);

    const char *tag = "\"_updated\":";
    const char *found = strstr(json, tag);
    if (!found) return strdup(json);    /* no _updated field, leave as-is */

    /* Find the value string */
    const char *vs = found + strlen(tag);
    while (*vs == ' ') vs++;
    if (*vs != '"') return strdup(json);
    vs++;
    const char *ve = strchr(vs, '"');
    if (!ve) return strdup(json);

    size_t outlen = strlen(json) + 32;
    char *out = (char *)malloc(outlen);
    if (!out) return NULL;
    size_t pre = (size_t)(vs - json);
    memcpy(out, json, pre);
    size_t oi = pre;
    oi += (size_t)sprintf(out + oi, "%s", ts);
    strcpy(out + oi, ve);
    return out;
}

/* ── GitHub Contents API PUT ─────────────────────────────────────────────── */

/* GET SHA + PUT new content. Returns commit SHA prefix or NULL on failure. */
static char *gh_put_vault(const char *api_path, const char *token,
                          const char *b64_content, const char *message) {
    if (!shell_safe(token)) { fprintf(stderr, "[vault-set] unsafe token\n"); return NULL; }

    /* Step 1: GET current SHA */
    char url[512];
    snprintf(url, sizeof(url), "https://api.github.com/repos/%s", api_path);
    buf_t meta = { malloc(65536), 0, 65536 };
    if (!meta.data) return NULL;

    if (https_get(url, token, "application/vnd.github.v3+json", &meta) != 0) {
        free(meta.data); return NULL;
    }

    /* Parse "sha": "..." from JSON response */
    const char *sha_tag = "\"sha\":";
    const char *sp = strstr(meta.data, sha_tag);
    char sha[64] = {0};
    if (sp) {
        sp += strlen(sha_tag);
        while (*sp == ' ' || *sp == '"') sp++;
        size_t i = 0;
        while (*sp && *sp != '"' && i < 63) sha[i++] = *sp++;
    }
    free(meta.data);

    /* Step 2: Build PUT body → temp file */
    char bodypath[256];
    snprintf(bodypath, sizeof(bodypath), "/tmp/vault-set-body-%d.json", (int)getpid());
    FILE *bf = fopen(bodypath, "w");
    if (!bf) return NULL;
    fprintf(bf, "{\"message\":\"%s\",\"content\":\"%s\"", message, b64_content);
    if (sha[0]) fprintf(bf, ",\"sha\":\"%s\"", sha);
    fprintf(bf, "}");
    fclose(bf);

    /* Step 3: curl PUT */
    char resppath[256];
    snprintf(resppath, sizeof(resppath), "/tmp/vault-set-resp-%d.json", (int)getpid());
    char cmd[2048];
    snprintf(cmd, sizeof(cmd),
        "curl -s --noproxy '*' --max-time 30 -X PUT "
        "-H 'Authorization: token %s' "
        "-H 'Content-Type: application/json' "
        "-H 'User-Agent: genesis/vault-decrypt' "
        "-w '\\n%%{http_code}' "
        "-d @%s '%s' > %s 2>/dev/null",
        token, bodypath, url, resppath);

    int rc = system(cmd);
    remove(bodypath);

    if (rc != 0) { remove(resppath); return NULL; }

    /* Read response and extract commit SHA */
    FILE *rf = fopen(resppath, "r");
    char *commit_sha = NULL;
    if (rf) {
        char resp[8192] = {0};
        (void)fread(resp, 1, sizeof(resp)-1, rf);
        fclose(rf);
        /* Find commit.sha */
        const char *cs = strstr(resp, "\"commit\"");
        if (cs) {
            const char *sh = strstr(cs, "\"sha\":");
            if (sh) {
                sh += 6;
                while (*sh == ' ' || *sh == '"') sh++;
                char buf[16] = {0};
                for (int i = 0; i < 12 && *sh && *sh != '"'; i++, sh++)
                    buf[i] = *sh;
                commit_sha = strdup(buf);
            }
        }
        /* Check HTTP status (last line) */
        char *last = strrchr(resp, '\n');
        if (last && atoi(last+1) / 100 != 2) {
            fprintf(stderr, "[vault-set] PUT HTTP %s for %s\n", last+1, api_path);
            free(commit_sha);
            commit_sha = NULL;
        }
    }
    remove(resppath);
    return commit_sha;  /* NULL = failure */
}

/* ── vault_set_all — main write orchestrator ─────────────────────────────── */

static int vault_set_all(const char *passphrase, const char *key, const char *value) {
    const char *zac   = getenv("ZAC_TOKEN");
    const char *pvtok = getenv("PV_TOKEN");

    if (!zac || !zac[0]) {
        fprintf(stderr, "[vault-set] ZAC_TOKEN required for vault writes\n");
        return 1;
    }

    /* 1. Load current vault JSON */
    buf_t raw = { malloc(MAX_VAULT), 0, MAX_VAULT };
    if (!raw.data) return 1;
    /* Try monorepo first (canonical), fall back to public */
    int loaded = 0;
    if (zac && zac[0]) {
        loaded = (https_get(
            "https://api.github.com/repos/wzbuck01/genesis-monorepo/contents/vault/vault.json",
            zac, "application/vnd.github.v3+json", &raw) == 0);
        if (loaded) {
            /* Unwrap Contents API base64 — find "content": "..." */
            const char *ctag = "\"content\":";
            const char *cp   = strstr(raw.data, ctag);
            if (cp) {
                cp += strlen(ctag);
                while (*cp == ' ' || *cp == '"') cp++;
                /* Decode base64 (standard, with \n) */
                char *decoded = (char *)malloc(raw.len);
                if (!decoded) { free(raw.data); return 1; }
                size_t di = 0;
                static const int T64[256] = {
                    [0 ... 255] = -1,
                    ['A']=0,['B']=1,['C']=2,['D']=3,['E']=4,['F']=5,['G']=6,['H']=7,
                    ['I']=8,['J']=9,['K']=10,['L']=11,['M']=12,['N']=13,['O']=14,['P']=15,
                    ['Q']=16,['R']=17,['S']=18,['T']=19,['U']=20,['V']=21,['W']=22,['X']=23,
                    ['Y']=24,['Z']=25,['a']=26,['b']=27,['c']=28,['d']=29,['e']=30,['f']=31,
                    ['g']=32,['h']=33,['i']=34,['j']=35,['k']=36,['l']=37,['m']=38,['n']=39,
                    ['o']=40,['p']=41,['q']=42,['r']=43,['s']=44,['t']=45,['u']=46,['v']=47,
                    ['w']=48,['x']=49,['y']=50,['z']=51,['0']=52,['1']=53,['2']=54,['3']=55,
                    ['4']=56,['5']=57,['6']=58,['7']=59,['8']=60,['9']=61,['+']=62,['/']=63,
                    ['=']=64 };
                uint32_t acc = 0; int bits = 0;
                for (const char *s = cp; *s && *s != '"'; s++) {
                    int v = T64[(unsigned char)*s];
                    if (v < 0) continue;
                    if (v == 64) break;
                    acc = (acc << 6) | (uint32_t)v; bits += 6;
                    if (bits >= 8) { bits -= 8; decoded[di++] = (char)((acc >> bits) & 0xFF); }
                }
                decoded[di] = '\0';
                free(raw.data);
                raw.data = decoded; raw.len = di;
            }
        }
    }
    if (!loaded || !strstr(raw.data, "entries")) {
        /* Fall back to public vault */
        raw.len = 0;
        loaded = (https_get(VAULT_URL_PUBLIC, NULL, NULL, &raw) == 0);
    }
    if (!loaded) {
        fprintf(stderr, "[vault-set] could not load vault\n");
        free(raw.data); return 1;
    }

    /* 2. Seal the value */
    char *token = (char *)malloc(MAX_VAL + 256);
    if (!token) { free(raw.data); return 1; }
    if (vault_seal(passphrase, key, value, token, MAX_VAL + 256) != 0) {
        fprintf(stderr, "[vault-set] seal failed\n");
        free(token); free(raw.data); return 1;
    }

    /* 3. Patch vault JSON */
    char *patched = json_patch_entry(raw.data, key, token);
    free(token); free(raw.data);
    if (!patched) { fprintf(stderr, "[vault-set] json patch failed\n"); return 1; }

    char *updated = json_set_updated(patched);
    free(patched);
    if (!updated) { fprintf(stderr, "[vault-set] timestamp update failed\n"); return 1; }

    /* 4. Base64-encode for Contents API */
    size_t jlen   = strlen(updated);
    size_t b64len = (jlen * 4 + 2) / 3 + 4;
    char *b64     = (char *)malloc(b64len);
    if (!b64) { free(updated); return 1; }
    /* Standard base64 (not url-safe) for GitHub Contents API */
    static const char B64STD[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    size_t bi = 0;
    for (size_t i = 0; i < jlen; i += 3) {
        uint32_t v  = (uint32_t)(uint8_t)updated[i] << 16;
        if (i+1 < jlen) v |= (uint32_t)(uint8_t)updated[i+1] << 8;
        if (i+2 < jlen) v |= (uint32_t)(uint8_t)updated[i+2];
        b64[bi++] = B64STD[(v >> 18) & 0x3F];
        b64[bi++] = B64STD[(v >> 12) & 0x3F];
        b64[bi++] = (i+1 < jlen) ? B64STD[(v >> 6) & 0x3F] : '=';
        b64[bi++] = (i+2 < jlen) ? B64STD[(v     ) & 0x3F] : '=';
    }
    b64[bi] = '\0';
    free(updated);

    char msg[MAX_KEY + 32];
    snprintf(msg, sizeof(msg), "vault: set %s", key);

    int rc = 0;

    /* 5a. Write target 1: monorepo (canonical) */
    char *sha1 = gh_put_vault(
        "wzbuck01/genesis-monorepo/contents/vault/vault.json",
        zac, b64, msg);
    if (sha1) { fprintf(stderr, "[vault-set] monorepo -> %s\n", sha1); free(sha1); }
    else      { fprintf(stderr, "[vault-set] monorepo write FAILED\n"); rc = 1; }

    /* 5b. Write target 2: public genesis-vault */
    char *sha2 = gh_put_vault(
        "wzbuck01/genesis-vault/contents/vault.json",
        zac, b64, msg);
    if (sha2) { fprintf(stderr, "[vault-set] genesis-vault -> %s\n", sha2); free(sha2); }
    else      { fprintf(stderr, "[vault-set] genesis-vault write failed (non-fatal)\n"); }

    /* 5c. Write target 3: ESB mirror */
    if (pvtok && pvtok[0]) {
        char *sha3 = gh_put_vault(
            "Prime-Velocity/exponential-session-bootstrap/contents/vault/vault.json",
            pvtok, b64, msg);
        if (sha3) { fprintf(stderr, "[vault-set] ESB mirror -> %s\n", sha3); free(sha3); }
        else      { fprintf(stderr, "[vault-set] ESB mirror failed (non-fatal)\n"); }
    } else {
        fprintf(stderr, "[vault-set] ESB mirror skipped — PV_TOKEN not set\n");
    }

    free(b64);
    return rc;
}


/* ── main ────────────────────────────────────────────────────────────────── */

/* Returns 1 if key is a valid POSIX shell identifier (no '/' or other invalid chars). */
static int is_valid_sh_identifier(const char *key) {
    if (!key || !*key) return 0;
    if (*key >= '0' && *key <= '9') return 0;  /* must not start with digit */
    for (const char *p = key; *p; p++) {
        if (!((*p >= 'a' && *p <= 'z') ||
              (*p >= 'A' && *p <= 'Z') ||
              (*p >= '0' && *p <= '9') ||
              *p == '_'))
            return 0;
    }
    return 1;
}

/* Prints val as a POSIX single-quoted string, escaping embedded single quotes
 * via the '\'' idiom so the output is always safe to eval/source. */
static void print_sh_escaped(const char *val) {
    putchar('\'');
    for (const char *p = val; *p; p++) {
        if (*p == '\'')
            fputs("'\\''", stdout);
        else
            putchar(*p);
    }
    putchar('\'');
}

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr,
            "Usage:\n"
            "  VAULT_PASSPHRASE=<pp> vault-decrypt <KEY>      print one value\n"
            "  VAULT_PASSPHRASE=<pp> vault-decrypt --all      print all keys\n"
            "  VAULT_PASSPHRASE=<pp> vault-decrypt --env      export KEY=val lines\n"
            "  VAULT_PASSPHRASE=<pp> vault-decrypt --list     list key names only\n"
            "  VAULT_PASSPHRASE=<pp> ZAC_TOKEN=<tok> [PV_TOKEN=<tok>] vault-decrypt --set KEY VALUE  write to all 3 vaults\n"
            "\n"
            "Build:\n"
            "  gcc -O2 -D_POSIX_C_SOURCE=200809L vault-decrypt.c sha512.c aes256gcm.c -o vault-decrypt\n"
            "\n"
            "Multi-file bootstrap (cold session):\n"
            "  for f in vault-decrypt.c sha512.c sha512.h aes256gcm.c aes256gcm.h genesis_secure.h; do\n"
            "    curl -fsSL https://raw.githubusercontent.com/wzbuck01/genesis-vault/main/$f -o $f\n"
            "  done\n"
            "  gcc -O2 -D_POSIX_C_SOURCE=200809L vault-decrypt.c sha512.c aes256gcm.c -o vault-decrypt\n");
        return 1;
    }

    const char *pp = getenv("VAULT_PASSPHRASE");
    if (!pp || !pp[0]) {
        fprintf(stderr, "[vault-decrypt] VAULT_PASSPHRASE not set\n");
        return 1;
    }

    if (load_vault(pp) != 0) return 1;

    const char *cmd = argv[1];

    if (strcmp(cmd, "--list") == 0) {
        for (int i = 0; i < g_count; i++)
            printf("%s\n", g_entries[i].key);
        return 0;
    }

    if (strcmp(cmd, "--all") == 0) {
        for (int i = 0; i < g_count; i++)
            printf("%s=%s\n", g_entries[i].key, g_entries[i].val);
        return 0;
    }

    if (strcmp(cmd, "--env") == 0) {
        for (int i = 0; i < g_count; i++) {
            if (!is_valid_sh_identifier(g_entries[i].key)) continue;
            printf("export %s=", g_entries[i].key);
            print_sh_escaped(g_entries[i].val);
            putchar('\n');
        }
        return 0;
    }

    if (strcmp(cmd, "--set") == 0) {
        if (argc < 4) {
            fprintf(stderr, "Usage: VAULT_PASSPHRASE=<pp> ZAC_TOKEN=<tok> vault-decrypt --set KEY VALUE\n");
            return 1;
        }
        return vault_set_all(pp, argv[2], argv[3]);
    }

    /* Single key lookup */
    for (int i = 0; i < g_count; i++) {
        if (strcmp(g_entries[i].key, cmd) == 0) {
            printf("%s\n", g_entries[i].val);
            return 0;
        }
    }

    fprintf(stderr, "[vault-decrypt] key not found: %s\n", cmd);
    return 1;
}
