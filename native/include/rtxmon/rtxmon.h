#ifndef RTXMON_RTXMON_H
#define RTXMON_RTXMON_H

#include <stdint.h>

#if defined(_WIN32)
#if defined(RTXMON_BUILDING_NATIVE)
#define RTXMON_API __declspec(dllexport)
#else
#define RTXMON_API __declspec(dllimport)
#endif
#define RTXMON_CALL __cdecl
#else
#define RTXMON_API __attribute__((visibility("default")))
#define RTXMON_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define RTXMON_ABI_VERSION 4U
#define RTXMON_TEXT_CAPACITY 96U
#define RTXMON_MAX_THERMAL_PROVIDERS 3U
#define RTXMON_MAX_THERMAL_CAPABILITIES 8U
#define RTXMON_MAX_PUBLIC_FIELDS 48U
#define RTXMON_MAX_COMPUTED_METRICS 4U
#define RTXMON_MAX_METRIC_INPUTS 2U

typedef struct rtxmon_context rtxmon_context_t;
typedef struct rtxmon_metrics_context rtxmon_metrics_context_t;

typedef enum rtxmon_status {
    RTXMON_STATUS_OK = 0,
    RTXMON_STATUS_INVALID_ARGUMENT = 1,
    RTXMON_STATUS_OUT_OF_MEMORY = 2,
    RTXMON_STATUS_BACKEND_NOT_FOUND = 3,
    RTXMON_STATUS_BACKEND_SYMBOL_MISSING = 4,
    RTXMON_STATUS_DRIVER_NOT_LOADED = 5,
    RTXMON_STATUS_NO_PERMISSION = 6,
    RTXMON_STATUS_GPU_NOT_FOUND = 7,
    RTXMON_STATUS_NOT_SUPPORTED = 8,
    RTXMON_STATUS_GPU_LOST = 9,
    RTXMON_STATUS_BACKEND_ERROR = 10,
    RTXMON_STATUS_ABI_MISMATCH = 11
} rtxmon_status_t;

typedef enum rtxmon_sensor_kind {
    RTXMON_SENSOR_GPU_DIE = 0
} rtxmon_sensor_kind_t;

typedef enum rtxmon_temperature_backend {
    RTXMON_BACKEND_NVML_TEMPERATURE_V1 = 1,
    RTXMON_BACKEND_NVML_TEMPERATURE_LEGACY = 2
} rtxmon_temperature_backend_t;

typedef enum rtxmon_thermal_provider {
    RTXMON_PROVIDER_NVML_THERMAL_SETTINGS = 1,
    RTXMON_PROVIDER_NVML_FIELD_VALUES = 2,
    RTXMON_PROVIDER_NVAPI_THERMAL_SETTINGS = 3
} rtxmon_thermal_provider_t;

typedef enum rtxmon_capability_state {
    RTXMON_CAPABILITY_UNKNOWN = 0,
    RTXMON_CAPABILITY_AVAILABLE = 1,
    RTXMON_CAPABILITY_NOT_SUPPORTED = 2,
    RTXMON_CAPABILITY_PROVIDER_UNAVAILABLE = 3,
    RTXMON_CAPABILITY_QUERY_FAILED = 4
} rtxmon_capability_state_t;

typedef enum rtxmon_thermal_target {
    RTXMON_THERMAL_TARGET_NONE = 0,
    RTXMON_THERMAL_TARGET_GPU = 1,
    RTXMON_THERMAL_TARGET_MEMORY = 2,
    RTXMON_THERMAL_TARGET_POWER_SUPPLY = 4,
    RTXMON_THERMAL_TARGET_BOARD = 8,
    RTXMON_THERMAL_TARGET_VCD_BOARD = 9,
    RTXMON_THERMAL_TARGET_VCD_INLET = 10,
    RTXMON_THERMAL_TARGET_VCD_OUTLET = 11,
    RTXMON_THERMAL_TARGET_UNKNOWN = 255
} rtxmon_thermal_target_t;

