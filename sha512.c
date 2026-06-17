/*
 * sha512.c — public domain SHA-512, HMAC-SHA512, PBKDF2-SHA512
 *
 * No external dependencies.  musl/uclibc compatible.
 * SHA-512 derived from Brad Conte's public domain crypto-algorithms.
 * PBKDF2 follows RFC 2898 §5.2.
 *
 * (c) 2026 Brandon Clark / Genesis Systems. All Rights Reserved.
 */

#include "sha512.h"
#include "genesis_secure.h"
#include <string.h>

/* ── SHA-512 ─────────────────────────────────────────────────────────────── */

#define ROTR64(x,n) (((x) >> (n)) | ((x) << (64-(n))))

#define CH64(x,y,z)  (((x)&(y))^(~(x)&(z)))
#define MAJ64(x,y,z) (((x)&(y))^((x)&(z))^((y)&(z)))
#define EP0_64(x) (ROTR64(x,28) ^ ROTR64(x,34) ^ ROTR64(x,39))
#define EP1_64(x) (ROTR64(x,14) ^ ROTR64(x,18) ^ ROTR64(x,41))
#define SIG0_64(x)(ROTR64(x, 1) ^ ROTR64(x, 8) ^ ((x) >> 7))
#define SIG1_64(x)(ROTR64(x,19) ^ ROTR64(x,61) ^ ((x) >> 6))

static const uint64_t K512[80] = {
    0x428a2f98d728ae22ULL, 0x7137449123ef65cdULL,
    0xb5c0fbcfec4d3b2fULL, 0xe9b5dba58189dbbcULL,
    0x3956c25bf348b538ULL, 0x59f111f1b605d019ULL,
    0x923f82a4af194f9bULL, 0xab1c5ed5da6d8118ULL,
    0xd807aa98a3030242ULL, 0x12835b0145706fbeULL,
    0x243185be4ee4b28cULL, 0x550c7dc3d5ffb4e2ULL,
    0x72be5d74f27b896fULL, 0x80deb1fe3b1696b1ULL,
    0x9bdc06a725c71235ULL, 0xc19bf174cf692694ULL,
    0xe49b69c19ef14ad2ULL, 0xefbe4786384f25e3ULL,
    0x0fc19dc68b8cd5b5ULL, 0x240ca1cc77ac9c65ULL,
    0x2de92c6f592b0275ULL, 0x4a7484aa6ea6e483ULL,
    0x5cb0a9dcbd41fbd4ULL, 0x76f988da831153b5ULL,
    0x983e5152ee66dfabULL, 0xa831c66d2db43210ULL,
    0xb00327c898fb213fULL, 0xbf597fc7beef0ee4ULL,
    0xc6e00bf33da88fc2ULL, 0xd5a79147930aa725ULL,
    0x06ca6351e003826fULL, 0x142929670a0e6e70ULL,
    0x27b70a8546d22ffcULL, 0x2e1b21385c26c926ULL,
    0x4d2c6dfc5ac42aedULL, 0x53380d139d95b3dfULL,
    0x650a73548baf63deULL, 0x766a0abb3c77b2a8ULL,
    0x81c2c92e47edaee6ULL, 0x92722c851482353bULL,
    0xa2bfe8a14cf10364ULL, 0xa81a664bbc423001ULL,
    0xc24b8b70d0f89791ULL, 0xc76c51a30654be30ULL,
    0xd192e819d6ef5218ULL, 0xd69906245565a910ULL,
    0xf40e35855771202aULL, 0x106aa07032bbd1b8ULL,
    0x19a4c116b8d2d0c8ULL, 0x1e376c085141ab53ULL,
    0x2748774cdf8eeb99ULL, 0x34b0bcb5e19b48a8ULL,
    0x391c0cb3c5c95a63ULL, 0x4ed8aa4ae3418acbULL,
    0x5b9cca4f7763e373ULL, 0x682e6ff3d6b2b8a3ULL,
    0x748f82ee5defb2fcULL, 0x78a5636f43172f60ULL,
    0x84c87814a1f0ab72ULL, 0x8cc702081a6439ecULL,
    0x90befffa23631e28ULL, 0xa4506cebde82bde9ULL,
    0xbef9a3f7b2c67915ULL, 0xc67178f2e372532bULL,
    0xca273eceea26619cULL, 0xd186b8c721c0c207ULL,
    0xeada7dd6cde0eb1eULL, 0xf57d4f7fee6ed178ULL,
    0x06f067aa72176fbaULL, 0x0a637dc5a2c898a6ULL,
    0x113f9804bef90daeULL, 0x1b710b35131c471bULL,
    0x28db77f523047d84ULL, 0x32caab7b40c72493ULL,
    0x3c9ebe0a15c9bebcULL, 0x431d67c49c100d4cULL,
    0x4cc5d4becb3e42b6ULL, 0x597f299cfc657e2aULL,
    0x5fcb6fab3ad6faecULL, 0x6c44198c4a475817ULL,
};

