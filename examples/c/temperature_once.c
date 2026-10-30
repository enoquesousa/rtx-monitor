#include <rtxmon/rtxmon.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int fail(rtxmon_status_t status)
{
    (void)fprintf(
        stderr,
        "rtxmon error: %s; %s\n",
        rtxmon_status_string(status),
        rtxmon_last_error());
    return 1;
}

int main(int argc, char **argv)
{
    rtxmon_context_t *context = NULL;
    rtxmon_gpu_info_t gpu;
    rtxmon_temperature_sample_t sample;
    rtxmon_status_t status;
    uint32_t gpu_index = 0U;

    if (argc > 2) {
        (void)fprintf(stderr, "usage: rtxmon-c [gpu-index]\n");
        return 2;
    }

    if (argc == 2) {
        char *end = NULL;
        unsigned long parsed = strtoul(argv[1], &end, 10);
        if (end == argv[1] || *end != '\0' || parsed > UINT32_MAX) {
            (void)fprintf(stderr, "invalid GPU index: %s\n", argv[1]);
            return 2;
        }
        gpu_index = (uint32_t)parsed;
    }

    status = rtxmon_context_create(&context);
    if (status != RTXMON_STATUS_OK) {
        return fail(status);
    }

    (void)memset(&gpu, 0, sizeof(gpu));
    gpu.struct_size = (uint32_t)sizeof(gpu);
    status = rtxmon_get_gpu_info(context, gpu_index, &gpu);
    if (status != RTXMON_STATUS_OK) {
        rtxmon_context_destroy(context);
        return fail(status);
    }

    (void)memset(&sample, 0, sizeof(sample));
    sample.struct_size = (uint32_t)sizeof(sample);
    status = rtxmon_read_gpu_die_temperature(context, gpu_index, &sample);
    if (status != RTXMON_STATUS_OK) {
        rtxmon_context_destroy(context);
        return fail(status);
    }

    (void)printf("GPU %u: %s\n", gpu.index, gpu.name);
    (void)printf("UUID: %s\n", gpu.uuid);
    (void)printf("Driver: %s | NVML: %s\n", gpu.driver_version, gpu.nvml_version);
    (void)printf("GPU die temperature: %d C\n", sample.temperature_c);
    (void)printf("Source: %s\n", rtxmon_temperature_backend_string(sample.backend));
    (void)printf("Timestamp (Unix ms): %llu\n", (unsigned long long)sample.timestamp_unix_ms);

    rtxmon_context_destroy(context);
    return 0;
}
