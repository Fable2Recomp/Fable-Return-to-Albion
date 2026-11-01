// refii - Fable 3's cloth, stated to the host where the guest's draw hands each piece's simulated
// vertices to its vertex buffer.
//
// Every address is the Fable 3 IDB; each offset names its base.
//
// THE PIPELINE. A render instance of a model (rigid: type 2, vtable 0x820064E0; skinned: type 5,
// vtable 0x820066C8, the type its base constructor is given at 0x82C93E7C) keeps one cloth PROXY
// per record at instance+0xB8..+0xBC (built once by sub_82202208 from the LOD's rigid records,
// LOD+0x2C count / +0x5C array of 152 bytes, or sub_82228000 from its skinned records, LOD+0x30 /
// +0x60 of 168 bytes; a record's +0x40 is its cloth definition, the .mdl block). The proxy
// (288 bytes, ctor sub_82CBF9E8, vtable 0x820E71C8) holds +8 the definition and +0xC the instance;
// sub_82CB8168 gives it a 544-byte SIM ITEM (item+0 the proxy). The definition's rest positions
// are def+0x68..+0x6C, so a piece has (def+0x6C - def+0x68) >> 4 vertices.
//
// THE STEP. Once per render frame (sub_821988C8 counts the frame, `stw r10,dword_83556810`
// 0x82198954, then calls the kicker sub_821C6800 at 0x82198BFC), the kicker asks each proxy's
// fade sub_821C65A8: above 0 it sets proxy+0x111 (0x821C6B20), hands the instance's world matrix
// (instance+0x150, sub_822654A8) and queues the item (sub_82265310: ResetEvent item+0x218, item
// +0x214 = 0); at 0 it clears proxy+0x111 (0x821C6B48). The "Cloth Simulation" thread
// (sub_82CB8070, hardware thread 2) drains the queue through sub_82CB6190(item, dt): forces and
// Verlet into the swapped position buffers item+0x34/+0x38 (sub_82CB76E8 swaps them), up to 31
// relaxation passes (0x82CB62D8), the welds, the bounds (sub_82CB56C8), and SetEvent item+0x218.
// Positions are the MODEL's own space (the file's Z-up), the instance's world matrix kept beside
// them (sub_822654A8 hands it to sub_82198470) — measured live: a piece's first vertex at
// (-0.18, -0.09, 0.96) while item+0x80 and item+0xC0 both held a world translation of
// (156.1, 106.4, 126.8).
//
// THE PUBLISH — the statement. The instance's draw builds a cloth draw item only for a record
// whose proxy+0x111 is set (sub_82C948F8 0x82C94ECC, sub_82C89108 0x82C894D8; otherwise the
// record draws its own skinned submesh), and that item's draw (sub_82240038, reached only
// indirectly; it finds the item whose +0 is its proxy) calls sub_82269870(item) at 0x822400C8:
//   - returns at once if item+0x214 is set (already published since the last queue, 0x8226988C);
//   - waits on item+0x218 until the step is done (0x822698A0..0x822698DC);
//   - below a fade of 1.0 (item+0x210 against flt_82065480 = 1.0, 0x822698FC) lerps the drawn
//     positions, sub_82CB7678(item, dst, skin item+0x3C, sim item+0x38, f1 fade): into item+0x3C
//     when the definition has a skin driver (def+0xC4 non-null with +4 > 0, 0x82269904..0x82269944),
//     else into item+0x38; the drawn buffer is r30 from 0x82269958 on;
//   - builds the normals into item+0x44 and the tangents into item+0x64 from the drawn positions
//     (sub_82269430 at 0x82269960);
//   - writes 48 bytes per vertex to the locked vertex buffer (0x82269B8C..0x82269BD8): the drawn
//     position (w from def+0x78's 4 bytes), the normal item+0x44, the tangent item+0x64 plus
//     def+0x88 (`vaddfp` 0x82269BD0);
//   - sets item+0x214 = 1 (0x82269C0C) whether or not the lock answered.
// Wrapped here, the hook copies exactly those three streams after the call returns, when the call
// published (item+0x214 went 0 -> 1). They are coherent: the step finished before the call read
// them, and the next queue (and so the next write) is the next frame's kicker on this same
// render frame path.
//
// THE PIECE'S END. sub_82CA0500(instance) marks each of its proxies' items +0x216 when the
// instance lets its cloth go; the kicker destroys a marked item, sub_82CB4D60(item) at 0x821C6844
// (also sub_82CB8168's failed build at 0x82CB81E4), which drops it here first.
//
// THE JOIN. The publish states the instance's own render handle (instance+0x10: the slot it holds
// in the render-side table dword_8355F464, sub_82207A98 0x82207AF8..0x82207B20; measured equal on
// 1818 live instances), the proxy's slot in the instance's list and the instance type. The host
// bridge joins handles to entities (Fable3/src/hostbridge/src/cloth_feed.cpp).
//
// THE TICK. Each publish is stamped with the world's game tick as GetWorldFrame states it
// (sub_82209868: component dword_83556540, game = component+0xC, game+0xBA set, then
// *(*(*(game+0x1C)+4)+0x100)) and with the render frame dword_83556810.

#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_ClothItem_Publish);
REX_EXTERN(__imp__rex_ClothItem_Destroy);

namespace {

constexpr uint32_t kRenderFrame = 0x83556810u;
constexpr uint32_t kMainGameComponent = 0x83556540u;
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

/// sub_82209868, GetWorldFrame: the world's game tick, 0 when no world stands.
uint32_t WorldTick(const uint8_t* base) {
  const uint32_t component = Word(base, kMainGameComponent);
  const uint32_t game = Word(base, component + 0xCu);
  if (!PlausibleGuest(game) || base[game + 0xBAu] == 0u) {
    return 0u;
  }
  return Word(base, Word(base, Word(base, game + 0x1Cu) + 4u) + 0x100u);
}

/// The vertices sub_82269870 wrote, as REFII_CLOTH_VERTEX_FLOATS floats each. One buffer: the
/// publish has one caller, the cloth draw item's draw (sub_82240038) on the render frame's path.
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

  // The drawn buffer, as 0x822698FC..0x82269958 chose it.
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
                               WorldTick(base), g_vertices, count);
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
