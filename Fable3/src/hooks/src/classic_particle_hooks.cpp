// refii - Fable 3's particle groups and trails, stated to the host where the particle system's own
// writers lay each group's stream into the frame's vertex buffer, and where each group's message
// names the sim data it was drawn from.
//
// Every address is the Fable 3 TU3 IDB ("disc" marks one TU3 lacks); each offset names its base.
//
// THE STREAM. Once per particle frame (sub_831BD0F8 -> sub_831C07B0, the particle thread) the two
// stream writers W = *0x8334F4EC / *disc 0x8330E9F4 have their cursor W+0x84 zeroed and their buffer
// locked to W+0x88 (sub_831BA0F8, 0x831C0874..), every live instance updates and then writes its
// emitters through its vtable slot 10 (sub_831BED30 -> sub_831BAF90): the root emitter block
// E = instance+336 by sub_831B3CA8, each child E = child+16 by sub_831AE0A0. Both switch on the
// renderer kind def+32 (def = E+0x2C) and hand the writer (W, D + 4, n), D = E+0x24 the sim's
// particle data, n = E+0x10 the live count (0x831B3CE8..0x831B3D2C), keeping the returned byte
// offset at E+0x78 (`stw r3,0x1C8(r31)` 0x831B3D30), -1 when nothing was written:
//   sub_831AA340  QUADS: n records of 48 bytes at *(D+8) (`lwz r10,0(r4)` 0x831AA3B8, stride 0x30
//                 0x831AA494) become n vertices of 48 bytes at W+0x88 + W+0x84: {rec+16..+31,
//                 rec+8, rec+12, rec+4, min(rec+0, 19.0 flt_8206BD08), rec+32..+47} — Fable 2's
//                 ring copy exactly, so the host takes the records as Fable 2's export does.
//   sub_831AC618  TRAILS: the ring at D (+8 base, +20 head, +32 mask, +36 count c), refused when
//                 c < n (`cmplw r11,r5` 0x831AC628), unrolled with a point either side: c + 3
//                 points of 32 bytes (the ring copied by sub_83133348 at 0x831AC8FC/58, the two
//                 end points stored at 32 (c+1) / 32 (c+2), 0x831AC960..BC).
//   sub_831AB6A0  DOUBLE TRAILS: the same c + 3 points (n >= 2, c >= n, 0x831AB6AC..0x831AB6EC).
// The render side never sees those records: the group's message is copied into the render ring
// with its data replaced by a render copy holding only the material (sub_82C7E270 -> sub_82C7E4E0
// / B70 / C78, `v7[3] = 0`), and the twin's draw (sub_82C88688 -> sub_82C89030) binds the frame's
// buffer at the message's offset R+432, stride 48 (sub_82D74798), skipping a group at -1.
//
// THE JOIN. The submit of the same emitter (sub_831BAA20 -> renderer slot 5 sub_826243C8) fills
// the group's message (holder+16, vtable 0x820019C0 type 6, or 0x820E63CC type 36 for a
// displacement group; disc sub_825FC478: +8 kind, +12 D, +28 n, +112 the offset E+0x78) and posts it
// to its render object with sub_821C20E0(id, message, ring) (0x82624E20). The id indexes the twin
// table *0x835A0080 (sub_8220CF10), so a twin's stream is the one written from the D its id was
// last posted with. The object's end is its remove (sub_82625010 -> sub_82C708C8, world_hooks.cpp).

#include <cstdint>
#include <cstring>
#include <vector>

#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_ParticleQuads_WriteStream);
REX_EXTERN(__imp__rex_ParticleTrail_WriteStream);
REX_EXTERN(__imp__rex_ParticleDoubleTrail_WriteStream);
REX_EXTERN(__imp__rex_RenderObject_PostMessage);

namespace {

const refii::guest::Addr kGroupMessage{0x820019C0u};
const refii::guest::Addr kDisplacementGroupMessage{0x820E63CCu};
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

/// The writer's stream base for the offset it returned: *(W+0x88) + offset (0x831AA378,
/// 0x831AC660, 0x831AB6F0). The buffer is the device's, so it may sit in the physical views.
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
