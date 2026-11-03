// refii - Fable 3's map tables, stated to the host at the guest's own entry points: the world map
// (GUI.ToggleWorldMap, the game component at GUI+0x54, ctor sub_824CC0E0) and the town map
// (GUI.ToggleTownMapPrototype, sub_82950FD0) each build a 3D table in a render scene of their own
// and PRESENT that scene in place of the world. Godot draws it (Fable3MapTable.cs) from what these
// hooks see the guest add, move, remove and present.
//
// Every address is the Fable 3 IDB; each offset names its base.
//
// THE HOLDER H (224 bytes, ctor sub_8292AC90; the world map's at component+40, made by
// sub_824CD738 at 0x824CD850, the town map's by sub_82951408 at 0x82951438):
//   H+0   the scene: sub_82C4FEF0(1, 1, 0) (0x8292ACFC, `stw r3, 0(r31)` 0x8292AD00)
//   H+4   std::list of every render object id added through H (sub_82CC79B0 at 0x8292AFD8)
//   H+16  the camera's render object id, -1 until the camera's first statement (0x8292ACB8)
//   H+32  the camera MESSAGE (vtable off_82001480, ctor sub_82C3C7E8 at 0x8292ACC4)
//   H+208 presented byte (0x8292ACF8; compared and stored by sub_8292AEA8, 0x8292AEB8 / 0x8292AF88)
// Its methods:
//   sub_8292AF98(H, msg) ADD: `sub_82257228(msg, *H, 1)` (0x8292AFC0), the id appended to H+4,
//       returned. Every map object comes through here: the static entities' sub_8292CCE8
//       (0x8292CF58 animated, 0x8292D0CC static), the characters' sub_829308A0 (0x8293092C), the
//       lights (sub_824CDF40), the town map's (sub_82951908), and sub_8292AFF8's add.
//   sub_8292AFF8(H, int* id, msg) ADD OR UPDATE: `*id == -1` adds (0x8292B044), else
//       `sub_821C2120(*id, msg, 1)` re-sends the whole message (0x8292B024). The cameras call it
//       as (H, H+16, H+32) after copying their 0x88-byte block into H+32 (sub_822645D8): the world
//       map's camera ctor sub_82935CA8 (0x82935F14) and its update sub_829360E8 (0x82936234), the
//       town map's sub_829516E0 (0x829518F4) and sub_82952D38 (0x82952E04).
//   sub_8292AEA8(H, on) PRESENT: on, `sub_822E26D0(engine, t, *H, *(H+16))` (r5 = `lwz r28,0(r3)`
//       0x8292AEE0, r6 = `lwz r29,0x10(r3)` 0x8292AEDC, call 0x8292AF84) makes the engine draw H's
//       scene through H's camera (engine view +144 scene, +140 camera id, sub_822E26D0
//       0x822E270C/0x822E2714); off, it hands back the world view's own (`**view`, `*(view+12)`,
//       0x8292AF40..0x8292AF68). The world map presents at open (sub_824CD738, 0x824CD8C4) and
//       un-presents at close (sub_824CDBB8, 0x824CDCEC); the town map at 0x829514B8.
//   sub_8292AD70(H) DESTROY: un-presents (0x8292AD84), then writes opcode 4 for every id of H+4
//       straight into the ring (0x8292ADB8..0x8292AE20, not through sub_82C3C148).
// sub_8292B060(H, id) removes one through sub_82C3C148(id, 1) (0x8292B0E4) — world_hooks.cpp's
// rex_RenderScene_RemoveObject, which hands the id here (MapTableObjectRemoved).
//
// THE CAMERA MESSAGE, the layout sub_82261CB8 fills for the world view at V+32 (V+48 position
// 0x82261DA0, V+64 orientation 0x82261D98, V+152 / V+156 the horizontal / vertical fov
// 0x82261DA4 / 0x82261DB8, V+88 aspect 0x82261DD4) and sub_82C3C7E8 constructs (near 0.1 at +48,
// far 5000 at +52, 0x82C3C864 / 0x82C3C868): +16 position, +32 orientation quaternion (x, y, z, w),
// +48 near, +52 far, +56 aspect, +120 horizontal fov, +124 vertical fov (2 atan(tan(h/2) /
// aspect), sub_82935CA8 0x82935EA0..0x82935EC4). The quaternion is the D3D one of the matrix whose
// rows are (forward x up, forward, up): sub_82274210 builds that matrix (rows at -0x80, -0x70,
// -0x60: `vsubfp v30` the cross, forward, up, 0x82274278..0x822742A0) and takes x = (m12 - m21)s,
// y = (m20 - m02)s, z = (m01 - m10)s (0x822742E4..0x82274350, lanes placed by the masks at
// unk_82065350/60/70). So the shell's forward is that quaternion's row 1, its up row 2.
//
// THE MESH MESSAGES. Both mesh classes lay out the same words: off_820E3A14 (static; built on
// the stack at 0x8292D00C..0x8292D0C8, and the FX mesh descriptor's own, 0x825FC72C) and
// off_820E3AA4 (animated; ctors sub_82C4B900 and sub_82C4B7D0):
//   +4 flags (0x2000 IgnoreViewportCulling, 0x800 displacement: 0x8292CD28 / 0x8292CD38)
//   +16 orientation quaternion (entity+128, 0x8292D034 / 0x82C4B940)
//   +32 position (entity+112, 0x8292D03C / 0x82C4B948)
//   +48 uniform scale (entity+48, 0x8292D010 / 0x82C4B938)
//   +112 the model's resource id (*arg: entity+260, MeshID's number or sub_8312F3D8 of MeshPath,
//        sub_8292B6A8 0x8292B728 / 0x8292B75C; `stw r31` 0x8292D0A4, sub_82C4B900 0x82C4B978)
//   +132 the static class's record id (entity+256, the low word of `std r29` 0x8292D0C8)
//
// THE COMMANDS. A map entity's update (sub_8292C070) moves its object with sub_821F5110(id, cmd,
// 1), only for what changed since it last sent (it keeps the sent copy, 0x8292C234 / 0x8292C270
// / 0x8292C300): each command class answers its id from slot 1 and serialises from slot 2.
//   off_82000D9C id 3 (`li r3, 3` 0x82D126E8): +4 +8 +12 position x y z, +16 orientation
//       quaternion (0x8292C1F8..0x8292C22C; serialiser sub_822411B0 copies +4..+12 and +16..+31)
//   off_820A0110 id 6 (0x82C49800): +4 uniform scale (0x8292C250; sub_82C4A728)
//   off_820A00D8 id 15 (0x82C4A660): +16 tint r g b a (entity+80, 0x8292C29C..0x8292C2C0; sub_82C4A668)
//   off_820A00CC id 14 (0x82C4A4F8): +4 alpha, the tint's a (0x8292C2D0; sub_82C4A500)
// The render ids are one pool across rings (sub_82C9A2F8 refills a ring's cache from the shared
// free list and counter), so an id names one object while it lives.