typedef enum rtxmon_thermal_controller {
    RTXMON_THERMAL_CONTROLLER_NONE = 0,
    RTXMON_THERMAL_CONTROLLER_GPU_INTERNAL = 1,
    RTXMON_THERMAL_CONTROLLER_ADM1032 = 2,
    RTXMON_THERMAL_CONTROLLER_ADT7461 = 3,
    RTXMON_THERMAL_CONTROLLER_MAX6649 = 4,
    RTXMON_THERMAL_CONTROLLER_MAX1617 = 5,
    RTXMON_THERMAL_CONTROLLER_LM99 = 6,
    RTXMON_THERMAL_CONTROLLER_LM89 = 7,
    RTXMON_THERMAL_CONTROLLER_LM64 = 8,
    RTXMON_THERMAL_CONTROLLER_G781 = 9,
    RTXMON_THERMAL_CONTROLLER_ADT7473 = 10,
    RTXMON_THERMAL_CONTROLLER_SBMAX6649 = 11,
    RTXMON_THERMAL_CONTROLLER_VBIOSEVT = 12,
    RTXMON_THERMAL_CONTROLLER_OS = 13,
    RTXMON_THERMAL_CONTROLLER_NVSYSCON_CANOAS = 14,
    RTXMON_THERMAL_CONTROLLER_NVSYSCON_E551 = 15,
    RTXMON_THERMAL_CONTROLLER_MAX6649R = 16,
    RTXMON_THERMAL_CONTROLLER_ADT7473S = 17,
    RTXMON_THERMAL_CONTROLLER_UNKNOWN = 255
} rtxmon_thermal_controller_t;

typedef enum rtxmon_sensor_confidence {
    RTXMON_CONFIDENCE_UNKNOWN = 0,
    RTXMON_CONFIDENCE_DRIVER_REPORTED = 1,
    RTXMON_CONFIDENCE_EXPERIMENTAL = 2
} rtxmon_sensor_confidence_t;

typedef enum rtxmon_data_origin {
    RTXMON_ORIGIN_UNKNOWN = 0,
    RTXMON_ORIGIN_DRIVER_REPORTED = 1,
    RTXMON_ORIGIN_COMPUTED = 2,
    RTXMON_ORIGIN_EXPERIMENTAL = 3
} rtxmon_data_origin_t;

typedef enum rtxmon_public_field {
    RTXMON_PUBLIC_FIELD_GPU_DIE_TEMPERATURE_C = 1,
    RTXMON_PUBLIC_FIELD_MEMORY_TEMPERATURE_C = 2,
    RTXMON_PUBLIC_FIELD_TOTAL_ENERGY_MJ = 3,
    RTXMON_PUBLIC_FIELD_POWER_AVERAGE_MW = 4,
    RTXMON_PUBLIC_FIELD_POWER_INSTANT_MW = 5,
    RTXMON_PUBLIC_FIELD_POWER_LIMIT_MIN_MW = 6,
    RTXMON_PUBLIC_FIELD_POWER_LIMIT_MAX_MW = 7,
    RTXMON_PUBLIC_FIELD_POWER_LIMIT_DEFAULT_MW = 8,
    RTXMON_PUBLIC_FIELD_POWER_LIMIT_CURRENT_MW = 9,
    RTXMON_PUBLIC_FIELD_POWER_LIMIT_REQUESTED_MW = 10,
    RTXMON_PUBLIC_FIELD_TEMPERATURE_SHUTDOWN_C = 11,
    RTXMON_PUBLIC_FIELD_TEMPERATURE_SLOWDOWN_C = 12,
    RTXMON_PUBLIC_FIELD_TEMPERATURE_MEMORY_MAX_C = 13,
    RTXMON_PUBLIC_FIELD_TEMPERATURE_GPU_MAX_C = 14,
    RTXMON_PUBLIC_FIELD_CLOCK_GRAPHICS_MHZ = 15,
    RTXMON_PUBLIC_FIELD_CLOCK_SM_MHZ = 16,
    RTXMON_PUBLIC_FIELD_CLOCK_MEMORY_MHZ = 17,
    RTXMON_PUBLIC_FIELD_CLOCK_VIDEO_MHZ = 18,
    RTXMON_PUBLIC_FIELD_UTILIZATION_GPU_PERCENT = 19,
    RTXMON_PUBLIC_FIELD_UTILIZATION_MEMORY_PERCENT = 20,
    RTXMON_PUBLIC_FIELD_MEMORY_TOTAL_BYTES = 21,
    RTXMON_PUBLIC_FIELD_MEMORY_FREE_BYTES = 22,
    RTXMON_PUBLIC_FIELD_MEMORY_USED_BYTES = 23,
    RTXMON_PUBLIC_FIELD_FAN_SPEED_PERCENT = 24,
    RTXMON_PUBLIC_FIELD_PERFORMANCE_STATE = 25,
    RTXMON_PUBLIC_FIELD_CLOCK_EVENT_REASONS_CURRENT = 26,
    RTXMON_PUBLIC_FIELD_CLOCK_EVENT_REASONS_SUPPORTED = 27,
    RTXMON_PUBLIC_FIELD_ENCODER_UTILIZATION_PERCENT = 28,
    RTXMON_PUBLIC_FIELD_ENCODER_SAMPLING_PERIOD_US = 29,
    RTXMON_PUBLIC_FIELD_DECODER_UTILIZATION_PERCENT = 30,
    RTXMON_PUBLIC_FIELD_DECODER_SAMPLING_PERIOD_US = 31
} rtxmon_public_field_t;

