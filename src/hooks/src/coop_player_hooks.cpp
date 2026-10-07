// Co-op: the Player unpublish, and a trace of the join handshake.
//
// PLAYER UNPUBLISH. A `Player` (0x48 bytes, ctor `sub_82389938`) is owned solely by the
// `PlayerManager` at `game+156`, but `sub_82371860` publishes the same pointer RAW into the
// App's two input handlers (`*(App+20)+12`, `*(App+24)+12`, at 0x823718A0 / 0x823718D0) and
// into 48-byte input records under `*(App+32)+56`. The guest's own unpublish `sub_82371948` is
// reached from ONE site, 0x82316A78, which needs both players present — the leave-co-op path.
// Running it from the destructor covers every other way a Player dies. It is idempotent: the
// clear and the `--App+101` are both guarded on a handler actually having pointed at this
// Player (0x8237199C).
//
// JOIN HANDSHAKE TRACE. The joining client is meant to leave the front end by itself:
// `sub_82319BE8`, the app-state Run loop, does
//     82319dc0 lbz r10,0x34(r11)  ; CM+52 in a session
//     82319dd0 lbz r10,0x36(r11)  ; CM+54 we are the JOINER
//     82319dec beq loc_82319E1C   ; not both -> stay in the front end
//     82319e00 stb r25,0x5C(r10)  ; *( *(0x83496AC0)+12 + 92 ) = 1
// and `+92` both suppresses the front-end update (0x8231A02C) and breaks the loop (0x82319D00).
// Those bytes are set by two received things: `sub_822EEA50` (join response approved) sets
// CXboxLive+1328 and CM+218 at 0x822EF1D8/DC, and inbound packet 0x118 -> `sub_82334E60` sets
// CM+52/CM+54 at 0x82334ED8/DC. The joiner's co-op SCREEN is separately opened by receiving
// message 156, handled at 0x823317C4 (verified: `li r4,0x18` screen id 24 at 0x823317F8,
// `lbz r8,0x10D(r31)` the up-latch at 0x8233180C).
//
// Observed: P2 stays in the front-end multiplayer menu through the join and must back out of
// the pause menu to regain control. So at least one of those bytes is never set. These hooks
// say which, rather than guessing.

#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_state.h"
#include "core/guest_build.h"

extern "C" REX_FUNC(__imp__rex_lhPlayer_ScalarDeletingDtor);
extern "C" REX_FUNC(__imp__rex_lhGui_RegisterPlayer);
extern "C" REX_FUNC(__imp__rex_lhNetManager_ProcessInboundSessionPacket);
extern "C" REX_FUNC(__imp__rex_lhNetManager_ProcessInboundGamePacket);
extern "C" REX_FUNC(__imp__rex_lhNetManager_AcceptJoinAsClient);
extern "C" REX_FUNC(__imp__rex_lhXboxLive_ProcessJoinResponse);
extern "C" REX_FUNC(__imp__rex_lhNetManager_DisplayCoopScreen);
extern "C" REX_FUNC(__imp__rex_lhCoopScreen_SpawnPreviewMannequin);
extern "C" REX_FUNC(__imp__rex_lhNetManager_GetHenchmanCreatureName);
extern "C" REX_FUNC(__imp__rex_lhCoopScreen_GetMannequinDefName);
extern "C" REX_FUNC(__imp__rex_lhCharString_CtorFromCStr);

/// The bit itself lives with the pump, in refii_vfs — see `guest_tick_pump.cpp`. It is set from
/// the host through the front-end action `CoopHeroBody:`, and the hooks already link that way
/// round (`core/guest_state.h` below). 0 henchman, 1 their own hero.
extern "C" int RefiiCoopGetOwnHero(void);

extern "C" REX_FUNC(__imp__rex_lhEntityManager_ChangeEntityType);
extern "C" REX_FUNC(__imp__rex_lhNetManager_SetupHenchman);
extern "C" void RefiiCoopRedress(void* ctx, uint8_t* base);
extern "C" const char* RefiiCoopMeleeName(void);
extern "C" const char* RefiiCoopRangedName(void);
extern "C" REX_FUNC(__imp__rex_lhNetManager_GetHenchmanMeleeWeaponName);
extern "C" REX_FUNC(__imp__rex_lhNetManager_GetHenchmanRangedWeaponName);
extern "C" REX_FUNC(__imp__rex_lhCoop_ArmHenchmanWeapons);
extern "C" REX_FUNC(__imp__rex_CECHeldItems_OnNetMessage);
extern "C" REX_FUNC(__imp__rex_lhEntity_Destroy);
extern "C" REX_FUNC(__imp__rex_CECNetwork_SendTransform);
extern "C" REX_FUNC(__imp__rex_lhNetManager_FindEntityByNetId);
extern "C" REX_FUNC(__imp__rex_CECNetwork_ApplyReplicaTarget);
extern "C" REX_FUNC(__imp__rex_CECNetwork_PushReceivedTransform);

