#include "private_profile_gate.h"
#include "fixtures/rtx3060-recorded-responses.h"

#include <stddef.h>
#include <stdio.h>
#include <string.h>

static const char *mock_uuid;
static const char *mock_driver;
static const char *mock_vbios;
static nvmlReturn_t mock_device_result;
static nvmlReturn_t mock_pci_result;
static nvmlReturn_t mock_uuid_result;
static nvmlReturn_t mock_driver_result;
static uint32_t mock_vendor;
static uint32_t mock_device;
static uint32_t mock_subsystem_vendor;
static uint32_t mock_subsystem_device;
static uint32_t mock_gpu_count;
static uint32_t mock_bus;
static int mock_enum_failure;
static int mock_second_identity_failure;
static int mock_unterminated_uuid;
static int mock_voltage_failure;
static int mock_voltage_wrong_version;
static int mock_thermal_wrong_version;
static uint32_t thermal_calls;
static uint32_t voltage_calls;
static uint32_t identity_calls;
static uint32_t association_calls;
static int lock_depth;
static uint64_t mock_now;
static uint32_t mock_identity_delay;
static uint32_t mock_association_delay;
static uint32_t mock_thermal_delay[2];
static uint32_t mock_voltage_delay;
static uint32_t mock_publish_delay;
static uint32_t mock_try_lock_delay;
static int mock_lock_blocked;
static uint32_t try_lock_calls;
static uint32_t unlock_calls;
static uint32_t mock_clock_reads_until_fault;
static uint64_t mock_clock_fault_value;

typedef enum recorded_response_change {
    RECORDED_UNCHANGED,
    RECORDED_SIZE_MINUS_FOUR,
    RECORDED_SIZE_PLUS_FOUR,
    RECORDED_REVISION_MINUS_ONE,
    RECORDED_REVISION_PLUS_ONE,
    RECORDED_WRONG_MASK,
    RECORDED_ERROR_AFTER_WRITE,
    RECORDED_ERROR_AFTER_PARTIAL_WRITE
} recorded_response_change_t;

static int mock_use_recorded_responses;
static recorded_response_change_t mock_recorded_thermal_change;
static recorded_response_change_t mock_recorded_voltage_change;

static int recorded_request_tail_zero(const void *request, size_t offset, size_t size)
{
    const unsigned char *bytes = (const unsigned char *)request;
    size_t index;
    for (index = offset; index < size; ++index) {
        if (bytes[index] != 0U) {
            return 0;
        }
    }
    return 1;
}

static uint32_t recorded_changed_version(uint32_t version, recorded_response_change_t change)
{
    /* Size and revision are independent halves of the NVAPI version word. */
    if (change == RECORDED_SIZE_MINUS_FOUR) { return version - 4U; }
    if (change == RECORDED_SIZE_PLUS_FOUR) { return version + 4U; }
    if (change == RECORDED_REVISION_MINUS_ONE) { return version - 0x00010000U; }
    if (change == RECORDED_REVISION_PLUS_ONE) { return version + 0x00010000U; }
    return version;
}

static int check(int condition, const char *message)
{
    if (condition != 0) {
        return 0;
    }
    (void)fprintf(stderr, "FAILED: %s\n", message);
    return 1;
}

void rtxmon_lock_nvapi_internal(void) { ++lock_depth; }
int rtxmon_try_lock_nvapi_internal(void)
{
    ++try_lock_calls;
    mock_now += mock_try_lock_delay;
    if (mock_lock_blocked || lock_depth != 0) {
        return 0;
    }
    ++lock_depth;
    return 1;
}
void rtxmon_unlock_nvapi_internal(void) { ++unlock_calls; --lock_depth; }
void rtxmon_pause_lock_wait_internal(void) { mock_now += 100U; }
uint64_t rtxmon_monotonic_ms_internal(void)
{
    if (mock_clock_reads_until_fault != 0U && --mock_clock_reads_until_fault == 0U) {
        return mock_clock_fault_value;
    }
    return mock_now;
}
uint64_t rtxmon_timestamp_unix_ms_internal(void) { mock_now += mock_publish_delay; return 1234U; }

static nvmlReturn_t RTXMON_NVML_CALL mock_get_device(uint32_t index, nvmlDevice_t *out_device)
{
    ++identity_calls;
    mock_now += mock_identity_delay;
    *out_device = NULL;
    if (index != 0U) {
        return NVML_ERROR_NOT_FOUND;
    }
    if (mock_device_result == NVML_SUCCESS) {
        *out_device = (nvmlDevice_t)(uintptr_t)1U;
    }
    return mock_device_result;
}

nvmlReturn_t rtxmon_get_pci_info_internal(
    rtxmon_context_t *context, nvmlDevice_t device, rtxmon_nvml_pci_info_t *pci)
{
    (void)context;
    (void)device;
    ++identity_calls;
    (void)memset(pci, 0, sizeof(*pci));
    pci->bus = 1U;
    pci->pci_device_id = (mock_device << 16U) | mock_vendor;
    pci->pci_subsystem_id = (mock_subsystem_device << 16U) | mock_subsystem_vendor;
    return mock_pci_result;
}

static nvmlReturn_t RTXMON_NVML_CALL mock_get_uuid(nvmlDevice_t device, char *value, uint32_t capacity)
{
    (void)device;
    ++identity_calls;
    if (mock_unterminated_uuid != 0) {
        (void)memset(value, 'A', capacity);
    } else {
        (void)snprintf(value, capacity, "%s", mock_uuid);
    }
    return mock_uuid_result;
}

static nvmlReturn_t RTXMON_NVML_CALL mock_get_vbios(nvmlDevice_t device, char *value, uint32_t capacity)
{
    (void)device;
    ++identity_calls;
    (void)snprintf(value, capacity, "%s", mock_vbios);
    return NVML_SUCCESS;
}