void sha512_init(sha512_ctx_t *ctx) {
    ctx->datalen   = 0;
    ctx->bitlen_hi = 0;
    ctx->bitlen_lo = 0;
    ctx->state[0]  = 0x6a09e667f3bcc908ULL;
    ctx->state[1]  = 0xbb67ae8584caa73bULL;
    ctx->state[2]  = 0x3c6ef372fe94f82bULL;
    ctx->state[3]  = 0xa54ff53a5f1d36f1ULL;
    ctx->state[4]  = 0x510e527fade682d1ULL;
    ctx->state[5]  = 0x9b05688c2b3e6c1fULL;
    ctx->state[6]  = 0x1f83d9abfb41bd6bULL;
    ctx->state[7]  = 0x5be0cd19137e2179ULL;
}

static void sha512_transform(sha512_ctx_t *ctx, const uint8_t *data) {
    uint64_t a,b,c,d,e,f,g,h,t1,t2,m[80];
    int i;

    for (i = 0; i < 16; i++) {
        m[i] = ((uint64_t)data[i*8+0] << 56) | ((uint64_t)data[i*8+1] << 48)
             | ((uint64_t)data[i*8+2] << 40) | ((uint64_t)data[i*8+3] << 32)
             | ((uint64_t)data[i*8+4] << 24) | ((uint64_t)data[i*8+5] << 16)
             | ((uint64_t)data[i*8+6] <<  8) | ((uint64_t)data[i*8+7]);
    }
    for (; i < 80; i++)
        m[i] = SIG1_64(m[i-2]) + m[i-7] + SIG0_64(m[i-15]) + m[i-16];

    a=ctx->state[0]; b=ctx->state[1]; c=ctx->state[2]; d=ctx->state[3];
    e=ctx->state[4]; f=ctx->state[5]; g=ctx->state[6]; h=ctx->state[7];

    for (i = 0; i < 80; i++) {
        t1 = h + EP1_64(e) + CH64(e,f,g) + K512[i] + m[i];
        t2 = EP0_64(a) + MAJ64(a,b,c);
        h=g; g=f; f=e; e=d+t1;
        d=c; c=b; b=a; a=t1+t2;
    }

    ctx->state[0]+=a; ctx->state[1]+=b; ctx->state[2]+=c; ctx->state[3]+=d;
    ctx->state[4]+=e; ctx->state[5]+=f; ctx->state[6]+=g; ctx->state[7]+=h;
}

void sha512_update(sha512_ctx_t *ctx, const uint8_t *data, size_t len) {
    for (size_t i = 0; i < len; i++) {
        ctx->data[ctx->datalen++] = data[i];
        if (ctx->datalen == 128) {
            sha512_transform(ctx, ctx->data);
            /* carry into 128-bit bit counter */
            ctx->bitlen_lo += 1024;
            if (ctx->bitlen_lo == 0) ctx->bitlen_hi++;
            ctx->datalen = 0;
        }
    }
}