// 0x82371948, which refii_config.toml misnames `rex_lhScene_UpdateHeightfield` — the body
// unpublishes a Player. That config line is the name's only reference in the tree.
extern "C" REX_FUNC(__imp__rex_lhScene_UpdateHeightfield);

namespace {
constexpr refii::guest::Addr kAppPointer{0x83496B08u};
constexpr refii::guest::Addr kGameRoot{0x83496AB8u};

constexpr uint32_t kFirstHandler = 20u;
constexpr uint32_t kSecondHandler = 24u;

constexpr bool Plausible(uint32_t address) {
  return address >= 0x10000u && address < 0xC0000000u;

}

/// The co-op manager — `g_XrnmNetContext` (0x83497490) itself. `game+140` is only a counted
/// COPY of it, and a title-joined client had that copy EMPTY in game; the global is the truth.
uint32_t CoopManager(uint8_t* base) {
  using refii::vfs::LoadGuestU32;
  const uint32_t manager = LoadGuestU32(base, refii::guest::Address(0x83497490u));
  if (Plausible(manager)) return manager;
  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  return Plausible(game) ? LoadGuestU32(base, game + 140u) : 0u;
}

/// The five bytes the front-end teardown is gated on, in one line.
void ReportJoinState(uint8_t* base, const char* where) {
  using refii::vfs::LoadGuestU8;

  const uint32_t cm = CoopManager(base);
  if (!Plausible(cm)) {
    REXLOG_INFO("coop JOIN [{}]: no co-op manager yet", where);
    return;
  }

  REXLOG_INFO(
      "coop JOIN [{}]: CM+52(session)={} CM+53(host)={} CM+54(joiner)={} CM+218={} "
      "CM+217={} CM+223={}",
      where, LoadGuestU8(base, cm + 52u), LoadGuestU8(base, cm + 53u),
      LoadGuestU8(base, cm + 54u), LoadGuestU8(base, cm + 218u), LoadGuestU8(base, cm + 217u),
      LoadGuestU8(base, cm + 223u));
}
}  // namespace


// THE CO-OP/GAME CHANNEL — the OTHER receive path, and the one we were blind to.
//
// `sub_8232B518` below carries session packets (ids 0x113+, 17 entries at jumptable
// 0x8232B588). Everything else — ids 1..273 through the jumptable at 0x8232E410, indexed
// `id - 1` — arrives HERE, and that is where the co-op screen (156), every screen fade
// (205/206/207/267), the screen-set messages (227/230/236/256/257) and the level-change
// barrier's siblings live. A trace that only hooked the session path could never see them.
//
// WHY THIS MATTERS: a sweep of all 351 typed-send call sites found 172 gated on the HOST pair
// (`coopMgr+52 && +53`) and EVERY id they send has a live receive handler — ids 263/264 are the
// only unhandled ones in 1..273 and neither is ever sent. So a client-side omission is never
// "the guest forgot to handle it"; it is "the message did not arrive". This says which did.
//
// One line per DISTINCT id, not per arrival: the question is which ids ever come through, and
// the simulation mirrors (entity, AI, economy) would otherwise bury the answer.
REX_EXTERN(rex_lhNetManager_ProcessInboundGamePacket) {
  using refii::vfs::LoadGuestU8;

  const uint32_t message = ctx.r4.u32;
  if (Plausible(message)) {
    const uint32_t id = (static_cast<uint32_t>(LoadGuestU8(base, message)) << 8) |
                        LoadGuestU8(base, message + 1u);

    static std::atomic<bool> seen[512]{};
    if (id < 512u && !seen[id].exchange(true, std::memory_order_relaxed)) {
      REXLOG_INFO("coop CHANNEL: first inbound game message id={} (0x{:X})", id, id);
    }
  }

  __imp__rex_lhNetManager_ProcessInboundGamePacket(ctx, base);
}

// ProcessInboundSessionPacket — a3 (r5) is the packet; its 16-bit id is the switch key.
REX_EXTERN(rex_lhNetManager_ProcessInboundSessionPacket) {
  using refii::vfs::LoadGuestU8;

  const uint32_t packet = ctx.r5.u32;
  if (Plausible(packet)) {
    const uint32_t id =
        (static_cast<uint32_t>(LoadGuestU8(base, packet)) << 8) | LoadGuestU8(base, packet + 1u);
    REXLOG_INFO("coop JOIN: inbound session packet id=0x{:X}", id);
  }

  __imp__rex_lhNetManager_ProcessInboundSessionPacket(ctx, base);
}

// Packet 0x118's handler — sets CM+52 = 1, CM+54 = 1 (0x82334ED8 / 0x82334EDC).
REX_EXTERN(rex_lhNetManager_AcceptJoinAsClient) {
  __imp__rex_lhNetManager_AcceptJoinAsClient(ctx, base);
  ReportJoinState(base, "packet 0x118 handled");
}

