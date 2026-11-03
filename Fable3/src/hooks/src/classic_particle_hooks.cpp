// refii - Fable 3's particle groups and trails, stated to the host where the particle system's own
// writers lay each group's stream into the frame's vertex buffer, and where each group's message
// names the sim data it was drawn from.
//
// Every address is the Fable 3 IDB; each offset names its base.
//
// THE STREAM. Once per particle frame (sub_8318B238 -> sub_8318E8F0, the particle thread) the two
// stream writers W = *0x8330E9F8 / *0x8330E9F4 have their cursor W+0x84 zeroed and their buffer
// locked to W+0x88 (sub_83188238, 0x8318E9B4..), every live instance updates and then writes its
// emitters through its vtable slot 10 (sub_8318CE70 -> sub_831890D0): the root emitter block
// E = instance+336 by sub_83181DE8, each child E = child+16 by sub_8317C1E0. Both switch on the
// renderer kind def+32 (def = E+0x2C) and hand the writer (W, D + 4, n), D = E+0x24 the sim's
// particle data, n = E+0x10 the live count (0x83181E28..0x83181E6C), keeping the returned byte
// offset at E+0x78 (`stw r3,0x1C8(r31)` 0x83181E70), -1 when nothing was written:
//   sub_83178480  QUADS: n records of 48 bytes at *(D+8) (`lwz r10,0(r4)` 0x831784F8, stride 0x30
//                 0x831785D4) become n vertices of 48 bytes at W+0x88 + W+0x84: {rec+16..+31,
//                 rec+8, rec+12, rec+4, min(rec+0, 19.0 flt_8206BD48), rec+32..+47} — Fable 2's
//                 ring copy exactly, so the host takes the records as Fable 2's export does.
//   sub_8317A758  TRAILS: the ring at D (+8 base, +20 head, +32 mask, +36 count c), refused when
//                 c < n (`cmplw r11,r5` 0x8317A768), unrolled with a point either side: c + 3
//                 points of 32 bytes (the ring copied by sub_83101680 at 0x8317AA3C/58, the two
//                 end points stored at 32 (c+1) / 32 (c+2), 0x8317AAA0..BC).
//   sub_831797E0  DOUBLE TRAILS: the same c + 3 points (n >= 2, c >= n, 0x831797EC..0x8317982C).
// The render side never sees those records: the group's message is copied into the render ring
// with its data replaced by a render copy holding only the material (sub_82C49808 -> sub_82C49A78
// / B70 / C78, `v7[3] = 0`), and the twin's draw (sub_82C53F10 -> sub_82C548B8) binds the frame's
// buffer at the message's offset R+432, stride 48 (sub_82D42308), skipping a group at -1.
//
// THE JOIN. The submit of the same emitter (sub_83188B60 -> renderer slot 5 sub_825FC5C0) fills
// the group's message (holder+16, vtable 0x82001A24 type 6, or 0x820E39FC type 36 for a
// displacement group; sub_825FC478: +8 kind, +12 D, +28 n, +112 the offset E+0x78) and posts it
// to its render object with sub_821C2120(id, message, ring) (0x825FD018). The id indexes the twin
// table *0x8355F464 (sub_8220D870), so a twin's stream is the one written from the D its id was
// last posted with. The object's end is its remove (sub_825FD208 -> sub_82C3C148, world_hooks.cpp).

#include <cstdint>
#include <cstring>
#include <vector>

#include <rex/ppc.h>

#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_ParticleQuads_WriteStream);
REX_EXTERN(__imp__rex_ParticleTrail_WriteStream);
REX_EXTERN(__imp__rex_ParticleDoubleTrail_WriteStream);
REX_EXTERN(__imp__rex_RenderObject_PostMessage);

