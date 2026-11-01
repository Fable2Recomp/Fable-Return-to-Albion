// refii - Fable 3's world, stated to the host at the guest's own entry points: a level's up and
// down, each effect's creation, placement and end, each decal the renderer is handed, the
// displacement quad the drunk warp adds to the render scene, and the render object an effect
// adds for each mesh particle.
//
// Every address is the Fable 3 IDB; each offset names its base.
//
// LEVELS. The level manager (vtable-less, ctor sub_82348128; the shell's level feed reaches it as
// *(*(*(*(game+0x1C)+4)+0x10)+4)) loads with sub_82348AD0(mgr, name, a3, a4): it unloads the
// standing level first (`bl sub_82349120` 0x82348B48, flag 1), stores the new level object at
// mgr+40 (sub_8250B878 at 0x82348BD8), runs the load (sub_826ED940 at 0x82348C8C) and, on its
// success byte, `bl sub_8234B7E0(mgr)` at 0x82348CBC — the level's post-load pass, after every
// entity of the .save stands. sub_82349120(mgr, flag) is the unload: it returns at once when
// mgr+40 is null (`lwz r11,0x28(r3)` 0x8234912C) and otherwise stops the level's effects and
// marks the teardown (`stb 1, 0xFD(r31)` 0x82349274). So: up at sub_8234B7E0's entry, down at
// sub_82349120's, keyed by the manager, since the front end's GUI world has a manager of its own.
//
// EFFECTS. Game code never touches a particle instance: it posts commands keyed by a HANDLE
// through the wrappers at 0x8318F288..0x8318FFB8, each `sub_83199860` allocating a command,
// `cmd+4` the opcode, `cmd+8` the handle, and `sub_8318A640` queueing it for the particle thread
// (sub_8318BA38 drains into the executor sub_8318B2E8). The wrappers hooked here:
//   sub_8318FDB8(int* handle_out, u32 name_hash)  CREATE: sub_8318A770 writes the new handle to
//       *handle_out (`stw` at 0x8318A820), then `cmd+12 = name_hash` (0x8318FDFC) — the FNV-1 of
//       the effect's backslashed path (sub_8312F3D8 → sub_822D78B0), the particle bank's key.
//   sub_8318FD60(handle)                          START (opcode 1, 0x8318FD90): the instance joins
//       the live registry dword_8356643C.
//   sub_8318FCF8(handle, scene)                   render scene (opcode 2, 0x8318FD34), `cmd+12`.
//   sub_8318FB30(handle; v1 position)             opcode 6: `stvx128 v127, r3, r10(16)` at
//       0x8318FB78 after `vmr128 v127, v1` — the root position every emitter takes (sub_83183750
//       stores it at emitter+0x50).
//   sub_8318FAA0(handle; v1 position, v2 velocity) opcode 7, the same position at cmd+16 (0x8318FAF8).
//   sub_8318F9D0(handle; v1 quaternion)           opcode 8 at cmd+16 (0x8318FA18): sub_83183700 →
//       sub_83184C98 per emitter.
//   sub_8318F8B0(handle)                          KILL (opcode 11, 0x8318F8E0): sub_83183610 sets
//       every emitter's +0xC8.
//   sub_8318F510(scene, callback)                 opcode 23 (0x8318F54C), `cmd+12 = scene`: the
//       executor kills every instance whose inst+100 is that scene (sub_8318E080); the level
//       teardown sub_82349480 posts it for both render scenes.
// Each hook forwards to the guest first and states what it saw; the create reads the handle the
// guest wrote.
//
// DECALS. The sim's per-frame submit (sub_82177868, from the world submit sub_821AF1B8) turns each
// pending decal into a render command; on the render thread sub_82C3F2A8 (vtable 0x820E2B44
// slot 2) resolves the owner and pushes a 64-byte node onto the std::list dword_8355FE98
// (sub_82C73580), which only the renderer's decal pass drains — a pass this host never runs, so
// every decal of a session would stay queued. The command is the statement and is taken whole
// here (0x82C3F2B4..0x82C3F2FC): cmd+8 owner index or -1 → twin `dword_8355F464[idx]`, +12 texture
// name hash, +16 position, +32 direction, +48/+52 half sizes, +56 life, +60 flag byte (+64 a
// float Fable 2's command lacks, left out of the shell's row). Not queued: the host draws decals.
// sub_82CA0CA0(twin) is a twin releasing its decals (its teardown, 0x82CA0D64 unlinks nodes whose
// +8 is the twin); sub_82C734B0() clears the list.
//
// DISPLACEMENT QUADS. The drunk warp (sub_82187D40, run each frame for the game view's player)
// builds a MESSAGE object of class off_820D19E8 on its stack (0x82187FA0..0x82187FDC): its slot 1
// answers message id 35 (`li r3, 0x23` 0x82C4C6D8), its slot 2 (sub_82C4C6E0) makes the render
// object it addresses — type 29 (`*(obj+4) = 29` 0x82C4C734), the displacement quad the scene's
// displacement pass draws, as Fable 2's — and its slot 3 (0x82C4C7B0) serialises its 0x44 bytes
// into the render ring. sub_82257228(message, scene, ring) is the scene's ADD: it allots the id
// (0x82257270..0x822572D8), posts opcode 3 with [id, scene] (0x822572F4..0x8225732C), then the
// message (0x82257344) and returns the id, which the warp keeps at owner+4 (0x8218800C); the
// render side's type-29 object takes message 35's fields in sub_82C96988: msg+8..+20 = x0 y0 x1 y1
// (ints, 0x822969D4..0x82296A3C), +24 z, +28 rotation, +32 texture name hash (sub_82263038),
// +36..+67 uv per corner — the warp's are (0,0)..(qword_83566C84 = the screen's width, height),
// the FNV-1 of "Art\GUI\HUD\FXT_Fullscreen_Drunk.tex" and (0,0) (1,0) (0,1) (1,1).
// sub_82C3C148(id, ring) is the REMOVE: opcode 4 with the id (0x82C3C188..0x82C3C1A4), posted by
// the warp when the drunkenness stat falls back (0x82187ECC). The add is stated for that message
// class, the remove for a stated id; neither call is altered.
//
// FX MESH PARTICLES. An emitter's renderer kind (def+32, switched at sub_8317B778 0x8317B8D4) 4
// is the mesh renderer: its data (sub_8319F780, 44 bytes) keeps the material block's mesh hash
// at +4 (0x8319F8E4) and displacement byte at +40 (0x8319F8F0). Each frame the emitter's submit
// (sub_83188B60) calls its particle renderer — made by the factory dword_8358BDA4 (slot 1,
// 0x8232F120: 32 bytes, vtable off_820A00E4) — slot 5 sub_825FC5C0(renderer, kind, ...), whose
// kind-4 path (`cmpwi r29, 4` 0x825FC634) keeps one DESCRIPTOR per live particle in its sorted
// vector renderer+8: 176 bytes, vtable off_820A0108 (0x825FC930), +4 the render object id (-1
// until added, 0x825FC920), +20 flags 0x40000 | 0x800 for a displacement mesh (0x825FC86C,
// 0x825FC938), +128 the mesh hash (`*(desc+128) = *(def+4)` 0x825FC984). A new particle's
// descriptor is added through sub_82271630(desc->slot1(desc) = desc+16 (`addi r3, r3, 0x10`
// 0x82326968), scene renderer+28, ring) — with a ring, which the particle system's is
// (dword_8330EA18 = 2, set by sub_8318F0D0 at 0x8318F10C), that is sub_82257228 (0x82271654),
// the ADD above; the id goes to desc+4 (0x825FC8EC). Every frame it then messages the object
// (sub_821F5110): 1 position, 2 orientation, 15 tint, 14 alpha, 6 scale. A particle gone that
// frame (desc+160 left clear) is removed with sub_82C3C148(id, ring) (0x825FCD00) and its
// descriptor destroyed (its dtor sub_825FC420 replaces the vtable, 0x825FC44C); a renderer reset
// (sub_825FD298) removes every one (0x825FD2D8). The add is stated for the message whose owner
// carries off_820A0108, by the id it returns; any remove drops that id.


