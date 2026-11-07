// refii - Fable 3's options: the values the running game is given, stated at the appliers that
// take them and at the player's saved settings' read and write, into the table the host bridge's
// options feed reads (core/payload_bridge.h, src/runtime/src/core/stated_options.cpp); and the
// Options screen, whose pad Godot owns — Godot's actions run through the screen's own handler,
// whose state is stated as it returns.
//
// Every address is the Fable 3 IDB; each offset names its base.
//
// THE APPLIERS (each `r3` the audio object *0x835569DC):
//   - sfx sub_82D1D440(A, f1): clamps to 0..1 (0x82D1D48C) and stores A+88 (0x82D1D494);
//   - voice sub_82D1D4E8(A, f1): clamps (0x82D1D520..0x82D1D538), stores A+92 (0x82D1D53C);
//   - music sub_82D1D668(A, f1): clamps (0x82D1D6A0..0x82D1D6B8), stores A+100 (0x82D1D6BC);
//   - speaker sub_82D1D710(A, r4 mode): 0 TV, 1 HIFI, 2 SURROUND, stored A+104 (0x82D1D7BC).
//   Their callers: the Options screen's live step and commit, its revert, the sign-in
//   sub_825B5778, the boot sub_82312520 and the SoundTools natives.
//   - gamma has no applier that takes a value: sub_825CD1F0(box) applies the Options screen's own
//     +128 (`lfs` at 0x825CD210; stores at 0x825CD264 / 0x825CD274), the only field it reads; the
//     profile's is applied from sub_822F27B0(user)'s answer (f1) by the display object's ctor
//     sub_824D1BB0 (0x824D1C20), the sign-in sub_825B5778 (0x825B5888, `fmr f31,f1` at
//     0x825B5894) and the revert sub_825CD2F8 (0x825CD310); the Options screen's fill
//     sub_825CD0E0 hands it to the screen instead (0x825CD150, returning at 0x825CD154).
// THE SAVED SETTINGS, title setting 0x63E83FFF, 92 bytes: read by sub_822F4058(user, out)
// (answers 1 and copies the blob at 0x822F40D0), written by sub_822F2A50(user, r4 subtitles,
// f1 gamma, r6 &{sfx, music, voice, speaker}) (`mr r30,r4` / `fmr f31,f1` / `mr r29,r6` at
// 0x822F2A64..0x822F2A70), which lays them at blob +8 (0x822F2AE4), +4 (0x822F2AF8), +12, +16,
// +20, +24 (0x822F2B4C..0x822F2B64).
//
// THE OPTIONS SCREEN (COptionsMenuBox, vtable 0x8209D230, ctor sub_825C95F0, opened by
// sub_825C8630): its pad handler sub_825CA410(box, ev*, player) (slot 4, reached only through
// the box manager's router sub_825C8B68) takes 29/30 up/down (sub_825CA980 / sub_825CA858,
// 0x825CA6BC / 0x825CA710), 31/32 left/right (sub_825CB028 / sub_825CAA98, 0x825CA72C /
// 0x825CA740), 74 A (sub_825CB588, 0x825CA848), 75 B (sub_825CBAA0, 0x825CA810), 77 Y (the
// defaults, 0x825CA77C..0x825CA7E0), and 74/75 answer its confirm query while +30 is set
// (0x825CA5AC..0x825CA664). The steps write the screen's own fields and re-run its row writer,
// which applies gamma and the volumes live (sub_825CD1F0, sub_825CCEF8 -> the four appliers);
// A on a settings page commits through the appliers and the save sub_822F2A50 (sub_825CD3F8).
// The screen's state, the fields its handler and Show wrote: +80 page (1 Options, 2 Save
// Management, 3 Game Settings, 4 the front end's, 5 Multiplayer), +84/+88/+92/+96 the selected
// row of pages 1 and 5 / 2 / 3 / 4, +31 invert aim, +32 subtitles, +33 tutorial, +104
// breadcrumb 0..4, +100 speaker, +64 language index, +60 language count, +108/+112 the
// multiplayer rows' flags, +34 opened from the pause, +37 the front end's variant, +36 save
// allowed, +30 the query up, +38 changed since open.

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "core/payload_bridge.h"
#include "gui_statements.h"

REX_EXTERN(__imp__rex_Audio_SetSfxVolume);
REX_EXTERN(__imp__rex_Audio_SetVoiceVolume);
REX_EXTERN(__imp__rex_Audio_SetMusicVolume);
REX_EXTERN(__imp__rex_Audio_SetSpeakerMode);
REX_EXTERN(__imp__rex_Options_ApplyGamma);
REX_EXTERN(__imp__rex_Profile_ReadGamma);
REX_EXTERN(__imp__rex_Profile_ReadSettings);
REX_EXTERN(__imp__rex_Profile_WriteSettings);
REX_EXTERN(__imp__rex_OptionsBox_Show);
REX_EXTERN(__imp__rex_OptionsBox_HandleInput);
REX_EXTERN(rex_OptionsBox_HandleInput);

