// refii - Fable 3's cloth, stated to the host where the guest's draw hands each piece's simulated
// vertices to its vertex buffer.
//
// Every address is the Fable 3 TU3 IDB ("disc" marks one TU3 lacks); each offset names its base.
//
// THE PIPELINE. A render instance of a model (rigid: type 2, vtable 0x82006460; skinned: type 5,
// vtable 0x82006648, the type its base constructor is given at 0x82CC7CA4) keeps one cloth PROXY
// per record at instance+0xB8..+0xBC (built once by sub_82201A80 from the LOD's rigid records,
// LOD+0x2C count / +0x5C array of 152 bytes, or sub_82226D20 from its skinned records, LOD+0x30 /
// +0x60 of 168 bytes; a record's +0x40 is its cloth definition, the .mdl block). The proxy
// (288 bytes, ctor sub_82CF2478, vtable 0x820E9BA4) holds +8 the definition and +0xC the instance;
// sub_82CEB888 gives it a 544-byte SIM ITEM (item+0 the proxy). The definition's rest positions
// are def+0x68..+0x6C, so a piece has (def+0x6C - def+0x68) >> 4 vertices.
//
// THE STEP. Once per render frame (sub_821989D8 counts the frame, `stw r10,dword_83597310`
// 0x82198A64, then calls the kicker sub_821C68D8 at 0x82198D0C), the kicker asks each proxy's
// fade sub_821C6680: above 0 it sets proxy+0x111 (0x821C6BF8), hands the instance's world matrix
// (instance+0x150, sub_82263DC8) and queues the item (sub_82263C30: ResetEvent item+0x218, item
// +0x214 = 0); at 0 it clears proxy+0x111 (0x821C6C20). The "Cloth Simulation" thread
// (sub_82CEB790, hardware thread 2) drains the queue through sub_82CE98B0(item, dt): forces and
// Verlet into the swapped position buffers item+0x34/+0x38 (sub_82CEAE08 swaps them), up to 31
// relaxation passes (0x82CE99F8), the welds, the bounds (sub_82CE8DE8), and SetEvent item+0x218.
// Positions are the MODEL's own space (the file's Z-up), the instance's world matrix kept beside
// them (sub_82263DC8 hands it to sub_82198580) — measured live: a piece's first vertex at
// (-0.18, -0.09, 0.96) while item+0x80 and item+0xC0 both held a world translation of
// (156.1, 106.4, 126.8).
//
// THE PUBLISH — the statement. The instance's draw builds a cloth draw item only for a record
// whose proxy+0x111 is set (sub_82CC8720 0x82CC8CF4, sub_82CBE0D0 0x82CBE4A0; otherwise the
// record draws its own skinned submesh), and that item's draw (sub_8223EDD0, reached only
// indirectly; it finds the item whose +0 is its proxy) calls sub_82267EE8(item) at 0x8223EE60:
//   - returns at once if item+0x214 is set (already published since the last queue, 0x82267F04);
//   - waits on item+0x218 until the step is done (0x82267F18..0x82267F54);
//   - below a fade of 1.0 (item+0x210 against flt_82065440 = 1.0, 0x82267F74) lerps the drawn
//     positions, sub_82CEAD98(item, dst, skin item+0x3C, sim item+0x38, f1 fade): into item+0x3C
//     when the definition has a skin driver (def+0xC4 non-null with +4 > 0, 0x82267F7C..0x82267FBC),
//     else into item+0x38; the drawn buffer is r30 from 0x82267FD0 on;
//   - builds the normals into item+0x44 and the tangents into item+0x64 from the drawn positions
//     (sub_82267AA8 at 0x82267FD8);
//   - writes 48 bytes per vertex to the locked vertex buffer (0x82268204..0x82268250): the drawn
//     position (w from def+0x78's 4 bytes), the normal item+0x44, the tangent item+0x64 plus
//     def+0x88 (`vaddfp` 0x82268248);
//   - sets item+0x214 = 1 (0x82268284) whether or not the lock answered.
// Wrapped here, the hook copies exactly those three streams after the call returns, when the call
// published (item+0x214 went 0 -> 1). They are coherent: the step finished before the call read
// them, and the next queue (and so the next write) is the next frame's kicker on this same
// render frame path.
//
// THE PIECE'S END. sub_82CD3EC8(instance) marks each of its proxies' items +0x216 when the
// instance lets its cloth go; the kicker destroys a marked item, sub_82CE8480(item) at 0x821C691C
// (also sub_82CEB888's failed build at 0x82CEB904), which drops it here first.
//
// THE JOIN. The publish states the instance's own render handle (instance+0x10: the slot it holds
// in the render-side table dword_835A0080, sub_822071F0 0x82207250..0x82207278; measured equal on
// 1818 live instances), the proxy's slot in the instance's list and the instance type. The host
// bridge joins handles to entities (Fable3/src/hostbridge/src/cloth_feed.cpp).
//
// THE INSTANT. The kicker poses the instance for the render frame's time R (sub_821989D8 stores
// it at dbl_83597318, 0x82198A60, and hands it to the view, 0x82198D00 -> sub_8218E148 view+0x308)
// inside the instance's transform window W = *(instance+0xB0): t0 at W+0, t1 at W+8, the times
// of the last two render-queue batches that moved it (sub_822127E8 shifts t1 into t0 for a newer
// batch, 0x82212804/0x82212808). sub_822055F8 takes alpha = clamp((R - t0) / (t1 - t0)), 1 when
// t0 is 0 or the span is not positive, and sub_82260D00 poses the instance there; the cloth's
// skin and colliders are built from that pose. So the pose is the instance at clamp(R, t0, t1)
// — not the newest step, and not the game tick's — and each publish is stamped with that time as
// a guest frame (refii_bridge_guest_frame_at, from the HF pass's step times, view_feed.cpp). The
// window is the one the kicker read: only the render queue's drain writes it, before the views.
// Stamped with the render frame dword_83597310 as well.