namespace {

constexpr uint32_t kGroupMessage = 0x82001A24u;
constexpr uint32_t kDisplacementGroupMessage = 0x820E39FCu;
constexpr uint32_t kQuads = 1u;
constexpr uint32_t kTrail = 2u;
constexpr uint32_t kDoubleTrail = 3u;
constexpr uint32_t kQuadFloats = 12u;
constexpr uint32_t kPointFloats = 8u;
constexpr uint32_t kMaxRecords = 8192u;
constexpr uint32_t kMaxPoints = 4096u;
constexpr uint32_t kNoOffset = 0xFFFFFFFFu;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

bool PlausibleGuest(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

uint32_t Word(const uint8_t* base, uint32_t address) {
  return PlausibleGuest(address) ? LoadGuestU32(base, address) : 0u;
}

/// `count` big-endian floats at `address` into `out`.
void Floats(const uint8_t* base, uint32_t address, float* out, uint32_t count) {
  for (uint32_t i = 0; i < count; ++i) {
    const uint32_t bits = LoadGuestU32(base, address + i * 4u);
    std::memcpy(out + i, &bits, sizeof(float));
  }
}

/// One buffer per guest thread: the writers run on the particle thread, but nothing here assumes it.
std::vector<float>& Scratch() {
  thread_local std::vector<float> floats;
  return floats;
}

/// The writer's stream base for the offset it returned: *(W+0x88) + offset (0x831784B8,
/// 0x8317A7A0, 0x83179830). The buffer is the device's, so it may sit in the physical views.
uint32_t StreamAt(const uint8_t* base, uint32_t writer, uint32_t offset, uint32_t bytes) {
  const uint32_t buffer = Word(base, writer + 0x88u);
  if (buffer == 0u || offset == kNoOffset) {
    return 0u;
  }
  const uint64_t at = static_cast<uint64_t>(buffer) + offset;
  return at + bytes <= 0x100000000ull ? static_cast<uint32_t>(at) : 0u;
}

void StateTrail(const uint8_t* base, uint32_t kind, uint32_t writer, uint32_t data, uint32_t n,
                uint32_t c, uint32_t offset) {
  if (offset == kNoOffset || c > kMaxPoints) {
    refii_bridge_classic_particle_written(data, kind, n, c, nullptr, 0u);
    return;
  }
  const uint32_t floats = (c + 3u) * kPointFloats;
  const uint32_t at = StreamAt(base, writer, offset, floats * 4u);
  if (at == 0u) {
    refii_bridge_classic_particle_written(data, kind, n, c, nullptr, 0u);
    return;
  }
  std::vector<float>& out = Scratch();
  out.resize(floats);
  Floats(base, at, out.data(), floats);
  refii_bridge_classic_particle_written(data, kind, n, c, out.data(), floats);
}

}  // namespace

// (W, D + 4, n) -> the byte offset of n vertices, -1 when there was no room.
REX_EXTERN(rex_ParticleQuads_WriteStream) {
  const uint32_t data = ctx.r4.u32 - 4u;
  const uint32_t n = ctx.r5.u32;
  __imp__rex_ParticleQuads_WriteStream(ctx, base);
  if (!PlausibleGuest(data)) {
    return;
  }
  const uint32_t offset = ctx.r3.u32;
  const uint32_t records = Word(base, data + 8u);
  if (offset == kNoOffset || n == 0u || n > kMaxRecords || !PlausibleGuest(records)) {
    refii_bridge_classic_particle_written(data, kQuads, n, 0u, nullptr, 0u);
    return;
  }
  std::vector<float>& out = Scratch();
  out.resize(n * kQuadFloats);
  Floats(base, records, out.data(), n * kQuadFloats);
  refii_bridge_classic_particle_written(data, kQuads, n, 0u, out.data(), n * kQuadFloats);
}

// (W, D + 4, n) -> the byte offset of c + 3 points, -1 when refused or out of room.
REX_EXTERN(rex_ParticleTrail_WriteStream) {
  const uint32_t writer = ctx.r3.u32;
  const uint32_t data = ctx.r4.u32 - 4u;
  const uint32_t n = ctx.r5.u32;
  const uint32_t c = PlausibleGuest(data) ? Word(base, data + 36u) : 0u;
  __imp__rex_ParticleTrail_WriteStream(ctx, base);
  if (PlausibleGuest(data)) {
    StateTrail(base, kTrail, writer, data, n, c, ctx.r3.u32);
  }
}

REX_EXTERN(rex_ParticleDoubleTrail_WriteStream) {
  const uint32_t writer = ctx.r3.u32;
  const uint32_t data = ctx.r4.u32 - 4u;
  const uint32_t n = ctx.r5.u32;
  const uint32_t c = PlausibleGuest(data) ? Word(base, data + 36u) : 0u;
  __imp__rex_ParticleDoubleTrail_WriteStream(ctx, base);
  if (PlausibleGuest(data)) {
    StateTrail(base, kDoubleTrail, writer, data, n, c, ctx.r3.u32);
  }
}

// (id, message, ring): a group's message names the sim data its stream is written from.
REX_EXTERN(rex_RenderObject_PostMessage) {
  const uint32_t id = ctx.r3.u32;
  const uint32_t message = ctx.r4.u32;
  if (id != kNoOffset && PlausibleGuest(message)) {
    const uint32_t type = LoadGuestU32(base, message);
    if (type == kGroupMessage || type == kDisplacementGroupMessage) {
      const uint32_t data = LoadGuestU32(base, message + 12u);
      if (PlausibleGuest(data)) {
        refii_bridge_classic_particle_bound(id, data);
      }
    }
  }
  __imp__rex_RenderObject_PostMessage(ctx, base);
}
