#include "runtime/refii_renderer/bik/bink_state_token_bridge.h"

#include "runtime/refii_renderer/bik/bink_guest_memory.h"

#include <atomic>
#include <mutex>
#include <unordered_map>

#include <rex/logging.h>
#include <rex/ppc.h>

namespace refii::renderer::bink {

namespace {

thread_local uint32_t g_state_lookup_depth = 0;
std::atomic<uint32_t> g_bridge_string_constructs{0};
std::atomic<uint32_t> g_bridge_string_destructs{0};
std::atomic<uint32_t> g_bridge_token_lookup_hits{0};
std::atomic<uint32_t> g_bridge_token_lookup_misses{0};
std::mutex g_token_lookup_mutex;
std::unordered_map<uint64_t, uint32_t> g_token_lookup_cache;

uint32_t StartupBinkGuestStringLength(uint8_t *base, uint32_t string_guest,
                                      uint32_t requested_length) {
  constexpr uint32_t kMaxStartupBinkStateTokenLength = 128u;
  if (requested_length != 0xFFFFFFFFu) {
    return requested_length <= kMaxStartupBinkStateTokenLength
               ? requested_length
               : 0u;
  }

  for (uint32_t length = 0; length < kMaxStartupBinkStateTokenLength;
       ++length) {
    if (!IsReadableGuestByteRange(base, string_guest + length, 1u)) {
      return 0;
    }
    if (LoadGuestU8(base, string_guest + length) == 0) {
      return length;
    }
  }
  return 0;
}

bool StartupBinkStackStringDescriptor(const RendererHookArgs &args,
                                      uint8_t *base, uint32_t &text_guest,
                                      uint32_t &length) {
  if (!IsStartupBinkRenderQueueCaller(args.lr) || args.r3 == 0 ||
      !IsReadableGuestByteRange(base, args.r3, sizeof(uint32_t))) {
    return false;
  }

  const uint32_t descriptor_guest = LoadGuestU32(base, args.r3);
  if (descriptor_guest != args.r3 + sizeof(uint32_t) ||
      !IsReadableGuestByteRange(base, descriptor_guest,
                                sizeof(uint32_t) * 2u)) {
    return false;
  }

  text_guest = LoadGuestU32(base, descriptor_guest + 0u);
  length = LoadGuestU32(base, descriptor_guest + 4u);
  return length > 0 && length <= 128u &&
         IsReadableGuestByteRange(base, text_guest, length);
}

uint64_t StartupBinkTokenLookupKey(const char *symbol, uint8_t *base,
                                   uint32_t text_guest, uint32_t length) {
  uint64_t hash = 1469598103934665603ull;
  for (const char *c = symbol; *c != '\0'; ++c) {
    hash ^= static_cast<uint8_t>(*c);
    hash *= 1099511628211ull;
  }
  hash ^= length;
  hash *= 1099511628211ull;
  for (uint32_t index = 0; index < length; ++index) {
    hash ^= LoadGuestU8(base, text_guest + index);
    hash *= 1099511628211ull;
  }
  return hash;
}

} // namespace

bool IsStartupBinkRenderQueueCaller(uint32_t link_register) {
  return link_register >= 0x82A41F10u && link_register <= 0x82A42D20u;
}

bool TryBridgeStartupBinkStackStringConstruct(const RendererHookArgs &args,
                                              PPCContext &ctx, uint8_t *base) {
  if (!IsStartupBinkRenderQueueCaller(args.lr) || args.r3 == 0 ||
      args.r4 == 0 || !IsReadableGuestByteRange(base, args.r3, 12u) ||
      !IsReadableGuestByteRange(base, args.r4, 1u)) {
    return false;
  }

  const uint32_t length = StartupBinkGuestStringLength(base, args.r4, args.r5);
  if (length == 0 || !IsReadableGuestByteRange(base, args.r4, length)) {
    return false;
  }

  const uint32_t descriptor_guest = args.r3 + sizeof(uint32_t);
  StoreGuestU32(base, args.r3, descriptor_guest);
  StoreGuestU32(base, descriptor_guest + 0u, args.r4);
  StoreGuestU32(base, descriptor_guest + 4u, length);
  ctx.r3.u32 = args.r3;

  const uint32_t count =
      g_bridge_string_constructs.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (count <= 8u) {
    REXLOG_INFO("Refii renderer startup BIK bridge stack string construct #{} "
                "lr=0x{:08X} object=0x{:08X} text=0x{:08X} len={}",
                count, args.lr, args.r3, args.r4, length);
  }
  return true;
}

bool TryBridgeStartupBinkStackStringDestruct(const RendererHookArgs &args,
                                             PPCContext &ctx, uint8_t *base) {
  if (!IsStartupBinkRenderQueueCaller(args.lr) || args.r3 == 0 ||
      !IsReadableGuestByteRange(base, args.r3, sizeof(uint32_t))) {
    return false;
  }

  const uint32_t descriptor_guest = LoadGuestU32(base, args.r3);
  if (descriptor_guest != args.r3 + sizeof(uint32_t) ||
      !IsReadableGuestByteRange(base, descriptor_guest,
                                sizeof(uint32_t) * 2u)) {
    return false;
  }

  StoreGuestU32(base, args.r3, 0u);
  ctx.r3.u32 = args.r3;

  const uint32_t count =
      g_bridge_string_destructs.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (count <= 8u) {
    REXLOG_INFO("Refii renderer startup BIK bridge stack string destruct #{} "
                "lr=0x{:08X} object=0x{:08X}",
                count, args.lr, args.r3);
  }
  return true;
}

bool TryGetBridgeStartupBinkTokenLookup(const char *symbol,
                                        const RendererHookArgs &args,
                                        PPCContext &ctx, uint8_t *base) {
  uint32_t text_guest = 0;
  uint32_t length = 0;
  if (!StartupBinkStackStringDescriptor(args, base, text_guest, length)) {
    return false;
  }

  const uint64_t key =
      StartupBinkTokenLookupKey(symbol, base, text_guest, length);
  uint32_t result = 0;
  {
    std::lock_guard<std::mutex> lock(g_token_lookup_mutex);
    auto it = g_token_lookup_cache.find(key);
    if (it == g_token_lookup_cache.end()) {
      return false;
    }
    result = it->second;
  }

  ctx.r3.u32 = result;
  const uint32_t count =
      g_bridge_token_lookup_hits.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (count <= 16u) {
    REXLOG_INFO(
        "Refii renderer startup BIK bridge token lookup hit #{} symbol={} "
        "lr=0x{:08X} object=0x{:08X} text=0x{:08X} len={} result=0x{:08X}",
        count, symbol, args.lr, args.r3, text_guest, length, result);
  }
  return true;
}

void CacheBridgeStartupBinkTokenLookup(const char *symbol,
                                       const RendererHookArgs &args,
                                       const PPCContext &ctx, uint8_t *base) {
  uint32_t text_guest = 0;
  uint32_t length = 0;
  if (!StartupBinkStackStringDescriptor(args, base, text_guest, length)) {
    return;
  }

  const uint64_t key =
      StartupBinkTokenLookupKey(symbol, base, text_guest, length);
  {
    std::lock_guard<std::mutex> lock(g_token_lookup_mutex);
    g_token_lookup_cache.emplace(key, ctx.r3.u32);
  }

  const uint32_t count =
      g_bridge_token_lookup_misses.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (count <= 16u) {
    REXLOG_INFO(
        "Refii renderer startup BIK bridge token lookup cached #{} symbol={} "
        "lr=0x{:08X} object=0x{:08X} text=0x{:08X} len={} result=0x{:08X}",
        count, symbol, args.lr, args.r3, text_guest, length, ctx.r3.u32);
  }
}

void EnterStartupBinkStateContextBridge() {
  ++g_state_lookup_depth;
}

void LeaveStartupBinkStateContextBridge() {
  --g_state_lookup_depth;
}

bool IsStartupBinkStateContextBridge() {
  return g_state_lookup_depth != 0;
}

} // namespace refii::renderer::bink
