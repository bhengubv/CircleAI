/* Fix: make uint64_t available textually for dispatch/time.h module processing */
#if defined(__clang__) && defined(_WIN32)
#include <stdint.h>
#endif