#include "map_table_hooks.h"

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_MapScene_Add);
REX_EXTERN(__imp__rex_MapScene_AddOrUpdate);
REX_EXTERN(__imp__rex_MapScene_Present);
REX_EXTERN(__imp__rex_MapScene_Destroy);
REX_EXTERN(__imp__rex_RenderObject_Command);

namespace {

constexpr uint32_t kStaticMeshMessage = 0x820E3A14u;
constexpr uint32_t kAnimatedMeshMessage = 0x820E3AA4u;
constexpr uint32_t kTransformCommand = 0x82000D9Cu;
constexpr uint32_t kScaleCommand = 0x820A0110u;
constexpr uint32_t kTintCommand = 0x820A00D8u;
constexpr uint32_t kAlphaCommand = 0x820A00CCu;

constexpr uint32_t kHolderCamera = 16u;
constexpr uint32_t kHolderCameraMessage = 32u;
constexpr uint32_t kHolderPresented = 208u;

/// Ids at or past this are not tracked (the twin table is a few thousand in a region).
constexpr uint32_t kIds = 65536u;
/// The holder that owns each render id, 0 for none.
std::atomic<uint32_t> g_owner[kIds];

// Row words (core/payload_bridge.h, REFII_SCENE_OBJECT_WORDS).
constexpr uint32_t kRowQuaternion = 4u;
constexpr uint32_t kRowPosition = 8u;
constexpr uint32_t kRowScale = 11u;
constexpr uint32_t kRowTint = 12u;
constexpr uint32_t kRowAlpha = 16u;
constexpr uint32_t kOne = 0x3F800000u;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

bool IsMeshMessage(const uint8_t* base, uint32_t message) {
  if (!Plausible(message)) {
    return false;
  }
  const uint32_t vtable = LoadGuestU32(base, message);
  return vtable == kStaticMeshMessage || vtable == kAnimatedMeshMessage;
}

/// A mesh message's row: model, flags, record, class, its transform, a white tint and full alpha
/// (the commands that follow move them).
void MeshRow(const uint8_t* base, uint32_t message, uint32_t* words) {
  const uint32_t vtable = LoadGuestU32(base, message);
  words[0] = LoadGuestU32(base, message + 112u);
  words[1] = LoadGuestU32(base, message + 4u);
  words[2] = vtable == kStaticMeshMessage ? LoadGuestU32(base, message + 132u) : 0u;
  words[3] = vtable;
  for (uint32_t i = 0u; i < 4u; ++i) {
    words[kRowQuaternion + i] = LoadGuestU32(base, message + 16u + i * 4u);
  }
  for (uint32_t i = 0u; i < 3u; ++i) {
    words[kRowPosition + i] = LoadGuestU32(base, message + 32u + i * 4u);
  }
  words[kRowScale] = LoadGuestU32(base, message + 48u);
  for (uint32_t i = 0u; i < 4u; ++i) {
    words[kRowTint + i] = kOne;
  }
  words[kRowAlpha] = kOne;
}

void StateMesh(const uint8_t* base, uint32_t holder, uint32_t id, uint32_t message) {
  if (id >= kIds) {
    return;
  }
  uint32_t words[REFII_SCENE_OBJECT_WORDS];
  MeshRow(base, message, words);
  g_owner[id].store(holder, std::memory_order_release);
  refii_bridge_scene_object_stated(holder, id, words);
}

/// The transform part of a re-sent mesh message, its tint and alpha kept.
void RestateMeshTransform(const uint8_t* base, uint32_t id, uint32_t message) {
  uint32_t words[REFII_SCENE_OBJECT_WORDS];
  MeshRow(base, message, words);
  refii_bridge_scene_object_patch(id, 0u, words, kRowTint);
}

}  // namespace

