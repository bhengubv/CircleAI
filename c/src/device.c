/*
 * device.c — DeviceProbe + tier defaults.
 */

#include "circle_ai/device.h"
#include <string.h>

#if defined(__APPLE__) || defined(__linux__) || defined(__unix__)
  #include <unistd.h>
  #include <sys/statvfs.h>
  #define CA_HAS_UNIX_SYSCONF 1
#endif

#if defined(__APPLE__)
  #include <sys/sysctl.h>
#endif

ca_device_tier_defaults_t ca_device_tier_defaults_for(ca_device_tier_t tier) {
    ca_device_tier_defaults_t d;
    memset(&d, 0, sizeof(d));
    switch (tier) {
        case CA_TIER_WEARABLE:
            d.context_window = 1024;  d.max_concurrent = 1;  d.max_agentic_iterations = 2;  break;
        case CA_TIER_PHONE:
            d.context_window = 4096;  d.max_concurrent = 2;  d.max_agentic_iterations = 4;  break;
        case CA_TIER_EMBEDDED:
            d.context_window = 2048;  d.max_concurrent = 1;  d.max_agentic_iterations = 2;  break;
        case CA_TIER_TABLET:
            d.context_window = 8192;  d.max_concurrent = 4;  d.max_agentic_iterations = 8;  break;
        case CA_TIER_LAPTOP:
            d.context_window = 16384; d.max_concurrent = 6;  d.max_agentic_iterations = 16; break;
        case CA_TIER_WORKSTATION:
            d.context_window = 32768; d.max_concurrent = 12; d.max_agentic_iterations = 32; break;
    }
    return d;
}

static ca_device_tier_t classify_tier(uint64_t ram_bytes, uint32_t cpu_cores) {
    uint64_t gib = ram_bytes / (1024ull * 1024ull * 1024ull);
    if (gib >= 32 && cpu_cores >= 16) return CA_TIER_WORKSTATION;
    if (gib >= 16 && cpu_cores >= 8)  return CA_TIER_LAPTOP;
    if (gib >= 6  && cpu_cores >= 4)  return CA_TIER_TABLET;
    if (gib >= 3) return CA_TIER_PHONE;
    if (gib >= 1) return CA_TIER_EMBEDDED;
    return CA_TIER_WEARABLE;
}

#if defined(__linux__) || defined(__APPLE__)
static uint64_t probe_ram(void) {
    long pages = sysconf(_SC_PHYS_PAGES);
    long page_size = sysconf(_SC_PAGESIZE);
    if (pages > 0 && page_size > 0) return (uint64_t)pages * (uint64_t)page_size;
    return 0;
}

static uint64_t probe_free_storage(void) {
    struct statvfs s;
    if (statvfs("/", &s) == 0) return (uint64_t)s.f_bavail * (uint64_t)s.f_frsize;
    return 0;
}

static uint32_t probe_cpu_cores(void) {
    long n = sysconf(_SC_NPROCESSORS_ONLN);
    return n > 0 ? (uint32_t)n : 1;
}
#else
static uint64_t probe_ram(void) { return 0; }
static uint64_t probe_free_storage(void) { return 0; }
static uint32_t probe_cpu_cores(void) { return 1; }
#endif

static const char *probe_os(void) {
#if defined(__APPLE__)
    return "darwin";
#elif defined(__linux__)
    return "linux";
#elif defined(_WIN32)
    return "windows";
#else
    return "unknown";
#endif
}

static const char *probe_arch(void) {
#if defined(__x86_64__) || defined(_M_X64)
    return "x86_64";
#elif defined(__aarch64__) || defined(_M_ARM64)
    return "aarch64";
#elif defined(__i386__) || defined(_M_IX86)
    return "i386";
#else
    return "unknown";
#endif
}

ca_device_snapshot_t ca_device_probe(void) {
    ca_device_snapshot_t s;
    memset(&s, 0, sizeof(s));
    s.ram_bytes = probe_ram();
    s.free_storage_bytes = probe_free_storage();
    s.cpu_cores = probe_cpu_cores();
    s.gpu_kind = CA_GPU_NONE;
    s.thermal = CA_THERMAL_NORMAL;
    s.connectivity = CA_CONN_WIFI;
    s.os = probe_os();
    s.arch = probe_arch();
    s.tier = classify_tier(s.ram_bytes, s.cpu_cores);
    return s;
}