static nvmlReturn_t RTXMON_NVML_CALL mock_get_driver(char *value, uint32_t capacity)
{
    ++identity_calls;
    (void)snprintf(value, capacity, "%s", mock_driver);
    return mock_driver_result;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_enum_gpus(
    rtxmon_nvapi_gpu_handle_t handles[RTXMON_NVAPI_MAX_PHYSICAL_GPUS], uint32_t *count)
{
    ++association_calls;
    mock_now += mock_association_delay;
    handles[0] = (rtxmon_nvapi_gpu_handle_t)(uintptr_t)2U;
    handles[1] = (rtxmon_nvapi_gpu_handle_t)(uintptr_t)3U;
    *count = mock_gpu_count;
    return mock_enum_failure != 0 ? RTXMON_NVAPI_ERROR : RTXMON_NVAPI_OK;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_get_bus(rtxmon_nvapi_gpu_handle_t handle, uint32_t *value)
{
    if (mock_second_identity_failure != 0 && handle == (rtxmon_nvapi_gpu_handle_t)(uintptr_t)3U) {
        return RTXMON_NVAPI_ERROR;
    }
    *value = mock_bus;
    return RTXMON_NVAPI_OK;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_get_slot(rtxmon_nvapi_gpu_handle_t handle, uint32_t *value)
{
    (void)handle;
    *value = 0U;
    return RTXMON_NVAPI_OK;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_get_ids(
    rtxmon_nvapi_gpu_handle_t handle, uint32_t *device, uint32_t *subsystem,
    uint32_t *revision, uint32_t *extended)
{
    (void)handle;
    *device = 0x250410deU;
    *subsystem = 0x153610deU;
    *revision = 0U;
    *extended = 0U;
    return RTXMON_NVAPI_OK;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_thermal(
    rtxmon_nvapi_gpu_handle_t handle, rtxmon_nvapi_therm_channel_status_v2_t *sample)
{
    ++thermal_calls;
    mock_now += mock_thermal_delay[(thermal_calls - 1U) % 2U];
    if (mock_use_recorded_responses != 0) {
        const uint32_t channel = (thermal_calls - 1U) % 2U;
        const recorded_response_change_t change = channel == 1U
            ? mock_recorded_thermal_change : RECORDED_UNCHANGED;
        const unsigned char *recorded = channel == 0U
            ? rtx3060_recorded_thermal_channel0 : rtx3060_recorded_thermal_channel1;
        if (sizeof(*sample) != sizeof(rtx3060_recorded_thermal_channel0) ||
            sample->version != 0x000200a8U || sample->channel_mask != (1U << channel) ||
            handle != (rtxmon_nvapi_gpu_handle_t)(uintptr_t)2U || lock_depth != 1 ||
            !recorded_request_tail_zero(sample, 8U, sizeof(*sample))) {
            return RTXMON_NVAPI_INVALID_ARGUMENT;
        }
        /* Replay captured bytes only after checking the caller's input. Never
         * construct the response using the decoder's current word offsets.
         */
        (void)memcpy(sample, recorded, change == RECORDED_ERROR_AFTER_PARTIAL_WRITE ? 44U : sizeof(*sample));
        sample->version = recorded_changed_version(sample->version, change);
        if (change == RECORDED_WRONG_MASK) {
            sample->channel_mask = 1U; /* The second request must return mask 2. */
        }
        return change == RECORDED_ERROR_AFTER_WRITE || change == RECORDED_ERROR_AFTER_PARTIAL_WRITE
            ? RTXMON_NVAPI_ERROR : RTXMON_RECORDED_THERMAL_RETURN_STATUS;
    }
    sample->words[8] = 40U * 256U;
    sample->words[9] = 50U * 256U;
    if (mock_thermal_wrong_version != 0) {
        sample->version = 0U;
    }
    return lock_depth == 1 ? RTXMON_NVAPI_OK : RTXMON_NVAPI_ERROR;
}

static rtxmon_nvapi_status_t RTXMON_NVAPI_CALL mock_voltage(
    rtxmon_nvapi_gpu_handle_t handle, rtxmon_nvapi_voltage_status_v1_t *sample)
{
    ++voltage_calls;
    mock_now += mock_voltage_delay;
    if (mock_use_recorded_responses != 0) {
        const recorded_response_change_t change = mock_recorded_voltage_change;
        if (sizeof(*sample) != sizeof(rtx3060_recorded_voltage) || sample->version != 0x0001004cU ||
            handle != (rtxmon_nvapi_gpu_handle_t)(uintptr_t)2U || lock_depth != 1 ||
            !recorded_request_tail_zero(sample, 4U, sizeof(*sample))) {
            return RTXMON_NVAPI_INVALID_ARGUMENT;
        }
        (void)memcpy(sample, rtx3060_recorded_voltage,
            change == RECORDED_ERROR_AFTER_PARTIAL_WRITE ? 44U : sizeof(*sample));
        sample->version = recorded_changed_version(sample->version, change);
        return change == RECORDED_ERROR_AFTER_WRITE || change == RECORDED_ERROR_AFTER_PARTIAL_WRITE
            ? RTXMON_NVAPI_ERROR : RTXMON_RECORDED_VOLTAGE_RETURN_STATUS;
    }
    sample->words[9] = 956250U;
    if (mock_voltage_wrong_version != 0) {
        sample->version = 0U;
    }
    return mock_voltage_failure != 0 || lock_depth != 1 ? RTXMON_NVAPI_ERROR : RTXMON_NVAPI_OK;
}

static void reset(rtxmon_context_t *context)
{
    (void)memset(context, 0, sizeof(*context));
    context->initialized = 1;
    context->nvapi_initialized = 1;
    context->nvml.device_get_handle_by_index_v2 = mock_get_device;
    context->nvml.device_get_uuid = mock_get_uuid;
    context->nvml.device_get_vbios_version = mock_get_vbios;
    context->nvml.system_get_driver_version = mock_get_driver;
    context->nvapi.enum_physical_gpus = mock_enum_gpus;
    context->nvapi.gpu_get_bus_id = mock_get_bus;
    context->nvapi.gpu_get_bus_slot_id = mock_get_slot;
    context->nvapi.gpu_get_pci_identifiers = mock_get_ids;
    context->nvapi.gpu_therm_channel_get_status = mock_thermal;
    context->nvapi.gpu_voltage_status = mock_voltage;
    mock_uuid = "GPU-fca3647e-8390-15a8-f23b-d0f870c9accd";
    mock_vbios = "94.06.25.00.FC";
    mock_driver = "610.88";
    mock_device_result = NVML_SUCCESS;
    mock_pci_result = NVML_SUCCESS;
    mock_uuid_result = NVML_SUCCESS;
    mock_driver_result = NVML_SUCCESS;
    mock_vendor = 0x10deU;
    mock_device = 0x2504U;
    mock_subsystem_vendor = 0x10deU;
    mock_subsystem_device = 0x1536U;
    mock_gpu_count = 1U;
    mock_bus = 1U;
    mock_enum_failure = 0;
    mock_second_identity_failure = 0;
    mock_unterminated_uuid = 0;
    mock_voltage_failure = 0;
    mock_voltage_wrong_version = 0;
    mock_thermal_wrong_version = 0;
    thermal_calls = voltage_calls = identity_calls = association_calls = 0U;
    lock_depth = 0;
    mock_now += 100U;
    mock_identity_delay = mock_association_delay = mock_voltage_delay = 0U;
    mock_thermal_delay[0] = mock_thermal_delay[1] = 0U;
    mock_publish_delay = mock_try_lock_delay = 0U;
    mock_lock_blocked = 0;
    try_lock_calls = unlock_calls = 0U;
    mock_clock_reads_until_fault = 0U;
    mock_clock_fault_value = 0U;
    mock_use_recorded_responses = 0;
    mock_recorded_thermal_change = mock_recorded_voltage_change = RECORDED_UNCHANGED;
}

static int run_case(rtxmon_context_t *context, uint32_t index,
    uint32_t expected_thermal, uint32_t expected_voltage,
    uint32_t checked, uint32_t matched, const char *label)
{
    rtxmon_private_profile_report_t report;
    rtxmon_private_thermal_sample_t thermal;
    rtxmon_private_voltage_sample_t voltage;
    rtxmon_status_t thermal_status, voltage_status;
    uint32_t thermal_calls_before_voltage;
    int failures = 0;
    const int profile_revoked = RTXMON_TEST_PROFILE_REVOKED;
    const int thermal_revoked = RTXMON_TEST_PROFILE_REVOKED || RTXMON_TEST_THERMAL_REVOKED;
    const int voltage_revoked = RTXMON_TEST_PROFILE_REVOKED || RTXMON_TEST_VOLTAGE_REVOKED;
    const uint32_t thermal_state = thermal_revoked != 0 ? RTXMON_PRIVATE_OPERATION_REVOKED : expected_thermal;
    const uint32_t voltage_state = voltage_revoked != 0 ? RTXMON_PRIVATE_OPERATION_REVOKED : expected_voltage;
    (void)checked;
    (void)matched;
    (void)memset(&report, 0xff, sizeof(report));
    report.struct_size = (uint32_t)sizeof(report);
    thermal_calls = voltage_calls = identity_calls = association_calls = 0U;
    failures += check(rtxmon_get_private_profile_status(context, index, &report) == RTXMON_STATUS_OK, label);
    failures += check(report.thermal_state == thermal_state && report.voltage_state == voltage_state, label);
    failures += check(thermal_calls == 0U && voltage_calls == 0U, "diagnostics never acquire private samples");
    failures += check(report.profile_revision == 2U &&
        strcmp(report.profile_id, "rtx3060-2504-1536-vbios-94.06.25.00.fc-driver-610.88") == 0,
        "report identity and revision come from compiled catalog");
    failures += check(report.thermal_min_interval_ms == 100U && report.thermal_timeout_ms == 2000U &&
        report.voltage_min_interval_ms == 100U && report.voltage_timeout_ms == 2000U,
        "report publishes reviewed compiled acquisition policy");
    failures += check(report.profile_state == (RTXMON_TEST_PROFILE_REVOKED
        ? RTXMON_PRIVATE_PROFILE_REVOKED : RTXMON_PRIVATE_PROFILE_ACTIVE), "profile policy is separate from device compatibility");
    failures += check(report.identity_checked_flags == (RTXMON_TEST_PROFILE_REVOKED ? 0U : checked) &&
        report.identity_match_flags == (RTXMON_TEST_PROFILE_REVOKED ? 0U : matched), label);
    failures += check((report.revocation_reason[0] != '\0') == (thermal_revoked || voltage_revoked),
        "revocation reason is populated only for compiled revocations");
    if (profile_revoked != 0) {
        failures += check(identity_calls == 0U && association_calls == 0U,
            "profile revocation stops before any backend query");
    }

    (void)memset(&thermal, 0xff, sizeof(thermal));
    (void)memset(&voltage, 0xff, sizeof(voltage));
    thermal.struct_size = (uint32_t)sizeof(thermal);
    voltage.struct_size = (uint32_t)sizeof(voltage);
    identity_calls = association_calls = try_lock_calls = unlock_calls = 0U;
    thermal_status = rtxmon_read_private_thermal_channels(context, index, &thermal);
    if (thermal_revoked != 0) {
        failures += check(thermal_status == RTXMON_STATUS_NOT_SUPPORTED &&
            thermal.native_status == RTXMON_NVAPI_NOT_SUPPORTED && thermal.timestamp_unix_ms == 1234U &&
            identity_calls == 0U && association_calls == 0U && thermal_calls == 0U && voltage_calls == 0U &&
            try_lock_calls == 0U && unlock_calls == 0U,
            "revoked thermal returns before the lock or any backend gate even when voltage remains active");
    }
    thermal_calls_before_voltage = thermal_calls;
    identity_calls = association_calls = try_lock_calls = unlock_calls = 0U;
    voltage_status = rtxmon_read_private_voltage_status(context, index, &voltage);
    if (voltage_revoked != 0) {
        failures += check(voltage_status == RTXMON_STATUS_NOT_SUPPORTED &&
            voltage.native_status == RTXMON_NVAPI_NOT_SUPPORTED && voltage.timestamp_unix_ms == 1234U &&
            identity_calls == 0U && association_calls == 0U && voltage_calls == 0U &&
            thermal_calls == thermal_calls_before_voltage && try_lock_calls == 0U && unlock_calls == 0U,
            "revoked voltage returns before the lock or any backend gate and preserves the active thermal operation");
    }
    failures += check((thermal_status == RTXMON_STATUS_OK) == (thermal_state == RTXMON_PRIVATE_OPERATION_COMPATIBLE) &&
        (voltage_status == RTXMON_STATUS_OK) == (voltage_state == RTXMON_PRIVATE_OPERATION_COMPATIBLE), label);
    failures += check(thermal_calls == (thermal_state == RTXMON_PRIVATE_OPERATION_COMPATIBLE ? 2U : 0U) &&
        voltage_calls == (voltage_state == RTXMON_PRIVATE_OPERATION_COMPATIBLE ? 1U : 0U),
        "readers enforce exactly the same operation gates as diagnostics");
    if (thermal_state != RTXMON_PRIVATE_OPERATION_COMPATIBLE) {
        failures += check(thermal.value_flags == 0U && thermal.gpu_die_temperature_millic == 0 &&
            thermal.gpu_hotspot_temperature_millic == 0 && thermal.reserved == 0 && thermal.native_status != RTXMON_NVAPI_OK,
            "blocked thermal clears old values and reports failure");
    }
    if (voltage_state != RTXMON_PRIVATE_OPERATION_COMPATIBLE) {
        failures += check(voltage.value_flags == 0U && voltage.gpu_core_voltage_microvolts == 0U &&
            voltage.reserved == 0U && voltage.native_status != RTXMON_NVAPI_OK,
            "blocked voltage clears old values and reports failure");
    }
    failures += check(lock_depth == 0, "all code paths release the NVAPI lock");
    return failures;
}

#if !RTXMON_TEST_PROFILE_REVOKED && !RTXMON_TEST_THERMAL_REVOKED && !RTXMON_TEST_VOLTAGE_REVOKED
static int test_recorded_rtx3060_responses(void)
{
    static const recorded_response_change_t metadata_changes[] = {
        RECORDED_SIZE_MINUS_FOUR, RECORDED_SIZE_PLUS_FOUR,
        RECORDED_REVISION_MINUS_ONE, RECORDED_REVISION_PLUS_ONE
    };
    static const recorded_response_change_t failure_changes[] = {
        RECORDED_ERROR_AFTER_WRITE, RECORDED_ERROR_AFTER_PARTIAL_WRITE
    };
    rtxmon_context_t context;
    rtxmon_private_thermal_sample_t thermal;
    rtxmon_private_voltage_sample_t voltage;
    size_t index;
    int failures = 0;

    reset(&context);
    mock_use_recorded_responses = 1;
    (void)memset(&thermal, 0xff, sizeof(thermal));
    (void)memset(&voltage, 0xff, sizeof(voltage));
    thermal.struct_size = (uint32_t)sizeof(thermal);
    voltage.struct_size = (uint32_t)sizeof(voltage);
    failures += check(rtxmon_read_private_thermal_channels(&context, 0U, &thermal) == RTXMON_STATUS_OK &&
        thermal_calls == 2U && thermal.native_status == RTXMON_RECORDED_THERMAL_RETURN_STATUS &&
        thermal.value_flags == (RTXMON_PRIVATE_THERMAL_DIE_VALID | RTXMON_PRIVATE_THERMAL_HOTSPOT_VALID) &&
        thermal.gpu_die_temperature_millic == RTXMON_RECORDED_DIE_MILLIC &&
        thermal.gpu_hotspot_temperature_millic == RTXMON_RECORDED_HOTSPOT_MILLIC && thermal.reserved == 0,
        "recorded x86 RTX3060 byte buffers decode to exact rounded 37063/47531 mC without hardware");
    failures += check(rtxmon_read_private_voltage_status(&context, 0U, &voltage) == RTXMON_STATUS_OK &&
        voltage_calls == 1U && voltage.native_status == RTXMON_RECORDED_VOLTAGE_RETURN_STATUS &&
        voltage.value_flags == RTXMON_PRIVATE_VOLTAGE_CORE_VALID &&
        voltage.gpu_core_voltage_microvolts == RTXMON_RECORDED_VOLTAGE_MICROVOLTS && voltage.reserved == 0U,
        "recorded x86 RTX3060 byte buffer decodes to exact 956250 microvolts without hardware");

    for (index = 0U; index < sizeof(metadata_changes) / sizeof(metadata_changes[0]); ++index) {
        uint32_t thermal_before, voltage_before;
        mock_now += 100U;
        thermal_before = thermal_calls;
        voltage_before = voltage_calls;
        mock_recorded_thermal_change = mock_recorded_voltage_change = metadata_changes[index];
        failures += check(rtxmon_read_private_thermal_channels(&context, 0U, &thermal) == RTXMON_STATUS_NOT_SUPPORTED &&
            thermal_calls == thermal_before + 2U && thermal.native_status == RTXMON_NVAPI_INCOMPATIBLE_STRUCT_VERSION &&
            thermal.value_flags == 0U && thermal.gpu_die_temperature_millic == 0 &&
            thermal.gpu_hotspot_temperature_millic == 0 && thermal.reserved == 0,
            "recorded second channel with changed size or revision discards the pair and any prior sample");
        failures += check(rtxmon_read_private_voltage_status(&context, 0U, &voltage) == RTXMON_STATUS_NOT_SUPPORTED &&
            voltage_calls == voltage_before + 1U && voltage.native_status == RTXMON_NVAPI_INCOMPATIBLE_STRUCT_VERSION &&
            voltage.value_flags == 0U && voltage.gpu_core_voltage_microvolts == 0U && voltage.reserved == 0U,
            "recorded voltage with independently changed size or revision cannot publish captured bytes");
    }

    mock_now += 100U;
    mock_recorded_thermal_change = RECORDED_WRONG_MASK;
    failures += check(rtxmon_read_private_thermal_channels(&context, 0U, &thermal) == RTXMON_STATUS_BACKEND_ERROR &&
        thermal.native_status == RTXMON_NVAPI_ERROR && thermal.value_flags == 0U &&
        thermal.gpu_die_temperature_millic == 0 && thermal.gpu_hotspot_temperature_millic == 0 && thermal.reserved == 0,
        "recorded second thermal channel with a wrong mask cannot publish the first channel alone");

    for (index = 0U; index < sizeof(failure_changes) / sizeof(failure_changes[0]); ++index) {
        mock_now += 100U;
        mock_recorded_thermal_change = mock_recorded_voltage_change = RECORDED_UNCHANGED;
        failures += check(rtxmon_read_private_thermal_channels(&context, 0U, &thermal) == RTXMON_STATUS_OK &&
            rtxmon_read_private_voltage_status(&context, 0U, &voltage) == RTXMON_STATUS_OK,
            "recorded complete samples are populated before callback failure regression");
        mock_now += 100U;
        mock_recorded_thermal_change = mock_recorded_voltage_change = failure_changes[index];
        failures += check(rtxmon_read_private_thermal_channels(&context, 0U, &thermal) == RTXMON_STATUS_BACKEND_ERROR &&
            thermal.native_status == RTXMON_NVAPI_ERROR && thermal.value_flags == 0U &&
            thermal.gpu_die_temperature_millic == 0 && thermal.gpu_hotspot_temperature_millic == 0 && thermal.reserved == 0,
            "error after complete or partial captured second-channel write clears the old sample and partial new pair");
        failures += check(rtxmon_read_private_voltage_status(&context, 0U, &voltage) == RTXMON_STATUS_BACKEND_ERROR &&
            voltage.native_status == RTXMON_NVAPI_ERROR && voltage.value_flags == 0U &&
            voltage.gpu_core_voltage_microvolts == 0U && voltage.reserved == 0U,
            "error after complete or partial captured voltage write clears the old and newly written value");
    }
    failures += check(lock_depth == 0, "recorded-response replay always releases the NVAPI lock");
    return failures;
}

static int test_private_output_validation(void)
{
    rtxmon_context_t context;
    rtxmon_private_voltage_sample_t voltage;
    rtxmon_private_thermal_sample_t thermal;
    int failures = 0;

    reset(&context);
    (void)memset(&voltage, 0, sizeof(voltage));
    voltage.struct_size = (uint32_t)sizeof(voltage);
    failures += check(rtxmon_read_private_voltage_status(&context, 0U, &voltage) == RTXMON_STATUS_OK &&
        voltage.value_flags == RTXMON_PRIVATE_VOLTAGE_CORE_VALID && voltage.gpu_core_voltage_microvolts == 956250U,
        "voltage regression begins with a populated sample");
    mock_voltage_failure = 1;
    mock_now += 100U;
    failures += check(rtxmon_read_private_voltage_status(&context, 0U, &voltage) == RTXMON_STATUS_BACKEND_ERROR &&
        voltage_calls == 2U && voltage.native_status == RTXMON_NVAPI_ERROR &&
        voltage.value_flags == 0U && voltage.gpu_core_voltage_microvolts == 0U && voltage.reserved == 0U,
        "voltage error discards bytes written by the failing callback and the prior valid sample");

    reset(&context);
    mock_voltage_wrong_version = 1;
    (void)memset(&voltage, 0xff, sizeof(voltage));
    voltage.struct_size = (uint32_t)sizeof(voltage);
    failures += check(rtxmon_read_private_voltage_status(&context, 0U, &voltage) == RTXMON_STATUS_NOT_SUPPORTED &&
        voltage_calls == 1U && voltage.native_status == RTXMON_NVAPI_INCOMPATIBLE_STRUCT_VERSION &&
        voltage.value_flags == 0U && voltage.gpu_core_voltage_microvolts == 0U && voltage.reserved == 0U,
        "successful voltage callback with changed structure version cannot publish a value");

    reset(&context);
    mock_thermal_wrong_version = 1;
    (void)memset(&thermal, 0xff, sizeof(thermal));
    thermal.struct_size = (uint32_t)sizeof(thermal);
    failures += check(rtxmon_read_private_thermal_channels(&context, 0U, &thermal) == RTXMON_STATUS_NOT_SUPPORTED &&
        thermal_calls == 1U && thermal.native_status == RTXMON_NVAPI_INCOMPATIBLE_STRUCT_VERSION &&
        thermal.value_flags == 0U && thermal.gpu_die_temperature_millic == 0 &&
        thermal.gpu_hotspot_temperature_millic == 0 && thermal.reserved == 0,
        "successful thermal callback with changed structure version cannot publish a partial pair");
    failures += check(lock_depth == 0, "callback failure paths release the NVAPI lock");
    return failures;
}
#endif

static int test_invalid_outputs(void)
{
    rtxmon_context_t context;
    rtxmon_private_profile_report_t report, before;
    rtxmon_private_thermal_sample_t thermal;
    rtxmon_private_voltage_sample_t voltage;
    int failures = 0;
    reset(&context);
    (void)memset(&report, 0xff, sizeof(report));
    report.struct_size = (uint32_t)sizeof(report);
    failures += check(rtxmon_get_private_profile_status(NULL, 7U, &report) == RTXMON_STATUS_INVALID_ARGUMENT &&
        report.gpu_index == 7U && report.profile_state == 0U && report.thermal_state == 0U &&
        report.voltage_state == 0U && report.profile_id[0] == '\0' && report.revocation_reason[0] == '\0',
        "invalid context clears a correctly sized diagnostic report");
    (void)memset(&report, 0xff, sizeof(report));
    report.struct_size = 4U;
    before = report;
    failures += check(rtxmon_get_private_profile_status(&context, 0U, &report) == RTXMON_STATUS_ABI_MISMATCH &&
        memcmp(&report, &before, sizeof(report)) == 0, "undersized report is untouched");
    (void)memset(&thermal, 0xff, sizeof(thermal));
    (void)memset(&voltage, 0xff, sizeof(voltage));
    thermal.struct_size = (uint32_t)sizeof(thermal);
    voltage.struct_size = (uint32_t)sizeof(voltage);
    failures += check(rtxmon_read_private_thermal_channels(NULL, 7U, &thermal) == RTXMON_STATUS_INVALID_ARGUMENT &&
        thermal.value_flags == 0U && thermal.gpu_die_temperature_millic == 0 && thermal.gpu_hotspot_temperature_millic == 0,
        "null context clears old thermal sample");
    failures += check(rtxmon_read_private_voltage_status(NULL, 7U, &voltage) == RTXMON_STATUS_INVALID_ARGUMENT &&
        voltage.value_flags == 0U && voltage.gpu_core_voltage_microvolts == 0U,
        "null context clears old voltage sample");
    failures += check(rtxmon_get_private_profile_status(&context, 0U, NULL) == RTXMON_STATUS_INVALID_ARGUMENT &&
        rtxmon_read_private_thermal_channels(&context, 0U, NULL) == RTXMON_STATUS_INVALID_ARGUMENT &&
        rtxmon_read_private_voltage_status(&context, 0U, NULL) == RTXMON_STATUS_INVALID_ARGUMENT,
        "null output pointers are rejected");
    failures += check(thermal_calls == 0U && voltage_calls == 0U && lock_depth == 0,
        "invalid calls never acquire samples or retain locks");
    return failures;
}

static int test_rate_fence(void)
{
    rtxmon_context_t first, second;
    rtxmon_private_thermal_sample_t thermal = {0};
    rtxmon_private_voltage_sample_t voltage = {0};
    rtxmon_private_profile_report_t report = {0};
    int failures = 0;
    reset(&first);
    second = first;
    thermal.struct_size = (uint32_t)sizeof(thermal);
    voltage.struct_size = (uint32_t)sizeof(voltage);
    report.struct_size = (uint32_t)sizeof(report);
    failures += check(rtxmon_read_private_thermal_channels(&first, 0U, &thermal) == RTXMON_STATUS_OK &&
        thermal_calls == 2U, "first thermal acquisition is admitted");
    failures += check(rtxmon_read_private_thermal_channels(&second, 0U, &thermal) == RTXMON_STATUS_RATE_LIMITED &&
        thermal_calls == 2U && thermal.value_flags == 0U && thermal.gpu_die_temperature_millic == 0 &&
        thermal.gpu_hotspot_temperature_millic == 0 && thermal.native_status != RTXMON_NVAPI_OK,
        "another context cannot bypass the fence or retain a previous thermal pair");
    failures += check(rtxmon_read_private_voltage_status(&second, 0U, &voltage) == RTXMON_STATUS_OK &&
        voltage_calls == 1U, "voltage fence is independent of thermal");
    failures += check(rtxmon_read_private_voltage_status(&first, 0U, &voltage) == RTXMON_STATUS_RATE_LIMITED &&
        voltage_calls == 1U && voltage.value_flags == 0U && voltage.gpu_core_voltage_microvolts == 0U &&
        voltage.native_status != RTXMON_NVAPI_OK, "voltage rate rejection makes zero additional private calls");
    failures += check(rtxmon_get_private_profile_status(&first, 0U, &report) == RTXMON_STATUS_OK &&
        report.thermal_state == RTXMON_PRIVATE_OPERATION_COMPATIBLE &&
        report.voltage_state == RTXMON_PRIVATE_OPERATION_COMPATIBLE,
        "diagnostic compatibility is independent of the rate fence");
    mock_now += 99U;
    failures += check(rtxmon_read_private_thermal_channels(&second, 0U, &thermal) == RTXMON_STATUS_RATE_LIMITED &&
        thermal_calls == 2U, "99 ms does not admit another thermal acquisition");
    ++mock_now;
    failures += check(rtxmon_read_private_thermal_channels(&second, 0U, &thermal) == RTXMON_STATUS_OK &&
        thermal_calls == 4U, "100 ms boundary admits a complete thermal acquisition");
    mock_voltage_failure = 1;
    failures += check(rtxmon_read_private_voltage_status(&first, 0U, &voltage) == RTXMON_STATUS_BACKEND_ERROR &&
        voltage_calls == 2U, "100 ms boundary also admits voltage even when callback fails");
    mock_voltage_failure = 0;
    failures += check(rtxmon_read_private_voltage_status(&first, 0U, &voltage) == RTXMON_STATUS_RATE_LIMITED &&
        voltage_calls == 2U, "failed private callbacks still consume the operation fence");
    failures += check(lock_depth == 0, "all rate rejections release the owned lock");
    return failures;
}

/* Each case runs in its own test process: the production latch has no reset or
 * clock setter. Link-time fake platform functions provide deterministic time.
 */
static int test_deadline(const char *scenario)
{
    rtxmon_context_t first, second;
    rtxmon_private_thermal_sample_t thermal;
    rtxmon_private_voltage_sample_t voltage;
    rtxmon_private_profile_report_t report = {0};
    rtxmon_status_t status;
    uint32_t expected_thermal = 0U, expected_voltage = 0U;
    uint32_t before_identity, before_association, before_try_lock, before_unlock;
    int failures = 0, voltage_first = 0, owner_depth = 0;
    reset(&first);
    second = first;
    (void)memset(&thermal, 0xff, sizeof(thermal));
    (void)memset(&voltage, 0xff, sizeof(voltage));
    (void)memset(&report, 0xff, sizeof(report));
    thermal.struct_size = (uint32_t)sizeof(thermal);
    voltage.struct_size = (uint32_t)sizeof(voltage);
    report.struct_size = (uint32_t)sizeof(report);
    if (strcmp(scenario, "identity_timeout") == 0) {
        mock_identity_delay = 2000U;
    } else if (strcmp(scenario, "association_timeout") == 0) {
        mock_association_delay = 2000U;
    } else if (strcmp(scenario, "thermal_first_timeout") == 0) {
        mock_thermal_delay[0] = 2000U;
        expected_thermal = 1U;
    } else if (strcmp(scenario, "thermal_second_timeout") == 0) {
        mock_thermal_delay[1] = 2000U;
        expected_thermal = 2U;
    } else if (strcmp(scenario, "voltage_timeout") == 0) {
        mock_voltage_delay = 2000U;
        voltage_first = 1;
        expected_voltage = 1U;
    } else if (strcmp(scenario, "total_timeout") == 0) {
        mock_identity_delay = 1900U;
        mock_thermal_delay[0] = 100U;
        expected_thermal = 1U;
    } else if (strcmp(scenario, "lock_timeout") == 0) {
        mock_lock_blocked = 1;
        lock_depth = owner_depth = 1;
    } else if (strcmp(scenario, "lock_acquired_late") == 0) {
        mock_try_lock_delay = 2000U;
    } else if (strcmp(scenario, "publication_timeout") == 0) {
        mock_publish_delay = 2000U;
        expected_thermal = 2U;
    } else if (strcmp(scenario, "clock_unavailable") == 0) {
        mock_now = UINT64_MAX;
    } else if (strcmp(scenario, "admission_clock_unavailable") == 0 ||
        strcmp(scenario, "admission_clock_regression") == 0 ||
        strcmp(scenario, "admission_clock_deadline") == 0) {
        rtxmon_private_acquisition_t acquisition;
        const rtxmon_private_operation_policy_t *policy = &rtxmon_private_catalog_get()->thermal;
        failures += check(rtxmon_private_acquisition_begin_internal(&acquisition, policy) == RTXMON_STATUS_OK,
            "clock fault scenario starts with a valid lock and acquisition budget");
        mock_clock_reads_until_fault = 2U; /* Check passes; only the fence timestamp is faulty. */
        mock_clock_fault_value = strcmp(scenario, "admission_clock_unavailable") == 0 ? UINT64_MAX
            : strcmp(scenario, "admission_clock_regression") == 0 ? acquisition.started_ms - 1U
            : acquisition.started_ms + policy->timeout_ms;
        failures += check(rtxmon_private_acquisition_admit_internal(&acquisition, RTXMON_PRIVATE_THERMAL, policy)
            == RTXMON_STATUS_TIMEOUT, "the exact fence clock reading is validated before admission");
        failures += check(mock_clock_reads_until_fault == 0U && rtxmon_monotonic_ms_internal() == mock_now,
            "the isolated clock fault recovers on the following read");
        rtxmon_private_acquisition_end_internal(&acquisition);
    } else {
        return check(0, "unknown deadline scenario");
    }
    status = voltage_first
        ? rtxmon_read_private_voltage_status(&first, 0U, &voltage)
        : rtxmon_read_private_thermal_channels(&first, 0U, &thermal);
    failures += check(status == RTXMON_STATUS_TIMEOUT, "elapsed budget returns timeout");
    failures += check(thermal_calls == expected_thermal && voltage_calls == expected_voltage,
        "deadline prevents any subsequent private call");
    failures += check(voltage_first
        ? voltage.value_flags == 0U && voltage.gpu_core_voltage_microvolts == 0U && voltage.reserved == 0U &&
            voltage.native_status != RTXMON_NVAPI_OK
        : thermal.value_flags == 0U && thermal.gpu_die_temperature_millic == 0 &&
            thermal.gpu_hotspot_temperature_millic == 0 && thermal.reserved == 0 && thermal.native_status != RTXMON_NVAPI_OK,
        "late callback bytes and earlier samples are discarded");
    failures += check(lock_depth == owner_depth, "timeout releases only a lock it acquired");
    if (mock_lock_blocked) {
        failures += check(unlock_calls == 0U && identity_calls == 0U && try_lock_calls == 20U,
            "bounded lock wait never unlocks the other owner's lock or enters gates");
    }
    if (mock_identity_delay == 2000U) {
        failures += check(identity_calls == 1U && association_calls == 0U,
            "deadline after first identity gate prevents all subsequent backend gates");
    }
    before_identity = identity_calls;
    before_association = association_calls;
    before_try_lock = try_lock_calls;
    before_unlock = unlock_calls;
    mock_publish_delay = 0U;
    failures += check(rtxmon_read_private_thermal_channels(&second, 0U, &thermal) == RTXMON_STATUS_TIMEOUT &&
        rtxmon_read_private_voltage_status(&second, 0U, &voltage) == RTXMON_STATUS_TIMEOUT,
        "timeout permanently blocks both operations even through another context");
    failures += check(rtxmon_get_private_profile_status(&second, 0U, &report) == RTXMON_STATUS_OK &&
        report.thermal_state == RTXMON_PRIVATE_OPERATION_TIMEOUT &&
        report.voltage_state == RTXMON_PRIVATE_OPERATION_TIMEOUT &&
        report.struct_size == sizeof(report) && report.gpu_index == 0U && report.profile_revision == 2U &&
        report.identity_checked_flags == 0U && report.identity_match_flags == 0U &&
        report.thermal_min_interval_ms == 100U && report.voltage_timeout_ms == 2000U &&
        report.revocation_reason[0] == '\0',
        "diagnostic reports process timeout without further backend access");
    failures += check(thermal_calls == expected_thermal && voltage_calls == expected_voltage &&
        identity_calls == before_identity && association_calls == before_association &&
        try_lock_calls == before_try_lock && unlock_calls == before_unlock && lock_depth == owner_depth,
        "latched process attempts neither backend access nor lock acquisition");
    return failures;
}

int main(int argc, char **argv)
{
    rtxmon_context_t context;
    int failures = 0;
    uint32_t i;
    uint32_t *pci_fields[] = {&mock_vendor, &mock_device, &mock_subsystem_vendor, &mock_subsystem_device};
    const uint32_t compatible = RTXMON_PRIVATE_OPERATION_COMPATIBLE;
    const uint32_t unavailable = RTXMON_PRIVATE_OPERATION_IDENTITY_UNAVAILABLE;
    const uint32_t mismatch = RTXMON_PRIVATE_OPERATION_IDENTITY_MISMATCH;
    const uint32_t module = RTXMON_PRIVATE_OPERATION_MODULE_UNAVAILABLE;
    const uint32_t not_found = RTXMON_PRIVATE_OPERATION_GPU_NOT_FOUND;
    const uint32_t failed = RTXMON_PRIVATE_OPERATION_QUERY_FAILED;
    if (argc == 2) {
        failures = strcmp(argv[1], "rate_fence") == 0 ? test_rate_fence() : test_deadline(argv[1]);
        return failures == 0 ? 0 : 1;
    }
#define CASE(state, checked, matched, label) run_case(&context, 0U, state, state, checked, matched, label)
    reset(&context);
    failures += CASE(compatible, 127U, 127U, "reviewed identity has compatible gates");
    for (i = 0U; i < 4U; ++i) {
        reset(&context);
        ++*pci_fields[i];
        failures += CASE(mismatch, 127U, 127U & ~(1U << i), "each PCI identity field gates both readers");
    }
    reset(&context); mock_uuid = "GPU-00000000-0000-0000-0000-000000000000";
    failures += CASE(mismatch, 127U, 111U, "UUID mismatch blocks both readers");
    reset(&context); mock_driver = "611.00";
    failures += CASE(mismatch, 127U, 63U, "driver drift blocks both readers");
    reset(&context); mock_vbios = "94.06.25.00.fd";
    failures += CASE(mismatch, 127U, 95U, "VBIOS drift blocks both readers");
    reset(&context); context.nvml.device_get_uuid = NULL;
    failures += CASE(unavailable, 111U, 111U, "missing UUID provider blocks both readers");
    reset(&context); mock_unterminated_uuid = 1;
    failures += CASE(unavailable, 111U, 111U, "unterminated UUID fails closed");
    reset(&context); mock_uuid = "";
    failures += CASE(unavailable, 111U, 111U, "empty UUID is unavailable");
    reset(&context); mock_uuid_result = NVML_ERROR_NOT_SUPPORTED;
    failures += CASE(unavailable, 111U, 111U, "unsupported UUID query is unavailable");
    reset(&context); mock_uuid_result = NVML_ERROR_UNKNOWN;
    failures += CASE(failed, 111U, 111U, "UUID query error cannot reuse returned bytes");
    reset(&context); context.nvml.system_get_driver_version = NULL;
    failures += CASE(unavailable, 63U, 63U, "missing driver identity blocks both readers");
    reset(&context); mock_driver_result = NVML_ERROR_UNKNOWN;
    failures += CASE(failed, 63U, 63U, "driver query failure blocks both readers");
    reset(&context); context.nvml.device_get_handle_by_index_v2 = NULL;
    failures += CASE(unavailable, 0U, 0U, "missing NVML device provider clears old samples");
    reset(&context); mock_device_result = NVML_ERROR_UNKNOWN;
    failures += CASE(failed, 0U, 0U, "NVML device error clears old samples");
    reset(&context); mock_pci_result = NVML_ERROR_UNKNOWN;
    failures += CASE(failed, 0U, 0U, "PCI query failure clears old samples");
    reset(&context); mock_pci_result = NVML_ERROR_FUNCTION_NOT_FOUND;
    failures += CASE(unavailable, 0U, 0U, "missing PCI provider clears old samples");
    reset(&context);
    failures += run_case(&context, 7U, not_found, not_found, 0U, 0U, "missing GPU clears old samples");
    reset(&context); context.nvapi_initialized = 0;
    failures += CASE(module, 127U, 127U, "uninitialized NVAPI blocks both readers");
    reset(&context); context.nvapi.gpu_therm_channel_get_status = NULL;
    failures += run_case(&context, 0U, module, compatible, 127U, 127U, "thermal module is gated independently");
    reset(&context); context.nvapi.gpu_voltage_status = NULL;
    failures += run_case(&context, 0U, compatible, module, 127U, 127U, "voltage module is gated independently");
    reset(&context); context.nvapi.gpu_get_bus_id = NULL;
    failures += CASE(module, 127U, 127U, "missing association interface blocks both readers");
    reset(&context); mock_gpu_count = 0U;
    failures += CASE(not_found, 127U, 127U, "empty NVAPI enumeration clears old samples");
    reset(&context); mock_bus = 2U;
    failures += CASE(not_found, 127U, 127U, "wrong NVAPI association blocks both readers");
    reset(&context); mock_gpu_count = 2U;
    failures += CASE(RTXMON_PRIVATE_OPERATION_IDENTITY_AMBIGUOUS, 127U, 127U, "duplicate associations never select a GPU");
    reset(&context); mock_gpu_count = 2U; mock_second_identity_failure = 1;
    failures += CASE(failed, 127U, 127U, "an unreadable second GPU prevents proving unique association");
    reset(&context); mock_enum_failure = 1;
    failures += CASE(failed, 127U, 127U, "enumeration failure clears old samples");
    reset(&context); mock_gpu_count = RTXMON_NVAPI_MAX_PHYSICAL_GPUS + 1U;
    failures += CASE(failed, 127U, 127U, "oversized enumeration is rejected before iteration");
    failures += test_invalid_outputs();
#if !RTXMON_TEST_PROFILE_REVOKED && !RTXMON_TEST_THERMAL_REVOKED && !RTXMON_TEST_VOLTAGE_REVOKED
    failures += test_private_output_validation();
    failures += test_recorded_rtx3060_responses();
#endif
#undef CASE
    return failures == 0 ? 0 : 1;
}
