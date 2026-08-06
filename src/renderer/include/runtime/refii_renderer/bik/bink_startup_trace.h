#pragma once

#include <chrono>
#include <cstdint>

struct PPCContext;

namespace refii::renderer {

struct RendererHookArgs;

namespace bink {

using StartupBinkTimingClock = std::chrono::steady_clock;

uint64_t StartupBinkTimingUs(StartupBinkTimingClock::time_point begin,
                             StartupBinkTimingClock::time_point end);

void LogStartupBinkTiming(const char *symbol, const RendererHookArgs &args,
                          const PPCContext &ctx, uint64_t elapsed_us);

void LogStartupBinkMovieState(const char *phase, const RendererHookArgs &args,
                              const PPCContext &ctx, uint8_t *base);

void LogStartupBinkDrainState(const RendererHookArgs &args,
                              const PPCContext &ctx, uint8_t *base,
                              uint64_t elapsed_us);

void LogStartupBinkBitstreamCopyTrace(
    const RendererHookArgs &args, const PPCContext &ctx, uint8_t *base,
    uint64_t elapsed_us, uint32_t before_write, uint32_t before_read,
    uint32_t before_fill);

void LogStartupBinkAudioTrace(const char *symbol,
                              const RendererHookArgs &args,
                              const PPCContext &ctx, uint8_t *base);

void LogStartupBinkCallbackTrace(const char *symbol,
                                 const RendererHookArgs &args,
                                 const PPCContext &ctx, uint8_t *base);

void LogStartupBinkSoundCallbackTrace(const char *symbol,
                                      const RendererHookArgs &args,
                                      const PPCContext &ctx, uint8_t *base);


} // namespace bink
} // namespace refii::renderer
