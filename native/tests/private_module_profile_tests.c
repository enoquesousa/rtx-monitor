#include "private_module_profile.h"

#include <stdint.h>
#include <stdio.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#include <bcrypt.h>
#endif

static int check(int condition, const char *message)
{
    if (condition) {
        return 0;
    }
    (void)fprintf(stderr, "FAILED: %s\n", message);
    return 1;
}

#if defined(_WIN32)
/* This data is part of this test executable's image. No NVIDIA library is
 * loaded or called: the fixture tests module binding, not a hardware profile.
 */
static const unsigned char module_anchor[2] = {0x3aU, 0xc7U};

static int fixture_sha256(const wchar_t *path, char output[65])
{
    BCRYPT_ALG_HANDLE algorithm = NULL;
    HANDLE file = INVALID_HANDLE_VALUE;
    unsigned char *bytes = NULL;
    unsigned char digest[32];
    LARGE_INTEGER file_size;
    DWORD read_size = 0U;
    DWORD length;
    uint32_t i;
    int success = 0;
    static const char hex[] = "0123456789abcdef";

    file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE || !GetFileSizeEx(file, &file_size) ||
        file_size.QuadPart <= 0 || file_size.QuadPart > 16 * 1024 * 1024) {
        goto cleanup;
    }
    length = (DWORD)file_size.QuadPart;
    bytes = (unsigned char *)HeapAlloc(GetProcessHeap(), 0U, length);
    if (bytes == NULL || !ReadFile(file, bytes, length, &read_size, NULL) || read_size != length ||
        BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0U) < 0 ||
        BCryptHash(algorithm, NULL, 0U, bytes, length, digest, (ULONG)sizeof(digest)) < 0) {
        goto cleanup;
    }
    for (i = 0U; i < 32U; ++i) {
        output[i * 2U] = hex[digest[i] >> 4U];
        output[i * 2U + 1U] = hex[digest[i] & 0xfU];
    }
    output[64] = '\0';
    success = 1;
cleanup:
    if (algorithm != NULL) {
        (void)BCryptCloseAlgorithmProvider(algorithm, 0U);
    }
    if (bytes != NULL) {
        (void)HeapFree(GetProcessHeap(), 0U, bytes);
    }
    if (file != INVALID_HANDLE_VALUE) {
        (void)CloseHandle(file);
    }
    return success;
}
#endif

int main(void)
{
    int failures = 0;
#if defined(_WIN32)
    HMODULE module = NULL;
    wchar_t path[32768];
    const void *pointer = &module_anchor[0];
    DWORD path_length;
    uint32_t rva;
    char sha256[65] = {0};
    char mutated_hash[65];
    char short_hash[64];
    char long_hash[66];
    void *heap_pointer;

    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)pointer, &module) ||
        module == NULL || module != GetModuleHandleW(NULL)) {
        return check(0, "fixture address belongs to the test executable image");
    }
    path_length = GetModuleFileNameW(module, path, (DWORD)(sizeof(path) / sizeof(path[0])));
    if (path_length == 0U || path_length >= (DWORD)(sizeof(path) / sizeof(path[0])) ||
        (uintptr_t)pointer < (uintptr_t)(const void *)module ||
        (uintptr_t)pointer - (uintptr_t)(const void *)module > UINT32_MAX) {
        return check(0, "fixture module has a complete path and a representable RVA");
    }
    path[path_length] = L'\0';
    rva = (uint32_t)((uintptr_t)pointer - (uintptr_t)(const void *)module);
    if (!fixture_sha256(path, sha256)) {
        return check(0, "independent BCrypt digest of the actual test executable");
    }
    failures += check(rtxmon_private_module_pointer_matches(pointer, rva, sha256),
        "actual executable module, actual RVA and independently calculated SHA256 are accepted");

    (void)memcpy(mutated_hash, sha256, sizeof(mutated_hash));
    mutated_hash[0] = mutated_hash[0] == '0' ? '1' : '0';
    failures += check(!rtxmon_private_module_pointer_matches(pointer, rva, mutated_hash),
        "one changed digest nibble fails with the correct pointer and RVA");
    failures += check(!rtxmon_private_module_pointer_matches(pointer, rva ^ 1U, sha256),
        "one changed RVA bit fails with the correct module hash and pointer");
    failures += check(!rtxmon_private_module_pointer_matches(&module_anchor[1], rva, sha256),
        "a different address in the same module cannot reuse the approved RVA");
    failures += check(!rtxmon_private_module_pointer_matches(NULL, rva, sha256),
        "a missing pointer fails independently of a valid hash and RVA");
    failures += check(!rtxmon_private_module_pointer_matches(pointer, rva, NULL),
        "a missing digest fails independently of a valid pointer and RVA");
    failures += check(!rtxmon_private_module_pointer_matches(pointer, rva, ""),
        "an empty digest is rejected");
    (void)memcpy(short_hash, sha256, sizeof(short_hash));
    short_hash[63] = '\0';
    failures += check(!rtxmon_private_module_pointer_matches(pointer, rva, short_hash),
        "a 63-character digest is rejected");
    (void)memcpy(long_hash, sha256, sizeof(sha256));
    long_hash[64] = '0';
    long_hash[65] = '\0';
    failures += check(!rtxmon_private_module_pointer_matches(pointer, rva, long_hash),
        "a 65-character digest is rejected");
    (void)memcpy(mutated_hash, sha256, sizeof(mutated_hash));
    mutated_hash[0] = 'g';
    failures += check(!rtxmon_private_module_pointer_matches(pointer, rva, mutated_hash),
        "a non-hexadecimal nibble is rejected without changing length");
    mutated_hash[0] = 'A';
    failures += check(!rtxmon_private_module_pointer_matches(pointer, rva, mutated_hash),
        "uppercase input is rejected by the existing lowercase digest contract");
    heap_pointer = HeapAlloc(GetProcessHeap(), 0U, 1U);
    failures += check(heap_pointer != NULL, "heap address fixture is allocated");
    if (heap_pointer != NULL) {
        failures += check(!rtxmon_private_module_pointer_matches(heap_pointer, rva, sha256),
            "an address outside loaded module images is rejected");
        (void)HeapFree(GetProcessHeap(), 0U, heap_pointer);
    }
    failures += check(rtxmon_private_module_pointer_matches(pointer, rva, sha256),
        "independent negative checks leave the accepted fixture unchanged");
#else
    failures += check(
        !rtxmon_private_module_pointer_matches(NULL, 0U, "invalid"),
        "private module profiles are unavailable off Windows");
#endif
    return failures == 0 ? 0 : 1;
}
