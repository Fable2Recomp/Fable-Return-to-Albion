#include "vfs_hooks_private.h"

#include <atomic>
#include <cstdint>
#include <cstring>

#include "hooks/ppc_externs.h"

// The particle transform setters, recorded per PSInstance.
//
// `instance+0x50` is an authored local translation; the world position is stated into each
// emitter at `emitter+0x50` through a call:
//
//     sub_832325A8(PSInstance* r3, position v1, velocity v2)
//       832325c8  lwz     r27, 0xC(r3)        ; emitters begin
//       832325cc  vmr128  v127, v1            ; the stated position
//       832325d0  lwz     r23, 0x10(r3)       ; emitters end
//       832325e8  li      r24, 0x50
//       83232600  lwz     r31, 0(r27)         ; each emitter
//       8323261c  stb     r28, 0xB0(r31)      ; transform dirty
//       83232628  stvx128 v12, r31, r24       ; emitter[0x50] = position
//
// `sub_83231C40(PSInstance* r3, quaternion r4:r5)` states the orientation the same way, through
// `sub_83228D78`, which spills the pair and stores it at `emitter+0x10` before recomposing the
// matrix at `emitter+0x20`:
//
//     83228d8c  std  r4, arg_20(r1)     ; the quaternion arrives in GPRs, not a VMX register
//     83228d90  std  r5, arg_28(r1)
//     83228da4  ld   r6, 0(r11)
//     83228db0  ld   r7, 8(r11)
//     83228dc0  std  r7, 0x18(r31)      ; emitter[0x18] = r5
//     83228dc4  std  r6, 0x10(r31)      ; emitter[0x10] = r4
//     83228da8  addi r3, r31, 0x20      ; then compose rows 0..3 at emitter+0x20
//
// This is an instrument, not the placement source. These two setters are not the only writers
// of an emitter's world transform; the composed matrix at `emitter+0x20` is 64 bytes, so its
// row 3 is `emitter+0x50`, and three more writers land in it:
//
//     sub_8322E4D0  8322e588 li r6,0x50 / 8322e598 stvx128 v11,r31,r6   PER TICK, type-5 path
//     sub_83230880  83230904 li r7,0x50 / 8323091c stvx128 v11,r31,r7   PER TICK, type-9 spline
//     sub_8322B090 / sub_8322B848                                       the authored .pfx load
//
// The two per-tick writers run later in the frame, from the manager update
// (`sub_83234C38` -> `sub_83233890` -> emitter vtable slot +36), so they WIN over anything these
// hooks saw; and an effect whose owner never fires component message 10 keeps the transform its
// .pfx authored without either setter ever being called. So the host places effects from
// `node+0x20`, where every writer lands, and reads this only to measure the setters' coverage.
//
// The instance pointer in r3 is the identity — the same one the host's walk reports, since a
// node's `+0xE8` names the effect instance whose emitter vector is at `+0xC`/`+0x10`.

namespace {

/// One effect's stated transform. Written only by the two hooks below.
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

REX_EXTERN(sub_832325A8) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();

  if (StatedFx* slot = Claim(ctx.r3.u32); slot != nullptr) {
    // All four lanes of v1 in the recompiler's register order; the host decides which is x.
    std::memcpy(slot->position, ctx.v1.f32, sizeof(slot->position));
    slot->have_position.store(1u, std::memory_order_relaxed);
    slot->sequence.store(g_sequence.fetch_add(1u, std::memory_order_relaxed) + 1u,
                         std::memory_order_release);
  }

  REFII_VFS_NATIVE_PASSTHROUGH(sub_832325A8);
}

REX_EXTERN(sub_83231C40) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();

  // r3 is the PSInstance. `sub_825F8900` calls this inside `sub_825F81E0`, synchronously on this
  // thread, for every spell FX it places, so it names a watched effect's instance (see
  // hooks/spell_fx_lifetime.cpp). A no-op unless a capture window is open.
  refii::vfs::NoteSpellFxInstance(ctx.r3.u32);

  if (StatedFx* slot = Claim(ctx.r3.u32); slot != nullptr) {
    // r4 then r5, high word first: `sub_83228D78` stores r4 at `emitter+0x10` and r5 at
    // `emitter+0x18`, so the four components are the register halves in that order.
    const uint32_t lanes[4] = {
        static_cast<uint32_t>(ctx.r4.u64 >> 32), static_cast<uint32_t>(ctx.r4.u64),
        static_cast<uint32_t>(ctx.r5.u64 >> 32), static_cast<uint32_t>(ctx.r5.u64)};
    std::memcpy(slot->orientation, lanes, sizeof(slot->orientation));
    slot->have_orientation.store(1u, std::memory_order_relaxed);
    slot->sequence.store(g_sequence.fetch_add(1u, std::memory_order_relaxed) + 1u,
                         std::memory_order_release);
  }

  REFII_VFS_NATIVE_PASSTHROUGH(sub_83231C40);
}

extern "C" {

/// How many effects have stated a transform since boot — the cheap "is this feed alive" check.
uint32_t RefiiFxStatedCount(void) { return g_sequence.load(std::memory_order_relaxed); }

/// Copies the stated transforms out, 10 words each: instance, sequence, 4 position lanes,
/// 4 orientation lanes. Returns how many were written.
uint32_t RefiiFxStatedTransforms(uint32_t* out_words, uint32_t word_capacity) {
  if (out_words == nullptr) {
    return 0u;
  }

  constexpr uint32_t kWords = 10u;
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

}  // extern "C"