// XboxLive_ProcessJoinResponse — on APPROVED sets CXboxLive+1328 and CM+218 (0x822EF1D8/DC).
REX_EXTERN(rex_lhXboxLive_ProcessJoinResponse) {
  REXLOG_INFO("coop JOIN: ProcessJoinResponse entered");
  __imp__rex_lhXboxLive_ProcessJoinResponse(ctx, base);
  ReportJoinState(base, "join response processed");
}

// Multiplayer.DisplayCoopScreen — the only thing that opens screen 24.
REX_EXTERN(rex_lhNetManager_DisplayCoopScreen) {
  ReportJoinState(base, "DisplayCoopScreen entered");
  __imp__rex_lhNetManager_DisplayCoopScreen(ctx, base);
}

// The henchman mannequin spawn. `screen+276` is the raw entity (0x824388CC) and `screen+284`
// the preview ticket (0x824389B4); both zero means the preview was never built.
REX_EXTERN(rex_lhCoopScreen_SpawnPreviewMannequin) {
  using refii::vfs::LoadGuestU32;

  const uint32_t screen = ctx.r3.u32;
  __imp__rex_lhCoopScreen_SpawnPreviewMannequin(ctx, base);

  if (Plausible(screen)) {
    REXLOG_INFO("coop MANNEQUIN: sub_82438818 ran — screen+276(entity)=0x{:08X} ticket=0x{:08X}",
                LoadGuestU32(base, screen + 276u), LoadGuestU32(base, screen + 284u));
  }
}

// sub_82371860 — publish a Player into the App's handlers.
REX_EXTERN(rex_lhGui_RegisterPlayer) {
  using refii::vfs::LoadGuestU32;

  const uint32_t player = ctx.r4.u32;
  const uint32_t player_id = Plausible(player) ? LoadGuestU32(base, player + 36u) : 0u;
  __imp__rex_lhGui_RegisterPlayer(ctx, base);

  const uint32_t app = LoadGuestU32(base, kAppPointer);
  if (Plausible(app)) {
    const uint32_t a = LoadGuestU32(base, app + kFirstHandler);
    const uint32_t b = LoadGuestU32(base, app + kSecondHandler);
    REXLOG_INFO("coop TRACE: publish(player=0x{:08X} id={}) -> h1=0x{:08X} h2=0x{:08X}", player,
                player_id, Plausible(a) ? LoadGuestU32(base, a + 12u) : 0u,
                Plausible(b) ? LoadGuestU32(base, b + 12u) : 0u);
  }
}

// Player::~Player — vtable off_820A9984 slot 0, through the shared_ptr deleter.
REX_EXTERN(rex_lhPlayer_ScalarDeletingDtor) {
  using refii::vfs::LoadGuestU32;

  const uint32_t player = ctx.r3.u32;
  const uint32_t app = LoadGuestU32(base, kAppPointer);

  // `sub_82371948` dereferences BOTH handler slots with no null check of its own (0x8237197C),
  // and this destructor also runs at shutdown when the App global can outlive what it points at.
  if (Plausible(player) && Plausible(app) &&
      Plausible(LoadGuestU32(base, app + kFirstHandler)) &&
      Plausible(LoadGuestU32(base, app + kSecondHandler))) {
    PPCContext unpublish = ctx;
    unpublish.r3.u32 = app;
    unpublish.r4.u32 = player;
    unpublish.lr = refii::guest::Address(0x82389B18u);
    __imp__rex_lhScene_UpdateHeightfield(unpublish, base);
  }

  __imp__rex_lhPlayer_ScalarDeletingDtor(ctx, base);
}

