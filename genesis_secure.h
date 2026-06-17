/*
 * genesis_secure.h — secure memory operations
 *
 * No external dependencies. Portable C99.
 *
 * Provides secure_zero() which clears memory in a way that is
 * resistant to compiler dead-store elimination. Use for:
 *   - PBKDF2 derived keys after use
 *   - X25519 private scalars
 *   - ML-KEM shared secrets and private keys
 *   - AES round key arrays
 *   - Any buffer containing key material
 *
 * (c) 2026 Brandon Clark / Genesis Systems. All Rights Reserved.
 */

#ifndef GENESIS_SECURE_H
#define GENESIS_SECURE_H

#include <stddef.h>
#include <string.h>

#if defined(_WIN32)
#  include <windows.h>   /* SecureZeroMemory */
#endif

/*
 * secure_zero -- zero memory in a compiler-resistant way.
 *
 * Standard memset() calls on key buffers are frequently eliminated by
 * optimizing compilers as "dead stores" when the buffer goes out of scope
 * immediately afterward.  This function defeats that optimization.
 *
 * Three platform paths, in priority order:
 *
 *   1. explicit_bzero (glibc >= 2.25, musl, OpenBSD, FreeBSD, macOS 10.12+)
 *      Guaranteed not to be optimized away by any conforming implementation.
 *
 *   2. memset_s (C11 Annex K, Windows, MSVC)
 *      Standardized; compliant implementations must not elide it.
 *
 *   3. Volatile-pointer memset fallback (portable C99)
 *      Write via a volatile function pointer.  Not standardized, but works
 *      on all known compilers that respect volatile semantics.
 *      (A sufficiently aggressive link-time optimizer could in theory
 *      still elide this, but no production compiler currently does.)
 *
 * Usage:
 *   uint8_t key[32];
 *   // ... use key ...
 *   secure_zero(key, sizeof(key));
 */
static inline void secure_zero(void *ptr, size_t len) {
    if (len == 0) return;

#if defined(_WIN32)
    /* Windows: SecureZeroMemory is guaranteed not to be optimized away */
    SecureZeroMemory(ptr, len);

#elif defined(__STDC_LIB_EXT1__)
    /* C11 Annex K: memset_s cannot be optimized away */
    memset_s(ptr, len, 0, len);

#elif defined(__OpenBSD__) || defined(__FreeBSD__) || defined(__NetBSD__)
    /* BSD: explicit_bzero is always available */
    explicit_bzero(ptr, len);

#elif defined(__GLIBC__) && (__GLIBC__ > 2 || (__GLIBC__ == 2 && __GLIBC_MINOR__ >= 25))
    /* glibc >= 2.25: explicit_bzero available under _GNU_SOURCE or _DEFAULT_SOURCE */
    /* Use volatile wrapper instead to avoid feature macro conflict with _POSIX_C_SOURCE */
    static void *(*const volatile vz)(void *, int, size_t) = memset;
    vz(ptr, 0, len);

#else
    /* Portable fallback: volatile function pointer prevents dead-store elimination
     * on all known production compilers (GCC, Clang, MSVC, ICC, TCC). */
    static void *(*const volatile vz)(void *, int, size_t) = memset;
    vz(ptr, 0, len);
#endif
}

#endif /* GENESIS_SECURE_H */
