// refii — Fable 2's world icons, stated at the guest's own entry points into the table both
// modules link (core/payload_bridge.h, src/runtime/src/core/stated_world_icons.cpp), as Fable 3's
// are (Fable3/src/hooks/src/world_icon_hooks.cpp). The host bridge (host_bridge.cpp,
// RefiiHostGetWorldSprites) reads only that table: nothing walks the manager's icon vector. Every
// hook runs the guest's body unchanged — the icons stay alive and run (tutorial 183 in the request
// gate `sub_823DD860`, the look-at cue, the prompt manager); the drawing is Godot's.
//
// Addresses are TU1; the disc build's are given beside them.
//
// THE MANAGER wim = *(*(GUI+32)+28). The requests (lhWorldIcons_Request `sub_823DD6B0` /
// `sub_823B6D98`, lhPromptManager_Request `sub_823DD730`, `sub_823DD7C8`) only build a 48-byte
// descriptor (`sub_823DD1D0`: +0 key CCharString, +4/+8 the entity weak ref, +12 text, +16 type,
// +32 offset) and queue it on wim+32. lhPromptManager_Update (`sub_821C5BB0`) hands each queued one
// to `sub_823DDF90` (0x821c5c64): an icon already held under the same entity and key is initialised
// again (vf +4 at 0x823de0c8); otherwise a CWorldIcon3D (368 bytes, vtable 0x820028C4 / disc
// 0x820028E4, `sub_828243D8`) or a pooled HUD-element icon (176 bytes, vtable 0x820DD0E0 / disc
// 0x820D69F0, claimed by `sub_82825618` on one of the 16 `WorldIconN` slots at 0x8349BC80, or the
// single slots of types 35/52/53) is made and initialised (vf +4, 0x823de240). Both Inits reach the
// base `sub_828203B8` / `sub_827D2830` (0x828247e4, 0x828252cc).
//
// WHAT THE CALLS STATE (all inside the base Init, each through the icon's own vtable):
//   - Init: r3 the icon, r4 the descriptor — key, entity, type (+16, written to +20 at 0x828203e4).
//   - SetType, vf +8 (0x82820498): CWorldIcon3D `sub_82824830` / `sub_827D1208`, r4 the type; it
//     names the art by type (`sub_82820640`, "art\gui\<name>.tex") and stores its resource hash at
//     +128 (0x82824878). The pooled `sub_82825358` puts the same art on its element instead
//     (GuiElement_SetTexture 0x828253d4, stated by the element hooks).
//   - SetColour, vf +40 (0x8282052c): CWorldIconBase `sub_82820F30` / `sub_827D33A0`, r4 four bytes
//     (the type's colour from `sub_82820C50`) copied to +60..+63; drawn as (+62, +61, +60).
//   - SetText, vf +12 (0x82820624): CWorldIcon3D `sub_82824968` / `sub_827D1340`, r4 the title and
//     r5 the amount (wide LHStrings) assigned to +140 / +148. An amount-bearing type (1..21,
//     36..49, 54) is given the request's text as its amount and a title from `sub_82820F58`; 38
//     and 39 get no title. The pooled `sub_828253F8` writes its elements' texts (element hooks).
//   - Timed or persistent: the Init's own `sub_821AB450(type)` — timed types get +48 = 5 s and
//     +52 = 0 (0x828204dc..0x828204f4).
// THE END: every icon class ends in the base destructor `sub_82820348` / `sub_827D27C0`
// (CWorldIcon3D 0x828245b0, the pooled `sub_82824B38` 0x82824bb4): the manager's erase
// `sub_823DDDD8` (Remove 0x821bd8c0, the update 0x821c5e34, the clear `sub_823DD918` 0x823dd9c8)
// drops the vector's reference and the last one destroys it.
//
// WHAT NO CALL STATES — READ, not stated. Each tick lhPromptManager_Update asks every icon whether
// it lives (vf +20, CWorldIconBase_vf5 `sub_821C3120`, which resolves the anchor through
// `sub_821DEDA0`: +112 = the descriptor's offset plus head height and stacking, `stvx128` with
// r26 = 112) and Updates it (vf +16, 0x821c5e00). The Update moves the icon's own state: CWorldIcon3D's
// lhWorldIcon_Update (`sub_82241AD8` / `sub_82241AE0`) — seconds left +48, age +68, the opacity
// tween +160 between +156 and +164 (its targets: fade out from age 4 s for a timed type,
// 0x82241c58; in or out across the type's distance gate `sub_822D77A0`), the hero distance² +132,
// a relationship icon's size +56 (`sub_82820A78`); the pooled `sub_82824D60` / `sub_827D1738` —
// its anchor lift +144 and the first-placement byte +160 (cleared 0x82825210). These are the icon's
// own fields read right after its Update returned, on the guest thread, for the object the call
// was given: the rules that move them are code switches in the binary (`sub_821AB450`,
// `sub_822D77A0`, `sub_82820C50`), not shipped data, and copying them into Godot would be
// emulating the HUD. The drawn size is the guest's own `sub_821AA758` / `sub_821AA8C0` (+56 by the
// +132 curve), called here.

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "hooks/ppc_externs.h"

