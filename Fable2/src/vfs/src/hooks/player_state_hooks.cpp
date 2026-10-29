// refii — PLAYER STATE'S SAVES: a hero's saved mod values go into the save the game writes that
// hero into, and come back when the game loads the hero from one (core/player_state.h).
//
// The game's own statements, read in the binary (TU1 / disc) rather than inferred:
//
//   Every save package is written by one of two writers, called from the write driver
//   (sub_82445398 / sub_824E8990) and from player two's (sub_82445680 / sub_824E8C78):
//     sub_824457E8 / sub_824E8DE0  the full write (main save, hero save, chapter, morphs)
//     sub_82445A28 / sub_824E9020  the hero save alone
//   Both take r3 the save manager, r4 the package's name (an LHString*: *r4 -> {char*, length}),
//   r5 the PLAYER NUMBER whose hero they serialise (player+0x24: `lwz r5,0x24(r27)` at 0x82445514
//   and 0x82445530, and 0x82445700 for player two), r6 the user index; both return their result
//   in r3 AFTER the package is closed (`bl sub_82449BD8` then `mr r3,r31`, 0x82445AA0). So on their
//   return, with r3 set, the package named by r4 holds player r5's hero — the moment to write.
//   Neither opens a package CREATE_ALWAYS (modes 4 and 3, 0x824458AC / 0x82445A58), so the file
//   written beside the game's survives every later save.
//
//   Loads:
//     sub_82446B10 / sub_824EA150  SaveGame_LoadFromDevice(save manager, name, fail-quest): the
//       main save, player one's. It assigns the name to SaveMgr+0 first (0x82446B34) and returns 1
//       when the save is loaded.
//     sub_824482E0 / sub_824EB910  the co-op hero load (save manager, user index, name): a save's
//       HeroSave for PLAYER TWO — a local player two's pick, or a joiner's own hero, picked on the
//       co-op screen before joining. It assigns the name to SaveMgr+4 once the stream is read
//       (0x82448408), and SaveMgr+4 is where player two's hero is then saved (sub_82445680).

#include <cstdint>
#include <string>

#include <rex/logging.h>

#include "core/mod_values.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "core/refii_mod_events.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

PPC_EXTERN_FUNC(__imp__rex_lhSaveManager_WritePackage);
PPC_EXTERN_FUNC(__imp__rex_lhSaveManager_WriteHeroPackage);
PPC_EXTERN_FUNC(__imp__rex_lhSaveManager_LoadFromDevice);
PPC_EXTERN_FUNC(__imp__rex_lhSaveManager_LoadCoopHero);

namespace {

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The text of the LHString at `at` ({char*, length} behind one pointer), or empty.
std::string LhText(uint8_t* base, uint32_t at) {
  const uint32_t rep = Plausible(at) ? refii::mods::LoadU32(base, at) : 0u;
  if (!Plausible(rep)) {
    return {};
  }
  const uint32_t text = refii::mods::LoadU32(base, rep);
  const uint32_t length = refii::mods::LoadU32(base, rep + 4u);
  if (!Plausible(text) || length == 0u || length > 64u) {
    return {};
  }
  return std::string(reinterpret_cast<const char*>(base + text), length);
}

/// After a package write: player `player`'s saved values go beside it.
void Written(uint8_t* base, uint32_t name_at, uint32_t player, bool ok) {
  const std::string name = LhText(base, name_at);
  if (!ok || name.empty() || (player != 1u && player != 2u)) {
    return;
  }
  refii_bridge_player_write_saved(static_cast<int32_t>(player), name.c_str());
  // The host stamps the package's sidecar (refii_save.json) with the mod set it was written
  // under: REFII_MOD_HOST_SAVE_KIND crosses the event drain whatever the mask says.
  std::string fields = "player=" + std::to_string(player) + ",name='";
  for (const char c : name) {
    if (c != '\'' && c != '\\' && c != '\n' && c != ',') {
      fields += c;
    }
  }
  fields += "'";
  refii::mods::PostEvent(REFII_MOD_HOST_SAVE_KIND, fields);
}

}  // namespace

// The full write: r4 the package's name, r5 the player number. TU1 0x824457E8.
REX_EXTERN(rex_lhSaveManager_WritePackage) {
  REX_FUNC_PROLOGUE();
  const uint32_t name_at = ctx.r4.u32;
  const uint32_t player = ctx.r5.u32;
  if (player == 1u) {
    // The world is in this package: player two's household as it stands goes with it.
    PPCContext stash = ctx;
    refii::vfs::StashHouseholdForSave(stash, base);
  }
  __imp__rex_lhSaveManager_WritePackage(ctx, base);
  Written(base, name_at, player, (ctx.r3.u32 & 0xFFu) != 0u);
}

// The hero save alone: the same arguments. TU1 0x82445A28.
REX_EXTERN(rex_lhSaveManager_WriteHeroPackage) {
  REX_FUNC_PROLOGUE();
  const uint32_t name_at = ctx.r4.u32;
  const uint32_t player = ctx.r5.u32;
  __imp__rex_lhSaveManager_WriteHeroPackage(ctx, base);
  Written(base, name_at, player, (ctx.r3.u32 & 0xFFu) != 0u);
}

// Player one's save loaded: the name is in SaveMgr+0 (r3+0). TU1 0x82446B10.
REX_EXTERN(rex_lhSaveManager_LoadFromDevice) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  __imp__rex_lhSaveManager_LoadFromDevice(ctx, base);
  const std::string name = (ctx.r3.u32 & 0xFFu) != 0u ? LhText(base, manager) : std::string();
  if (!name.empty()) {
    refii_bridge_player_load_saved(1, name.c_str());
  }
}

// Player two's hero loaded from a save: the name it was asked for (r5) is SaveMgr+4 once the
// stream was read; a failed open leaves SaveMgr+4 as it was. TU1 0x824482E0.
REX_EXTERN(rex_lhSaveManager_LoadCoopHero) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  const std::string asked = LhText(base, ctx.r5.u32);
  __imp__rex_lhSaveManager_LoadCoopHero(ctx, base);
  if (!asked.empty() && LhText(base, manager + 4u) == asked) {
    refii_bridge_player_load_saved(2, asked.c_str());
  }
}