typedef enum rtxmon_public_provider {
    RTXMON_PUBLIC_PROVIDER_NVML_TEMPERATURE_V1 = 1,
    RTXMON_PUBLIC_PROVIDER_NVML_TEMPERATURE_LEGACY = 2,
    RTXMON_PUBLIC_PROVIDER_NVML_FIELD_VALUES = 3,
    RTXMON_PUBLIC_PROVIDER_NVML_CLOCK_INFO = 4,
    RTXMON_PUBLIC_PROVIDER_NVML_UTILIZATION_RATES = 5,
    RTXMON_PUBLIC_PROVIDER_NVML_MEMORY_INFO = 6,
    RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_V2 = 7,
    RTXMON_PUBLIC_PROVIDER_NVML_FAN_SPEED_LEGACY = 8,
    RTXMON_PUBLIC_PROVIDER_NVML_PERFORMANCE_STATE = 9,
    RTXMON_PUBLIC_PROVIDER_NVML_CLOCK_EVENT_REASONS = 10,
    RTXMON_PUBLIC_PROVIDER_NVML_CLOCK_THROTTLE_REASONS_LEGACY = 11,
    RTXMON_PUBLIC_PROVIDER_NVML_ENCODER_UTILIZATION = 12,
    RTXMON_PUBLIC_PROVIDER_NVML_DECODER_UTILIZATION = 13,
    RTXMON_PUBLIC_PROVIDER_NVML_SUPPORTED_CLOCK_EVENT_REASONS = 14,
    RTXMON_PUBLIC_PROVIDER_NVML_SUPPORTED_CLOCK_THROTTLE_REASONS_LEGACY = 15
} rtxmon_public_provider_t;

typedef enum rtxmon_value_type {
    RTXMON_VALUE_TYPE_UNKNOWN = 0,
    RTXMON_VALUE_TYPE_UNSIGNED_INTEGER = 1,
    RTXMON_VALUE_TYPE_SIGNED_INTEGER = 2,
    RTXMON_VALUE_TYPE_DOUBLE = 3,
    RTXMON_VALUE_TYPE_BITMASK = 4
} rtxmon_value_type_t;

typedef enum rtxmon_unit {
    RTXMON_UNIT_UNKNOWN = 0,
    RTXMON_UNIT_CELSIUS = 1,
    RTXMON_UNIT_MILLIWATT = 2,
    RTXMON_UNIT_MILLIJOULE = 3,
    RTXMON_UNIT_MEGAHERTZ = 4,
    RTXMON_UNIT_PERCENT = 5,
    RTXMON_UNIT_BYTES = 6,
    RTXMON_UNIT_PSTATE = 7,
    RTXMON_UNIT_BITMASK = 8,
    RTXMON_UNIT_MICROSECONDS = 9,
    RTXMON_UNIT_CELSIUS_PER_SECOND = 10,
    RTXMON_UNIT_SECONDS = 11
} rtxmon_unit_t;

typedef enum rtxmon_computed_metric_kind {
    RTXMON_METRIC_GPU_TEMPERATURE_WINDOW_AVERAGE = 1,
    RTXMON_METRIC_GPU_TEMPERATURE_SLOPE = 2,
    RTXMON_METRIC_GPU_TEMPERATURE_TIME_ABOVE_THRESHOLD = 3,
    RTXMON_METRIC_GPU_MEMORY_TEMPERATURE_DELTA = 4
} rtxmon_computed_metric_kind_t;

typedef enum rtxmon_metric_state {
    RTXMON_METRIC_STATE_UNKNOWN = 0,
    RTXMON_METRIC_STATE_AVAILABLE = 1,
    RTXMON_METRIC_STATE_INSUFFICIENT_DATA = 2,
    RTXMON_METRIC_STATE_INPUT_UNAVAILABLE = 3
} rtxmon_metric_state_t;

enum {
    RTXMON_THERMAL_VALUE_CURRENT_VALID = 1U << 0U,
    RTXMON_THERMAL_VALUE_DEFAULT_MIN_VALID = 1U << 1U,
    RTXMON_THERMAL_VALUE_DEFAULT_MAX_VALID = 1U << 2U
};

enum {
    RTXMON_BOARD_IDENTITY_PCI_VALID = 1U << 0U,
    RTXMON_BOARD_IDENTITY_VBIOS_VALID = 1U << 1U
};

