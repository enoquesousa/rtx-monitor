#ifndef RTXMON_PRIVATE_MODULE_PROFILE_H
#define RTXMON_PRIVATE_MODULE_PROFILE_H

#include <stdint.h>

int rtxmon_private_module_pointer_matches(
    const void *pointer,
    uint32_t expected_rva,
    const char *expected_sha256);

#endif
