#pragma once

#include "renderer_runtime.h"

#include <cstdint>

struct PPCContext;

namespace refii::renderer::bink {

bool IsStartupBinkRenderQueueCaller(uint32_t link_register);

bool TryBridgeStartupBinkStackStringConstruct(const RendererHookArgs &args,
                                              PPCContext &ctx, uint8_t *base);
bool TryBridgeStartupBinkStackStringDestruct(const RendererHookArgs &args,
                                             PPCContext &ctx, uint8_t *base);

bool TryGetBridgeStartupBinkTokenLookup(const char *symbol,
                                        const RendererHookArgs &args,
                                        PPCContext &ctx, uint8_t *base);
void CacheBridgeStartupBinkTokenLookup(const char *symbol,
                                       const RendererHookArgs &args,
                                       const PPCContext &ctx, uint8_t *base);

void EnterStartupBinkStateContextBridge();
void LeaveStartupBinkStateContextBridge();
bool IsStartupBinkStateContextBridge();

} // namespace refii::renderer::bink
