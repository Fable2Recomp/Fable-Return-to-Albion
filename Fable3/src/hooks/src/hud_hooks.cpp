// refii - Fable 3's HUD element registries and its screen fade, stated at the guest's own entry
// points into the table both modules link (core/payload_bridge.h, src/runtime/src/core/
// stated_hud.cpp); the host bridge (Fable3/src/hostbridge/src/hud_feed.cpp) reads only that table.
// Every hook runs the guest's body unchanged: the registries, their elements and the fader stay
// alive and run, and every guest read-back of them is the guest's. The drawing is Godot's.
//
// Every address is the Fable 3 TU3 IDB ("disc" marks one TU3 lacks); each offset names its base.
//
// THE REGISTRIES. The HUD.bgf load sub_824CD3F8 runs the builder sub_8269EB40 (0x824CD450), which
// makes fourteen 20-byte registries (ctor sub_8269E1D8: map at +4, head at +8) into the holder's
// vector and stores each class's vtable: [1] meters 0x820A8240 (0x8269EF18), [4] pulsers
// 0x820A829C (0x8269EF6C), [6] texts 0x820A82CC (0x8269EFA4). Every writer below is handed one of
// them by `*(*(*(H+0x24)+0x18)+4*i)`, H = `*(GUI+0x20)`.
//   - Filing: each registry's slot 1, ONE shared body sub_82BCF250(registry, element, pres), makes
//     an element through the class's factory (slot 5, 0x82BCF2DC) and inserts it under the
//     attribute's FNV-1 through the map insert sub_82976060(registry, hash, element) (0x82BCF330): a new name gets a record (+4..+8 the element vector, 0x829760A8..0x82976160),
//     a known one appends (0x829761CC). The text boxes are inserted there directly too
//     (CHUDTextBox_p_ctor 0x824CF0B8), always into [7] (0x824CD494, 0x824D0988, 0x824CD73C).
//   - Unfiling: slot 2, ONE shared body sub_82BCF348(registry, element, pres), hashes the same
//     attribute and erases the NAME's map node (sub_82314140 at 0x82BCF430) — every element under
//     it — when the name is held. A screen leaving the HUD (sub_8269F208 -> sub_8269F270, slot 2
//     at 0x8269F2B0: Epilogue_Hide, the Natal unload, sub_825F0CC8 / sub_825F2E10) runs it.
//   - Destruction: the registries' destructor body sub_82975EE0(registry) deletes every element
//     (`vt[0](e, 1)` at 0x82975F44), frees the records and clears the map (slot 0 sub_8269E278
//     at 0x8269E294, CHUDScriptPositionManager_vf0).
//
// THE ELEMENTS (offsets from the element; the factories set +4 = the filed element, +8 = pres):
//   - text, CHUDText (sub_8269E760): +12 dirty, +16 the LHString shown, 0 at creation
//     (0x8269E7CC). Its one writer is sub_827681B8(registry, hash, LHString*): every element under
//     the name assigns +16 (sub_8226D528 at 0x827681F8) and sets +12 (0x82768200). All 45 callers
//     pass registry [6] — the subtitler's page writer sub_829763F8 among them (r21 =
//     `*(vec+0x18)` at 0x82976474, the call at 0x82976608), as do the TopBox publisher
//     sub_8217D7A0 and Epilogue_Show. A subtitle is therefore stated here, page by page: the
//     page writer lays the `Subtitles` text box (registry [7] under dword_83599C84, looked up by
//     sub_82976D48) into its line elements, each line a text-registry write under the line's own
//     name (0x82976600). Its queue (sub_8260CA90) and clear (sub_8260D228, the empty page through
//     sub_82976D48 at 0x8260D380) only feed that writer.
//   - meter (CHUDMeterManager_vf5 sub_8269E3B8): +12 mode, +16 last time, +20 value, +24 rate,
//     +28 target, all 0 at creation (0x8269E428..0x8269E444). The setters sub_82975CF8 (target
//     over seconds), sub_82975C30 (target), sub_82975E08 (target, now) and sub_82975AC8 (mode 1
//     from 0) write the target, rate and mode; the VALUE has one writer, sub_82975A58(meter, f1),
//     which applies it to the element (GuiElement_SetTime(+4, +8, value * 10) at 0x82975AA8) and
//     stores +20 (0x82975AAC). Its callers are the meter's update sub_8221C750 (0x8221C8A8, the
//     step toward the target or the mode-1 ping-pong) and sub_82975E08 (0x82975EB8).
//   - pulser (sub_8269E5F0): +12 mode 2 = stopped (0x8269E660), +20 phase and +24 rate 0.0
//     (0x8269E670 / 0x8269E674). Its update sub_82BCF448 advances nothing while mode is 2
//     (disc 0x82B9A340) and writes mode only as 1 -> 2 (disc 0x82B9A370). NOTHING ELSE WRITES IT: no code
//     reaches registry [4] but the holder's own iteration (filing, unfiling, destruction) — no
//     load of `*(vec+0x10)` follows `*(H+0x24)+0x18` anywhere in the image, the registry handed
//     to every registry writer and lookup is [1], [2], [3], [5], [6], [7], [9], [10] or [12] — and
//     the element's vtable 0x820DDF58 has no setter (slots 1-2 sub_828173B8, 4 sub_82F5D180). Every
//     pulser is stated at its creation, stopped at phase 0, and stays so.
//
// THE FADER `*(GUI+0x3C)` (built by sub_82600428: +0 progress 0, +4 seconds 0, +8..+11 from and
// +12..+15 to both byte_83599994.., +33 lock 0, 0x82600454..0x826004C4). Its offsets:
//   +0 progress t, +4 seconds, +8..+11 the colour it starts from and +12..+15 the target, four
//   bytes in B, G, R, A order (the lerp sub_82182C38, its draw sub_82181920), +33 the lock
//   (LockScreenFade 0x8254B8A0, UnlockScreenFade 0x8254B930, the net packet at disc 0x82373AA4 /
//   disc 0x82373AB0), +34 a level start pending, +80 hold while the game mode is 1 or 8 (its callers
//   store it just before they start a fade, e.g. disc 0x8251B714).
//   - THE ONE START, sub_82600658(fader, bgra*, f1 seconds, …, r7 entity): the public start
//     sub_826005D8 (every FadeScreen* native, the level and save-game modes, the actions) and the
//     net receive disc sub_825D5898 (packet 283) both end in it. Unless the entity is off screen or the
//     target is reached already (returns 0, disc 0x825D5848), it stores the current lerp as `from`
//     (0x826006F4..0x82600710), the target (0x82600718..0x82600738), the seconds (disc 0x825D5850) and
//     t = 1 below 0.0001 s (disc 0x825D5860) or 0 (0x826007CC), and returns 1. No other code writes
//     +0..+15 (scanning every `*(GUI+0x3C)` user: they write only +33, +34, +35 and +80).
//   - THE ADVANCE, sub_822852C8(fader), called once per HF step (disc 0x821CBD88, on both the game-tick
//     and the HF-only path): nothing while locked (disc 0x822870A0) or at the target (sub_82183498,
//     disc 0x822870B0), nothing while +80 holds in mode 1 or 8 (disc 0x82287130), one step of nothing while
//     +34 is pending (it clears it, disc 0x8228735C); else t += 1 / (dbl_832DF4D0 * seconds), clamped to
//     1 (0x822855D4..0x82285604), t = 1 for seconds <= 0 (0x82285614). Per second of HF steps
//     (dbl_832DF4D8 of them) that is dbl_832DF4D8 / (dbl_832DF4D0 * seconds).
//   The readers stay the guest's: sub_82183498 (fading), sub_82600558 (fully faded),
//   sub_82194588 (fully in) — the level change and save load block on them.
//
// THE SLIDE REGISTRIES. Four writers address a registry name and set every element filed under
// it, looking the name up through sub_8218D820(registry, hash) (the vector of element records,
// +4..+8; each record +4 the Gameface element, +8 its presentation) and applying at once — the
// element classes' update slot is a nullsub for these registries, so nothing is deferred:
//   - sub_829B4CA0(registry, hash, N): SetState(+4, +8, N) per element (0x829B4CE0). Its callers
//     hand it [10] SlideSetName (vtable 0x820A8314, store 0x8269F014) with N the slide verbatim —
//     the guild seal's digits sub_82620788, the d-pad counters (0x82603ADC), the TopBox (0x8217D890)
//     — or [3] SlideFader2Name (0x820A8284, store 0x8269EF50) with 2, 3 or 4.
//   - sub_829759E8(registry, hash, on): sub_82878198(+4, +8, on ? 2 : 1) (`li r5,2` 0x82975A24,
//     `li r5,1` 0x82975A34, the call 0x82975A38), always [9] SlideToggleName (0x820A82FC, store
//     0x8269EFF8): the TopBox's toggles, Counter%d, InfoBoxOpen, EpilogueItem1/2.
//   - sub_82B847F0(registry, hash, slide): sub_82878198(+4, +8, slide) (0x82B84830), always [2]
//     SlideFaderName (0x820A826C, store 0x8269EF34).
//   - [13] ScriptPositioned (0x820A835C): sub_82B84A10(registry, hash, float* xy, net) places each
//     element at (xy[0], xy[1]) keeping its own z (sub_82B84860: the first move saves the authored
//     position at record+16 and sets record+12, 0x82B8489C; SetPosition 0x82B84970), and
//     sub_82B84AF8(registry, hash, net) puts every moved element back (record+12 cleared 0x82B84BBC,
//     SetPosition to record+16 0x82B84BF0).
// Two writes reach those elements past the writers: the HUD update sub_82258E60(H, t), once per
// g_HUD:Enable() (it runs the Lua while H+9 is set, then clears it at 0x82258FBC), finds
// [9] under dword_83598118 ("ItemReceived") and sets each element 2 then 1 (0x82259028,
// 0x82259040) — it rests at 1; and the HUD reset sub_824CD8D0(H) (CmdLoadSaveGame_Update
// disc 0x826DD74C, CGameModeLoadLevel_vf6 disc 0x82722BFC) runs every element's reset slot (vt+8,
// 0x824CD95C): [2]'s goes to slide 2 (CHUDSlideFader_p_vf2 0x82B847E0), [3]'s to 1
// (0x829B4C90), the rest are nullsubs. Each is stated as the slide the element rests on.
// A name leaves when its registry unfiles an element filed under it (sub_82BCF348 erases the
// whole node, 0x82BCF430) or the registry is destroyed: the slides it held are stated gone.
//
// THE D-PAD. Its slide sub_82603B28(D, s) resolves `dpad` / `dpad2` (D+45) + `.dpad.dpadmenu` and
// sets state s (0x82603B94): 2 open (0x82602BD0), 1 closed (0x82602C58). Its slot writer
// sub_82603980(D, &texture, slot, count, flash) — every slot of every refresh sub_82602D80
// (0x8260323C) — sets the slot's icon `group.<dir>.<dir>` to the texture (0x82603A34; slot 0 up,
// 1 left, 2 down, 3 right: the paths dword_835999D0 / D48 / D3C / D54, built at 0x831EA220 /
// 0x831EA400 / 0x831EA310 / 0x831EA4F0), the count's decimal text on `group.<dir>.Counter.Counter`
// (empty at 0, 0x82603A64; committed 0x82603B10), DP<Dir>Counter's slide 1 / 2 / 3 (through
// sub_829B4CA0, 0x82603ADC), and for slot 0 alone `group.up.fx`'s opacity 100 flashing / 0
// (0x826039FC..0x82603A20).