PPC_EXTERN_FUNC(__imp__rex_lhWorldIconBase_Init);
PPC_EXTERN_FUNC(__imp__rex_lhWorldIconBase_Destroy);
PPC_EXTERN_FUNC(__imp__rex_lhWorldIconBase_SetColour);
PPC_EXTERN_FUNC(__imp__rex_lhWorldIcon3D_SetType);
PPC_EXTERN_FUNC(__imp__rex_lhWorldIcon3D_SetText);
PPC_EXTERN_FUNC(__imp__rex_lhWorldIconPooled_Update);

namespace {

using refii::mods::LoadF32;
using refii::mods::LoadU32;
using refii::mods::LoadU8;

constexpr uint32_t kVtWorldIcon3D = 0x820028C4u;
constexpr uint32_t kVtPooledIcon = 0x820DD0E0u;
constexpr uint32_t kPoolBase = 0x8349BC80u;
constexpr uint32_t kIconSize = 0x821AA758u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xE0000000u; }

bool Milestone(uint64_t n) { return n <= 16u || (n & (n - 1u)) == 0u; }

/// What the icon's calls stated, kept from its Init until it is destroyed.
struct Stated {
  uint32_t kind = 0u;  // REFII_WORLD_ICON_CLASS_*
  uint32_t type = 0u;
  uint32_t texture = 0u;
  uint32_t colour = 0u;  // 0xRRGGBB00, from SetColour's bytes (+62, +61, +60)
  uint32_t slot = 0xFFFFFFFFu;
  uint32_t player = 1u;
  std::string key;
  std::string title;
  std::string amount;
};

std::mutex g_lock;
std::unordered_map<uint32_t, Stated>* g_stated = nullptr;

std::unordered_map<uint32_t, Stated>& StatedIcons() {
  if (g_stated == nullptr) {
    g_stated = new std::unordered_map<uint32_t, Stated>();
  }
  return *g_stated;
}

std::atomic<uint64_t> g_inits{0};
std::atomic<uint64_t> g_destroyed{0};

uint32_t ClassOf(const uint8_t* base, uint32_t icon) {
  const uint32_t vtable = LoadU32(base, icon);
  if (vtable == refii::guest::Address(kVtWorldIcon3D)) return REFII_WORLD_ICON_CLASS_3D;
  if (vtable == refii::guest::Address(kVtPooledIcon)) return REFII_WORLD_ICON_CLASS_HUD_ELEMENT;
  return 0u;
}

/// A CCharString's bytes (`rep = *string`, `*rep` the chars, `rep+4` the length).
std::string CharString(const uint8_t* base, uint32_t string) {
  const uint32_t rep = Plausible(string) ? LoadU32(base, string) : 0u;
  const uint32_t chars = Plausible(rep) ? LoadU32(base, rep) : 0u;
  const uint32_t length = Plausible(rep) ? LoadU32(base, rep + 4u) : 0u;
  if (!Plausible(chars) || length > 512u) {
    return std::string();
  }
  return std::string(reinterpret_cast<const char*>(base + chars), length);
}

