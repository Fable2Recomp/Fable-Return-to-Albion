#pragma once

#include <cstdint>

namespace refii_host_tracy {

/// From a crash handler: has the Tracy library save a live capture beside the others, waiting up
/// to `timeout_ms`. Async-signal-safe on this side (see RefiiTracyCaptureSaveOnCrash); 0 when no
/// capture is live or the build has no profiler.
int SaveOnCrash(uint32_t timeout_ms);

}  // namespace refii_host_tracy
