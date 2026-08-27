#include "private_module_profile.h"

#include <stdint.h>
#include <stdio.h>

#if defined(_WIN32)
#include <windows.h>
#endif

static int check(int condition, const char *message)
{
    if (condition) {
        return 0;
    }
    (void)fprintf(stderr, "FAILED: %s\n", message);
    return 1;
}

int main(void)
{
    int failures = 0;
#if defined(_WIN32)
    HMODULE module = GetModuleHandleW(L"kernel32.dll");
    const void *pointer = (const void *)GetProcAddress(module, "GetModuleHandleW");
    const uint32_t rva = (uint32_t)((uintptr_t)pointer - (uintptr_t)(const void *)module);
    failures += check(
        !rtxmon_private_module_pointer_matches(
            pointer,
            rva,
            "0000000000000000000000000000000000000000000000000000000000000000"),
        "module hash mismatch fails closed");
    failures += check(
        !rtxmon_private_module_pointer_matches(
            pointer,
            rva + 1U,
            "0000000000000000000000000000000000000000000000000000000000000000"),
        "module RVA mismatch fails closed");
    failures += check(
        !rtxmon_private_module_pointer_matches(NULL, rva, "invalid"),
        "missing pointer and malformed hash fail closed");
#else
    failures += check(
        !rtxmon_private_module_pointer_matches(NULL, 0U, "invalid"),
        "private module profiles are unavailable off Windows");
#endif
    return failures == 0 ? 0 : 1;
}
