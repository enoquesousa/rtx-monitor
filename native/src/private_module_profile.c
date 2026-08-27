#include "private_module_profile.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#include <bcrypt.h>

static int hex_nibble(char value)
{
    if (value >= '0' && value <= '9') {
        return value - '0';
    }
    if (value >= 'a' && value <= 'f') {
        return value - 'a' + 10;
    }
    return -1;
}

static int decode_sha256(const char *text, unsigned char output[32])
{
    size_t index;
    if (text == NULL || strlen(text) != 64U) {
        return 0;
    }
    for (index = 0U; index < 32U; ++index) {
        const int high = hex_nibble(text[index * 2U]);
        const int low = hex_nibble(text[index * 2U + 1U]);
        if (high < 0 || low < 0) {
            return 0;
        }
        output[index] = (unsigned char)((high << 4) | low);
    }
    return 1;
}

static int hash_file_sha256(const wchar_t *path, unsigned char output[32])
{
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    HANDLE file = INVALID_HANDLE_VALUE;
    PUCHAR hash_object = NULL;
    DWORD object_size = 0U;
    DWORD property_size = 0U;
    unsigned char buffer[64U * 1024U];
    DWORD read_size = 0U;
    LARGE_INTEGER size_before;
    LARGE_INTEGER size_after;
    uint64_t total_read = 0U;
    int success = 0;

    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0U) < 0 ||
        BCryptGetProperty(
            algorithm,
            BCRYPT_OBJECT_LENGTH,
            (PUCHAR)&object_size,
            (ULONG)sizeof(object_size),
            &property_size,
            0U) < 0 ||
        object_size == 0U) {
        goto cleanup;
    }
    hash_object = (PUCHAR)HeapAlloc(GetProcessHeap(), 0U, object_size);
    if (hash_object == NULL ||
        BCryptCreateHash(algorithm, &hash, hash_object, object_size, NULL, 0U, 0U) < 0) {
        goto cleanup;
    }
    file = CreateFileW(
        path,
        GENERIC_READ,
        FILE_SHARE_READ,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (file == INVALID_HANDLE_VALUE || !GetFileSizeEx(file, &size_before) ||
        size_before.QuadPart < 0) {
        goto cleanup;
    }
    do {
        if (!ReadFile(file, buffer, (DWORD)sizeof(buffer), &read_size, NULL)) {
            goto cleanup;
        }
        if (read_size > 0U && BCryptHashData(hash, buffer, read_size, 0U) < 0) {
            goto cleanup;
        }
        total_read += read_size;
    } while (read_size > 0U);
    if (!GetFileSizeEx(file, &size_after) ||
        size_after.QuadPart != size_before.QuadPart ||
        total_read != (uint64_t)size_before.QuadPart ||
        BCryptFinishHash(hash, output, 32U, 0U) < 0) {
        goto cleanup;
    }
    success = 1;

cleanup:
    if (file != INVALID_HANDLE_VALUE) {
        (void)CloseHandle(file);
    }
    if (hash != NULL) {
        (void)BCryptDestroyHash(hash);
    }
    if (hash_object != NULL) {
        (void)HeapFree(GetProcessHeap(), 0U, hash_object);
    }
    if (algorithm != NULL) {
        (void)BCryptCloseAlgorithmProvider(algorithm, 0U);
    }
    return success;
}

int rtxmon_private_module_pointer_matches(
    const void *pointer,
    uint32_t expected_rva,
    const char *expected_sha256)
{
    HMODULE module = NULL;
    wchar_t path[32768];
    unsigned char expected_hash[32];
    unsigned char actual_hash[32];
    uintptr_t pointer_value;
    uintptr_t module_value;

    if (pointer == NULL || !decode_sha256(expected_sha256, expected_hash) ||
        !GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            (LPCWSTR)pointer,
            &module) ||
        module == NULL ||
        GetModuleFileNameW(module, path, (DWORD)(sizeof(path) / sizeof(path[0]))) == 0U) {
        return 0;
    }
    path[(sizeof(path) / sizeof(path[0])) - 1U] = L'\0';
    pointer_value = (uintptr_t)pointer;
    module_value = (uintptr_t)(const void *)module;
    if (pointer_value < module_value || pointer_value - module_value != expected_rva ||
        !hash_file_sha256(path, actual_hash)) {
        return 0;
    }
    return memcmp(actual_hash, expected_hash, sizeof(actual_hash)) == 0;
}

#else

int rtxmon_private_module_pointer_matches(
    const void *pointer,
    uint32_t expected_rva,
    const char *expected_sha256)
{
    (void)pointer;
    (void)expected_rva;
    (void)expected_sha256;
    return 0;
}

#endif