namespace {

using namespace refii::fable3;

/// The Options screen's fill hands the profile's gamma to the screen, not to the game.
constexpr uint32_t kGammaToScreenReturn = 0x825CD154u;

constexpr uint32_t kBlobGamma = 4u;
constexpr uint32_t kBlobSubtitles = 8u;
constexpr uint32_t kBlobSfx = 12u;
constexpr uint32_t kBlobMusic = 16u;
constexpr uint32_t kBlobVoice = 20u;
constexpr uint32_t kBlobSpeaker = 24u;

constexpr uint32_t kScreenGamma = 128u;

/// The actions the screen's handler takes (see the header).
constexpr long kActionUp = 29;
constexpr long kActionRight = 32;
constexpr long kActionA = 74;
constexpr long kActionB = 75;
constexpr long kActionY = 77;

float Clamp01(double value) { return value < 0.0 ? 0.0f : value > 1.0 ? 1.0f : static_cast<float>(value); }

uint32_t g_screen = 0u;         // the Options screen its Show last stated
uint32_t g_screen_player = 0u;  // the player the router last handed its handler
std::mutex g_mutex;
std::string g_last_state;

/// `@Options.State(box, page, row, invert, subtitles, tutorial, breadcrumb, speaker, language,
/// languages, flag108, flag112, fromPause, frontEnd, canSave, query, changed)` — when it differs
/// from the last.
void StateScreen(const uint8_t* base, uint32_t box) {
  if (base == nullptr || !Plausible(box)) {
    return;
  }
  const uint32_t page = LoadGuestU32(base, box + 80u);
  const uint32_t row_at = page == 2u ? 88u : page == 3u ? 92u : page == 4u ? 96u : 84u;
  const uint32_t words[] = {
      page,
      LoadGuestU32(base, box + row_at),
      base[box + 31u],
      base[box + 32u],
      base[box + 33u],
      LoadGuestU32(base, box + 104u),
      LoadGuestU32(base, box + 100u),
      LoadGuestU32(base, box + 64u),
      LoadGuestU32(base, box + 60u),
      LoadGuestU32(base, box + 108u),
      LoadGuestU32(base, box + 112u),
      base[box + 34u],
      base[box + 37u],
      base[box + 36u],
      base[box + 30u],
      base[box + 38u],
  };
  std::string key = Hex(box);
  for (uint32_t word : words) {
    key += ':' + std::to_string(word);
  }
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (key == g_last_state) {
      return;
    }
    g_last_state = key;
  }
  Statement state("@Options.State");
  state.Add(Text(Hex(box)));
  for (uint32_t word : words) {
    state.Add(Number(static_cast<float>(static_cast<int32_t>(word))));
  }
  state.Post();
}

thread_local bool t_host_action = false;

PPCContext Frame(const PPCContext& ctx, uint32_t bytes = 0x200u) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - bytes) & ~0xFu;
  return work;
}

void StoreGuestU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t swapped = __builtin_bswap32(value);
  std::memcpy(base + address, &swapped, sizeof(swapped));
}

}  // namespace

namespace refii::fable3 {

bool AnswerOptionsAction(PPCContext& ctx, uint8_t* base, const char* action) {
  const uint32_t box = g_screen;
  if (base == nullptr || box == 0u || !BoxIsShowing(box)) {
    REXLOG_WARN("[fable3-options] '{}' with no Options screen showing; dropped", action);
    return false;
  }
  const long id = std::strtol(action + sizeof(kOptionsAction) - 1u, nullptr, 10);
  if (!((id >= kActionUp && id <= kActionRight) || id == kActionA || id == kActionB || id == kActionY)) {
    return false;
  }
  PPCContext work = Frame(ctx);
  const uint32_t event = work.r1.u32 + 0x100u;
  StoreGuestU32(base, event, static_cast<uint32_t>(id));
  StoreGuestU32(base, event + 4u, 0u);
  StoreGuestU32(base, event + 8u, 0u);
  StoreGuestU32(base, event + 12u, 0u);
  work.r3.u64 = box;
  work.r4.u64 = event;
  work.r5.u64 = g_screen_player;
  t_host_action = true;
  rex_OptionsBox_HandleInput(work, base);
  t_host_action = false;
  return true;
}

}  // namespace refii::fable3

// (A, f1).
REX_EXTERN(rex_Audio_SetSfxVolume) {
  const float value = Clamp01(ctx.f1.f64);
  __imp__rex_Audio_SetSfxVolume(ctx, base);
  refii_bridge_option_applied(REFII_OPTION_SFX, value);
}

// (A, f1).
REX_EXTERN(rex_Audio_SetVoiceVolume) {
  const float value = Clamp01(ctx.f1.f64);
  __imp__rex_Audio_SetVoiceVolume(ctx, base);
  refii_bridge_option_applied(REFII_OPTION_VOICE, value);
}

