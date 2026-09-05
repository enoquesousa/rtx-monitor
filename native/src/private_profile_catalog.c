#include "private_profile_catalog.h"
#include "nvapi_abi.h"

/* Test variants can only revoke; they cannot add identities or interfaces. */
#if defined(RTXMON_PRIVATE_PROFILE_TESTING)
#define RTXMON_CATALOG_PROFILE_REVOKED RTXMON_TEST_PROFILE_REVOKED
#define RTXMON_CATALOG_THERMAL_REVOKED RTXMON_TEST_THERMAL_REVOKED
#define RTXMON_CATALOG_VOLTAGE_REVOKED RTXMON_TEST_VOLTAGE_REVOKED
#else
#if defined(RTXMON_TEST_PROFILE_REVOKED) || defined(RTXMON_TEST_THERMAL_REVOKED) || defined(RTXMON_TEST_VOLTAGE_REVOKED)
#error Test policy overrides require an isolated test target
#endif
#define RTXMON_CATALOG_PROFILE_REVOKED 0U
#define RTXMON_CATALOG_THERMAL_REVOKED 0U
#define RTXMON_CATALOG_VOLTAGE_REVOKED 0U
#endif

/* Changes to this single reviewed entry require a revision and source review.
 * Revocation flags and reasons are checked before resolving/calling private APIs.
 * Production revocation is an ordinary source change here followed by a build.
 */
static const rtxmon_private_profile_catalog_t catalog = {
    "rtx3060-2504-1536-vbios-94.06.25.00.fc-driver-610.88",
    2U,
    RTXMON_CATALOG_PROFILE_REVOKED,
    RTXMON_CATALOG_PROFILE_REVOKED ? "profile revoked by compiled policy" : "",
    0x10deU, 0x2504U, 0x10deU, 0x1536U,
    "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd",
    "94.06.25.00.fc",
    "610.88",
    "df6455ccf83e43cfe68f405af1eec4e053c7f95da998bf358053b7583980c2f4",
    {
        RTXMON_CATALOG_THERMAL_REVOKED,
        RTXMON_CATALOG_THERMAL_REVOKED ? "thermal operation revoked by compiled policy" : "",
        RTXMON_NVAPI_ID_GPU_THERM_CHANNEL_GET_STATUS,
        0x001e0bc0U,
        RTXMON_NVAPI_THERM_CHANNEL_STATUS_V2_VERSION,
        100U, 2000U
    },
    {
        RTXMON_CATALOG_VOLTAGE_REVOKED,
        RTXMON_CATALOG_VOLTAGE_REVOKED ? "voltage operation revoked by compiled policy" : "",
        RTXMON_NVAPI_ID_GPU_VOLTAGE_STATUS,
        0x001c9070U,
        RTXMON_NVAPI_VOLTAGE_STATUS_V1_VERSION,
        100U, 2000U
    }
};

const rtxmon_private_profile_catalog_t *rtxmon_private_catalog_get(void)
{
    return &catalog;
}