#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_ClothItem_Publish);
REX_EXTERN(__imp__rex_ClothItem_Destroy);

namespace {

const refii::guest::Addr kRenderFrame{0x83597310u};
/// The render frame's time R, the instant the kicker poses every cloth instance for.
const refii::guest::Addr kRenderClock{0x83597318u};
constexpr uint32_t kSkinnedInstanceType = 5u;
constexpr uint32_t kMaxSlots = 256u;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

float LoadGuestF32(const uint8_t* base, uint32_t address) {
  const uint32_t bits = LoadGuestU32(base, address);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

bool PlausibleGuest(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

uint32_t Word(const uint8_t* base, uint32_t address) {
  return PlausibleGuest(address) ? LoadGuestU32(base, address) : 0u;
}

double LoadGuestF64(const uint8_t* base, uint32_t address) {
  uint64_t bits = 0;
  std::memcpy(&bits, base + address, sizeof(bits));
  bits = __builtin_bswap64(bits);
  double value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

/// The guest frame of the instant sub_82263DC8 posed `instance` for: clamp(R, t0, t1) (the
/// header's THE INSTANT). The current guest frame when the window or the step times are missing.
double PoseFrame(const uint8_t* base, uint32_t instance) {
  const uint32_t window = Word(base, instance + 0xB0u);
  double posed = -1.0;
  if (PlausibleGuest(window)) {
    const double t0 = LoadGuestF64(base, window);
    const double t1 = LoadGuestF64(base, window + 8u);
    const double now = LoadGuestF64(base, kRenderClock);
    if (t0 == 0.0 || !(t1 > t0)) {
      posed = t1;
    } else {
      posed = now < t0 ? t0 : (now > t1 ? t1 : now);
    }
  }

  const double frame = posed > 0.0 ? refii_bridge_guest_frame_at(posed) : -1.0;
  return frame >= 0.0 ? frame : static_cast<double>(refii_bridge_guest_frame());
}

/// The vertices sub_82267EE8 wrote, as REFII_CLOTH_VERTEX_FLOATS floats each. One buffer: the
/// publish has one caller, the cloth draw item's draw (sub_8223EDD0) on the render frame's path.
float g_vertices[REFII_CLOTH_MAX_VERTICES * REFII_CLOTH_VERTEX_FLOATS];

void State(const uint8_t* base, uint32_t item) {
  const uint32_t proxy = Word(base, item);
  const uint32_t def = Word(base, proxy + 8u);
  const uint32_t instance = Word(base, proxy + 0xCu);
  if (!PlausibleGuest(proxy) || !PlausibleGuest(def) || !PlausibleGuest(instance)) {
    return;
  }

  const uint32_t rest_begin = Word(base, def + 0x68u);
  const uint32_t rest_end = Word(base, def + 0x6Cu);
  if (!PlausibleGuest(rest_begin) || rest_end <= rest_begin) {
    return;
  }
  const uint32_t count = (rest_end - rest_begin) >> 4;
  if (count > REFII_CLOTH_MAX_VERTICES) {
    return;
  }

  // The proxy's record slot in the instance's list (instance+0xB8..+0xBC).
  const uint32_t first = Word(base, instance + 0xB8u);
  const uint32_t last = Word(base, instance + 0xBCu);
  if (!PlausibleGuest(first) || last <= first || (last - first) / 4u > kMaxSlots) {
    return;
  }
  uint32_t slot = 0xFFFFFFFFu;
  for (uint32_t at = first; at < last; at += 4u) {
    if (LoadGuestU32(base, at) == proxy) {
      slot = (at - first) / 4u;
      break;
    }
  }
  if (slot == 0xFFFFFFFFu) {
    return;
  }

  // The drawn buffer, as 0x82267F74..0x82267FD0 chose it.
  uint32_t drawn = Word(base, item + 0x38u);
  if (LoadGuestF32(base, item + 0x210u) < 1.0f) {
    const uint32_t driver = Word(base, def + 0xC4u);
    if (driver != 0u && static_cast<int32_t>(Word(base, driver + 4u)) > 0) {
      drawn = Word(base, item + 0x3Cu);
    }
  }
  const uint32_t normals = Word(base, item + 0x44u);
  const uint32_t tangents = Word(base, item + 0x64u);
  const uint32_t handedness = Word(base, def + 0x88u);
  if (!PlausibleGuest(drawn) || !PlausibleGuest(normals) || !PlausibleGuest(tangents) ||
      !PlausibleGuest(handedness)) {
    return;
  }

  for (uint32_t v = 0; v < count; ++v) {
    float* out = g_vertices + (v * REFII_CLOTH_VERTEX_FLOATS);
    const uint32_t at = v * 16u;
    for (uint32_t lane = 0; lane < 3u; ++lane) {
      out[lane] = LoadGuestF32(base, drawn + at + (lane * 4u));
      out[4u + lane] = LoadGuestF32(base, normals + at + (lane * 4u));
    }
    out[3] = 1.0f;
    out[7] = 0.0f;
    for (uint32_t lane = 0; lane < 4u; ++lane) {
      out[8u + lane] = LoadGuestF32(base, tangents + at + (lane * 4u)) +
                       LoadGuestF32(base, handedness + at + (lane * 4u));
    }
  }

  const uint32_t flags = Word(base, instance + 4u) == kSkinnedInstanceType ? REFII_CLOTH_SKINNED : 0u;
  refii_bridge_cloth_published(item, Word(base, instance + 0x10u), slot, flags, Word(base, kRenderFrame),
                               PoseFrame(base, instance), g_vertices, count);
}

}  // namespace

REX_EXTERN(rex_ClothItem_Publish) {
  const uint32_t item = ctx.r3.u32;
  const bool pending = PlausibleGuest(item) && base[item + 0x214u] == 0u;
  __imp__rex_ClothItem_Publish(ctx, base);
  if (pending && base[item + 0x214u] != 0u) {
    State(base, item);
  }
}

REX_EXTERN(rex_ClothItem_Destroy) {
  const uint32_t item = ctx.r3.u32;
  if (PlausibleGuest(item)) {
    refii_bridge_cloth_dropped(item);
  }
  __imp__rex_ClothItem_Destroy(ctx, base);
}
