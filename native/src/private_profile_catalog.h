#ifndef RTXMON_PRIVATE_PROFILE_CATALOG_H
#define RTXMON_PRIVATE_PROFILE_CATALOG_H

#include <stdint.h>

/* Internal, compiled policy. No external profile loader or runtime setter. */
typedef struct rtxmon_private_operation_policy {
    uint32_t revoked;
    const char *revocation_reason;
    uint32_t interface_id;
    uint32_t function_rva;
    uint32_t structure_version;
    uint32_t min_interval_ms;
    uint32_t timeout_ms;
} rtxmon_private_operation_policy_t;

typedef struct rtxmon_private_profile_catalog {
    const char *profile_id;
    uint32_t revision;
    uint32_t revoked;
    const char *revocation_reason;
    uint32_t vendor_id;
    uint32_t device_id;
    uint32_t subsystem_vendor_id;
    uint32_t subsystem_device_id;
    const char *uuid;
    const char *vbios;
    const char *driver;
    const char *module_sha256;
    rtxmon_private_operation_policy_t thermal;
    rtxmon_private_operation_policy_t voltage;
} rtxmon_private_profile_catalog_t;

#if defined(__GNUC__) || defined(__clang__)
__attribute__((visibility("hidden")))
#endif
const rtxmon_private_profile_catalog_t *rtxmon_private_catalog_get(void);

#endif
