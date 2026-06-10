#ifndef CIRCLE_AI_DEVICE_H
#define CIRCLE_AI_DEVICE_H

/*
 * device.h — DeviceProbe + tier classification.
 */

#include <stdint.h>

typedef enum {
    CA_TIER_PHONE       = 0,
    CA_TIER_WEARABLE    = 1,
    CA_TIER_TABLET      = 2,
    CA_TIER_LAPTOP      = 3,
    CA_TIER_WORKSTATION = 4,
    CA_TIER_EMBEDDED    = 5
} ca_device_tier_t;

typedef enum {
    CA_GPU_NONE          = 0,
    CA_GPU_INTEGRATED    = 1,
    CA_GPU_DISCRETE      = 2,
    CA_GPU_NEURAL_ENGINE = 3
} ca_gpu_kind_t;

typedef enum {
    CA_THERMAL_NORMAL   = 0,
    CA_THERMAL_FAIR     = 1,
    CA_THERMAL_SERIOUS  = 2,
    CA_THERMAL_CRITICAL = 3
} ca_thermal_class_t;

typedef enum {
    CA_CONN_OFFLINE  = 0,
    CA_CONN_CELLULAR = 1,
    CA_CONN_WIFI     = 2,
    CA_CONN_ETHERNET = 3
} ca_connectivity_t;

typedef struct {
    ca_device_tier_t   tier;
    uint64_t           ram_bytes;
    uint64_t           free_storage_bytes;
    uint32_t           cpu_cores;
    ca_gpu_kind_t      gpu_kind;
    ca_thermal_class_t thermal;
    ca_connectivity_t  connectivity;
    const char        *os;        /* "linux"/"darwin"/"windows" */
    const char        *arch;      /* "x86_64"/"aarch64" */
} ca_device_snapshot_t;

typedef struct {
    uint32_t context_window;
    uint32_t max_concurrent;
    uint32_t max_agentic_iterations;
} ca_device_tier_defaults_t;

ca_device_tier_defaults_t ca_device_tier_defaults_for(ca_device_tier_t tier);

/* Probes the current device. Best-effort: zeros fields it can't determine. */
ca_device_snapshot_t ca_device_probe(void);

#endif /* CIRCLE_AI_DEVICE_H */
