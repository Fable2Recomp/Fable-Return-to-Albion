// refii - Fable 3's world, stated to the host at the guest's own entry points: a level's up and
// down, each effect's creation, placement and end, each decal the renderer is handed, the
// displacement quad the drunk warp adds to the render scene, and the render object an effect
// adds for each mesh particle.
//
// Every address is the Fable 3 TU3 IDB ("disc" marks one TU3 lacks); each offset names its base.
//
// LEVELS. The level manager (vtable-less, ctor sub_8234FA98; the shell's level feed reaches it as
// *(*(*(*(game+0x1C)+4)+0x10)+4)) loads with sub_82350488(mgr, name, a3, a4): it unloads the
// standing level first (`bl sub_82350AD8` 0x82350500, flag 1), stores the new level object at
// mgr+40 (sub_8234F878 at 0x82350590), runs the load (sub_827172A8 at 0x82350644) and, on its
// success byte, `bl sub_823535D8(mgr)` at 0x82350674 — the level's post-load pass, after every
// entity of the .save stands. sub_82350AD8(mgr, flag) is the unload: it returns at once when
// mgr+40 is null (`lwz r11,0x28(r3)` 0x82350AE4) and otherwise stops the level's effects and
// marks the teardown (`stb 1, 0xFD(r31)` 0x82350C2C). So: up at sub_823535D8's entry, down at
// sub_82350AD8's, keyed by the manager, since the front end's GUI world has a manager of its own.
//
// EFFECTS. Game code never touches a particle instance: it posts commands keyed by a HANDLE
// through the wrappers at 0x831C1148..0x831C1E78, each `sub_831CB720` allocating a command,
// `cmd+4` the opcode, `cmd+8` the handle, and `sub_831BC500` queueing it for the particle thread
// (sub_831BD8F8 drains into the executor sub_831BD1A8). The wrappers hooked here:
//   sub_831C1C78(int* handle_out, u32 name_hash)  CREATE: sub_831BC630 writes the new handle to
//       *handle_out (`stw` at 0x831BC6E0), then `cmd+12 = name_hash` (0x831C1CBC) — the FNV-1 of
//       the effect's backslashed path (sub_83165FC0 → sub_822D5DE8), the particle bank's key.
//   sub_831C1C20(handle)                          START (opcode 1, 0x831C1C50): the instance joins
//       the live registry dword_835A706C.
//   sub_831C1BB8(handle, scene)                   render scene (opcode 2, 0x831C1BF4), `cmd+12`.
//   sub_831C19F0(handle; v1 position)             opcode 6: `stvx128 v127, r3, r10(16)` at
//       0x831C1A38 after `vmr128 v127, v1` — the root position every emitter takes (sub_831B5618
//       stores it at emitter+0x50).
//   sub_831C1960(handle; v1 position, v2 velocity) opcode 7, the same position at cmd+16 (0x831C19B8).
//   sub_831C1890(handle; v1 quaternion)           opcode 8 at cmd+16 (0x831C18D8): sub_831B55C8 →
//       sub_831B6B58 per emitter.
//   sub_831C1770(handle)                          KILL (opcode 11, 0x831C17A0): sub_831B54D8 sets
//       every emitter's +0xC8.
//   sub_831C1A68(handle; f1 seconds)              KILL WITH FADE-OUT (opcode 5, `stfs f31, 0xC`
//       0x831C1A94): sub_831BC920 → sub_831B5570 takes n = floor(seconds × flt_8334F4F0 + 0.5)
//       frames (flt_8334F4F0 = 30.0, sub_831C0F90) and sub_831B6AE0 stores n at +192 and +196 of
//       every chained emitter (0x831B6B04/0x831B6B08). +196 counts down a frame at a time; the
//       emitter's intensity is +196/+192 (sub_831B6760) and it ends at 0 — Fable 2's
//       KillParticleWithFadeOut counter word for word. The Lua KillParticleWithFadeOut, spells and
//       melee strikes post it.
//   sub_831BC758(cmd)                             RELEASE (executor case 33, `bl` at 0x831BD2B8):
//       erases the handle-map node of cmd+8 (sub_82D373E0 / sub_82D37028 under dword_835A7130).
//       The guest posts it once for EVERY handle that ends — a natural end, a kill, a fade's end:
//       the sim retires the instance (sub_831BF490) and its listener sub_82D36DD0 queues the
//       handle; the main thread's sub_831BCB68 tells the owner (CECParticleEmitter zeroes its
//       +0x14, sub_826ADB48 0x826ADCAC) and posts op 33 (0x831BCE34). Kills (op 11, op 5) do not
//       erase a handle; this does, and only after it may sub_831BC630 hand the value out again.
//   sub_831C1B30(handle, colour*)                 COLOUR (opcode 3): the four bytes are blue, green,
//       red, alpha (`lbz` 0..3 into cmd+0xE/+0xD/+0xC/+0xF, 0x831C1B70..8C); the executor scales
//       them by 1/255 and sub_831B52C0 `vsel`s red, green, blue into inst+80 under mask
//       unk_82065300 (lane 3 set), keeping the instance's alpha.
//   sub_831C15E8(handle; f1 alpha)                ALPHA (opcode 15): sub_831B5188 `vperm`s the scalar
//       into lane 3 of inst+80 (unk_82065340 = 00..0B,10..13). Both setters then copy inst+80 to
//       +400 of every node of type 0 or 9. The collection ctor seeds inst+80 with 1.0 x4
//       (`vspltisw v0,1` / `vcfux` / `stvx128 v13,r3,0x50`, 0x831B6454..0x831B64B4).
//   sub_831C1828(handle; f1 scale)                SCALE (opcode 9): sub_831B5068 calls node vtable
//       +48 on every node — an emitter stores it at +0xCC and +0x1B4 (disc 0x82D05FB8).
//   sub_831C1650(handle; v1 start, v2 end)        SPLINE ENDS (opcode 13, v1 -> cmd+0x10, v2 -> cmd+0x20):
//       the effect's first spline emitter (type 9, sub_82D37828) takes the two world points;
//       sub_831B2660 → sub_831B20E8 spreads a 2-point list over type+244 + 1 even points, the
//       interior ones displaced (sub_831B2598). Energy beams (CECEnergyBeamAttack) and arcs.
//   sub_831C1E78(handle, list, index, vector*)    SPLINE POINTS (opcode 14): a whole point list,
//       16-byte points from vector+4 to vector+8, for spline emitter (list 0) or spline entity
//       (list 1) `index`. Arcs, CECParticleEmitter spline entities, the emotion icon.
//   sub_831C1908(handle)                          FLUSH (opcode 22): sub_831BA3D0 sets every live
//       particle's age to 999999.88 on the standard emitters, so all die next update.
//   sub_831C17C8(handle)                          SATURATION MASK (opcode 10, byte 1): the standard
//       emitters' diffuse texture takes ApplySaturationMask (sub_831B5B00; hit impacts).
//   sub_831C13D0(scene, callback)                 opcode 23 (0x831C140C), `cmd+12 = scene`: the
//       executor kills every instance whose inst+100 is that scene (sub_831BFF40); the level
//       teardown sub_82350E80 posts it for both render scenes.
// Each hook forwards to the guest first and states what it saw; the create reads the handle the
// guest wrote.
//
// DECALS. The sim's per-frame submit (sub_821777A8, from the world submit sub_821AF2F0) turns each
// pending decal into a render command; on the render thread sub_82C73BF8 (vtable 0x820E54FC
// slot 2) resolves the owner and pushes a 64-byte node onto the std::list dword_835A0AB8
// (sub_82CA7A98), which only the renderer's decal pass drains — a pass this host never runs, so
// every decal of a session would stay queued. The command is the statement and is taken whole
// here (0x82C73C04..0x82C73C4C): cmd+8 owner index or -1 → twin `dword_835A0080[idx]`, +12 texture
// name hash, +16 position, +32 direction, +48/+52 half sizes, +56 life, +60 flag byte (+64 a
// float Fable 2's command lacks, left out of the shell's row). Not queued: the host draws decals.
// sub_82CD46A8(twin) is a twin releasing its decals (its teardown, 0x82CD476C unlinks nodes whose
// +8 is the twin); sub_82CA79C8() clears the list.
//
// DISPLACEMENT QUADS. The drunk warp (sub_82187C58, run each frame for the game view's player)
// builds a MESSAGE object of class off_820C3F74 on its stack (0x82187EB8..0x82187EF4): its slot 1
// answers message id 35 (`li r3, 0x23` 0x82C80B20), its slot 2 (sub_82C80B28) makes the render
// object it addresses — type 29 (`*(obj+4) = 29` 0x82C80B7C), the displacement quad the scene's
// displacement pass draws, as Fable 2's — and its slot 3 (disc 0x82C4C7B0) serialises its 0x44 bytes
// into the render ring. sub_82256458(message, scene, ring) is the scene's ADD: it allots the id
// (0x822564A0..0x82256508), posts opcode 3 with [id, scene] (0x82256524..0x8225655C), then the
// message (0x82256574) and returns the id, which the warp keeps at owner+4 (0x82187F24); the
// render side's type-29 object takes message 35's fields in sub_82CC9BC0: msg+8..+20 = x0 y0 x1 y1
// (ints, 0x82CC9C0C..0x82CC9C74), +24 z, +28 rotation, +32 texture name hash (sub_82261B48),
// +36..+67 uv per corner — the warp's are (0,0)..(qword_835A78B0 = the screen's width, height),
// the FNV-1 of "Art\GUI\HUD\FXT_Fullscreen_Drunk.tex" and (0,0) (1,0) (0,1) (1,1).
// sub_82C708C8(id, ring) is the REMOVE: opcode 4 with the id (0x82C70908..0x82C70924), posted by
// the warp when the drunkenness stat falls back (0x82187DE4). The add is stated for that message
// class, the remove for a stated id; neither call is altered.
//
// FX MESH PARTICLES. An emitter's renderer kind (def+32, switched at sub_831AD638 0x831AD794) 4
// is the mesh renderer: its data (sub_831D1640, 44 bytes) keeps the material block's mesh hash
// at +4 (0x831D17A4) and displacement byte at +40 (0x831D17B0). Each frame the emitter's submit
// (sub_831BAA20) calls its particle renderer — made by the factory dword_835CC9C4 (slot 1,
// 0x823366A8: 32 bytes, vtable off_820A3910) — slot 5 sub_826243C8(renderer, kind, ...), whose
// kind-4 path (`cmpwi r29, 4` 0x8262443C) keeps one DESCRIPTOR per live particle in its sorted
// vector renderer+8: 176 bytes, vtable off_820A3934 (0x82624738), +4 the render object id (-1
// until added, 0x82624728), +20 flags 0x40000 | 0x800 for a displacement mesh (0x82624674,
// 0x82624740), +128 the mesh hash (`*(desc+128) = *(def+4)` 0x8262478C). A new particle's
// descriptor is added through sub_8226FD08(desc->slot1(desc) = desc+16 (`addi r3, r3, 0x10`
// 0x823B5228), scene renderer+28, ring) — with a ring, which the particle system's is
// (dword_8334F50C = 2, set by sub_831C0F90 at 0x831C0FCC), that is sub_82256458 (0x8226FD2C),
// the ADD above; the id goes to desc+4 (0x826246F4). Every frame it then messages the object
// (sub_821F4630): 1 position, 2 orientation, 15 tint, 14 alpha, 6 scale. A particle gone that
// frame (desc+160 left clear) is removed with sub_82C708C8(id, ring) (0x82624B08) and its
// descriptor destroyed (its dtor sub_826242B0 replaces the vtable, 0x826242DC); a renderer reset
// (sub_826250A0) removes every one (0x826250E0). The add is stated for the message whose owner
// carries off_820A3934, by the id it returns; any remove drops that id.