#include <atomic>
#include <cstdint>
#include <cstring>
#include <iterator>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_HudRegistry_Insert);
REX_EXTERN(__imp__rex_HudRegistry_Unfile);
REX_EXTERN(__imp__rex_HudRegistry_Destroy);
REX_EXTERN(__imp__rex_HudText_Set);
REX_EXTERN(__imp__rex_HudMeter_Apply);
REX_EXTERN(__imp__rex_ScreenFade_Start);
REX_EXTERN(__imp__rex_ScreenFade_Advance);
REX_EXTERN(__imp__rex_HudSlides_SetState);
REX_EXTERN(__imp__rex_HudSlides_Toggle);
REX_EXTERN(__imp__rex_HudSlides_Fade);
REX_EXTERN(__imp__rex_HudPositions_Place);
REX_EXTERN(__imp__rex_HudPositions_Restore);
REX_EXTERN(__imp__rex_Hud_Update);
REX_EXTERN(__imp__rex_Hud_Reset);
REX_EXTERN(__imp__rex_HudDpad_Slide);
REX_EXTERN(__imp__rex_HudDpad_Slot);

namespace {

using refii::fable3::LoadGuestF32;
using refii::fable3::LoadGuestU32;
using refii::fable3::Plausible;
using refii::fable3::ReadCharString;
using refii::fable3::ReadLhString;

constexpr uint32_t kVtMeterRegistry = 0x820A8240u;  // classified by ToTu3
constexpr uint32_t kVtPulserRegistry = 0x820A829Cu;  // classified by ToTu3
constexpr uint32_t kVtTextRegistry = 0x820A82CCu;  // classified by ToTu3
constexpr uint32_t kPulserStopped = 2u;

constexpr uint32_t kFaderProgress = 0u;
constexpr uint32_t kFaderSeconds = 4u;
constexpr uint32_t kFaderFrom = 8u;
constexpr uint32_t kFaderTo = 12u;
constexpr uint32_t kFaderLock = 33u;
const refii::guest::Addr kGameTickRate{0x832DF4D0u};  // double, 15.0
const refii::guest::Addr kHfStepRate{0x832DF4D8u};    // double, 30.0

// Not inlined: the phone's Clang folded the two constant addresses of FadeRate's reads into a
// sign-extended 64-bit offset, so the second one read below the guest's memory and faulted.
[[gnu::noinline]] double LoadGuestF64(const uint8_t* base, uint32_t address) {
  const uint64_t bits = (static_cast<uint64_t>(LoadGuestU32(base, address)) << 32) | LoadGuestU32(base, address + 4u);
  double value = 0.0;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

/// Progress per second of a fade over `seconds`: dbl_832DF4D8 HF steps a second, each adding
/// 1 / (dbl_832DF4D0 * seconds) (0x822855D4..0x822855DC).
float FadeRate(const uint8_t* base, float seconds) {
  const double tick_rate = LoadGuestF64(base, kGameTickRate);
  const double step_rate = LoadGuestF64(base, kHfStepRate);
  return seconds > 0.0f && tick_rate > 0.0 ? static_cast<float>(step_rate / (tick_rate * seconds)) : 0.0f;
}

uint32_t KindOf(uint32_t vtable) {
  switch (refii::guest::ToTu3(vtable)) {
    case kVtMeterRegistry:
      return REFII_HUD_KIND_METER;
    case kVtPulserRegistry:
      return REFII_HUD_KIND_PULSER;
    case kVtTextRegistry:
      return REFII_HUD_KIND_TEXT;
    default:
      return 0u;
  }
}

const char* KindName(uint32_t kind) {
  switch (kind) {
    case REFII_HUD_KIND_METER:
      return "meter";
    case REFII_HUD_KIND_PULSER:
      return "pulser";
    default:
      return "text";
  }
}

// The slide registries, by vtable.
constexpr uint32_t kVtSlideFaderRegistry = 0x820A826Cu;  // classified by ToTu3
constexpr uint32_t kVtSlideFader2Registry = 0x820A8284u;  // classified by ToTu3
constexpr uint32_t kVtSlideToggleRegistry = 0x820A82FCu;  // classified by ToTu3
constexpr uint32_t kVtSlideSetRegistry = 0x820A8314u;  // classified by ToTu3
constexpr uint32_t kVtScriptPositionedRegistry = 0x820A835Cu;  // classified by ToTu3

/// The HUD object's g_HUD:Enable() pending flag (H+9, cleared 0x82258FBC) and the name the enable
/// restates (dword_83598118, "ItemReceived", loaded at 0x82258FC8).
constexpr uint32_t kHudEnablePending = 9u;
const refii::guest::Addr kItemReceivedName{0x83598118u};
constexpr uint32_t kToggleOff = 1u;
/// The resets' rest slides (0x82B847E4, disc 0x829646A4).
constexpr uint32_t kFaderReset = 2u;
constexpr uint32_t kFader2Reset = 1u;

/// D+45: the second player's d-pad (`dpad2`, 0x82603B34).
constexpr uint32_t kDpadSecond = 45u;

/// The .bgf attribute each slide registry files by — the spelling the host binds names with.
const char* SlideAttribute(uint32_t vtable) {
  switch (refii::guest::ToTu3(vtable)) {
    case kVtSlideFaderRegistry: return "SlideFaderName";
    case kVtSlideFader2Registry: return "SlideFader2Name";
    case kVtSlideToggleRegistry: return "SlideToggleName";
    case kVtSlideSetRegistry: return "SlideSetName";
    case kVtScriptPositionedRegistry: return "ScriptPositioned";
    default: return nullptr;
  }
}

/// Per (attribute, name): the slide last stated, so a writer restating it posts nothing; and per
/// (registry, filed element) the name it is under, for the unfiling.
std::mutex g_slides_mutex;
std::unordered_map<std::string, uint32_t>* g_slides = nullptr;
std::unordered_map<uint64_t, uint32_t>* g_slide_names = nullptr;

std::unordered_map<std::string, uint32_t>& Slides() {
  if (g_slides == nullptr) g_slides = new std::unordered_map<std::string, uint32_t>();
  return *g_slides;
}

std::unordered_map<uint64_t, uint32_t>& SlideNames() {
  if (g_slide_names == nullptr) g_slide_names = new std::unordered_map<uint64_t, uint32_t>();
  return *g_slide_names;
}

std::string SlideKey(const char* attribute, uint32_t hash) { return std::string(attribute) + ":" + refii::fable3::Hex(hash); }

/// `@Hud.Slide(attribute, name hash, slide)` when it changed.
void StateSlide(const char* attribute, uint32_t hash, uint32_t slide) {
  {
    std::lock_guard<std::mutex> lock(g_slides_mutex);
    auto [it, fresh] = Slides().try_emplace(SlideKey(attribute, hash), slide);
    if (!fresh && it->second == slide) {
      return;
    }
    it->second = slide;
  }
  refii::fable3::Statement("@Hud.Slide")
      .Add(refii::fable3::Text(attribute))
      .Add(refii::fable3::Text(refii::fable3::Hex(hash)))
      .Add(refii::fable3::Number(static_cast<float>(slide)))
      .Post();
}

/// Every name of `attribute` rests on `slide` (the reset): the host sets every bound element.
void StateSlideReset(const char* attribute, uint32_t slide) {
  {
    std::lock_guard<std::mutex> lock(g_slides_mutex);
    const std::string prefix = std::string(attribute) + ":";
    for (auto it = Slides().begin(); it != Slides().end();) {
      it = it->first.compare(0, prefix.size(), prefix) == 0 ? Slides().erase(it) : std::next(it);
    }
  }
  refii::fable3::Statement("@Hud.SlideReset")
      .Add(refii::fable3::Text(attribute))
      .Add(refii::fable3::Number(static_cast<float>(slide)))
      .Post();
}

const char* SlideAttributeOf(const uint8_t* base, uint32_t registry) {
  return base != nullptr && Plausible(registry) ? SlideAttribute(LoadGuestU32(base, registry)) : nullptr;
}

std::atomic<uint32_t> g_filed[8]{};
std::atomic<uint32_t> g_texts_stated{0u};
std::atomic<int32_t> g_fade_lock{-1};
std::atomic<uint32_t> g_fader{0u};

const refii::guest::Addr kRegistryLookup{0x8218D820u};  // (registry, hash) -> record or 0
constexpr uint32_t kRecordBegin = 4u;
constexpr uint32_t kRecordEnd = 8u;
constexpr uint32_t kTextString = 16u;

PPCFunc* FindGuest(uint32_t address) {
  for (PPCFuncMapping* mapping = PPCFuncMappings; mapping != nullptr && mapping->host != nullptr; ++mapping) {
    if (static_cast<uint32_t>(mapping->guest) == address) {
      return mapping->host;
    }
  }
  return nullptr;
}

/// The first element filed under `hash` in the text registry, by the guest's own lookup, or 0.
uint32_t FirstTextElement(const PPCContext& from, uint8_t* base, uint32_t registry, uint32_t hash) {
  static PPCFunc* const lookup = FindGuest(kRegistryLookup);
  if (lookup == nullptr || base == nullptr || !Plausible(registry)) {
    return 0u;
  }
  PPCContext call = from;
  call.r3.u32 = registry;
  call.r4.u32 = hash;
  call.ctr.u32 = kRegistryLookup;
  lookup(call, base);
  const uint32_t record = call.r3.u32;
  if (!Plausible(record)) {
    return 0u;
  }
  const uint32_t first = LoadGuestU32(base, record + kRecordBegin);
  if (!Plausible(first) || first >= LoadGuestU32(base, record + kRecordEnd)) {
    return 0u;
  }
  const uint32_t element = LoadGuestU32(base, first);
  return Plausible(element) ? element : 0u;
}

}  // namespace

// (registry, hash, element): the registry map's insert.
REX_EXTERN(rex_HudRegistry_Insert) {
  const uint32_t registry = ctx.r3.u32;
  const uint32_t hash = ctx.r4.u32;
  const uint32_t element = ctx.r5.u32;
  const uint32_t kind = Plausible(registry) ? KindOf(LoadGuestU32(base, registry)) : 0u;
  const uint32_t source = refii::fable3::FilingElement();
  const char* slides = SlideAttributeOf(base, registry);
  __imp__rex_HudRegistry_Insert(ctx, base);
  if (slides != nullptr && source != 0u) {
    std::lock_guard<std::mutex> lock(g_slides_mutex);
    SlideNames()[(static_cast<uint64_t>(registry) << 32) | source] = hash;
  }
  if (kind == 0u || !Plausible(element)) {
    return;
  }
  refii_bridge_hud_element_filed(kind, registry, hash, element, source, 0.0f,
                                 kind == REFII_HUD_KIND_PULSER ? kPulserStopped : 0u);
  const uint32_t filed = g_filed[kind & 7u].fetch_add(1u) + 1u;
  if (filed == 1u || (filed & 63u) == 0u) {
    REXLOG_INFO("Refii HUD registries: {} {} filed (registry {:08X}, latest name {:08X})", filed, KindName(kind),
                registry, hash);
  }
}

// (registry, element, pres): the registry erases the name `element` is filed under.
REX_EXTERN(rex_HudRegistry_Unfile) {
  const uint32_t registry = ctx.r3.u32;
  const uint32_t element = ctx.r4.u32;
  const char* slides = SlideAttributeOf(base, registry);
  __imp__rex_HudRegistry_Unfile(ctx, base);
  refii_bridge_hud_source_unfiled(registry, element);
  if (slides == nullptr) {
    return;
  }
  uint32_t hash = 0u;
  {
    std::lock_guard<std::mutex> lock(g_slides_mutex);
    auto it = SlideNames().find((static_cast<uint64_t>(registry) << 32) | element);
    if (it == SlideNames().end()) {
      return;
    }
    hash = it->second;
    SlideNames().erase(it);
    Slides().erase(SlideKey(slides, hash));
  }
  refii::fable3::Statement("@Hud.Unfiled").Add(refii::fable3::Text(slides)).Add(refii::fable3::Text(refii::fable3::Hex(hash))).Post();
}

// (registry): the registries' destructor body.
REX_EXTERN(rex_HudRegistry_Destroy) {
  const uint32_t registry = ctx.r3.u32;
  const uint32_t kind = Plausible(registry) ? KindOf(LoadGuestU32(base, registry)) : 0u;
  refii_bridge_hud_registry_destroyed(registry);
  if (const char* slides = SlideAttributeOf(base, registry); slides != nullptr) {
    {
      std::lock_guard<std::mutex> lock(g_slides_mutex);
      const std::string prefix = std::string(slides) + ":";
      for (auto it = Slides().begin(); it != Slides().end();) {
        it = it->first.compare(0, prefix.size(), prefix) == 0 ? Slides().erase(it) : std::next(it);
      }
      for (auto it = SlideNames().begin(); it != SlideNames().end();) {
        it = static_cast<uint32_t>(it->first >> 32) == registry ? SlideNames().erase(it) : std::next(it);
      }
    }
    refii::fable3::Statement("@Hud.Destroyed").Add(refii::fable3::Text(slides)).Post();
  }
  if (kind != 0u) {
    REXLOG_INFO("Refii HUD registries: {} registry {:08X} destroyed", KindName(kind), registry);
  }
  __imp__rex_HudRegistry_Destroy(ctx, base);
}

// (registry, hash, LHString*): every text element under the name shows the string.
REX_EXTERN(rex_HudText_Set) {
  const uint32_t registry = ctx.r3.u32;
  const uint32_t hash = ctx.r4.u32;
  const uint32_t text = ctx.r5.u32;
  const PPCContext entry = ctx;
  __imp__rex_HudText_Set(ctx, base);
  // The string the element HOLDS now, read off the first element the write reached: the writer
  // looks the name up with `sub_8218D820`(registry, hash) (0x827681C8) and assigns each element's
  // +16 from the argument (`addi r3,r30,0x10` / `bl sub_8226D528` 0x827681F4..0x827681F8). The
  // argument itself is read only when the name holds no element.
  std::string value;
  if (const uint32_t held = FirstTextElement(entry, base, registry, hash); held != 0u) {
    value = ReadLhString(base, held + kTextString);
  } else {
    value = ReadLhString(base, text);
  }
  refii_bridge_hud_text_stated(registry, hash, value.data(), static_cast<uint32_t>(value.size()));
  if (g_texts_stated.fetch_add(1u) == 0u) {
    REXLOG_INFO("Refii HUD registries: first text stated (name {:08X}): '{}'", hash, value);
  }
}

// (meter, f1 value): the value applied to the meter's element.
REX_EXTERN(rex_HudMeter_Apply) {
  const uint32_t meter = ctx.r3.u32;
  const float value = static_cast<float>(ctx.f1.f64);
  __imp__rex_HudMeter_Apply(ctx, base);
  refii_bridge_hud_value_stated(meter, value);
}

// (fader, bgra*, f1 seconds, …): the fade's one start; returns 1 when it started a fade. A start to the
// colour the fader already holds writes from/to and returns 0: TU3 snaps it complete (`stfs` t = 1.0
// 0x82600784, `li r3,0` 0x82600788), the disc leaves it when already complete (disc 0x825D5834).
// Either way the fader then holds the target, complete, and that is stated too.
static bool FaderHolds(const uint8_t* base, uint32_t fader, const uint8_t* to) {
  return std::memcmp(base + fader + kFaderTo, to, 4u) == 0 && std::memcmp(base + fader + kFaderFrom, to, 4u) == 0 &&
         LoadGuestF32(base, fader + kFaderProgress) >= 1.0f;
}

REX_EXTERN(rex_ScreenFade_Start) {
  const uint32_t fader = ctx.r3.u32;
  const uint32_t target = ctx.r4.u32;
  const float seconds = static_cast<float>(ctx.f1.f64);
  uint8_t to[4]{};
  if (Plausible(target)) {
    std::memcpy(to, base + target, sizeof(to));
  }
  __imp__rex_ScreenFade_Start(ctx, base);
  if (!Plausible(fader) || (ctx.r3.u32 == 0u && !FaderHolds(base, fader, to))) {
    return;
  }
  uint8_t from[4]{};
  std::memcpy(from, base + fader + kFaderFrom, sizeof(from));
  const float progress = LoadGuestF32(base, fader + kFaderProgress);
  const float rate = FadeRate(base, seconds);
  refii_bridge_screen_fade_started(fader, from, to, progress, rate);
  g_fader.store(fader);
  REXLOG_INFO("Refii screen fade: start B,G,R,A {},{},{},{} -> {},{},{},{} over {:.2f} s (t {:.0f}, {:.3f}/s)", from[0],
              from[1], from[2], from[3], to[0], to[1], to[2], to[3], seconds, progress, rate);
}

// (fader): the fade's advance, once per HF step.
REX_EXTERN(rex_ScreenFade_Advance) {
  const uint32_t fader = ctx.r3.u32;
  if (!Plausible(fader)) {
    __imp__rex_ScreenFade_Advance(ctx, base);
    return;
  }
  const int32_t locked = base[fader + kFaderLock] != 0u ? 1 : 0;
  const float before = LoadGuestF32(base, fader + kFaderProgress);
  if (g_fader.exchange(fader) != fader) {
    // A fader the table has not seen: its state as built (sub_82600428, from = to) or as its last
    // start left it, held.
    uint8_t from[4]{};
    uint8_t to[4]{};
    std::memcpy(from, base + fader + kFaderFrom, sizeof(from));
    std::memcpy(to, base + fader + kFaderTo, sizeof(to));
    refii_bridge_screen_fade_started(fader, from, to, before, FadeRate(base, LoadGuestF32(base, fader + kFaderSeconds)));
    REXLOG_INFO("Refii screen fade: fader {:08X} at t {:.2f}", fader, before);
  }
  __imp__rex_ScreenFade_Advance(ctx, base);
  const float after = LoadGuestF32(base, fader + kFaderProgress);
  refii_bridge_screen_fade_stepped(fader, after, after != before ? 1 : 0, locked);
  if (g_fade_lock.exchange(locked) != locked) {
    REXLOG_INFO("Refii screen fade: {} at t {:.2f}", locked != 0 ? "locked" : "unlocked", after);
  }
  if (before < 1.0f && after >= 1.0f) {
    REXLOG_INFO("Refii screen fade: complete");
  }
}

// (registry, hash, N): [10] SlideSetName's slide or [3] SlideFader2Name's.
REX_EXTERN(rex_HudSlides_SetState) {
  const char* attribute = SlideAttributeOf(base, ctx.r3.u32);
  const uint32_t hash = ctx.r4.u32;
  const uint32_t slide = ctx.r5.u32;
  __imp__rex_HudSlides_SetState(ctx, base);
  if (attribute != nullptr) {
    StateSlide(attribute, hash, slide);
  }
}

// (registry, hash, on): [9] SlideToggleName, slide 2 on / 1 off.
REX_EXTERN(rex_HudSlides_Toggle) {
  const char* attribute = SlideAttributeOf(base, ctx.r3.u32);
  const uint32_t hash = ctx.r4.u32;
  const uint32_t slide = (ctx.r5.u32 & 0xFFu) != 0u ? 2u : 1u;
  __imp__rex_HudSlides_Toggle(ctx, base);
  if (attribute != nullptr) {
    StateSlide(attribute, hash, slide);
  }
}

// (registry, hash, slide): [2] SlideFaderName.
REX_EXTERN(rex_HudSlides_Fade) {
  const char* attribute = SlideAttributeOf(base, ctx.r3.u32);
  const uint32_t hash = ctx.r4.u32;
  const uint32_t slide = ctx.r5.u32;
  __imp__rex_HudSlides_Fade(ctx, base);
  if (attribute != nullptr) {
    StateSlide(attribute, hash, slide);
  }
}

// (registry, hash, float* xy, net): `@Hud.Place(name, x, y)` — every ScriptPositioned element under
// the name at (x, y), its own z kept.
REX_EXTERN(rex_HudPositions_Place) {
  const char* attribute = SlideAttributeOf(base, ctx.r3.u32);
  const uint32_t hash = ctx.r4.u32;
  const uint32_t xy = ctx.r5.u32;
  const bool readable = attribute != nullptr && Plausible(xy);
  const float x = readable ? LoadGuestF32(base, xy) : 0.0f;
  const float y = readable ? LoadGuestF32(base, xy + 4u) : 0.0f;
  __imp__rex_HudPositions_Place(ctx, base);
  if (readable) {
    refii::fable3::Statement("@Hud.Place")
        .Add(refii::fable3::Text(refii::fable3::Hex(hash)))
        .Add(refii::fable3::Number(x))
        .Add(refii::fable3::Number(y))
        .Post();
  }
}

// (registry, hash, net): `@Hud.Unplace(name)` — every moved element back at its authored place.
REX_EXTERN(rex_HudPositions_Restore) {
  const char* attribute = SlideAttributeOf(base, ctx.r3.u32);
  const uint32_t hash = ctx.r4.u32;
  __imp__rex_HudPositions_Restore(ctx, base);
  if (attribute != nullptr) {
    refii::fable3::Statement("@Hud.Unplace").Add(refii::fable3::Text(refii::fable3::Hex(hash))).Post();
  }
}

// (H, f1 t): the HUD update; the g_HUD:Enable() it runs while H+9 is set rests ItemReceived at 1.
REX_EXTERN(rex_Hud_Update) {
  const uint32_t hud = ctx.r3.u32;
  const bool pending = base != nullptr && Plausible(hud) && base[hud + kHudEnablePending] != 0u;
  __imp__rex_Hud_Update(ctx, base);
  if (pending && base[hud + kHudEnablePending] == 0u) {
    StateSlide("SlideToggleName", LoadGuestU32(base, kItemReceivedName), kToggleOff);
  }
}

// (H): the HUD reset — every SlideFaderName element to 2, every SlideFader2Name element to 1.
REX_EXTERN(rex_Hud_Reset) {
  __imp__rex_Hud_Reset(ctx, base);
  StateSlideReset("SlideFaderName", kFaderReset);
  StateSlideReset("SlideFader2Name", kFader2Reset);
}

// (D, s): `@Hud.Dpad.Slide(player, s)`.
REX_EXTERN(rex_HudDpad_Slide) {
  const uint32_t dpad = ctx.r3.u32;
  const uint32_t slide = ctx.r4.u32;
  const uint32_t player = base != nullptr && Plausible(dpad) && base[dpad + kDpadSecond] != 0u ? 1u : 0u;
  __imp__rex_HudDpad_Slide(ctx, base);
  refii::fable3::Statement("@Hud.Dpad.Slide")
      .Add(refii::fable3::Number(static_cast<float>(player)))
      .Add(refii::fable3::Number(static_cast<float>(slide)))
      .Post();
}

// (D, &texture, slot, count, flash): `@Hud.Dpad.Slot(player, slot, texture, count, flash)`.
REX_EXTERN(rex_HudDpad_Slot) {
  const uint32_t dpad = ctx.r3.u32;
  const uint32_t slot = ctx.r5.u32;
  const int32_t count = static_cast<int32_t>(ctx.r6.u32);
  const bool flash = (ctx.r7.u32 & 0xFFu) != 0u;
  const bool readable = base != nullptr && Plausible(dpad);
  const uint32_t player = readable && base[dpad + kDpadSecond] != 0u ? 1u : 0u;
  const std::string texture = readable ? ReadCharString(base, ctx.r4.u32, 260u) : std::string();
  __imp__rex_HudDpad_Slot(ctx, base);
  if (!readable || slot > 3u) {
    return;
  }
  refii::fable3::Statement("@Hud.Dpad.Slot")
      .Add(refii::fable3::Number(static_cast<float>(player)))
      .Add(refii::fable3::Number(static_cast<float>(slot)))
      .Add(refii::fable3::Text(texture))
      .Add(refii::fable3::Number(static_cast<float>(count)))
      .Add(refii::fable3::Boolean(flash))
      .Post();
}
