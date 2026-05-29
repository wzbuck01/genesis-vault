/*
 * vault-decrypt.c — Genesis vault bootstrap tool
 *
 * Self-contained. No Node.js. No ZAC_TOKEN. Just gcc + OpenSSL.
 *
 * Fetches vault.json from the public genesis-vault repo, decrypts
 * entries with AES-256-GCM + PBKDF2-SHA512, and prints values to stdout.
 *
 * Build:
 *   gcc -O2 vault-decrypt.c -lssl -lcrypto -o vault-decrypt
 *
 * Usage:
 *   VAULT_PASSPHRASE=<passphrase> ./vault-decrypt <KEY>
 *   VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --all
 *   VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --env   # export KEY=val lines
 *
 * One-liner bootstrap (cold session, nothing present):
 *   curl -sL https://raw.githubusercontent.com/wzbuck01/genesis-vault/main/vault-decrypt.c \
 *     | gcc -O2 -x c - -lssl -lcrypto -o vault-decrypt
 *   VAULT_PASSPHRASE=<passphrase> ./vault-decrypt --env > .env && . .env
 *
 * Vault tiers attempted in order:
 *   1. Public genesis-vault (no token required)  -- always attempted first
 *   2. ZAC_TOKEN -> monorepo vault               -- if ZAC_TOKEN in env
 *   3. VAULT_FETCH_CREDENTIAL -> ESB vault       -- if VFC in env
 *
 * (c) 2026 Brandon Clark / Primevelocity. All Rights Reserved.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <openssl/bio.h>
#include <openssl/ssl.h>
#include <openssl/err.h>
#include <openssl/evp.h>

/* ── Constants ───────────────────────────────────────────────────────────── */

#define VAULT_URL_PUBLIC  "https://raw.githubusercontent.com/wzbuck01/genesis-vault/main/vault.json"
#define VAULT_URL_MONO    "https://api.github.com/repos/wzbuck01/genesis-monorepo/contents/vault/vault.json"
#define VAULT_URL_ESB     "https://api.github.com/repos/Prime-Velocity/exponential-session-bootstrap/contents/vault/vault.json"

#define PBKDF2_ROUNDS  210000
#define KEY_LEN        32
#define TAG_LEN        16
#define NONCE_LEN      12
#define MAX_VAULT      (1024 * 1024)
#define MAX_ENTRIES    64
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

/* ── HTTPS GET via OpenSSL BIO (no libcurl, no proxy) ────────────────────── */
/*
 * Proxy is intentionally not honoured here. Key material must not route
 * through an untrusted intermediary. See transport policy in seed_vault.h.
 */

static int https_get(const char *url, const char *token,
                     const char *accept, buf_t *out) {
    /* Parse URL */
    char host[512] = {0}, path[1024] = {0};
    int  port = 443;
    const char *p = url;
    if (strncmp(p, "https://", 8) == 0) p += 8;
    const char *sl = strchr(p, '/');
    size_t hl = sl ? (size_t)(sl - p) : strlen(p);
    if (hl >= sizeof(host)) hl = sizeof(host) - 1;
    memcpy(host, p, hl); host[hl] = '\0';
    char *colon = strchr(host, ':');
    if (colon) { port = atoi(colon + 1); *colon = '\0'; }
    strncpy(path, sl ? sl : "/", sizeof(path) - 1);

    SSL_CTX *ctx = SSL_CTX_new(TLS_client_method());
    if (!ctx) return -1;
    SSL_CTX_set_default_verify_paths(ctx);
    SSL_CTX_set_verify(ctx, SSL_VERIFY_PEER, NULL);

    BIO *bio = BIO_new_ssl_connect(ctx);
    if (!bio) { SSL_CTX_free(ctx); return -1; }
    SSL *ssl = NULL; BIO_get_ssl(bio, &ssl);
    SSL_set_mode(ssl, SSL_MODE_AUTO_RETRY);
    SSL_set_tlsext_host_name(ssl, host);

    char addr[600]; snprintf(addr, sizeof(addr), "%s:%d", host, port);
    BIO_set_conn_hostname(bio, addr);

    if (BIO_do_connect(bio) <= 0 || BIO_do_handshake(bio) <= 0) {
        ERR_clear_error();
        BIO_free_all(bio); SSL_CTX_free(ctx);
        return -1;
    }

    char req[2048];
    snprintf(req, sizeof(req),
        "GET %s HTTP/1.1\r\nHost: %s\r\n"
        "User-Agent: genesis/vault-decrypt\r\nConnection: close\r\n"
        "%s%s%s"
        "%s%s%s"
        "\r\n",
        path, host,
        token  && token[0]  ? "Authorization: token " : "",
        token  && token[0]  ? token  : "",
        token  && token[0]  ? "\r\n" : "",
        accept && accept[0] ? "Accept: "              : "",
        accept && accept[0] ? accept : "",
        accept && accept[0] ? "\r\n" : "");
    BIO_write(bio, req, (int)strlen(req));

    buf_t raw = { malloc(65536), 0, 65536 };
    char rbuf[8192]; int n;
    while ((n = BIO_read(bio, rbuf, sizeof(rbuf))) > 0)
        buf_append(&raw, rbuf, (size_t)n);
    BIO_free_all(bio); SSL_CTX_free(ctx);

    long code = 0;
    sscanf(raw.data, "HTTP/%*s %ld", &code);
    char *body = strstr(raw.data, "\r\n\r\n");
    if (!body || code != 200) {
        fprintf(stderr, "[vault-decrypt] HTTP %ld from %s\n", code, url);
        free(raw.data); return -1;
    }
    body += 4;
    buf_append(out, body, raw.len - (size_t)(body - raw.data));
    free(raw.data);
    return 0;
}

