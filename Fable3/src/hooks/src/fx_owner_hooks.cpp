// refii - WHO PLAYS AN EFFECT, as Fable 3 states it: the entity whose CECParticleEmitter made the
// effect's handle, and the entity that emitter was attached to. The live-effect feed
// (Fable3/src/hostbridge/src/world_feed.cpp, RefiiHostGetActiveFx [20] [21]) hands both to the
// shell, which mounts the effect on the owner's attachment (the CECObjectAttachment feed,
// entity_feed.cpp) as it does Fable 2's.
//
// Every address is the Fable 3 IDB; each offset names its base.
//
// CECParticleEmitter (vtable 0x820A5090; slot 5 sub_82682360 names it "CECParticleEmitter",
// 0x8209885C): component+4 its entity, component+0x14 the effect handle, component+0x1C a weak
// ref {vtable, box} with the box at +0x20, box+0 the target entity (sub_8221E2E8 writes it) —
// Fable 2's emitter layout word for word (its feed reads comp+4 and *(comp+0x20)).
//
//   sub_82682738(emitter)  CREATE THE EFFECT. Returns 0 at once when +0x14 holds a handle
//       (`lwz r11,0x14(r3)` / `bne` 0x82682744..0x82682750); otherwise hashes the name at +0x18,
//       creates through sub_8318FE40 (which calls ParticleSystem_Create, the hook that states the
//       handle and its name) and keeps the handle: `stw r8, 0x14(r31)` 0x826827C4, then Start
//       and the scene of `*(*(emitter+4)+8)` (0x826827CC..0x826827D8). Called from the
//       component's init (slot 10 sub_82682628), its re-show (sub_82683538) and the attach below.
//   sub_82683000(emitter r3, parent r4, dummy name r5, dummy index r6, ..., matrix r10)
//       ATTACH TO AN ENTITY'S DUMMY (the Lua "AttachToEntity(entity, dummy, index)" of PC's
//       CLuaECFunction3Parameters<CECParticleEmitter, void, CEntity*, CCharString const&, int>).
//       Keeps r4 in the weak ref at emitter+0x1C (`addi r3, r27, 0x1C` / `bl sub_82308B28`
//       0x8268305C..0x82683060), appends (r5, r6) to the dummy vector at emitter+0x2C
//       (0x82683078..0x826830B0), takes the PARENT's component 26 — CECObjectAttachment
//       (`lbz r10, 0x1A(r11)` 0x826830F8) — and attaches the emitter's own entity to it at the
//       dummy: sub_82715270(attachment, `lwz r4, 4(r27)` 0x82683194, r5, r6, r10, 1) at
//       0x826831A8. 40 callers: the particle attacher (sub_829C42A0 0x829C453C), weapon effects,
//       spells, the "ParticleEmitter" action.
// Once attached the effect follows its entity through the guest's own restatement: the emitter's
// message handler (slot 9 sub_82683D78) answers message 12 with sub_82683E00, which reads the
// entity's placement (`*(entity+128)` slots +80 position, +84 orientation) into sub_82683708 ->
// ParticleSystem_SetPosition / SetOrientation on +0x14. Those rows are the tick's; the owner is
// what lets the shell draw the effect on the bone of the frame it draws.
//
// A create states its handle's owner and the parent the emitter already holds (the attach writes
// the weak ref before it can re-create, `bl sub_82683538` at 0x826832E4); an attach states its own
// r4. The handle's create (ParticleSystem_Create) clears what a reused handle carried.

#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_ParticleEmitter_CreateEffect);
REX_EXTERN(__imp__rex_ParticleEmitter_AttachToEntity);

namespace {

constexpr uint32_t kEmitterEntity = 0x4u;
constexpr uint32_t kEmitterHandle = 0x14u;
constexpr uint32_t kEmitterParentBox = 0x20u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  if (!Plausible(address)) {
    return 0u;
  }
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

/// The entity behind the emitter's attach weak ref, 0 when never attached or the parent is gone.
uint32_t HeldParent(const uint8_t* base, uint32_t emitter) {
  const uint32_t parent = LoadGuestU32(base, LoadGuestU32(base, emitter + kEmitterParentBox));
  return Plausible(parent) ? parent : 0u;
}

void StateOwner(const uint8_t* base, uint32_t emitter, uint32_t parent) {
  const uint32_t handle = LoadGuestU32(base, emitter + kEmitterHandle);
  const uint32_t entity = LoadGuestU32(base, emitter + kEmitterEntity);
  if (handle != 0u && Plausible(entity)) {
    refii_bridge_fx_owner(handle, entity, Plausible(parent) ? parent : 0u);
  }
}

}  // namespace

REX_EXTERN(rex_ParticleEmitter_CreateEffect) {
  const uint32_t emitter = ctx.r3.u32;
  const uint32_t before = LoadGuestU32(base, emitter + kEmitterHandle);
  __imp__rex_ParticleEmitter_CreateEffect(ctx, base);
  if (Plausible(emitter) && before == 0u) {
    StateOwner(base, emitter, HeldParent(base, emitter));
  }
}

REX_EXTERN(rex_ParticleEmitter_AttachToEntity) {
  const uint32_t emitter = ctx.r3.u32;
  const uint32_t parent = ctx.r4.u32;
  __imp__rex_ParticleEmitter_AttachToEntity(ctx, base);
  if (Plausible(emitter)) {
    StateOwner(base, emitter, parent);
  }
}