void sha512_final(sha512_ctx_t *ctx, uint8_t hash[64]) {
    uint32_t i = ctx->datalen;

    /* Padding */
    if (ctx->datalen < 112) {
        ctx->data[i++] = 0x80;
        while (i < 112) ctx->data[i++] = 0x00;
    } else {
        ctx->data[i++] = 0x80;
        while (i < 128) ctx->data[i++] = 0x00;
        sha512_transform(ctx, ctx->data);
        memset(ctx->data, 0, 112);
    }

    /* Append bit length as 128-bit big-endian */
    uint64_t add_bits = (uint64_t)ctx->datalen * 8;
    uint64_t lo = ctx->bitlen_lo + add_bits;
    uint64_t hi = ctx->bitlen_hi + (lo < add_bits ? 1 : 0);

    /* high 64 bits */
    ctx->data[112] = (uint8_t)(hi >> 56); ctx->data[113] = (uint8_t)(hi >> 48);
    ctx->data[114] = (uint8_t)(hi >> 40); ctx->data[115] = (uint8_t)(hi >> 32);
    ctx->data[116] = (uint8_t)(hi >> 24); ctx->data[117] = (uint8_t)(hi >> 16);
    ctx->data[118] = (uint8_t)(hi >>  8); ctx->data[119] = (uint8_t)(hi);
    /* low 64 bits */
    ctx->data[120] = (uint8_t)(lo >> 56); ctx->data[121] = (uint8_t)(lo >> 48);
    ctx->data[122] = (uint8_t)(lo >> 40); ctx->data[123] = (uint8_t)(lo >> 32);
    ctx->data[124] = (uint8_t)(lo >> 24); ctx->data[125] = (uint8_t)(lo >> 16);
    ctx->data[126] = (uint8_t)(lo >>  8); ctx->data[127] = (uint8_t)(lo);
    sha512_transform(ctx, ctx->data);

    /* Output: 8 x 64-bit words, big-endian */
    for (i = 0; i < 8; i++) {
        hash[i*8+0] = (uint8_t)(ctx->state[i] >> 56);
        hash[i*8+1] = (uint8_t)(ctx->state[i] >> 48);
        hash[i*8+2] = (uint8_t)(ctx->state[i] >> 40);
        hash[i*8+3] = (uint8_t)(ctx->state[i] >> 32);
        hash[i*8+4] = (uint8_t)(ctx->state[i] >> 24);
        hash[i*8+5] = (uint8_t)(ctx->state[i] >> 16);
        hash[i*8+6] = (uint8_t)(ctx->state[i] >>  8);
        hash[i*8+7] = (uint8_t)(ctx->state[i]);
    }
}

/* ── HMAC-SHA512 ─────────────────────────────────────────────────────────── */

void hmac_sha512(const uint8_t *key, size_t key_len,
                 const uint8_t *msg, size_t msg_len,
                 uint8_t mac[64]) {
    uint8_t k_ipad[128], k_opad[128];
    uint8_t tk[64];
    sha512_ctx_t ctx;

    if (key_len > 128) {
        sha512_init(&ctx);
        sha512_update(&ctx, key, key_len);
        sha512_final(&ctx, tk);
        key     = tk;
        key_len = 64;
    }

    memset(k_ipad, 0x36, 128);
    memset(k_opad, 0x5c, 128);
    for (size_t i = 0; i < key_len; i++) {
        k_ipad[i] ^= key[i];
        k_opad[i] ^= key[i];
    }

    uint8_t inner[64];
    sha512_init(&ctx);
    sha512_update(&ctx, k_ipad, 128);
    sha512_update(&ctx, msg, msg_len);
    sha512_final(&ctx, inner);

    sha512_init(&ctx);
    sha512_update(&ctx, k_opad, 128);
    sha512_update(&ctx, inner, 64);
    sha512_final(&ctx, mac);
    secure_zero(k_ipad, sizeof(k_ipad));
    secure_zero(k_opad, sizeof(k_opad));
    secure_zero(inner, sizeof(inner));
}

/* ── PBKDF2-SHA512 (RFC 2898 §5.2, single block) ────────────────────────── */
/*
 * Produces up to 64 bytes (one PRF block).  Vault use: out_len = 32.
 *
 * T_1 = PRF(password, salt || 0x00000001)
 * out = T_1[0..out_len-1]
 *
 * where PRF = HMAC-SHA512.
 */
void pbkdf2_sha512(const uint8_t *password, size_t pwd_len,
                   const uint8_t *salt,     size_t salt_len,
                   uint32_t       rounds,
                   uint8_t       *out,      size_t out_len) {
    /* U_1 = PRF(password, salt || INT(1)) */
    uint8_t salt_blk[4096];   /* salt + 4-byte counter */
    uint8_t u[64], t[64];

    if (salt_len > sizeof(salt_blk) - 4) return;  /* salt too long */
    if (out_len  > 64)                    return;  /* single block only */

    memcpy(salt_blk, salt, salt_len);
    /* Block index 1, big-endian */
    salt_blk[salt_len+0] = 0;
    salt_blk[salt_len+1] = 0;
    salt_blk[salt_len+2] = 0;
    salt_blk[salt_len+3] = 1;

    hmac_sha512(password, pwd_len, salt_blk, salt_len + 4, u);
    memcpy(t, u, 64);

    for (uint32_t c = 1; c < rounds; c++) {
        hmac_sha512(password, pwd_len, u, 64, u);
        for (int i = 0; i < 64; i++) t[i] ^= u[i];
    }

    memcpy(out, t, out_len);
    /* Zero intermediate key material */
    secure_zero(u, sizeof(u));
    secure_zero(t, sizeof(t));
    secure_zero(salt_blk, sizeof(salt_blk));
}