// --- Player two in their own hero's body --------------------------------
//
// `sub_8233C508(out, manager, index)` is a pure name table: eight henchman creatures, picked by
// the co-op screen's appearance pickbox. IT IS THE ONLY NAME SOURCE FOR PLAYER TWO — both real
// callers go through it, `sub_8233D090` (spawn at 0x8233D270, RE-SKIN at 0x8233D2E0) and
// `sub_8233CB18`. So answering it differently is the whole feature: every path that makes player
// two a body asks us for the name first.
//
// WHICH IS WHY IT IS DONE HERE AND NOT AS A ONE-SHOT SWAP. `sub_8233D090` is not called once.
// `sub_82436548` re-runs it EVERY FRAME the co-op screen is up (0x824365CC gates on
// `!screen+268 && screen+48 && screen+160`, then 0x824365DC → `sub_82437168` → 0x8243720C), and
// `sub_823276D0` re-applies it from the manager's stored index on level change and save load
// (`CmdLevelChange_Update` 0x823D6174/0x823D6268, `CmdLoadSaveGame_Update` 0x823D7938). A body
// swapped by calling `sub_82389FB8` ourselves is therefore undone within a frame on the screen,
// and again the moment play starts. Answering the name means the guest's own machinery spawns,
// re-skins and previews the hero for us, on both machines, with no race to lose.
//
// The index mapping is the pickbox's own: 0-2 female, 3-5 male, 6 male child, 7 female child.
REX_EXTERN(rex_lhNetManager_GetHenchmanCreatureName) {
  if (RefiiCoopGetOwnHero() == 0) {
    __imp__rex_lhNetManager_GetHenchmanCreatureName(ctx, base);
    return;
  }

  // GENDER COMES OFF THE INDEX THE GUEST SET, and the mapping is the guest's own: 0-2 female,
  // 3-5 male, 6 male child, 7 female child. When a save has been chosen, `sub_8243AEF8` derives
  // that index from the save's real Gender and Morality, so it is the authority — a captured
  // kit's gender was taken from no real hero and built a female hero over a male henchman.
  const uint32_t index = ctx.r5.u32;
  const char* creature = index == 6u   ? "CreatureHeroChild"
                         : index == 7u ? "CreatureHeroFemaleChild"
                         : index >= 3u ? "CreatureHero"
                                       : "CreatureHeroFemale";

  // The callee wants a guest pointer, so the name goes on a scratch frame below the caller's.
  // `r5 = -1` is the length `sub_822078A0` reads as strlen, and the guest sets it at every one
  // of these call sites (0x8233C560 and its siblings).
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t text = frame + 0x20u;

  const char* at = creature;
  for (uint32_t i = 0; *at != '\0'; ++i, ++at) {
    base[text + i] = static_cast<uint8_t>(*at);
  }

  base[text + std::strlen(creature)] = 0;

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;
  __imp__rex_lhCharString_CtorFromCStr(work, base);

  static std::atomic<bool> said{false};
  if (!said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("coop HERO: player two's creature is '{}' (pickbox index {})", creature, index);
  }
}

// --- The co-op screen's preview, in the hero's body too --------------------
//
// `sub_82437BF0(out, manager, index)` is the preview's own name table (0x82437BF0, decompiled):
// six `CreatureHenchmanMannequin*` by the same pickbox index, and the two child HERO mannequins
// `CreatureHeroMannequin{Male,Female}Child`. The adult hero mannequins exist beside them in the
// image (`CreatureHeroMannequinMale` 0x820CFF38, `...Female` 0x820CFF54 — the equipment book's
// mannequin), so while player two stands in their own hero the preview answers those, and the
// guest's own `sub_82438818` spawns the hero on the right page. The pump dresses it and asks for
// a respawn when the bit flips.
REX_EXTERN(rex_lhCoopScreen_GetMannequinDefName) {
  const uint32_t index = ctx.r5.u32;
  if (RefiiCoopGetOwnHero() == 0 || index >= 6u) {
    __imp__rex_lhCoopScreen_GetMannequinDefName(ctx, base);
    return;
  }

  const char* creature = index >= 3u ? "CreatureHeroMannequinMale" : "CreatureHeroMannequinFemale";
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t text = frame + 0x20u;
  const char* at = creature;
  for (uint32_t i = 0; *at != '\0'; ++i, ++at) {
    base[text + i] = static_cast<uint8_t>(*at);
  }

  base[text + std::strlen(creature)] = 0;

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;
  __imp__rex_lhCharString_CtorFromCStr(work, base);

  static std::atomic<bool> said{false};
  if (!said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("coop MANNEQUIN: the preview's creature is '{}' (pickbox index {})", creature, index);
  }
}

// --- Dressing the hero body --------------------------------------------
//
// Neither of the guest's two ways of giving player two a body dresses a HERO body. A RETYPE
// (`sub_82365258`) tests the new name for "Henchman" (0x82365938) and, for a hero name, re-wears
// the STASH — filled only when a hero becomes a henchman, so empty here. CREATION
// (`sub_82315030`, the branch `sub_8233D090` takes when no entity exists yet) runs the mode-0
// `Initialise` that reads `InitialItems`/`InitialModifiers`, but on a co-op client its gate byte
// `comp91+104` is set only AFTER creation (0x82315224), so those passes are skipped. A run
// confirmed the second path: both players typed to the same def, no retype, no dress.
//
// So the pump's redress runs after BOTH — `sub_8233D090` is the one entry every body build goes
// through, create or retype, and `sub_82365258` also covers the direct nudge `sub_82389FB8`.
// The redress itself dresses once per entity and outfit; see CoopRedress in the pump.
REX_EXTERN(rex_lhNetManager_SetupHenchman) {
  __imp__rex_lhNetManager_SetupHenchman(ctx, base);
  RefiiCoopRedress(&ctx, base);
}

REX_EXTERN(rex_lhEntityManager_ChangeEntityType) {
  __imp__rex_lhEntityManager_ChangeEntityType(ctx, base);
  RefiiCoopRedress(&ctx, base);
}