namespace refii::fable3 {

void MapTableObjectRemoved(uint32_t id) {
  if (id < kIds && g_owner[id].exchange(0u, std::memory_order_acq_rel) != 0u) {
    refii_bridge_scene_object_removed(id);
  }
}

}  // namespace refii::fable3

REX_EXTERN(rex_MapScene_Add) {
  const uint32_t holder = ctx.r3.u32;
  const uint32_t message = ctx.r4.u32;
  const bool mesh = IsMeshMessage(base, message);
  __imp__rex_MapScene_Add(ctx, base);
  const uint32_t id = ctx.r3.u32;
  if (mesh && id != 0xFFFFFFFFu) {
    StateMesh(base, holder, id, message);
  }
}

REX_EXTERN(rex_MapScene_AddOrUpdate) {
  const uint32_t holder = ctx.r3.u32;
  const uint32_t id_slot = ctx.r4.u32;
  const uint32_t message = ctx.r5.u32;
  const uint32_t before = Plausible(id_slot) ? LoadGuestU32(base, id_slot) : 0xFFFFFFFFu;
  __imp__rex_MapScene_AddOrUpdate(ctx, base);
  if (!Plausible(holder) || !Plausible(message)) {
    return;
  }

  if (id_slot == holder + kHolderCamera && message == holder + kHolderCameraMessage) {
    uint32_t words[REFII_SCENE_CAMERA_WORDS];
    for (uint32_t i = 0u; i < 3u; ++i) {
      words[i] = LoadGuestU32(base, message + 16u + i * 4u);
    }
    for (uint32_t i = 0u; i < 4u; ++i) {
      words[3u + i] = LoadGuestU32(base, message + 32u + i * 4u);
    }
    words[7] = LoadGuestU32(base, message + 124u);
    words[8] = LoadGuestU32(base, message + 48u);
    words[9] = LoadGuestU32(base, message + 52u);
    refii_bridge_scene_camera_stated(holder, words);
    return;
  }

  // An add went through sub_8292AF98 (rex_MapScene_Add states it); an update re-sent the message.
  if (before != 0xFFFFFFFFu && before < kIds && IsMeshMessage(base, message) &&
      g_owner[before].load(std::memory_order_acquire) == holder) {
    RestateMeshTransform(base, before, message);
  }
}

