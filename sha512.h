/*
 * sha512.h — public domain SHA-512, HMAC-SHA512, PBKDF2-SHA512
 *
 * No external dependencies.  musl/uclibc compatible.
 * Derived from public domain reference implementations.
 *
 * (c) 2026 Brandon Clark / Genesis Systems. All Rights Reserved.
 */

#ifndef GENESIS_SHA512_H
#define GENESIS_SHA512_H

#include <stdint.h>
#include <stddef.h>

typedef struct {
    uint8_t  data[128];
    uint32_t datalen;
    uint64_t bitlen_hi;   /* high 64 bits of message bit length */
    uint64_t bitlen_lo;   /* low  64 bits of message bit length */
    uint64_t state[8];
} sha512_ctx_t;

void sha512_init  (sha512_ctx_t *ctx);
void sha512_update(sha512_ctx_t *ctx, const uint8_t *data, size_t len);
void sha512_final (sha512_ctx_t *ctx, uint8_t hash[64]);

/* HMAC-SHA512: mac[64] = HMAC(key, msg) */
void hmac_sha512(const uint8_t *key, size_t key_len,
                 const uint8_t *msg, size_t msg_len,
                 uint8_t mac[64]);

/*
 * PBKDF2-SHA512: out[out_len] = PBKDF2(password, salt, rounds, out_len)
 *
 * out_len may be up to 64 bytes (one PRF output block).
 * For vault use: password = passphrase, salt = label string,
 *                rounds = 210000, out_len = 32.
 */
void pbkdf2_sha512(const uint8_t *password, size_t pwd_len,
                   const uint8_t *salt,     size_t salt_len,
                   uint32_t       rounds,
                   uint8_t       *out,      size_t out_len);

#endif /* GENESIS_SHA512_H */