#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "map_table_hooks.h"
#include "mod_lua.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_LevelManager_LevelLoaded);
REX_EXTERN(__imp__rex_LevelManager_Unload);
REX_EXTERN(__imp__rex_ParticleSystem_Create);
REX_EXTERN(__imp__rex_ParticleSystem_KillWithFadeOut);
REX_EXTERN(__imp__rex_ParticleSystem_ReleaseHandle);
REX_EXTERN(__imp__rex_ParticleSystem_Start);
REX_EXTERN(__imp__rex_ParticleSystem_SetScene);
REX_EXTERN(__imp__rex_ParticleSystem_SetPosition);
REX_EXTERN(__imp__rex_ParticleSystem_SetPositionAndVelocity);
REX_EXTERN(__imp__rex_ParticleSystem_SetOrientation);
REX_EXTERN(__imp__rex_ParticleSystem_Kill);
REX_EXTERN(__imp__rex_ParticleSystem_KillScene);
REX_EXTERN(__imp__rex_ParticleSystem_SetColour);
REX_EXTERN(__imp__rex_ParticleSystem_SetAlpha);
REX_EXTERN(__imp__rex_ParticleSystem_SetScale);
REX_EXTERN(__imp__rex_ParticleSystem_SetSplineEnds);
REX_EXTERN(__imp__rex_ParticleSystem_SetSplinePoints);
REX_EXTERN(__imp__rex_ParticleSystem_Flush);
REX_EXTERN(__imp__rex_ParticleSystem_SaturationMask);
REX_EXTERN(__imp__rex_RenderTwin_ReleaseDecals);
REX_EXTERN(__imp__rex_Decal_ClearAll);
REX_EXTERN(__imp__rex_RenderScene_AddObject);
REX_EXTERN(__imp__rex_RenderScene_RemoveObject);