typedef struct rtxmon_gpu_info {
    uint32_t struct_size;
    uint32_t index;
    char name[RTXMON_TEXT_CAPACITY];
    char uuid[RTXMON_TEXT_CAPACITY];
    char driver_version[RTXMON_TEXT_CAPACITY];
    char nvml_version[RTXMON_TEXT_CAPACITY];
} rtxmon_gpu_info_t;

typedef struct rtxmon_temperature_sample {
    uint32_t struct_size;
    uint32_t gpu_index;
    int32_t temperature_c;
    uint32_t sensor_kind;
    uint32_t backend;
    uint32_t reserved;
    uint64_t timestamp_unix_ms;
} rtxmon_temperature_sample_t;

enum {
    RTXMON_PRIVATE_THERMAL_DIE_VALID = 1U << 0U,
    RTXMON_PRIVATE_THERMAL_HOTSPOT_VALID = 1U << 1U
};

typedef struct rtxmon_private_thermal_sample {
    uint32_t struct_size;
    uint32_t gpu_index;
    uint32_t value_flags;
    int32_t native_status;
    int32_t gpu_die_temperature_millic;
    int32_t gpu_hotspot_temperature_millic;
    int32_t reserved;
    uint64_t timestamp_unix_ms;
} rtxmon_private_thermal_sample_t;

typedef struct rtxmon_board_identity {
    uint32_t struct_size;
    uint32_t gpu_index;
    uint32_t pci_vendor_id;
    uint32_t pci_device_id;
    uint32_t pci_subsystem_vendor_id;
    uint32_t pci_subsystem_device_id;
    uint32_t pci_domain;
    uint32_t pci_bus;
    uint32_t pci_device;
    uint32_t pci_function;
    uint32_t flags;
    uint32_t reserved;
    char pci_bus_id[RTXMON_TEXT_CAPACITY];
    char vbios_version[RTXMON_TEXT_CAPACITY];
} rtxmon_board_identity_t;

typedef struct rtxmon_thermal_provider_result {
    uint32_t provider;
    uint32_t state;
    int32_t native_status;
    /* Number of emitted capability records, including explicit negative results. */
    uint32_t capability_count;
} rtxmon_thermal_provider_result_t;

typedef struct rtxmon_thermal_capability {
    uint32_t provider;
    uint32_t target;
    uint32_t controller;
    uint32_t state;
    uint32_t confidence;
    uint32_t value_flags;
    int32_t current_temperature_c;
    int32_t default_min_temperature_c;
    int32_t default_max_temperature_c;
    int32_t native_status;
    /* Provider-specific ID: thermal array index or NVML field ID. */
    uint32_t provider_native_id;
    uint32_t reserved;
} rtxmon_thermal_capability_t;

typedef struct rtxmon_thermal_report {
    uint32_t struct_size;
    uint32_t gpu_index;
    uint32_t provider_count;
    uint32_t capability_count;
    uint64_t timestamp_unix_ms;
    rtxmon_thermal_provider_result_t providers[RTXMON_MAX_THERMAL_PROVIDERS];
    rtxmon_thermal_capability_t capabilities[RTXMON_MAX_THERMAL_CAPABILITIES];
} rtxmon_thermal_report_t;

typedef struct rtxmon_public_field_value {
    uint32_t field;
    uint32_t provider;
    uint32_t state;
    uint32_t origin;
    uint32_t value_type;
    uint32_t unit;
    int32_t native_status;
    uint32_t provider_native_id;
    uint64_t value_u64;
    int64_t value_i64;
    double value_f64;
    uint64_t timestamp_unix_ms;
} rtxmon_public_field_value_t;

typedef struct rtxmon_public_telemetry_report {
    uint32_t struct_size;
    uint32_t gpu_index;
    uint32_t field_count;
    uint32_t reserved;
    uint64_t timestamp_unix_ms;
    rtxmon_public_field_value_t fields[RTXMON_MAX_PUBLIC_FIELDS];
} rtxmon_public_telemetry_report_t;

typedef struct rtxmon_metrics_options {
    uint32_t struct_size;
    uint32_t window_ms;
    int32_t temperature_threshold_c;
    uint32_t maximum_samples;
} rtxmon_metrics_options_t;

typedef struct rtxmon_computed_metric {
    uint32_t metric;
    uint32_t state;
    uint32_t origin;
    uint32_t unit;
    double value;
    uint64_t timestamp_unix_ms;
    uint64_t window_ms;
    uint32_t sample_count;
    uint32_t input_count;
    int32_t temperature_threshold_c;
    uint32_t reserved;
    uint32_t input_fields[RTXMON_MAX_METRIC_INPUTS];
} rtxmon_computed_metric_t;