// --- Player two's own weapons ------------------------------------------
//
// `sub_8233D090` equips player two from two name tables — `sub_8233C640` (melee, 8 names) and
// `sub_8233C778` (ranged, 11) — both called from it alone (xrefs verified). It then creates,
// carries, equips and REPLICATES the weapons itself (`sub_82315AD0` → `sub_8238B9C8` →
// `sub_824FB450` → `sub_824AD108` → `sub_824AD698`). So answering the hero's own weapon names
// here is the whole feature, the same lever as the body: nothing on the equip path changes.
//
// The names come from the host: the equipped slots hold GDB record ids, which the host's GDB
// view turns into def names and the kit carries as strings. A name is written to a scratch
// frame for `sub_8222CED0`, exactly as the body override does.
namespace {
void AnswerWeaponName(PPCContext& ctx, uint8_t* base, const char* name) {
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;

  const uint32_t text = frame + 0x20u;
  const std::size_t length = std::strlen(name);
  for (std::size_t i = 0; i < length && i < 0x80u; ++i) {
    base[text + i] = static_cast<uint8_t>(name[i]);
  }

  base[text + (length < 0x80u ? length : 0x80u)] = 0;

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;
  __imp__rex_lhCharString_CtorFromCStr(work, base);
}
}  // namespace


REX_EXTERN(rex_lhNetManager_GetHenchmanMeleeWeaponName) {
  const char* name = RefiiCoopGetOwnHero() != 0 ? RefiiCoopMeleeName() : "";
  if (name == nullptr || name[0] == '\0') {
    __imp__rex_lhNetManager_GetHenchmanMeleeWeaponName(ctx, base);
    return;
  }

  AnswerWeaponName(ctx, base, name);
}

REX_EXTERN(rex_lhNetManager_GetHenchmanRangedWeaponName) {
  const char* name = RefiiCoopGetOwnHero() != 0 ? RefiiCoopRangedName() : "";
  if (name == nullptr || name[0] == '\0') {
    __imp__rex_lhNetManager_GetHenchmanRangedWeaponName(ctx, base);
    return;
  }

  AnswerWeaponName(ctx, base, name);
}

// `sub_82315AD0(playerMap, meleeNetId, rangedNetId)` arms player two from the name globals
// `dword_834975B4/B8`: it destroys every held item, creates the pair (host) or attaches the
// replicas by net id (client), and replicates (`sub_82317EA8`). Callers: `sub_8233D090`,
// `sub_8232E398`, `sub_824AAF10`. A hero body is armed by RECORD (the pump's EquipRecord and
// CoopAttachHeld), and a record with no def name cannot be answered by the tables, so for a
// hero body the routine is a no-op.
REX_EXTERN(rex_lhCoop_ArmHenchmanWeapons) {
  if (RefiiCoopGetOwnHero() != 0) {
    static bool said = false;
    if (!said) {
      said = true;
      REXLOG_INFO("coop hooks: sub_82315AD0 (arm player two from the name tables) skipped — "
                  "player two is a hero armed by record");
    }
    ctx.r3.u32 = 0u;
    return;
  }

  __imp__rex_lhCoop_ArmHenchmanWeapons(ctx, base);
}