#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_LevelManager_LevelLoaded);
REX_EXTERN(__imp__rex_LevelManager_Unload);
REX_EXTERN(__imp__rex_ParticleSystem_Create);
REX_EXTERN(__imp__rex_ParticleSystem_Start);
REX_EXTERN(__imp__rex_ParticleSystem_SetScene);
REX_EXTERN(__imp__rex_ParticleSystem_SetPosition);
REX_EXTERN(__imp__rex_ParticleSystem_SetPositionAndVelocity);
REX_EXTERN(__imp__rex_ParticleSystem_SetOrientation);
REX_EXTERN(__imp__rex_ParticleSystem_Kill);
REX_EXTERN(__imp__rex_ParticleSystem_KillScene);
REX_EXTERN(__imp__rex_RenderTwin_ReleaseDecals);
REX_EXTERN(__imp__rex_Decal_ClearAll);
REX_EXTERN(__imp__rex_RenderScene_AddObject);
REX_EXTERN(__imp__rex_RenderScene_RemoveObject);

namespace {

constexpr uint32_t kTwinTable = 0x8355F464u;
/// The full-screen FXT message class (vtable off_820D19E8): slot 1 message 35, slot 2 a type 29.
constexpr uint32_t kFullscreenFxtMessage = 0x820D19E8u;
/// The FX mesh particle descriptor (vtable off_820A0108); the scene is handed its message at +16.
constexpr uint32_t kFxMeshDescriptor = 0x820A0108u;
constexpr uint32_t kFxMeshMessageOffset = 16u;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

bool PlausibleGuest(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

}  // namespace

REX_EXTERN(rex_LevelManager_LevelLoaded) {
  const uint32_t manager = ctx.r3.u32;
  __imp__rex_LevelManager_LevelLoaded(ctx, base);
  refii_bridge_level_stated(manager, 1);
}

REX_EXTERN(rex_LevelManager_Unload) {
  const uint32_t manager = ctx.r3.u32;
  if (manager != 0u && LoadGuestU32(base, manager + 40u) != 0u) {
    refii_bridge_level_stated(manager, 0);
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
  // The executor's own return (`li r3, 0` at 0x82C3F3DC); its push is not made.
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