// (A, f1).
REX_EXTERN(rex_Audio_SetMusicVolume) {
  const float value = Clamp01(ctx.f1.f64);
  __imp__rex_Audio_SetMusicVolume(ctx, base);
  refii_bridge_option_applied(REFII_OPTION_MUSIC, value);
}

// (A, mode).
REX_EXTERN(rex_Audio_SetSpeakerMode) {
  const uint32_t mode = ctx.r4.u32;
  __imp__rex_Audio_SetSpeakerMode(ctx, base);
  refii_bridge_option_applied(REFII_OPTION_SPEAKER, static_cast<float>(mode));
}

// (screen): the applier's one input is the screen's +128.
REX_EXTERN(rex_Options_ApplyGamma) {
  const uint32_t screen = ctx.r3.u32;
  const bool readable = base != nullptr && Plausible(screen);
  const float gamma = readable ? LoadGuestF32(base, screen + kScreenGamma) : 0.0f;
  __imp__rex_Options_ApplyGamma(ctx, base);
  if (readable) {
    refii_bridge_option_applied(REFII_OPTION_GAMMA, gamma);
  }
}

// (user) -> f1: the profile's gamma, applied by every caller but the Options screen's fill.
REX_EXTERN(rex_Profile_ReadGamma) {
  const uint32_t caller = static_cast<uint32_t>(ctx.lr);
  __imp__rex_Profile_ReadGamma(ctx, base);
  const float gamma = static_cast<float>(ctx.f1.f64);
  refii_bridge_option_profiled(REFII_OPTION_GAMMA, gamma);
  if (caller != kGammaToScreenReturn) {
    refii_bridge_option_applied(REFII_OPTION_GAMMA, gamma);
  }
}

// (user, out) -> 1 with the blob copied.
REX_EXTERN(rex_Profile_ReadSettings) {
  const uint32_t out = ctx.r4.u32;
  __imp__rex_Profile_ReadSettings(ctx, base);
  if (base == nullptr || ctx.r3.u32 == 0u || !Plausible(out)) {
    return;
  }
  refii_bridge_option_profiled(REFII_OPTION_GAMMA, LoadGuestF32(base, out + kBlobGamma));
  refii_bridge_option_profiled(REFII_OPTION_SUBTITLES, base[out + kBlobSubtitles] != 0u ? 1.0f : 0.0f);
  refii_bridge_option_profiled(REFII_OPTION_SFX, LoadGuestF32(base, out + kBlobSfx));
  refii_bridge_option_profiled(REFII_OPTION_MUSIC, LoadGuestF32(base, out + kBlobMusic));
  refii_bridge_option_profiled(REFII_OPTION_VOICE, LoadGuestF32(base, out + kBlobVoice));
  refii_bridge_option_profiled(REFII_OPTION_SPEAKER, static_cast<float>(LoadGuestU32(base, out + kBlobSpeaker)));
}

// (user, subtitles, f1 gamma, r6 &{sfx, music, voice, speaker}).
REX_EXTERN(rex_Profile_WriteSettings) {
  const bool subtitles = (ctx.r4.u32 & 0xFFu) != 0u;
  const float gamma = static_cast<float>(ctx.f1.f64);
  const uint32_t levels = ctx.r6.u32;
  if (base != nullptr && Plausible(levels)) {
    refii_bridge_option_profiled(REFII_OPTION_SUBTITLES, subtitles ? 1.0f : 0.0f);
    refii_bridge_option_profiled(REFII_OPTION_GAMMA, gamma);
    refii_bridge_option_profiled(REFII_OPTION_SFX, LoadGuestF32(base, levels));
    refii_bridge_option_profiled(REFII_OPTION_MUSIC, LoadGuestF32(base, levels + 4u));
    refii_bridge_option_profiled(REFII_OPTION_VOICE, LoadGuestF32(base, levels + 8u));
    refii_bridge_option_profiled(REFII_OPTION_SPEAKER, static_cast<float>(LoadGuestU32(base, levels + 12u)));
  }
  __imp__rex_Profile_WriteSettings(ctx, base);
}

// (screen).
REX_EXTERN(rex_OptionsBox_Show) {
  const uint32_t screen = ctx.r3.u32;
  __imp__rex_OptionsBox_Show(ctx, base);
  g_screen = screen;
  g_screen_player = 0u;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_last_state.clear();
  }
  StateScreen(base, screen);
}

/// (screen, ev*, player): Godot owns the showing screen's pad; its actions run here, the
/// screen's state stated after each.
REX_EXTERN(rex_OptionsBox_HandleInput) {
  const uint32_t screen = ctx.r3.u32;
  if (!t_host_action && screen == g_screen && BoxIsShowing(screen)) {
    g_screen_player = ctx.r5.u32;
    ctx.r3.u64 = 1u;
    return;
  }
  __imp__rex_OptionsBox_HandleInput(ctx, base);
  if (screen == g_screen) {
    StateScreen(base, screen);
  }
}