namespace {

const refii::guest::Addr kTwinTable{0x835A0080u};
/// The full-screen FXT message class (vtable off_820C3F74): slot 1 message 35, slot 2 a type 29.
const refii::guest::Addr kFullscreenFxtMessage{0x820C3F74u};
/// The FX mesh particle descriptor (vtable off_820A3934); the scene is handed its message at +16.
const refii::guest::Addr kFxMeshDescriptor{0x820A3934u};
constexpr uint32_t kFxMeshMessageOffset = 16u;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

uint8_t LoadGuestU8(const uint8_t* base, uint32_t address) { return base[address]; }

float LoadGuestF32(const uint8_t* base, uint32_t address) {
  const uint32_t bits = LoadGuestU32(base, address);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

bool PlausibleGuest(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

}  // namespace

REX_EXTERN(rex_LevelManager_LevelLoaded) {
  const uint32_t manager = ctx.r3.u32;
  __imp__rex_LevelManager_LevelLoaded(ctx, base);
  refii_bridge_level_stated(manager, 1);
  refii::fable3::modlua::NoteLevel(base, manager, true);
}

REX_EXTERN(rex_LevelManager_Unload) {
  const uint32_t manager = ctx.r3.u32;
  if (manager != 0u && LoadGuestU32(base, manager + 40u) != 0u) {
    refii_bridge_level_stated(manager, 0);
    refii::fable3::modlua::NoteLevel(base, manager, false);
  }
  __imp__rex_LevelManager_Unload(ctx, base);
}

REX_EXTERN(rex_ParticleSystem_Create) {
  const uint32_t handle_out = ctx.r3.u32;
  const uint32_t name_hash = ctx.r4.u32;
  __imp__rex_ParticleSystem_Create(ctx, base);
  if (PlausibleGuest(handle_out)) {
    const uint32_t handle = LoadGuestU32(base, handle_out);
    if (handle != 0u) {
      refii_bridge_fx_created(handle, name_hash);
    }
  }
}

REX_EXTERN(rex_ParticleSystem_Start) {
  const uint32_t handle = ctx.r3.u32;
  __imp__rex_ParticleSystem_Start(ctx, base);
  refii_bridge_fx_started(handle);
}

REX_EXTERN(rex_ParticleSystem_SetScene) {
  const uint32_t handle = ctx.r3.u32;
  const uint32_t scene = ctx.r4.u32;
  __imp__rex_ParticleSystem_SetScene(ctx, base);
  refii_bridge_fx_scene(handle, scene);
}

REX_EXTERN(rex_ParticleSystem_SetPosition) {
  const uint32_t handle = ctx.r3.u32;
  float lanes[4];
  std::memcpy(lanes, ctx.v1.f32, sizeof(lanes));
  __imp__rex_ParticleSystem_SetPosition(ctx, base);
  refii_bridge_fx_position(handle, lanes);
}

REX_EXTERN(rex_ParticleSystem_SetPositionAndVelocity) {
  const uint32_t handle = ctx.r3.u32;
  float lanes[4];
  std::memcpy(lanes, ctx.v1.f32, sizeof(lanes));
  __imp__rex_ParticleSystem_SetPositionAndVelocity(ctx, base);
  refii_bridge_fx_position(handle, lanes);
}

REX_EXTERN(rex_ParticleSystem_SetOrientation) {
  const uint32_t handle = ctx.r3.u32;
  float lanes[4];
  std::memcpy(lanes, ctx.v1.f32, sizeof(lanes));
  __imp__rex_ParticleSystem_SetOrientation(ctx, base);
  refii_bridge_fx_orientation(handle, lanes);
}

REX_EXTERN(rex_ParticleSystem_Kill) {
  const uint32_t handle = ctx.r3.u32;
  __imp__rex_ParticleSystem_Kill(ctx, base);
  refii_bridge_fx_killed(handle);
}

REX_EXTERN(rex_ParticleSystem_KillWithFadeOut) {
  const uint32_t handle = ctx.r3.u32;
  const float seconds = static_cast<float>(ctx.f1.f64);
  __imp__rex_ParticleSystem_KillWithFadeOut(ctx, base);
  refii_bridge_fx_fade(handle, seconds);
}

REX_EXTERN(rex_ParticleSystem_ReleaseHandle) {
  const uint32_t command = ctx.r3.u32;
  const uint32_t handle = PlausibleGuest(command) ? LoadGuestU32(base, command + 8u) : 0u;
  __imp__rex_ParticleSystem_ReleaseHandle(ctx, base);
  if (handle != 0u) {
    refii_bridge_fx_killed(handle);
  }
}

REX_EXTERN(rex_ParticleSystem_SetColour) {
  const uint32_t handle = ctx.r3.u32;
  const uint32_t colour = ctx.r4.u32;
  __imp__rex_ParticleSystem_SetColour(ctx, base);
  if (PlausibleGuest(colour)) {
    // Blue, green, red, alpha bytes; the executor keeps the instance's alpha.
    constexpr float kByte = 1.0f / 255.0f;
    refii_bridge_fx_colour(handle, static_cast<float>(LoadGuestU8(base, colour + 2u)) * kByte,
                           static_cast<float>(LoadGuestU8(base, colour + 1u)) * kByte,
                           static_cast<float>(LoadGuestU8(base, colour)) * kByte);
  }
}

REX_EXTERN(rex_ParticleSystem_SetAlpha) {
  const uint32_t handle = ctx.r3.u32;
  const float alpha = static_cast<float>(ctx.f1.f64);
  __imp__rex_ParticleSystem_SetAlpha(ctx, base);
  refii_bridge_fx_alpha(handle, alpha);
}

REX_EXTERN(rex_ParticleSystem_SetScale) {
  const uint32_t handle = ctx.r3.u32;
  const float scale = static_cast<float>(ctx.f1.f64);
  __imp__rex_ParticleSystem_SetScale(ctx, base);
  refii_bridge_fx_scale(handle, scale);
}

REX_EXTERN(rex_ParticleSystem_SetSplineEnds) {
  const uint32_t handle = ctx.r3.u32;
  // The recompiler's lanes run w z y x: the point's x y z are lanes 3 2 1.
  const float points[6] = {ctx.v1.f32[3], ctx.v1.f32[2], ctx.v1.f32[1],
                           ctx.v2.f32[3], ctx.v2.f32[2], ctx.v2.f32[1]};
  __imp__rex_ParticleSystem_SetSplineEnds(ctx, base);
  refii_bridge_fx_spline(handle, points, 2u);
}

REX_EXTERN(rex_ParticleSystem_SetSplinePoints) {
  const uint32_t handle = ctx.r3.u32;
  const uint32_t index = ctx.r5.u32;
  const uint32_t list = ctx.r6.u32;
  float points[REFII_STATED_FX_SPLINE_POINTS * 3u];
  uint32_t count = 0u;
  // The effect's first spline child, of either list (spline emitters 0, spline entities 1).
  if (index == 0u && PlausibleGuest(list)) {
    const uint32_t begin = LoadGuestU32(base, list + 4u);
    const uint32_t end = LoadGuestU32(base, list + 8u);
    if (PlausibleGuest(begin) && end > begin && ((end - begin) & 15u) == 0u) {
      const uint32_t stated = (end - begin) >> 4;
      count = stated < REFII_STATED_FX_SPLINE_POINTS ? stated : REFII_STATED_FX_SPLINE_POINTS;
      for (uint32_t i = 0u; i < count * 3u; ++i) {
        points[i] = LoadGuestF32(base, begin + (i / 3u) * 16u + (i % 3u) * 4u);
      }
    }
  }
  __imp__rex_ParticleSystem_SetSplinePoints(ctx, base);
  if (count >= 2u) {
    refii_bridge_fx_spline(handle, points, count);
  }
}

REX_EXTERN(rex_ParticleSystem_Flush) {
  const uint32_t handle = ctx.r3.u32;
  __imp__rex_ParticleSystem_Flush(ctx, base);
  refii_bridge_fx_flush(handle);
}

REX_EXTERN(rex_ParticleSystem_SaturationMask) {
  const uint32_t handle = ctx.r3.u32;
  __imp__rex_ParticleSystem_SaturationMask(ctx, base);
  refii_bridge_fx_saturation_mask(handle);
}

REX_EXTERN(rex_ParticleSystem_KillScene) {
  const uint32_t scene = ctx.r3.u32;
  __imp__rex_ParticleSystem_KillScene(ctx, base);
  if (scene != 0xFFFFFFFFu) {
    refii_bridge_fx_scene_killed(scene);
  }
}

REX_EXTERN(rex_Decal_ExecuteRenderCommand) {
  const uint32_t command = ctx.r3.u32;
  if (PlausibleGuest(command)) {
    uint32_t words[REFII_WORLD_DECAL_WORDS] = {};
    const uint32_t index = LoadGuestU32(base, command + 8u);
    uint32_t owner = 0u;
    if (index != 0xFFFFFFFFu) {
      const uint32_t twins = LoadGuestU32(base, kTwinTable);
      owner = PlausibleGuest(twins) ? LoadGuestU32(base, twins + index * 4u) : 0u;
    }
    words[1] = owner;
    words[2] = LoadGuestU32(base, command + 12u);
    for (uint32_t i = 0u; i < 4u; ++i) {
      words[3 + i] = LoadGuestU32(base, command + 16u + i * 4u);
      words[7 + i] = LoadGuestU32(base, command + 32u + i * 4u);
    }
    words[11] = LoadGuestU32(base, command + 48u);
    words[12] = LoadGuestU32(base, command + 52u);
    words[13] = LoadGuestU32(base, command + 56u);
    words[14] = base[command + 60u];
    refii_bridge_world_decal_stated(words);
  }
  // The executor's own return (`li r3, 0` at 0x82C73D2C); its push is not made.
  ctx.r3.u64 = 0u;
}

REX_EXTERN(rex_RenderScene_AddObject) {
  const uint32_t message = ctx.r3.u32;
  const uint32_t scene = ctx.r4.u32;
  const bool quad = PlausibleGuest(message) && LoadGuestU32(base, message) == kFullscreenFxtMessage;
  const uint32_t descriptor = message - kFxMeshMessageOffset;
  const bool fx_mesh = PlausibleGuest(message) && PlausibleGuest(descriptor) &&
                       LoadGuestU32(base, descriptor) == kFxMeshDescriptor;
  __imp__rex_RenderScene_AddObject(ctx, base);
  const uint32_t id = ctx.r3.u32;
  if (fx_mesh && id != 0xFFFFFFFFu) {
    refii_bridge_world_fx_mesh_stated(id, descriptor);
  }
  if (quad && id != 0xFFFFFFFFu) {
    uint32_t words[REFII_WORLD_QUAD_WORDS];
    for (uint32_t i = 0u; i < REFII_WORLD_QUAD_WORDS; ++i) {
      // msg+8..+20 the corners, +24 z, +28 rotation, (+32 the texture), +36..+67 the uv.
      const uint32_t at = i < 6u ? 8u + i * 4u : 36u + (i - 6u) * 4u;
      words[i] = LoadGuestU32(base, message + at);
    }
    refii_bridge_world_quad_stated(id, scene, words);
  }
}

REX_EXTERN(rex_RenderScene_RemoveObject) {
  const uint32_t id = ctx.r3.u32;
  const int32_t ring = static_cast<int32_t>(ctx.r4.u32);
  if (ring > 0 && id != 0xFFFFFFFFu) {
    refii_bridge_world_quad_removed(id);
  }
  refii_bridge_world_fx_mesh_removed(id);
  refii_bridge_classic_particle_removed(id);
  // A map table's object (map_table_hooks.cpp): its holder's remove sub_82984740 posts it here.
  refii::fable3::MapTableObjectRemoved(id);
  __imp__rex_RenderScene_RemoveObject(ctx, base);
}

REX_EXTERN(rex_RenderTwin_ReleaseDecals) {
  const uint32_t twin = ctx.r3.u32;
  refii_bridge_world_decals_dropped(twin);
  __imp__rex_RenderTwin_ReleaseDecals(ctx, base);
}

REX_EXTERN(rex_Decal_ClearAll) {
  refii_bridge_world_decals_cleared();
  __imp__rex_Decal_ClearAll(ctx, base);
}