namespace {
/// One component off an entity by id, or 0: the presence bitmask (eight dwords at `+0x24`),
/// then the sorted stride-8 `{s32 id, u32 ptr}` vector at `+0x48..+0x4C`. The bridge's walk,
/// without its accelerator hop.
uint32_t ComponentOf(uint8_t* base, uint32_t entity, uint32_t id) {
  using refii::vfs::LoadGuestU32;
  if (!Plausible(entity) || id >= 256u) return 0u;
  const uint32_t present = LoadGuestU32(base, entity + 0x24u + (id / 32u) * 4u);
  if (((present >> (id % 32u)) & 1u) == 0u) return 0u;
  const uint32_t pairs = LoadGuestU32(base, entity + 0x48u);
  const uint32_t pairs_end = LoadGuestU32(base, entity + 0x4Cu);
  if (!Plausible(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) return 0u;
  for (uint32_t at = pairs; at + 8u <= pairs_end; at += 8u) {
    if (LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return Plausible(component) ? component : 0u;
    }
  }
  return 0u;
}

/// The item entities a CECCarrying (component 24) holds: entries of 12 bytes at `+16..+20`,
/// holder at entry+8, item = `*holder`. Fills `out`, returns the count.
std::size_t HeldItems(uint8_t* base, uint32_t carrying, std::array<uint32_t, 16>& out) {
  using refii::vfs::LoadGuestU32;
  std::size_t count = 0u;
  const uint32_t begin = LoadGuestU32(base, carrying + 16u);
  const uint32_t end = LoadGuestU32(base, carrying + 20u);
  if (!Plausible(begin) || end < begin || (end - begin) > 12u * 64u) return 0u;
  for (uint32_t at = begin; at + 12u <= end && count < out.size(); at += 12u) {
    const uint32_t holder = LoadGuestU32(base, at + 8u);
    const uint32_t item = Plausible(holder) ? LoadGuestU32(base, holder) : 0u;
    if (Plausible(item)) out[count++] = item;
  }
  return count;
}
}  // namespace

// `sub_824FEC48(carrying, msg)` — the held-items message off the net dispatcher
// (`sub_82392250`): type 1 attaches the replica named by the message's net id
// (`sub_82334A60` → `sub_824FB5D8`), anything else RELEASES the named slot (`sub_824FCB20`).
//
// THE RELEASE NEVER DESTROYS. `sub_824FCB20` detaches the item from the holder's attachment
// graph and leaves it as a free world entity; only the HOST's wrapper `sub_824FDB70` follows
// with `sub_8238D828`, and the host's own destroy message names the HOST's entity, which the
// client's replica map (`netmgr+128`) cannot resolve for an item the client made itself. So
// when the host releases a slot on player two's body — our EquipRecord does, before it
// attaches the hero's own weapon — the joiner's save-loaded weapon in that slot is cut loose
// on the joiner's floor and stays there. The guest's own client branch of `sub_82315AD0`
// releases AND destroys every local held item first (0x82315d8c/0x82315db8); that routine is
// a no-op for a hero body, so the same thing is done here: an item this message released
// that is not a replica is destroyed. A REPLICA IS WHAT THE REPLICA MAP SAYS: the client's
// spawn handler keys `netmgr+128` on the host's id and writes it to CECNetwork+120, so
// `sub_82334A60(netmgr, *(comp91+120))` answers the item itself; a local item either has no
// CECNetwork, carries no id there, or is not in the map. "Has no component 91" was the first
// test and it was wrong — a save-loaded weapon carries one (run 2026-10-07 20:31: the pistol
// on the ice, no "cut loose" line).
REX_EXTERN(rex_CECHeldItems_OnNetMessage) {
  using refii::vfs::LoadGuestU32;
  using refii::vfs::LoadGuestU8;

  const uint32_t carrying = ctx.r3.u32;
  const uint32_t message = ctx.r4.u32;
  const uint32_t cm = CoopManager(base);
  const bool client = Plausible(cm) && LoadGuestU8(base, cm + 52u) != 0u &&
                      LoadGuestU8(base, cm + 54u) != 0u;
  const bool release = Plausible(message) && LoadGuestU32(base, message + 4u) != 1u;
  if (!client || !release || !Plausible(carrying)) {
    __imp__rex_CECHeldItems_OnNetMessage(ctx, base);
    return;
  }

  std::array<uint32_t, 16> before{};
  const std::size_t had = HeldItems(base, carrying, before);
  const PPCContext saved = ctx;
  __imp__rex_CECHeldItems_OnNetMessage(ctx, base);

  std::array<uint32_t, 16> after{};
  const std::size_t has = HeldItems(base, carrying, after);
  const auto is_replica = [&](uint32_t item) -> bool {
    const uint32_t network = ComponentOf(base, item, 91u);
    if (network == 0u) return false;
    const uint64_t id = (static_cast<uint64_t>(LoadGuestU32(base, network + 120u)) << 32) |
                        LoadGuestU32(base, network + 124u);
    if (id == 0u || !Plausible(cm)) return false;
    PPCContext find = saved;
    find.r3.u32 = cm;
    find.r4.u64 = id;
    __imp__rex_lhNetManager_FindEntityByNetId(find, base);
    return find.r3.u32 == item;
  };
  for (std::size_t i = 0; i < had; ++i) {
    const uint32_t item = before[i];
    bool still = false;
    for (std::size_t j = 0; j < has && !still; ++j) still = after[j] == item;
    if (still) continue;
    if ((LoadGuestU8(base, item + 144u) & 0x40u) == 0u) continue;  // not active: nothing to drop
    if (is_replica(item)) continue;                                // the host owns it
    REXLOG_INFO("coop hooks: held-items release from the host cut loose LOCAL item 0x{:08X} "
                "(record {:08X}) on 0x{:08X} — destroyed, as the guest's own client arm does",
                item, LoadGuestU32(base, item + 28u), carrying);
    PPCContext destroy = saved;
    destroy.r3.u32 = item;
    __imp__rex_lhEntity_Destroy(destroy, base);
  }
}

// THE REPLICA MOTION CADENCE — the guest's own transport, re-paced for a network that is not
// 2008 Xbox Live.
//
// A networked entity's authority sends its transform from `sub_822BCA68` (CECNetwork tick,
// vtable +56, under `sub_82214AF8`): message 5 when it moved past a threshold, message 6 once
// when it came to rest, and never more often than an interval in GAME TICKS (`dword_83319650`,
// 15 Hz under refii, 66 ms): `*(this+248) >= *(this+252) + interval`, interval 30 by default,
// 2 for a player (`sub_82232778`), 5 visible and within 15 m, 8 visible and farther, 3 for a
// few flagged classes (0x822bcd4c..0x822bcdb4). The receiver (`sub_8239E9E0`) pushes each
// sample into an 8-deep ring on the entity's CECNetwork with a playback time of ARRIVAL +
// 0.1 s (slot+0x20, 0x8239eab8..), and the replica tick (`sub_8239EB00`) lerps between the two
// samples around "now", extrapolating up to three segments past the newest, then moves the
// placement halfway to that target each tick (`sub_8239ED30`, 0.5 at 0x8239ee14).
//
// So a hero arrives every 133 ms and is played 100 ms behind: the receiver runs past its
// newest sample between almost every pair, extrapolates, and is pulled back when the next one
// lands — the partner's stutter on both machines. A villager arrives every 333 or 533 ms
// against the same 100 ms and does worse, which is the joiner's whole town.
//
// Two seams, both the guest's own numbers: the SENDER is let through every tick for a player
// and every third tick for anyone else, by backdating `this+252` so its own test passes (the
// movement thresholds and the at-rest message are untouched, so a still body still sends
// nothing); the RECEIVER's playback delay is widened to cover the cadence with a sample to
// spare: 0.15 s for a player, 0.4 s for the rest. The 0.1 literal is shared by fifty other
// functions and is left alone; the slot's time is adjusted after the push instead.
namespace {
/// Whether `entity` is one of the two player bodies — the entity manager's own slots, which
/// `sub_82232778` reads (`EM+104`/`+112`, objects at `+108`/`+116`).
bool IsPlayerBody(uint8_t* base, uint32_t entity) {
  using refii::vfs::LoadGuestU32;
  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t holder = Plausible(game) ? LoadGuestU32(base, game + 88u) : 0u;
  const uint32_t slot = Plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  const uint32_t manager = Plausible(slot) ? LoadGuestU32(base, slot) : 0u;
  if (!Plausible(manager)) return false;
  for (uint32_t at : {108u, 116u}) {
    const uint32_t object = LoadGuestU32(base, manager + at);
    if (Plausible(object) && LoadGuestU32(base, object) == entity) return true;
  }
  return false;
}

/// Ticks between sends, by class: a player every tick, anyone else every third.
constexpr uint32_t kPlayerSendTicks = 1u;
constexpr uint32_t kOtherSendTicks = 3u;

/// Playback delay behind arrival, by class, replacing the guest's 0.1 s.
constexpr float kPlayerPlaybackDelay = 0.15f;
constexpr float kOtherPlaybackDelay = 0.4f;
constexpr float kGuestPlaybackDelay = 0.1f;

float LoadGuestF32(uint8_t* base, uint32_t at) {
  const uint32_t bits = refii::vfs::LoadGuestU32(base, at);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

void StoreGuestF32(uint8_t* base, uint32_t at, float value) {
  uint32_t bits;
  std::memcpy(&bits, &value, sizeof(bits));
  refii::vfs::StoreGuestU32(base, at, bits);
}
}  // namespace

REX_EXTERN(rex_CECNetwork_SendTransform) {
  using refii::vfs::LoadGuestU32;
  using refii::vfs::StoreGuestU32;

  const uint32_t net = ctx.r3.u32;
  if (Plausible(net)) {
    const uint32_t entity = LoadGuestU32(base, net + 4u);
    const uint32_t now = LoadGuestU32(base, net + 248u);
    const uint32_t last = LoadGuestU32(base, net + 252u);
    const uint32_t wanted = Plausible(entity) && IsPlayerBody(base, entity) ? kPlayerSendTicks
                                                                           : kOtherSendTicks;
    // Due by our cadence but not yet by the guest's: backdate the last send so the guest's own
    // test passes. 30 is its widest interval. A fresh component (last 0) is left to the guest.
    if (last != 0u && now >= last + wanted && now < last + 30u) {
      StoreGuestU32(base, net + 252u, now - 30u);
    }
  }

  __imp__rex_CECNetwork_SendTransform(ctx, base);
}

REX_EXTERN(rex_CECNetwork_PushReceivedTransform) {
  using refii::vfs::LoadGuestU32;

  const uint32_t net = ctx.r3.u32;
  const uint32_t head_before = Plausible(net) ? LoadGuestU32(base, net + 0x304u) : 0u;
  __imp__rex_CECNetwork_PushReceivedTransform(ctx, base);
  if (!Plausible(net)) return;

  // Only a sample the guest accepted (the head moved) is re-timed, and only one that carries
  // the shared 0.1 s delay — the zero-delay snap class (entity+0x40 bit 10) keeps its own.
  const uint32_t head = LoadGuestU32(base, net + 0x304u);
  if (head == head_before || head == 0u) return;
  const uint32_t slot = net + 0x180u + 48u * ((head - 1u) & 7u);
  const float playback = LoadGuestF32(base, slot + 0x20u);
  if (!std::isfinite(playback)) return;
  const uint32_t entity = LoadGuestU32(base, net + 4u);
  const uint32_t flags = Plausible(entity) ? LoadGuestU32(base, entity + 0x40u) : 0u;
  if (((flags >> 10) & 1u) != 0u) return;
  const float delay = Plausible(entity) && IsPlayerBody(base, entity) ? kPlayerPlaybackDelay
                                                                       : kOtherPlaybackDelay;
  StoreGuestF32(base, slot + 0x20u, playback - kGuestPlaybackDelay + delay);
}

// THE REPLICA MOVER — exact placement at the interpolated target, not a half-step toward it.
//
// `sub_8239ED30(net, quat in r4:r5, position in v1)` is what the replica tick (`sub_8239EB00`)
// calls with the transform it interpolated between two received samples. The shipped body
// does not go there: it reads the placement's current position (vt+64) and orientation
// (vt+68) and sets `cur + 0.5 * (target - cur)` (0x8239ee14 / 0x8239ee2c, vt+100) and the
// same half-way quaternion (0x8239ee78 / 0x8239eeac, vt+104) — a low-pass filter on top of
// an interpolation that is already smooth, converging on each target only as the next one
// arrives. It hides sample jitter, which the playback buffer now covers, and it costs a
// permanent lag of several ticks behind a body that is already played back late. A modern
// receiver draws the interpolated sample itself. So: the target goes straight into vt+100
// and vt+104, through the same virtuals, with the original's hemisphere correction kept (the
// quaternion is negated when it faces away from the current one, so the renderable's own
// prev/live slerp never takes the long way round). The snap class (entity+0x40 bit 10, a
// matrix set through vt+132) is left to the original.
REX_EXTERN(rex_CECNetwork_ApplyReplicaTarget) {
  using refii::vfs::LoadGuestU32;

  const uint32_t net = ctx.r3.u32;
  const uint32_t entity = Plausible(net) ? LoadGuestU32(base, net + 4u) : 0u;
  const uint32_t placement = Plausible(entity) ? LoadGuestU32(base, entity + 124u) : 0u;
  const uint32_t vtable = Plausible(placement) ? LoadGuestU32(base, placement) : 0u;
  const bool snaps = Plausible(entity) && ((LoadGuestU32(base, entity + 64u) >> 10) & 1u) != 0u;
  PPCFunc* set_position = Plausible(vtable) ? refii::vfs::FindGuestFunction(LoadGuestU32(base, vtable + 100u)) : nullptr;
  PPCFunc* set_orientation = Plausible(vtable) ? refii::vfs::FindGuestFunction(LoadGuestU32(base, vtable + 104u)) : nullptr;
  PPCFunc* get_orientation = Plausible(vtable) ? refii::vfs::FindGuestFunction(LoadGuestU32(base, vtable + 68u)) : nullptr;
  if (snaps || set_position == nullptr || set_orientation == nullptr) {
    __imp__rex_CECNetwork_ApplyReplicaTarget(ctx, base);
    return;
  }

  const PPCContext saved = ctx;
  {
    PPCContext call = saved;  // v1 already holds the target position
    call.r3.u32 = placement;
    set_position(call, base);
  }

  // Target quaternion as the guest passes it: r4 = {x, y}, r5 = {z, w}, big-endian floats.
  uint64_t q0 = saved.r4.u64;
  uint64_t q1 = saved.r5.u64;
  if (get_orientation != nullptr) {
    const uint32_t frame = (saved.r1.u32 - 0x100u) & ~0xFu;
    const uint32_t out = frame + 0x20u;
    PPCContext call = saved;
    call.r1.u32 = frame;
    call.r3.u32 = out;
    call.r4.u32 = placement;
    get_orientation(call, base);
    float current[4];
    float target[4];
    const uint32_t target_bits[4] = {static_cast<uint32_t>(q0 >> 32), static_cast<uint32_t>(q0),
                                     static_cast<uint32_t>(q1 >> 32), static_cast<uint32_t>(q1)};
    for (int i = 0; i < 4; ++i) {
      const uint32_t bits = LoadGuestU32(base, out + 4u * static_cast<uint32_t>(i));
      std::memcpy(&current[i], &bits, sizeof(float));
      std::memcpy(&target[i], &target_bits[i], sizeof(float));
    }
    const float dot = current[0] * target[0] + current[1] * target[1] + current[2] * target[2] +
                      current[3] * target[3];
    if (std::isfinite(dot) && dot < 0.0f) {
      uint32_t flipped[4];
      for (int i = 0; i < 4; ++i) {
        const float value = -target[i];
        std::memcpy(&flipped[i], &value, sizeof(uint32_t));
      }
      q0 = (static_cast<uint64_t>(flipped[0]) << 32) | flipped[1];
      q1 = (static_cast<uint64_t>(flipped[2]) << 32) | flipped[3];
    }
  }
  {
    PPCContext call = saved;
    call.r3.u32 = placement;
    call.r4.u64 = q0;
    call.r5.u64 = q1;
    set_orientation(call, base);
  }
}
