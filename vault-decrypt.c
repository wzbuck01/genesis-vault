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

/* ── main ────────────────────────────────────────────────────────────────── */

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr,
            "Usage:\n"
            "  VAULT_PASSPHRASE=<pp> vault-decrypt <KEY>      print one value\n"
            "  VAULT_PASSPHRASE=<pp> vault-decrypt --all      print all keys\n"
            "  VAULT_PASSPHRASE=<pp> vault-decrypt --env      export KEY=val lines\n"
            "  VAULT_PASSPHRASE=<pp> vault-decrypt --list     list key names only\n"
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
        for (int i = 0; i < g_count; i++)
            printf("export %s='%s'\n", g_entries[i].key, g_entries[i].val);
        return 0;
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
