#include "nvapi_loader.h"
#include "private_module_profile.h"
#include "private_profile_catalog.h"

#include <stdio.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#endif

static void rtxmon_nvapi_loader_error(char *error, size_t capacity, const char *message)
{
    if (error == NULL || capacity == 0U) {
        return;
    }

    (void)snprintf(error, capacity, "%s", message != NULL ? message : "");
    error[capacity - 1U] = '\0';
}

#if defined(_WIN32)
static HMODULE rtxmon_load_windows_nvapi(void)
{
    wchar_t path[32768];
    const wchar_t *library_name;
    UINT system_length;

#if defined(_WIN64)
    library_name = L"\\nvapi64.dll";
#else
    library_name = L"\\nvapi.dll";
#endif

    system_length = GetSystemDirectoryW(path, (UINT)(sizeof(path) / sizeof(path[0])));
    if (system_length == 0U || system_length >= (UINT)(sizeof(path) / sizeof(path[0]))) {
        return NULL;
    }

    if (wcscat_s(path, sizeof(path) / sizeof(path[0]), library_name) != 0) {
        return NULL;
    }

    return LoadLibraryW(path);
}

static void *rtxmon_nvapi_export(void *library, const char *name)
{
    return (void *)GetProcAddress((HMODULE)library, name);
}

static void rtxmon_nvapi_close(void *library)
{
    if (library != NULL) {
        (void)FreeLibrary((HMODULE)library);
    }
}
#endif

#define RTXMON_NVAPI_QUERY_REQUIRED(api, member, type, interface_id, interface_name) \
    do {                                                                            \
        (api)->member = (type)(api)->query_interface((uint32_t)(interface_id));      \
        if ((api)->member == NULL) {                                                 \
            (void)snprintf(                                                         \
                error,                                                              \
                error_capacity,                                                     \
                "NVAPI interface is missing: %s",                                 \
                (interface_name));                                                   \
            if (error != NULL && error_capacity > 0U) {                             \
                error[error_capacity - 1U] = '\0';                                  \
            }                                                                        \
            rtxmon_nvapi_unload(api, 0);                                             \
            return RTXMON_NVAPI_LOADER_INTERFACE_MISSING;                           \
        }                                                                            \
    } while (0)

rtxmon_nvapi_loader_status_t rtxmon_nvapi_load(
    rtxmon_nvapi_api_t *api,
    char *error,
    size_t error_capacity)
{
    if (api == NULL) {
        rtxmon_nvapi_loader_error(error, error_capacity, "NVAPI loader received a null API table");
        return RTXMON_NVAPI_LOADER_INTERFACE_MISSING;
    }

    (void)memset(api, 0, sizeof(*api));

#if !defined(_WIN32)
    rtxmon_nvapi_loader_error(error, error_capacity, "NVAPI is only available on Windows");
    return RTXMON_NVAPI_LOADER_PLATFORM_UNAVAILABLE;
#else
    api->library = (void *)rtxmon_load_windows_nvapi();
    if (api->library == NULL) {
        rtxmon_nvapi_loader_error(
            error,
            error_capacity,
            "NVAPI library was not found in Windows System32");
        return RTXMON_NVAPI_LOADER_LIBRARY_NOT_FOUND;
    }

    api->query_interface = (rtxmon_nvapi_query_interface_fn)rtxmon_nvapi_export(
        api->library,
        "nvapi_QueryInterface");
    if (api->query_interface == NULL) {
        rtxmon_nvapi_loader_error(error, error_capacity, "nvapi_QueryInterface export is missing");
        rtxmon_nvapi_unload(api, 0);
        return RTXMON_NVAPI_LOADER_QUERY_INTERFACE_MISSING;
    }

    RTXMON_NVAPI_QUERY_REQUIRED(
        api,
        initialize,
        rtxmon_nvapi_initialize_fn,
        RTXMON_NVAPI_ID_INITIALIZE,
        "NvAPI_Initialize");
    RTXMON_NVAPI_QUERY_REQUIRED(
        api,
        unload,
        rtxmon_nvapi_unload_fn,
        RTXMON_NVAPI_ID_UNLOAD,
        "NvAPI_Unload");
    RTXMON_NVAPI_QUERY_REQUIRED(
        api,
        enum_physical_gpus,
        rtxmon_nvapi_enum_physical_gpus_fn,
        RTXMON_NVAPI_ID_ENUM_PHYSICAL_GPUS,
        "NvAPI_EnumPhysicalGPUs");
    RTXMON_NVAPI_QUERY_REQUIRED(
        api,
        gpu_get_pci_identifiers,
        rtxmon_nvapi_gpu_get_pci_identifiers_fn,
        RTXMON_NVAPI_ID_GPU_GET_PCI_IDENTIFIERS,
        "NvAPI_GPU_GetPCIIdentifiers");
    RTXMON_NVAPI_QUERY_REQUIRED(
        api,
        gpu_get_bus_id,
        rtxmon_nvapi_gpu_get_bus_id_fn,
        RTXMON_NVAPI_ID_GPU_GET_BUS_ID,
        "NvAPI_GPU_GetBusId");
    RTXMON_NVAPI_QUERY_REQUIRED(
        api,
        gpu_get_bus_slot_id,
        rtxmon_nvapi_gpu_get_bus_slot_id_fn,
        RTXMON_NVAPI_ID_GPU_GET_BUS_SLOT_ID,
        "NvAPI_GPU_GetBusSlotId");
    RTXMON_NVAPI_QUERY_REQUIRED(
        api,
        gpu_get_thermal_settings,
        rtxmon_nvapi_gpu_get_thermal_settings_fn,
        RTXMON_NVAPI_ID_GPU_GET_THERMAL_SETTINGS,
        "NvAPI_GPU_GetThermalSettings");

    /* Only resolve non-revoked operations in the compiled reviewed catalog. */
    {
        const rtxmon_private_profile_catalog_t *profile = rtxmon_private_catalog_get();
        void *private_pointer = profile->revoked == 0U && profile->thermal.revoked == 0U
            ? api->query_interface(profile->thermal.interface_id) : NULL;
        if (rtxmon_private_module_pointer_matches(
                private_pointer,
                profile->thermal.function_rva,
                profile->module_sha256)) {
            api->gpu_therm_channel_get_status =
                (rtxmon_nvapi_gpu_therm_channel_get_status_fn)private_pointer;
        }
    }
    {
        const rtxmon_private_profile_catalog_t *profile = rtxmon_private_catalog_get();
        void *private_pointer = profile->revoked == 0U && profile->voltage.revoked == 0U
            ? api->query_interface(profile->voltage.interface_id) : NULL;
        if (rtxmon_private_module_pointer_matches(
                private_pointer,
                profile->voltage.function_rva,
                profile->module_sha256)) {
            api->gpu_voltage_status =
                (rtxmon_nvapi_gpu_voltage_status_fn)private_pointer;
        }
    }

    rtxmon_nvapi_loader_error(error, error_capacity, "");
    return RTXMON_NVAPI_LOADER_OK;
#endif
}

void rtxmon_nvapi_unload(rtxmon_nvapi_api_t *api, int initialized)
{
    if (api == NULL) {
        return;
    }

#if defined(_WIN32)
    if (initialized != 0 && api->unload != NULL) {
        (void)api->unload();
    }
    rtxmon_nvapi_close(api->library);
#else
    (void)initialized;
#endif

    (void)memset(api, 0, sizeof(*api));
}