/* ── Base64url decode ────────────────────────────────────────────────────── */

static int b64url_decode(const char *in, size_t inlen,
                         unsigned char *out, size_t *outlen) {
    char *buf = malloc(inlen + 4); if (!buf) return -1;
    for (size_t i = 0; i < inlen; i++) {
        char c = in[i];
        if      (c == '-') c = '+';
        else if (c == '_') c = '/';
        buf[i] = c;
    }
    size_t pad = inlen;
    while (pad % 4) buf[pad++] = '=';
    buf[pad] = '\0';

    BIO *b64 = BIO_new(BIO_f_base64());
    BIO *mem = BIO_new_mem_buf(buf, (int)pad);
    BIO_set_flags(b64, BIO_FLAGS_BASE64_NO_NL);
    b64 = BIO_push(b64, mem);
    int r = BIO_read(b64, out, (int)pad);
    BIO_free_all(b64); free(buf);
    if (r < 0) return -1;
    *outlen = (size_t)r;
    return 0;
}

/* ── PBKDF2-SHA512 + AES-256-GCM decrypt ────────────────────────────────── */

static int decrypt_entry(const char *passphrase, const char *label,
                         const char *b64token, char *plain, size_t *plen) {
    /* strip P⟨ prefix and ⟩ suffix if present — handle both raw UTF-8 and
     * JSON-escaped \\u27e8 / \\u27e9 forms */
    const char *b64 = b64token;
    size_t b64len   = strlen(b64token);

    /* raw UTF-8: P + E2 9F A8 ... E2 9F A9 */
    if (b64len > 7 && b64[0] == 'P' &&
        (unsigned char)b64[1] == 0xE2 && (unsigned char)b64[2] == 0x9F &&
        (unsigned char)b64[3] == 0xA8) {
        b64 += 4;
        b64len -= 4;
        /* strip trailing E2 9F A9 */
        if (b64len >= 3 &&
            (unsigned char)b64[b64len-3] == 0xE2 &&
            (unsigned char)b64[b64len-2] == 0x9F &&
            (unsigned char)b64[b64len-1] == 0xA9)
            b64len -= 3;
    }
    /* JSON-escaped: P\u27e8...\u27e9 */
    else if (b64len > 13 && b64[0] == 'P' && b64[1] == '\\' &&
             b64[2] == 'u' && b64[3] == '2' && b64[4] == '7' &&
             b64[5] == 'e' && b64[6] == '8') {
        b64 += 7;
        b64len -= 7;
        if (b64len >= 6 && b64[b64len-6] == '\\' &&
            b64[b64len-5] == 'u' && b64[b64len-4] == '2' &&
            b64[b64len-3] == '7' && b64[b64len-2] == 'e' &&
            b64[b64len-1] == '9')
            b64len -= 6;
    }

    unsigned char blob[MAX_VAL + 64]; size_t blen = 0;
    /* work on a NUL-terminated copy of the b64 slice */
    char *b64copy = malloc(b64len + 1);
    if (!b64copy) return -1;
    memcpy(b64copy, b64, b64len); b64copy[b64len] = '\0';
    int rc = b64url_decode(b64copy, b64len, blob, &blen);
    free(b64copy);
    if (rc != 0) return -1;
    if (blen < (size_t)(NONCE_LEN + TAG_LEN + 1)) return -1;

    char salt[256];
    snprintf(salt, sizeof(salt), "pv-vault-v1:%s", label);
    unsigned char key[KEY_LEN];
    if (!PKCS5_PBKDF2_HMAC(passphrase, (int)strlen(passphrase),
                            (unsigned char *)salt, (int)strlen(salt),
                            PBKDF2_ROUNDS, EVP_sha512(), KEY_LEN, key))
        return -1;

    unsigned char *nonce  = blob;
    unsigned char *ct     = blob + NONCE_LEN;
    size_t         ct_len = blen - NONCE_LEN - TAG_LEN;
    unsigned char *tag    = blob + blen - TAG_LEN;

    EVP_CIPHER_CTX *dc = EVP_CIPHER_CTX_new(); if (!dc) return -1;
    int ok = 0, l = 0, fl = 0;
    ok  = EVP_DecryptInit_ex(dc, EVP_aes_256_gcm(), NULL, NULL, NULL);
    ok &= EVP_CIPHER_CTX_ctrl(dc, EVP_CTRL_GCM_SET_IVLEN, NONCE_LEN, NULL);
    ok &= EVP_DecryptInit_ex(dc, NULL, NULL, key, nonce);
    ok &= EVP_DecryptUpdate(dc, (unsigned char *)plain, &l, ct, (int)ct_len);
    ok &= EVP_CIPHER_CTX_ctrl(dc, EVP_CTRL_GCM_SET_TAG, TAG_LEN, tag);
    ok &= EVP_DecryptFinal_ex(dc, (unsigned char *)plain + l, &fl);
    EVP_CIPHER_CTX_free(dc);
    if (!ok) return -1;

    *plen = (size_t)(l + fl);
    plain[*plen] = '\0';
    return 0;
}

