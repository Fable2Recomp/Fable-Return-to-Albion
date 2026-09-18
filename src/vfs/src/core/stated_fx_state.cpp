// refii — the effect transforms the guest states, as SHARED STATE.
//
// THIS FILE MUST BE IN librefii_vfs, NOT IN THE PPC HOOK SET, for the reason
// guest_tick_pump_state.cpp states at length: the two ends of this table live in different
// libraries. The PAYLOAD writes (the two setter hooks in src/hooks/particle_transform.cpp, on
// the guest's thread) and the HOST reads (RefiiHostGetStatedFx in the host bridge, on Godot's).
// Both link refii_vfs, so this is the one place they can agree on one table.
//
// IT USED TO LIVE BESIDE THE HOOKS, reached by `dlsym(RTLD_DEFAULT, "RefiiFxStatedTransforms")`.
// That resolves on Linux — the payload is dlopen'd RTLD_GLOBAL, so its symbols are in the global
// scope — and cannot resolve on Windows at all: RTLD_DEFAULT does not exist there, so the host
// bridge did not compile, and a Windows DLL has no global scope to look the name up in even if
// it had. refii_payload/payload.h states the rule the dlsym was bending: NOTHING WE SHIP MAY
// LINK AGAINST THE PAYLOAD, which is built from the player's own game image and is absent on a
// build machine.
//
// The host reaches these as ordinary C++ symbols; both sides of that call are our build. The
// PAYLOAD reaches them through the C entries in core/payload_bridge.h, because its side of that
// call is the player's compiler — see that header for the contract.

#include <atomic>
#include <cstdint>
#include <cstring>

#include "core/payload_bridge.h"

namespace refii::vfs {
namespace {

/// One effect's stated transform. Written only through the two C entries below.
struct StatedFx {
  std::atomic<uint32_t> instance{0};
  std::atomic<uint32_t> sequence{0};
  float position[4]{};
  float orientation[4]{0.0f, 0.0f, 0.0f, 1.0f};
  std::atomic<uint8_t> have_position{0};
  std::atomic<uint8_t> have_orientation{0};
};

/// Sized above the live effect-instance population (one slot per instance, not per particle).
constexpr uint32_t kSlots = 1024u;

/// Ten words a row: instance, sequence, four position lanes, four orientation lanes. All four
/// VMX lanes are carried because which lane holds x depends on the recompiler's register layout.
constexpr uint32_t kWords = 10u;

StatedFx g_fx[kSlots];
std::atomic<uint32_t> g_sequence{0};

/// Open addressing on the instance pointer. Pointers are 8- or 16-byte aligned, so the low bits
/// carry no entropy — multiply before masking.
uint32_t SlotFor(uint32_t instance) {
  uint32_t h = instance * 2654435761u;
  return (h >> 12) & (kSlots - 1u);
}

StatedFx* Claim(uint32_t instance) {
  if (instance == 0u) {
    return nullptr;
  }

  const uint32_t start = SlotFor(instance);
  for (uint32_t probe = 0u; probe < 16u; ++probe) {
    StatedFx& slot = g_fx[(start + probe) & (kSlots - 1u)];
    uint32_t held = slot.instance.load(std::memory_order_relaxed);
    if (held == instance) {
      return &slot;
    }

    if (held == 0u &&
        slot.instance.compare_exchange_strong(held, instance, std::memory_order_relaxed)) {
      return &slot;
    }
  }

  // A full probe run evicts the stalest slot: the guest frees instances without notice, so slots
  // are never released otherwise. The instance is cleared before it is replaced and every read
  // checks it, so an eviction cannot misattribute a row.
  uint32_t oldest = start;
  uint32_t oldest_sequence = 0xFFFFFFFFu;
  for (uint32_t probe = 0u; probe < 16u; ++probe) {
    const uint32_t at = (start + probe) & (kSlots - 1u);
    const uint32_t seq = g_fx[at].sequence.load(std::memory_order_relaxed);
    if (seq < oldest_sequence) {
      oldest_sequence = seq;
      oldest = at;
    }
  }

  StatedFx& victim = g_fx[oldest];
  victim.instance.store(0u, std::memory_order_relaxed);
  victim.have_position.store(0u, std::memory_order_relaxed);
  victim.have_orientation.store(0u, std::memory_order_relaxed);
  victim.instance.store(instance, std::memory_order_release);
  return &victim;
}

}  // namespace

/// How many effects have stated a transform since boot — the cheap "is this feed alive" check.
uint32_t StatedFxCount() { return g_sequence.load(std::memory_order_relaxed); }

/// Copies the stated transforms out, kWords each. Returns how many rows were written.
uint32_t StatedFxTransforms(uint32_t* out_words, uint32_t word_capacity) {
  if (out_words == nullptr) {
    return 0u;
  }

  const uint32_t room = word_capacity / kWords;
  uint32_t written = 0u;
  for (uint32_t i = 0u; i < kSlots && written < room; ++i) {
    StatedFx& slot = g_fx[i];
    const uint32_t instance = slot.instance.load(std::memory_order_acquire);
    if (instance == 0u || slot.have_position.load(std::memory_order_relaxed) == 0u) {
      continue;
    }

    uint32_t* row = out_words + (written * kWords);
    row[0] = instance;
    row[1] = slot.sequence.load(std::memory_order_acquire);
    std::memcpy(row + 2, slot.position, sizeof(slot.position));
    std::memcpy(row + 6, slot.orientation, sizeof(slot.orientation));
    ++written;
  }

  return written;
}

}  // namespace refii::vfs

// --- The payload's side. C only; see core/payload_bridge.h. --------------------------------

extern "C" {

void refii_bridge_note_fx_position(uint32_t instance, const float* lanes) {
  if (lanes == nullptr) {
    return;
  }

  if (refii::vfs::StatedFx* slot = refii::vfs::Claim(instance); slot != nullptr) {
    std::memcpy(slot->position, lanes, sizeof(slot->position));
    slot->have_position.store(1u, std::memory_order_relaxed);
    slot->sequence.store(
        refii::vfs::g_sequence.fetch_add(1u, std::memory_order_relaxed) + 1u,
        std::memory_order_release);
  }
}

void refii_bridge_note_fx_orientation(uint32_t instance, const uint32_t* lanes) {
  if (lanes == nullptr) {
    return;
  }

  if (refii::vfs::StatedFx* slot = refii::vfs::Claim(instance); slot != nullptr) {
    std::memcpy(slot->orientation, lanes, sizeof(slot->orientation));
    slot->have_orientation.store(1u, std::memory_order_relaxed);
    slot->sequence.store(
        refii::vfs::g_sequence.fetch_add(1u, std::memory_order_relaxed) + 1u,
        std::memory_order_release);
  }
}

}  // extern "C"