typedef struct rtxmon_computed_metrics_report {
    uint32_t struct_size;
    uint32_t gpu_index;
    uint32_t metric_count;
    uint32_t reserved;
    uint64_t timestamp_unix_ms;
    rtxmon_computed_metric_t metrics[RTXMON_MAX_COMPUTED_METRICS];
} rtxmon_computed_metrics_report_t;

RTXMON_API uint32_t RTXMON_CALL rtxmon_abi_version(void);
RTXMON_API const char *RTXMON_CALL rtxmon_status_string(rtxmon_status_t status);
RTXMON_API const char *RTXMON_CALL rtxmon_temperature_backend_string(uint32_t backend);
RTXMON_API const char *RTXMON_CALL rtxmon_thermal_provider_string(uint32_t provider);
RTXMON_API const char *RTXMON_CALL rtxmon_capability_state_string(uint32_t state);
RTXMON_API const char *RTXMON_CALL rtxmon_thermal_target_string(uint32_t target);
RTXMON_API const char *RTXMON_CALL rtxmon_thermal_controller_string(uint32_t controller);
RTXMON_API const char *RTXMON_CALL rtxmon_sensor_confidence_string(uint32_t confidence);
RTXMON_API const char *RTXMON_CALL rtxmon_data_origin_string(uint32_t origin);
RTXMON_API const char *RTXMON_CALL rtxmon_public_field_string(uint32_t field);
RTXMON_API const char *RTXMON_CALL rtxmon_public_provider_string(uint32_t provider);
RTXMON_API const char *RTXMON_CALL rtxmon_value_type_string(uint32_t value_type);
RTXMON_API const char *RTXMON_CALL rtxmon_unit_string(uint32_t unit);
RTXMON_API const char *RTXMON_CALL rtxmon_computed_metric_string(uint32_t metric);
RTXMON_API const char *RTXMON_CALL rtxmon_computed_metric_formula(uint32_t metric);
RTXMON_API const char *RTXMON_CALL rtxmon_metric_state_string(uint32_t state);

/*
 * Returns a thread-local diagnostic for the most recent API call on the
 * current thread. The pointer remains valid until the next rtxmon call on
 * that thread.
 */
RTXMON_API const char *RTXMON_CALL rtxmon_last_error(void);

RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_context_create(rtxmon_context_t **out_context);

RTXMON_API void RTXMON_CALL
rtxmon_context_destroy(rtxmon_context_t *context);

RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_get_gpu_count(rtxmon_context_t *context, uint32_t *out_count);

/* Set out_info->struct_size to sizeof(rtxmon_gpu_info_t) before calling. */
RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_get_gpu_info(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_gpu_info_t *out_info);

/* Set out_identity->struct_size to sizeof(rtxmon_board_identity_t). */
RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_get_board_identity(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_board_identity_t *out_identity);

/* Set out_sample->struct_size to sizeof(rtxmon_temperature_sample_t). */
RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_read_gpu_die_temperature(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_temperature_sample_t *out_sample);

/* Reads driver thermal channels directly through NVAPI; GPU-Z is not used. */
RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_read_private_thermal_channels(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_private_thermal_sample_t *out_sample);

/* Set out_report->struct_size to sizeof(rtxmon_thermal_report_t). */
RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_scan_thermal_capabilities(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_thermal_report_t *out_report);

/* Set out_report->struct_size to sizeof(rtxmon_public_telemetry_report_t). */
RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_read_public_telemetry(
    rtxmon_context_t *context,
    uint32_t gpu_index,
    rtxmon_public_telemetry_report_t *out_report);

/*
 * Creates a bounded, per-GPU metric window. Options are copied by the callee.
 * The context is independent from rtxmon_context_t and never touches hardware.
 */
RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_metrics_context_create(
    const rtxmon_metrics_options_t *options,
    rtxmon_metrics_context_t **out_context);

RTXMON_API void RTXMON_CALL
rtxmon_metrics_context_destroy(rtxmon_metrics_context_t *context);

RTXMON_API void RTXMON_CALL
rtxmon_metrics_context_reset(rtxmon_metrics_context_t *context);

/* Set out_report->struct_size to sizeof(rtxmon_computed_metrics_report_t). */
RTXMON_API rtxmon_status_t RTXMON_CALL
rtxmon_metrics_observe(
    rtxmon_metrics_context_t *context,
    const rtxmon_public_telemetry_report_t *telemetry,
    rtxmon_computed_metrics_report_t *out_report);

#ifdef __cplusplus
}
#endif

#endif