/* ── JSON vault walker ───────────────────────────────────────────────────── */
/*
 * Walks the vault JSON looking for sealed P⟨⟩ token values.
 * Handles both raw UTF-8 and JSON-escaped \u27e8/\u27e9 bracket forms.
 * Tolerates the GitHub Contents API wrapper (base64-encoded "content" field)
 * and raw JSON (public raw.githubusercontent.com response).
 */

static int walk_vault(const char *src, const char *passphrase) {
    static char plain[MAX_VAL + 1];
    const char *p = src;
    int count = 0;

    /* If this is a GitHub Contents API response, unwrap the base64 content */
    const char *content_key = "\"content\":\"";
    char *ck = strstr((char *)src, content_key);
    if (ck) {
        /* Extract and decode the base64 blob */
        ck += strlen(content_key);
        char *end = strchr(ck, '"');
        if (end && end > ck) {
            size_t raw_len = (size_t)(end - ck);
            /* Remove embedded \n from GitHub's line-wrapped base64 */
            char *clean = malloc(raw_len + 1);
            if (!clean) return -1;
            size_t cl = 0;
            for (size_t i = 0; i < raw_len; i++)
                if (ck[i] != '\\' && ck[i] != 'n') clean[cl++] = ck[i];
                else if (ck[i] == 'n' && i > 0 && ck[i-1] == '\\') cl--;
            clean[cl] = '\0';

            /* Standard base64 (not base64url) — use OpenSSL BIO directly */
            unsigned char *decoded = malloc(cl + 4);
            if (!decoded) { free(clean); return -1; }
            BIO *b64bio = BIO_new(BIO_f_base64());
            BIO *membio = BIO_new_mem_buf(clean, (int)cl);
            BIO_set_flags(b64bio, BIO_FLAGS_BASE64_NO_NL);
            b64bio = BIO_push(b64bio, membio);
            int dlen = BIO_read(b64bio, decoded, (int)cl);
            BIO_free_all(b64bio); free(clean);
            if (dlen > 0) {
                decoded[dlen] = '\0';
                count = walk_vault((char *)decoded, passphrase);
                free(decoded);
                return count;
            }
            free(decoded);
        }
    }

    /* Walk raw vault JSON */
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
            /* Find the end of this JSON string value */
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

/* ── Load vault from all available tiers ─────────────────────────────────── */

static int load_vault(const char *passphrase) {
    const char *zac = getenv("ZAC_TOKEN");
    const char *vfc = getenv("VAULT_FETCH_CREDENTIAL");

    buf_t buf = { malloc(131072), 0, 131072 };
    if (!buf.data) return -1;
    int ok = 0;

    /* Tier 1: public genesis-vault — no token needed, always try first */
    fprintf(stderr, "[vault-decrypt] trying public vault...\n");
    ok = (https_get(VAULT_URL_PUBLIC, NULL, NULL, &buf) == 0);
    if (!ok) {
        /* Tier 2: monorepo via ZAC (if available) */
        if (zac && zac[0]) {
            fprintf(stderr, "[vault-decrypt] trying monorepo vault (ZAC)...\n");
            buf.len = 0;
            ok = (https_get(VAULT_URL_MONO, zac,
                            "application/vnd.github.raw+json", &buf) == 0);
        }
    }
    if (!ok) {
        /* Tier 3: ESB via VFC (if available) */
        if (vfc && vfc[0]) {
            fprintf(stderr, "[vault-decrypt] trying ESB vault (VFC)...\n");
            buf.len = 0;
            ok = (https_get(VAULT_URL_ESB, vfc,
                            "application/vnd.github.raw+json", &buf) == 0);
        }
    }

    if (!ok || buf.len == 0) {
        fprintf(stderr, "[vault-decrypt] all vault tiers failed\n");
        free(buf.data); return -1;
    }

    fprintf(stderr, "[vault-decrypt] fetched %zu bytes, decrypting...\n", buf.len);
    int n = walk_vault(buf.data, passphrase);
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
            "  gcc -O2 vault-decrypt.c -lssl -lcrypto -o vault-decrypt\n"
            "\n"
            "One-liner (cold session):\n"
            "  curl -sL https://raw.githubusercontent.com/wzbuck01/genesis-vault"
                        "/main/vault-decrypt.c \\\n"
            "    | gcc -O2 -x c - -lssl -lcrypto -o vault-decrypt\n");
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
