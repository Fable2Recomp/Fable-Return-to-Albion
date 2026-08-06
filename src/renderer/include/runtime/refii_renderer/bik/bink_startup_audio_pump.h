#pragma once

#include <chrono>
#include <cstdint>

struct PPCContext;

namespace refii::renderer {

struct RendererHookArgs;

namespace bink {

void RegisterStartupAudioCursorBridge(uint8_t *base, uint32_t plane_guest);
void RegisterStartupAudioCursorBridgesForMovie(uint8_t *base,
                                               uint32_t movie_guest);
void ClearStartupAudioCursorsForMovie(uint8_t *base, uint32_t movie_guest);

bool TryBridgeStartupAudioCursor(const RendererHookArgs &args,
                                 PPCContext &ctx, uint8_t *base);

void RememberStartupActiveMovie(uint8_t *base, uint32_t movie_guest);
void ClearStartupActiveMovie(uint8_t *base, uint32_t movie_guest);

bool AdvanceStartupAudioCursorsForMovie(
    uint8_t *base, uint32_t movie_guest,
    std::chrono::nanoseconds advance_duration, uint32_t frame_delta);
void AdvanceStartupAudioCursorsForMovieFrame(uint8_t *base,
                                             uint32_t movie_guest,
                                             uint32_t frame_delta);

void PrimeStartupAudioDrainBudgetForMovie(uint8_t *base,
                                          uint32_t movie_guest);
bool StartupAudioDrainBudgetAvailableForPlane(uint32_t plane_guest);
uint32_t StartupAudioDrainOutputBudgetForPlane(uint32_t plane_guest);
bool PublishStartupAudioDrainBudgetForPlane(uint8_t *base,
                                            uint32_t plane_guest,
                                            uint32_t output_bytes);
uint32_t StartupAudioDrainSourceBudgetForPlane(uint32_t plane_guest,
                                               bool high_byte_plane);
void ConsumeStartupAudioDrainBudgetForPlane(uint32_t plane_guest,
                                            uint32_t output_bytes);

} // namespace bink
} // namespace refii::renderer
