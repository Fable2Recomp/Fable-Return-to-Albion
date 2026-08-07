#include "runtime/renderer_hook_policy.h"

#include "runtime/cvars.h"
#include "runtime/refii_renderer/hooks/root/fable_hook_symbols.h"

#include <algorithm>
#include <cstdint>
#include <utility>
#include <vector>

#include <rex/logging.h>

namespace refii::renderer {
namespace {

constexpr int HexDigitValue(char value) {
  if (value >= '0' && value <= '9') {
    return value - '0';
  }
  if (value >= 'a' && value <= 'f') {
    return value - 'a' + 10;
  }
  if (value >= 'A' && value <= 'F') {
    return value - 'A' + 10;
  }
  return -1;
}

bool ParseSubSymbolAddress(std::string_view symbol, uint32_t &address) {
  constexpr std::string_view prefix = "sub_";
  if (symbol.size() != prefix.size() + 8u ||
      symbol.substr(0, prefix.size()) != prefix) {
    return false;
  }

  uint32_t parsed = 0;
  for (size_t index = prefix.size(); index < symbol.size(); ++index) {
    const int digit = HexDigitValue(symbol[index]);
    if (digit < 0) {
      return false;
    }
    parsed = (parsed << 4u) | static_cast<uint32_t>(digit);
  }
  address = parsed;
  return true;
}

const RendererHookTarget* RendererHookTargetByStableName(
    std::string_view name) {
  for (const RendererHookTarget &target : kRendererHookTargets) {
    if (target.name != nullptr && name == target.name) {
      return &target;
    }
  }
  return nullptr;
}

const RendererHookTarget* RendererHookTargetByFableAddress(uint32_t address) {
  // Targets may share a Fable address (GetBackBuffer and GetRenderTarget both
  // carry 0x82B981D0, the IDA-proven D3DDevice_GetRenderTarget). Attribute
  // the address to the highest-confidence claimant, not table order.
  const RendererHookTarget *best = nullptr;
  for (const RendererHookTarget &target : kRendererHookTargets) {
    if (target.fable_address != address) {
      continue;
    }
    if (best == nullptr || target.confidence < best->confidence) {
      best = &target;
    }
  }
  return best;
}

const RendererHookTarget* RendererHookTargetForGeneratedSymbol(
    std::string_view symbol) {
  if (symbol == "rex_lhRenderer_EndScene") {
    return RendererHookTargetById(RendererHookId::kPresent);
  }
  if (symbol == "rex_lhGfx_CreateVertexBuffer") {
    return RendererHookTargetById(RendererHookId::kCreateVertexBuffer);
  }
  if (symbol == "rex_lhGfx_CreateIndexBuffer") {
    return RendererHookTargetById(RendererHookId::kCreateIndexBuffer);
  }
  if (symbol == "rex_lhGfx_CreateGPUState") {
    return RendererHookTargetById(RendererHookId::kCreateVertexDeclaration);
  }
  if (symbol == "rex_lhTexture_Create") {
    return RendererHookTargetById(RendererHookId::kCreateSurface);
  }
  return nullptr;
}

} // namespace

const RendererHookTarget* ResolveRendererHookTargetUncached(
    std::string_view symbol);

// Dumps which hook SYMBOLS actually fire. Thread-local, so this is one thread's
// view -- the guest thread is the one that matters. Collisions in the memo can
// undercount, but the zero/non-zero signal is what the prune list needs.
template <typename EntryT>
void DumpRendererHookSymbolCensus(const EntryT *entries, size_t count) {
  std::vector<std::pair<uint64_t, const char *>> rows;
  rows.reserve(count);
  for (size_t i = 0; i < count; ++i) {
    if (entries[i].key != nullptr && entries[i].hits != 0) {
      rows.emplace_back(entries[i].hits, entries[i].key);
    }
  }
  std::sort(rows.begin(), rows.end(),
            [](const auto &a, const auto &b) { return a.first > b.first; });
  REXLOG_INFO("Refii hook symbol census distinct_live={}", rows.size());
  for (size_t i = 0; i < rows.size() && i < 20u; ++i) {
    REXLOG_INFO("Refii hook symbol #{} {} hits={}", i + 1, rows[i].second,
                static_cast<unsigned long long>(rows[i].first));
  }
}

const RendererHookTarget* ResolveRendererHookTarget(std::string_view symbol) {
  // MEASURED 2026-07-28: 1,703,936 hooked calls consumed 56.5 SECONDS of an
  // 82-second run -- 69% of wall time, ~33us per call -- which at ~5,200 calls
  // per frame is the ~172ms the present pump reports as "blocked on guest".
  // This function is the reason: it performs up to FOUR lookups (generated-symbol
  // table, stable-name table, sub_ address parse + address table, Lumen-name
  // fallback), most of them string comparisons, and it is called several times
  // per hooked call. In a Debug build none of it inlines.
  //
  // The symbols are compile-time string LITERALS produced by the hook macro
  // (`#wrapper_symbol`), so their ADDRESSES are stable and unique per call site.
  // Memoising on the pointer turns the whole thing into one load with no string
  // comparison at all. The target table is constexpr, so a resolution can never
  // change for a given symbol and the memo never needs invalidating.
  //
  // Thread-local: hooks run on several threads and this avoids any lock.
  // Length plus first/last byte are verified so a non-literal caller reusing a
  // freed pointer cannot produce a false hit.
  struct HookTargetMemoEntry {
    const char *key = nullptr;
    size_t size = 0;
    char first = 0;
    char last = 0;
    const RendererHookTarget *value = nullptr;
    // Per-SYMBOL fire count, free here because the pointer lookup already
    // happened. The existing target census only covers the 63-entry target
    // table; this covers ALL ~304 declared hooks including the majority that
    // resolve to no target at all. Symbols that never appear are removable:
    // deleting the hook lets the guest function run natively with no wrapper.
    uint64_t hits = 0;
  };
  constexpr size_t kHookTargetMemoSize = 512u;  // power of two
  static thread_local HookTargetMemoEntry memo[kHookTargetMemoSize];
  const char *memo_key = symbol.data();
  HookTargetMemoEntry *memo_entry = nullptr;
  if (memo_key != nullptr && !symbol.empty()) {
    const size_t slot = (reinterpret_cast<uintptr_t>(memo_key) >> 4u) &
                        (kHookTargetMemoSize - 1u);
    memo_entry = &memo[slot];
    if (memo_entry->key == memo_key && memo_entry->size == symbol.size() &&
        memo_entry->first == symbol.front() &&
        memo_entry->last == symbol.back()) {
      ++memo_entry->hits;
      if ((memo_entry->hits & 0x3FFFFu) == 0u) {
        DumpRendererHookSymbolCensus(memo, kHookTargetMemoSize);
      }
      return memo_entry->value;
    }
  }
  const RendererHookTarget *resolved = nullptr;
  const auto remember = [&](const RendererHookTarget *value) {
    if (memo_entry != nullptr) {
      memo_entry->key = memo_key;
      memo_entry->size = symbol.size();
      memo_entry->first = symbol.front();
      memo_entry->last = symbol.back();
      memo_entry->value = value;
    }
    return value;
  };
  resolved = ResolveRendererHookTargetUncached(symbol);
  return remember(resolved);
}

const RendererHookTarget* ResolveRendererHookTargetUncached(
    std::string_view symbol) {
  if (const RendererHookTarget *target =
          RendererHookTargetForGeneratedSymbol(symbol)) {
    return target;
  }
  if (const RendererHookTarget *target =
          RendererHookTargetByStableName(symbol)) {
    return target;
  }

  uint32_t address = 0;
  if (ParseSubSymbolAddress(symbol, address)) {
    return RendererHookTargetByFableAddress(address);
  }

  const char *lumen_name = FableHookName(symbol);
  if (lumen_name != nullptr && lumen_name[0] != '\0') {
    return RendererHookTargetByStableName(lumen_name);
  }
  return nullptr;
}

bool RendererHookAllowedBySurface(const RendererHookTarget &target) {
  if (!RendererRootOnlyHooksEnabled() || RendererDeepObserverHooksEnabled()) {
    return true;
  }

  return target.surface == RendererHookSurface::RequiredRoot;
}

bool RendererHookAllowedBySurface(const RendererHookTarget *target) {
  if (!RendererRootOnlyHooksEnabled() || RendererDeepObserverHooksEnabled()) {
    return true;
  }
  if (target == nullptr) {
    return false;
  }
  return RendererHookAllowedBySurface(*target);
}

bool RendererHookAllowedInCurrentMode(
    std::string_view symbol, RendererHookKind kind,
    RendererPhysicalWrapperRequiredPredicate physical_wrapper_required) {
  if (RendererRootOnlyHooksEnabled() && !RendererDeepObserverHooksEnabled() &&
      physical_wrapper_required != nullptr &&
      physical_wrapper_required(symbol, kind)) {
    return true;
  }
  return RendererHookAllowedBySurface(ResolveRendererHookTarget(symbol));
}

} // namespace refii::renderer
