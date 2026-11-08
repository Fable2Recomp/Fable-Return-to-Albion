// refii - Fable 3's options as the guest states them: the levels its appliers were given and its
// saved settings carry (Fable3/src/hooks/src/options_hooks.cpp, into core/payload_bridge.h's
// options table), and the installed languages (the culture table game_state_hooks.cpp states).
// This feed reads only those tables; the Options screen's own state reaches Godot as the
// `@Options.State` statement its handler's hook posts.
//
// The defaults before anything is stated are the Options screen's own: gamma 0.5 (disc 0x825C9660),
// music 1.0, sfx 1.0, voice 0.9 (disc 0x825C9670..84), speaker 2 (disc 0x825C96F8).

#include <cstdint>

#include "core/payload_bridge.h"
#include "host_abi.h"

namespace {

constexpr float kDefaultGamma = 0.5f;
constexpr float kDefaultMusic = 1.0f;
constexpr float kDefaultSfx = 1.0f;
constexpr float kDefaultVoice = 0.9f;
constexpr uint32_t kDefaultSpeaker = 2u;

bool Unit(float value) { return value >= 0.0f && value <= 1.0f; }

}  // namespace

extern "C" {

/// The levels the game runs with: each option's last applied value, else the saved settings',
/// else the screen's default. Returns 1 once any is stated.
REFII_HOST_ABI_EXPORT int RefiiHostGetOptionsSliders(float* out_gamma, float* out_sfx, float* out_music,
                                                     float* out_speech, uint32_t* out_speaker) {
    float values[REFII_OPTION_COUNT] = {};
    const uint32_t stated = refii_bridge_options_read(values, REFII_OPTION_COUNT);
    auto pick = [&](uint32_t option, float fallback) {
        return (stated & (1u << option)) != 0u && Unit(values[option]) ? values[option] : fallback;
    };
    if (out_gamma != nullptr) *out_gamma = pick(REFII_OPTION_GAMMA, kDefaultGamma);
    if (out_sfx != nullptr) *out_sfx = pick(REFII_OPTION_SFX, kDefaultSfx);
    if (out_music != nullptr) *out_music = pick(REFII_OPTION_MUSIC, kDefaultMusic);
    if (out_speech != nullptr) *out_speech = pick(REFII_OPTION_VOICE, kDefaultVoice);
    if (out_speaker != nullptr) {
        const float speaker = values[REFII_OPTION_SPEAKER];
        *out_speaker = (stated & (1u << REFII_OPTION_SPEAKER)) != 0u && speaker >= 0.0f && speaker <= 2.0f
                           ? static_cast<uint32_t>(speaker)
                           : kDefaultSpeaker;
    }
    return stated != 0u ? 1 : 0;
}

/// The installed languages, names NUL-separated as UTF-8 in table order, and the row in use
/// (0xFFFFFFFF for none), as the culture table's builds and switch stated them
/// (Fable3/src/hooks/src/game_state_hooks.cpp). Returns the count, 0 until the table is stated.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLanguages(uint32_t* out_current, char* names, uint32_t capacity) {
    int32_t current = -1;
    const uint32_t count = refii_bridge_cultures_read(&current, names, capacity);
    if (out_current != nullptr) *out_current = current < 0 ? 0xFFFFFFFFu : static_cast<uint32_t>(current);
    return count;
}

}  // extern "C"