/// A wide LHString (`rep = *string`, `*rep` the UTF-16BE units, `rep+4` the length, at most 256)
/// as UTF-8, basic plane, stopping at a NUL.
std::string WideText(const uint8_t* base, uint32_t string) {
  const uint32_t rep = Plausible(string) ? LoadU32(base, string) : 0u;
  const uint32_t units = Plausible(rep) ? LoadU32(base, rep) : 0u;
  if (!Plausible(units)) {
    return std::string();
  }
  const uint32_t length = std::min(LoadU32(base, rep + 4u), 256u);
  std::string out;
  for (uint32_t i = 0; i < length; ++i) {
    const uint32_t unit = refii::mods::LoadU16(base, units + (i * 2u));
    if (unit == 0u) {
      break;
    }
    if (unit < 0x80u) {
      out.push_back(static_cast<char>(unit));
    } else if (unit < 0x800u) {
      out.push_back(static_cast<char>(0xC0u | (unit >> 6)));
      out.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
    } else {
      out.push_back(static_cast<char>(0xE0u | (unit >> 12)));
      out.push_back(static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu)));
      out.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
    }
  }
  return out;
}

/// The CThing behind the weak ref whose block is at `icon+28`: the block's first word, 0 when gone.
uint32_t Entity(const uint8_t* base, uint32_t icon) {
  const uint32_t block = LoadU32(base, icon + 28u);
  return Plausible(block) ? LoadU32(base, block) : 0u;
}

/// The guest's own drawn size for a CWorldIcon3D (`sub_821AA758`: +56 by the +132 curve).
float GuestSize(const PPCContext& ctx, uint8_t* base, uint32_t icon) {
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u64 = icon;
  return refii::mods::CallGuest(work, base, refii::guest::Address(kIconSize)) ? static_cast<float>(work.f1.f64)
                                                                               : 0.0f;
}

/// Writes the icon into the shared table: the stated fields, and — after an Update — the fields its
/// Update moved (`updated`; read, see the file note). `visible` as refii_bridge_world_icon_stated.
void Publish(const PPCContext& ctx, uint8_t* base, uint32_t icon, const Stated& stated, bool updated,
             int32_t visible) {
  const bool pooled = stated.kind == REFII_WORLD_ICON_CLASS_HUD_ELEMENT;
  uint32_t words[REFII_WORLD_ICON_WORDS]{};
  float values[REFII_WORLD_ICON_VALUES]{};
  words[0] = stated.kind;
  words[1] = stated.type;
  words[2] = pooled ? 0u : stated.texture;
  words[4] = Entity(base, icon);
  uint32_t flags = 0u;
  if (LoadU8(base, icon + 52u) != 0u) flags |= REFII_WORLD_ICON_PERSISTENT;
  if (LoadU8(base, icon + 100u) != 0u) flags |= REFII_WORLD_ICON_FADING;
  words[5] = flags;
  words[6] = stated.slot;
  words[7] = stated.player;

  uint32_t alpha = 0u;
  if (updated) {
    values[0] = LoadF32(base, icon + 112u);
    values[1] = LoadF32(base, icon + 116u);
    values[2] = LoadF32(base, icon + 120u);
    if (pooled) {
      values[0] += LoadF32(base, icon + 144u);
      values[1] += LoadF32(base, icon + 148u);
      values[2] += LoadF32(base, icon + 152u);
      values[3] = 1.0f;
      alpha = 255u;
    } else {
      const float opacity = LoadF32(base, icon + 160u);
      alpha = opacity <= 0.0f ? 0u : opacity >= 1.0f ? 255u : static_cast<uint32_t>(opacity * 255.0f);
      values[3] = GuestSize(ctx, base, icon);
      values[4] = LoadF32(base, icon + 48u);
      values[5] = LoadF32(base, icon + 68u);
    }
  }
  words[3] = (stated.colour & 0xFFFFFF00u) | alpha;

  std::string text = stated.key;
  text.push_back('\0');
  text += stated.title;
  text.push_back('\0');
  text += stated.amount;
  refii_bridge_world_icon_stated(icon, visible, words, values, text.data(), static_cast<uint32_t>(text.size()));
}

