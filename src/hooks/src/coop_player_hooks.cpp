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

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_state.h"

extern "C" REX_FUNC(__imp__sub_82389AF8);
extern "C" REX_FUNC(__imp__sub_82371860);
extern "C" REX_FUNC(__imp__sub_8232B518);
extern "C" REX_FUNC(__imp__sub_8232E398);
extern "C" REX_FUNC(__imp__sub_82334E60);
extern "C" REX_FUNC(__imp__sub_822EEA50);
extern "C" REX_FUNC(__imp__sub_8232CBA8);
extern "C" REX_FUNC(__imp__sub_82438818);
extern "C" REX_FUNC(__imp__sub_8233C508);
extern "C" REX_FUNC(__imp__sub_82437BF0);
extern "C" REX_FUNC(__imp__sub_8222CED0);

/// The bit itself lives with the pump, in refii_vfs — see `guest_tick_pump.cpp`. It is set from
/// the host through the front-end action `CoopHeroBody:`, and the hooks already link that way
/// round (`core/guest_state.h` below). 0 henchman, 1 their own hero.
extern "C" int RefiiCoopGetOwnHero(void);
extern "C" uint32_t RefiiCoopPlayerTwoEntity(uint8_t* base);
extern "C" uint32_t RefiiCoopEntityComponent(uint8_t* base, uint32_t entity, uint32_t id);

extern "C" REX_FUNC(__imp__sub_82365258);
extern "C" REX_FUNC(__imp__sub_8233D090);
extern "C" void RefiiCoopRedress(void* ctx, uint8_t* base);
extern "C" const char* RefiiCoopMeleeName(void);
extern "C" const char* RefiiCoopRangedName(void);
extern "C" REX_FUNC(__imp__sub_8233C640);
extern "C" REX_FUNC(__imp__sub_8233C778);
extern "C" REX_FUNC(__imp__sub_82315AD0);

// 0x82371948, which refii_config.toml misnames `rex_lhScene_UpdateHeightfield` — the body
// unpublishes a Player. That config line is the name's only reference in the tree.
extern "C" REX_FUNC(__imp__rex_lhScene_UpdateHeightfield);

namespace {

constexpr uint32_t kAppPointer = 0x83496B08u;
constexpr uint32_t kGameRoot = 0x83496AB8u;
constexpr uint32_t kFirstHandler = 20u;
constexpr uint32_t kSecondHandler = 24u;


constexpr bool Plausible(uint32_t address) {
  return address >= 0x10000u && address < 0xC0000000u;
}

/// The co-op manager — `g_XrnmNetContext` (0x83497490) itself. `game+140` is only a counted
/// COPY of it, and a title-joined client had that copy EMPTY in game; the global is the truth.
uint32_t CoopManager(uint8_t* base) {
  using refii::vfs::LoadGuestU32;
  const uint32_t manager = LoadGuestU32(base, 0x83497490u);
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
REX_EXTERN(sub_8232E398) {
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

  __imp__sub_8232E398(ctx, base);
}

// ProcessInboundSessionPacket — a3 (r5) is the packet; its 16-bit id is the switch key.
REX_EXTERN(sub_8232B518) {
  using refii::vfs::LoadGuestU8;

  const uint32_t packet = ctx.r5.u32;
  if (Plausible(packet)) {
    const uint32_t id =
        (static_cast<uint32_t>(LoadGuestU8(base, packet)) << 8) | LoadGuestU8(base, packet + 1u);
    REXLOG_INFO("coop JOIN: inbound session packet id=0x{:X}", id);
  }

  __imp__sub_8232B518(ctx, base);
}

// Packet 0x118's handler — sets CM+52 = 1, CM+54 = 1 (0x82334ED8 / 0x82334EDC).
REX_EXTERN(sub_82334E60) {
  __imp__sub_82334E60(ctx, base);
  ReportJoinState(base, "packet 0x118 handled");
}

// XboxLive_ProcessJoinResponse — on APPROVED sets CXboxLive+1328 and CM+218 (0x822EF1D8/DC).
REX_EXTERN(sub_822EEA50) {
  REXLOG_INFO("coop JOIN: ProcessJoinResponse entered");
  __imp__sub_822EEA50(ctx, base);
  ReportJoinState(base, "join response processed");
}

// Multiplayer.DisplayCoopScreen — the only thing that opens screen 24.
REX_EXTERN(sub_8232CBA8) {
  ReportJoinState(base, "DisplayCoopScreen entered");
  __imp__sub_8232CBA8(ctx, base);
}

// The henchman mannequin spawn. `screen+276` is the raw entity (0x824388CC) and `screen+284`
// the preview ticket (0x824389B4); both zero means the preview was never built.
REX_EXTERN(sub_82438818) {
  using refii::vfs::LoadGuestU32;

  const uint32_t screen = ctx.r3.u32;
  __imp__sub_82438818(ctx, base);

  if (Plausible(screen)) {
    REXLOG_INFO("coop MANNEQUIN: sub_82438818 ran — screen+276(entity)=0x{:08X} ticket=0x{:08X}",
                LoadGuestU32(base, screen + 276u), LoadGuestU32(base, screen + 284u));
  }
}

// sub_82371860 — publish a Player into the App's handlers.
REX_EXTERN(sub_82371860) {
  using refii::vfs::LoadGuestU32;

  const uint32_t player = ctx.r4.u32;
  const uint32_t player_id = Plausible(player) ? LoadGuestU32(base, player + 36u) : 0u;
  __imp__sub_82371860(ctx, base);

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
REX_EXTERN(sub_82389AF8) {
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
    unpublish.lr = 0x82389B18u;
    __imp__rex_lhScene_UpdateHeightfield(unpublish, base);
  }

  __imp__sub_82389AF8(ctx, base);
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
REX_EXTERN(sub_8233C508) {
  if (RefiiCoopGetOwnHero() == 0) {
    __imp__sub_8233C508(ctx, base);
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
  __imp__sub_8222CED0(work, base);

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
REX_EXTERN(sub_82437BF0) {
  const uint32_t index = ctx.r5.u32;
  if (RefiiCoopGetOwnHero() == 0 || index >= 6u) {
    __imp__sub_82437BF0(ctx, base);
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
  __imp__sub_8222CED0(work, base);

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
REX_EXTERN(sub_8233D090) {
  __imp__sub_8233D090(ctx, base);
  RefiiCoopRedress(&ctx, base);
}

REX_EXTERN(sub_82365258) {
  __imp__sub_82365258(ctx, base);
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
  __imp__sub_8222CED0(work, base);
}

}  // namespace

REX_EXTERN(sub_8233C640) {
  const char* name = RefiiCoopGetOwnHero() != 0 ? RefiiCoopMeleeName() : "";
  if (name == nullptr || name[0] == '\0') {
    __imp__sub_8233C640(ctx, base);
    return;
  }

  AnswerWeaponName(ctx, base, name);
}

REX_EXTERN(sub_8233C778) {
  const char* name = RefiiCoopGetOwnHero() != 0 ? RefiiCoopRangedName() : "";
  if (name == nullptr || name[0] == '\0') {
    __imp__sub_8233C778(ctx, base);
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
REX_EXTERN(sub_82315AD0) {
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

  __imp__sub_82315AD0(ctx, base);
}
