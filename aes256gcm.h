/*
 * aes256gcm.h — public domain AES-256-GCM encrypt/decrypt
 *
 * No external dependencies.  Portable C99.
 *
 * AES-256: FIPS 197 (256-bit key, 14 rounds).
 * GCM:     NIST SP 800-38D (GHASH + AES-CTR).
 *
 * Decryption-only surface exposed here (vault use case).
 * Returns 0 on success, -1 on authentication failure.
 *
 * (c) 2026 Brandon Clark / Genesis Systems. All Rights Reserved.
 */

#ifndef GENESIS_AES256GCM_H
#define GENESIS_AES256GCM_H

#include <stdint.h>
#include <stddef.h>

/*
 * aes256gcm_decrypt -- authenticated decryption
 *
 *   key      [32]      AES-256 key
 *   nonce    [nonce_len] IV/nonce (12 bytes for standard GCM)
 *   nonce_len           length of nonce in bytes
 *   ct       [ct_len]  ciphertext
 *   ct_len              length of ciphertext
 *   aad      [aad_len] additional authenticated data (may be NULL)
 *   aad_len             length of AAD
 *   tag      [16]      authentication tag
 *   pt       [ct_len]  output plaintext buffer (must be >= ct_len)
 *
 * Returns  0 on success (tag verified, pt populated).
 * Returns -1 on authentication failure (tag mismatch; pt is zeroed).
 */
int aes256gcm_decrypt(const uint8_t *key,
                      const uint8_t *nonce,    size_t nonce_len,
                      const uint8_t *ct,       size_t ct_len,
                      const uint8_t *aad,      size_t aad_len,
                      const uint8_t  tag[16],
                      uint8_t       *pt);

/*
 * aes256gcm_encrypt -- authenticated encryption
 *
 *   key      [32]      AES-256 key
 *   nonce    [nonce_len] IV/nonce (12 bytes for standard GCM)
 *   nonce_len           length of nonce in bytes
 *   pt       [pt_len]  plaintext
 *   pt_len              length of plaintext
 *   aad      [aad_len] additional authenticated data (may be NULL)
 *   aad_len             length of AAD
 *   ct       [pt_len]  output ciphertext buffer (must be >= pt_len)
 *   tag      [16]      output authentication tag
 */
void aes256gcm_encrypt(const uint8_t *key,
                       const uint8_t *nonce,    size_t nonce_len,
                       const uint8_t *pt,       size_t pt_len,
                       const uint8_t *aad,      size_t aad_len,
                       uint8_t       *ct,
                       uint8_t        tag[16]);

#endif /* GENESIS_AES256GCM_H */