/// Runs `body` on the icon's stated record under the lock, creating it.
template <typename Body>
void WithStated(uint32_t icon, Body&& body) {
  std::lock_guard<std::mutex> guard(g_lock);
  body(StatedIcons()[icon]);
}

/// A copy of the icon's stated record; false when no Init stated it.
bool CopyStated(uint32_t icon, Stated* out) {
  std::lock_guard<std::mutex> guard(g_lock);
  auto& table = StatedIcons();
  const auto it = table.find(icon);
  if (it == table.end() || it->second.kind == 0u) {
    return false;
  }
  *out = it->second;
  return true;
}

}  // namespace

// The base Init (TU1 `sub_828203B8`, disc `sub_827D2830`): r3 the icon, r4 the descriptor. Its
// SetType, SetColour and SetText (above) state into the record during the call; afterwards the
// icon is stated with the descriptor's key and type, the player whose key it is (player two's ends
// "_P2", mod_events.cpp rex_lhWorldIcons_Request) and a pooled icon's slot (+128, the slot record
// `sub_82825618` claimed at 0x82825668 before the Init: (record - 0x8349BC80) / 64, 16..18 the
// single slots of 35/52/53). Not drawn until its first Update. An icon initialised again in place
// keeps its row until that Update.
REX_EXTERN(rex_lhWorldIconBase_Init) {
  REX_FUNC_PROLOGUE();
  const uint32_t icon = ctx.r3.u32;
  const uint32_t descriptor = ctx.r4.u32;
  const PPCContext entry = ctx;
  __imp__rex_lhWorldIconBase_Init(ctx, base);
  if (!Plausible(icon) || !Plausible(descriptor)) {
    return;
  }
  const uint32_t kind = ClassOf(base, icon);
  if (kind == 0u) {
    return;
  }
  const std::string key = CharString(base, descriptor);
  const uint32_t type = LoadU32(base, descriptor + 16u);
  uint32_t slot = 0xFFFFFFFFu;
  if (kind == REFII_WORLD_ICON_CLASS_HUD_ELEMENT) {
    const uint32_t record = LoadU32(base, icon + 128u);
    const uint32_t pool = refii::guest::Address(kPoolBase);
    if (record >= pool) {
      slot = (record - pool) / 64u;
    }
  }
  Stated stated;
  bool fresh = false;
  WithStated(icon, [&](Stated& record) {
    fresh = record.kind == 0u;
    record.kind = kind;
    record.type = type;
    record.key = key;
    record.slot = slot;
    record.player = key.size() >= 3u && key.compare(key.size() - 3u, 3u, "_P2") == 0 ? 2u : 1u;
    stated = record;
  });
  if (!fresh) {
    return;  // initialised again in place (0x823de0c8): its Update in this tick restates it
  }
  Publish(entry, base, icon, stated, false, -1);
  const uint64_t n = ++g_inits;
  if (Milestone(n)) {
    REXLOG_INFO("Refii world icons: icon 0x{:08X} stated at Init, {} type {} key '{}' texture 0x{:08X} title '{}' "
                "amount '{}' slot {} player {} (#{})",
                icon, kind == REFII_WORLD_ICON_CLASS_3D ? "3D" : "pooled", type, key, stated.texture, stated.title,
                stated.amount, static_cast<int32_t>(slot), stated.player, n);
  }
}

// CWorldIcon3D SetType, vf +8 (TU1 `sub_82824830`, disc `sub_827D1208`): r3 the icon, r4 the type.
// The texture is the resource hash the call stores at +128 (0x82824878), taken after it.
REX_EXTERN(rex_lhWorldIcon3D_SetType) {
  REX_FUNC_PROLOGUE();
  const uint32_t icon = ctx.r3.u32;
  const uint32_t type = ctx.r4.u32;
  __imp__rex_lhWorldIcon3D_SetType(ctx, base);
  if (!Plausible(icon)) {
    return;
  }
  const uint32_t texture = LoadU32(base, icon + 128u);
  WithStated(icon, [&](Stated& record) {
    record.type = type;
    record.texture = texture;
  });
}