REX_EXTERN(rex_MapScene_Present) {
  const uint32_t holder = ctx.r3.u32;
  __imp__rex_MapScene_Present(ctx, base);
  if (Plausible(holder)) {
    refii_bridge_scene_presented(holder, base[holder + kHolderPresented] != 0u ? 1 : 0);
  }
}

REX_EXTERN(rex_MapScene_Destroy) {
  const uint32_t holder = ctx.r3.u32;
  __imp__rex_MapScene_Destroy(ctx, base);
  if (holder == 0u) {
    return;
  }
  for (uint32_t id = 0u; id < kIds; ++id) {
    uint32_t owner = holder;
    g_owner[id].compare_exchange_strong(owner, 0u, std::memory_order_acq_rel);
  }
  refii_bridge_scene_holder_dropped(holder);
}

REX_EXTERN(rex_RenderObject_Command) {
  const uint32_t id = ctx.r3.u32;
  const uint32_t command = ctx.r4.u32;
  const bool owned = id < kIds && g_owner[id].load(std::memory_order_acquire) != 0u && Plausible(command);
  __imp__rex_RenderObject_Command(ctx, base);
  if (!owned) {
    return;
  }

  uint32_t words[4];
  switch (LoadGuestU32(base, command)) {
    case kTransformCommand:
      for (uint32_t i = 0u; i < 4u; ++i) {
        words[i] = LoadGuestU32(base, command + 16u + i * 4u);
      }
      refii_bridge_scene_object_patch(id, kRowQuaternion, words, 4u);
      for (uint32_t i = 0u; i < 3u; ++i) {
        words[i] = LoadGuestU32(base, command + 4u + i * 4u);
      }
      refii_bridge_scene_object_patch(id, kRowPosition, words, 3u);
      break;
    case kScaleCommand:
      words[0] = LoadGuestU32(base, command + 4u);
      refii_bridge_scene_object_patch(id, kRowScale, words, 1u);
      break;
    case kTintCommand:
      for (uint32_t i = 0u; i < 4u; ++i) {
        words[i] = LoadGuestU32(base, command + 16u + i * 4u);
      }
      refii_bridge_scene_object_patch(id, kRowTint, words, 4u);
      break;
    case kAlphaCommand:
      words[0] = LoadGuestU32(base, command + 4u);
      refii_bridge_scene_object_patch(id, kRowAlpha, words, 1u);
      break;
    default:
      break;
  }
}