// CWorldIconBase SetColour, vf +40 (TU1 `sub_82820F30`, disc `sub_827D33A0`): r3 the icon, r4 the
// four bytes it copies to +60..+63; drawn as R = byte 2, G = byte 1, B = byte 0.
REX_EXTERN(rex_lhWorldIconBase_SetColour) {
  REX_FUNC_PROLOGUE();
  const uint32_t icon = ctx.r3.u32;
  const uint32_t bytes = ctx.r4.u32;
  if (Plausible(icon) && Plausible(bytes)) {
    const uint32_t colour = (static_cast<uint32_t>(LoadU8(base, bytes + 2u)) << 24) |
                            (static_cast<uint32_t>(LoadU8(base, bytes + 1u)) << 16) |
                            (static_cast<uint32_t>(LoadU8(base, bytes)) << 8);
    WithStated(icon, [&](Stated& record) { record.colour = colour; });
  }
  __imp__rex_lhWorldIconBase_SetColour(ctx, base);
}

// CWorldIcon3D SetText, vf +12 (TU1 `sub_82824968`, disc `sub_827D1340`): r3 the icon, r4 the
// title and r5 the amount, wide LHStrings.
REX_EXTERN(rex_lhWorldIcon3D_SetText) {
  REX_FUNC_PROLOGUE();
  const uint32_t icon = ctx.r3.u32;
  if (Plausible(icon)) {
    std::string title = Plausible(ctx.r4.u32) ? WideText(base, ctx.r4.u32) : std::string();
    std::string amount = Plausible(ctx.r5.u32) ? WideText(base, ctx.r5.u32) : std::string();
    WithStated(icon, [&](Stated& record) {
      record.title = std::move(title);
      record.amount = std::move(amount);
    });
  }
  __imp__rex_lhWorldIcon3D_SetText(ctx, base);
}

// The base destructor (TU1 `sub_82820348`, disc `sub_827D27C0`): r3 the icon. Every class ends here.
REX_EXTERN(rex_lhWorldIconBase_Destroy) {
  REX_FUNC_PROLOGUE();
  const uint32_t icon = ctx.r3.u32;
  bool known = false;
  {
    std::lock_guard<std::mutex> guard(g_lock);
    known = StatedIcons().erase(icon) != 0u;
  }
  refii_bridge_world_icon_removed(icon);
  __imp__rex_lhWorldIconBase_Destroy(ctx, base);
  if (known) {
    const uint64_t n = ++g_destroyed;
    if (Milestone(n)) {
      REXLOG_INFO("Refii world icons: icon 0x{:08X} destroyed (#{})", icon, n);
    }
  }
}

// The pooled icon's Update, vf +16 (TU1 `sub_82824D60`, disc `sub_827D1738`): r3 the icon. Drawn
// once its first placement ran (+160 cleared at 0x82825210 / 0x827D1BE8); the anchor is +112 plus
// the lift +144 (`vaddfp128` 0x8282509c).
REX_EXTERN(rex_lhWorldIconPooled_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t icon = ctx.r3.u32;
  const PPCContext entry = ctx;
  __imp__rex_lhWorldIconPooled_Update(ctx, base);
  Stated stated;
  if (Plausible(icon) && CopyStated(icon, &stated)) {
    Publish(entry, base, icon, stated, true, LoadU8(base, icon + 160u) == 0u ? 1 : 0);
  }
}

// CWorldIcon3D's Update (lhWorldIcon_Update, mod_events.cpp): states the icon after it ran.
void RefiiWorldIcon3DUpdated(const PPCContext& entry, uint8_t* base, uint32_t icon) {
  Stated stated;
  if (Plausible(icon) && CopyStated(icon, &stated) && stated.kind == REFII_WORLD_ICON_CLASS_3D) {
    Publish(entry, base, icon, stated, true, 1);
  }
}
