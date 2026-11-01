// refii - Fable 3's options as the guest holds them: the applied gamma and audio levels, the
// installed languages, and the Options screen's object while it shows.
//
// THE APPLIED VALUES, NOT THE SCREEN. The Options object is a box that exists only while the
// screen shows (sub_825C8630 allocates 172 bytes and pushes it on the box stack); what the game
// RUNS with is written by its appliers into two long-lived objects, so those are the stated
// values at any time:
//   - gamma: sub_825CD1F0 (and sub_825CD2F8 at open, from the profile) store the renderer's
//     R = *(*(*(g_Game+0xC)+0x10)+4) — g_Game = *0x83556540, its vtable off_82001AE8 slot 5
//     sub_825101C8 is `lwz r11,0xC(r3); lwz r3,0x10(r11)` and the applier adds 4 (0x825cd260) —
//     `*(R+24) = g` (0x825cd264) and `*(R+20) = 1 - (2g - 1) * 0.4` (0x825cd274). Both are
//     read, and the pair is accepted only when the derived word agrees with the gamma.
//   - audio: A = *0x835569DC; sub_82D1D440 clamps and stores sfx at A+88 (0x82d1d494),
//     sub_82D1D4E8 voice at A+92 (0x82d1d53c), sub_82D1D668 music at A+100 (0x82d1d6bc),
//     sub_82D1D710 the speaker mode at A+104 (0x82d1d7bc; 0 SPEAKERMODE_TV, 1 HIFI, 2 SURROUND).
//   The defaults when nothing has been applied are the object's own: gamma 0.5 (0x825c9660),
//   music 1.0, sfx 1.0, voice 0.9 (0x825c9670..84), speaker 2 (0x825c96f8).
//
// THE OPTIONS OBJECT (sub_825C95F0, vtable off_8209D230 at 0x825c9680, box type 15): +80 page
// (1 Options, 2 SaveManagement, 3 GameSettings, 4 GameSettingsLite, 5 Multiplayer; 0x825c96e4),
// +84/+88/+92/+96 the selected row of pages 1(and 5)/2/3/4, +31 Controller, +32 Subtitles, +33
// Tutorial (row writer sub_825CC938: 0x825cc994/0x825cc9c4/0x825cca64 with the ON/OFF tags
// `dword_83558A64[bool]` = TEXT_GUI_OPTIONS_OFF/ON), +128 gamma (0x825cc9fc), +104 breadcrumb
// 0..4 (0x825cca34; GUI_OPTIONS_BCRUMB_OFF/LOW/MEDIUM/HIGH/VERYHIGH), +116 music, +120 sound
// effects, +124 voice (0x825cca9c/0x825ccae4/0x825ccb2c), +100 speaker type (0x825ccb84;
// GUI_SCREEN_OPTIONS_AUDIO_TV/HIFI/SURROUND), +60 language count / +64 language index (page 4
// row 1, sub_825CDAA8 fills them from the culture table), +34 opened from the pause, +37 the
// front-end (lite) variant, +36 save allowed, +108/+112 the multiplayer rows' flags. The list pages
// are the A-press handler sub_825CB588's: pages 1 and 5 (`+84`, 0x825cb73c) row 1 Save Management
// (-> page 2, 0x825cb794), row 2 Game Settings (-> page 3, or the lite page 4 when sub_82347CE8 says
// no, 0x825cb7f8/0x825cb830), row 3 New Game (the confirm query TEXT_GUI_OPTIONS_NEWGAME_CONFIRM /
// _CANNOT_LIMIT, 0x825cb8ac/c0), row 4 Kick/Leave (multiplayer only, 0x825cba94); page 2 (`+88`,
// 0x825cb5b4) row 1 Save and Continue (GUI_SAVING_OVERWRITE_WARNING when +36, then sub_822F5098 in
// sub_825CBF20 at 0x825cc10c), row 2 Load Management (when +112 is 0, 0x825cb5c8). Steps are the guest's
// (sub_825CAA98 right / sub_825CB028 left: ±0.025 on a slider, a toggle on a bool), so this reads.
// It shows on the box manager's first queue: BM = *(*(GUI+0x1C)+0x30), the showing box at
// BM+4+20 (display_box_hooks.cpp).
//
// THE LANGUAGES are Fable 2's table layout at Fable 3's addresses: rows of 96 bytes at
// *0x832A0AC8, count 0x832A0ACC, current index 0x832A0AD0 (sub_825CDAA8 copies the two into the
// Options object at 0x825cdad0/0x825cdad8 and names each row from the UTF-16 string at +88,
// `sub_8229B650(&str, *(row+88))` at 0x825cdb38..40 taking a `wchar_t*`).

#include <cstdint>
#include <cstring>

#include "host_abi.h"
#include "host_core.h"

namespace {

using refii::hostcore::GuestByteAt;
using refii::hostcore::GuestHalfAt;
using refii::hostcore::GuestWordAt;
using refii::hostcore::SafeKernelMemory;

constexpr uint32_t kGuiManager = 0x83556490u;
constexpr uint32_t kGame = 0x83556540u;
constexpr uint32_t kAudio = 0x835569DCu;
constexpr uint32_t kOptionsVtable = 0x8209D230u;

constexpr uint32_t kLanguageTable = 0x832A0AC8u;
constexpr uint32_t kLanguageCount = 0x832A0ACCu;
constexpr uint32_t kLanguageCurrent = 0x832A0AD0u;
constexpr uint32_t kLanguageStride = 96u;
constexpr uint32_t kLanguageName = 88u;

constexpr float kDefaultGamma = 0.5f;
constexpr float kDefaultMusic = 1.0f;
constexpr float kDefaultSfx = 1.0f;
constexpr float kDefaultVoice = 0.9f;
constexpr uint32_t kDefaultSpeaker = 2u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

const uint8_t* GuestBase() {
    rex::memory::Memory* memory = SafeKernelMemory();
    return memory == nullptr ? nullptr : memory->virtual_membase();
}

uint32_t Word(const uint8_t* base, uint32_t address) {
    return Plausible(address) ? GuestWordAt(base, address) : 0u;
}

float Float(const uint8_t* base, uint32_t address) {
    const uint32_t bits = Word(base, address);
    float value = 0.0f;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

bool Unit(float value) { return value >= 0.0f && value <= 1.0f; }

/// The Options box while it is the showing box of the manager's first queue, else 0.
uint32_t OptionsBox(const uint8_t* base) {
    const uint32_t screens = Word(base, Word(base, kGuiManager) + 0x1Cu);
    const uint32_t manager = Word(base, screens + 0x30u);
    const uint32_t box = Word(base, manager + 4u + 20u);
    return Plausible(box) && GuestWordAt(base, box) == kOptionsVtable ? box : 0u;
}

/// UTF-16 at `wide` as UTF-8 into `out`, NUL-terminated; returns the bytes written.
uint32_t WriteUtf16(const uint8_t* base, uint32_t wide, char* out, uint32_t capacity) {
    uint32_t written = 0u;
    if (out == nullptr || capacity == 0u) {
        return 0u;
    }
    for (uint32_t c = 0; Plausible(wide) && c < 128u; ++c) {
        const uint16_t unit = GuestHalfAt(base, wide + (c * 2u));
        if (unit == 0u || (unit >= 0xD800u && unit <= 0xDFFFu)) {
            break;
        }
        char encoded[3];
        uint32_t bytes = 0u;
        if (unit < 0x80u) {
            encoded[bytes++] = static_cast<char>(unit);
        } else if (unit < 0x800u) {
            encoded[bytes++] = static_cast<char>(0xC0u | (unit >> 6));
            encoded[bytes++] = static_cast<char>(0x80u | (unit & 0x3Fu));
        } else {
            encoded[bytes++] = static_cast<char>(0xE0u | (unit >> 12));
            encoded[bytes++] = static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu));
            encoded[bytes++] = static_cast<char>(0x80u | (unit & 0x3Fu));
        }
        if (written + bytes + 1u >= capacity) {
            break;
        }
        for (uint32_t b = 0; b < bytes; ++b) {
            out[written++] = encoded[b];
        }
    }
    out[written++] = '\0';
    return written;
}

}  // namespace

extern "C" {

/// The levels the game runs with: gamma from the renderer's applied pair, the volumes and speaker
/// mode from the audio object; each the object's own default until an applier has written it.
/// Returns 1 once the guest's objects exist.
REFII_HOST_ABI_EXPORT int RefiiHostGetOptionsSliders(float* out_gamma, float* out_sfx, float* out_music,
                                                     float* out_speech, uint32_t* out_speaker) {
    if (out_gamma != nullptr) *out_gamma = kDefaultGamma;
    if (out_sfx != nullptr) *out_sfx = kDefaultSfx;
    if (out_music != nullptr) *out_music = kDefaultMusic;
    if (out_speech != nullptr) *out_speech = kDefaultVoice;
    if (out_speaker != nullptr) *out_speaker = kDefaultSpeaker;

    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0;
    }

    const uint32_t game = Word(base, kGame);
    const uint32_t audio = Word(base, kAudio);
    if (!Plausible(game) && !Plausible(audio)) {
        return 0;
    }

    const uint32_t renderer = Word(base, Word(base, Word(base, game + 0xCu) + 0x10u) + 4u);
    if (Plausible(renderer)) {
        const float gamma = Float(base, renderer + 24u);
        const float derived = Float(base, renderer + 20u);
        const float expected = (-((gamma * 2.0f) - 1.0f) * 0.4f) + 1.0f;
        if (Unit(gamma) && derived > expected - 0.002f && derived < expected + 0.002f && out_gamma != nullptr) {
            *out_gamma = gamma;
        }
    }

    if (Plausible(audio)) {
        const float sfx = Float(base, audio + 88u);
        const float voice = Float(base, audio + 92u);
        const float music = Float(base, audio + 100u);
        const uint32_t speaker = GuestWordAt(base, audio + 104u);
        if (Unit(sfx) && out_sfx != nullptr) *out_sfx = sfx;
        if (Unit(voice) && out_speech != nullptr) *out_speech = voice;
        if (Unit(music) && out_music != nullptr) *out_music = music;
        if (speaker <= 2u && out_speaker != nullptr) *out_speaker = speaker;
    }

    return 1;
}

/// The installed languages, names NUL-separated as UTF-8 in table order, and the current index.
/// Returns the count, 0 until the table is built.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLanguages(uint32_t* out_current, char* names, uint32_t capacity) {
    if (out_current != nullptr) *out_current = 0u;
    if (names != nullptr && capacity > 0u) names[0] = '\0';

    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    const uint32_t table = Word(base, kLanguageTable);
    const uint32_t count = GuestWordAt(base, kLanguageCount);
    if (!Plausible(table) || count == 0u || count > 64u) {
        return 0u;
    }
    if (out_current != nullptr) *out_current = GuestWordAt(base, kLanguageCurrent);

    uint32_t written = 0u;
    for (uint32_t i = 0; i < count; ++i) {
        const uint32_t wide = Word(base, table + (i * kLanguageStride) + kLanguageName);
        if (names != nullptr && capacity > written) {
            written += WriteUtf16(base, wide, names + written, capacity - written);
        }
    }
    return count;
}

/// The Options object while its screen shows. Words: [0] the object, [1] page, [2] the selected
/// row of that page (1-based), [3] Controller, [4] Subtitles, [5] Tutorial, [6] breadcrumb 0..4,
/// [7] speaker 0..2, [8] language index, [9] language count, [10] multiplayer flag +108, [11]
/// flag +112, [12] opened from the pause, [13] the front-end variant, [14] the save allowed
/// (+36, `sub_822F9818` at 0x825c9804; the Show ghosts the save rows when it is 0, 0x825c99f4).
/// Values: [0] gamma, [1] music, [2] sound effects, [3] voice. Returns 1 when it shows, else 0.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFable3Options(uint32_t* out_words, uint32_t word_capacity,
                                                         float* out_values, uint32_t value_capacity) {
    constexpr uint32_t kWords = 15u;
    constexpr uint32_t kValues = 4u;
    if (out_words == nullptr || word_capacity < kWords || out_values == nullptr || value_capacity < kValues) {
        return 0u;
    }
    for (uint32_t i = 0; i < kWords; ++i) out_words[i] = 0u;
    for (uint32_t i = 0; i < kValues; ++i) out_values[i] = 0.0f;

    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }
    const uint32_t box = OptionsBox(base);
    if (box == 0u) {
        return 0u;
    }

    const uint32_t page = GuestWordAt(base, box + 80u);
    const uint32_t row_at = page == 2u ? 88u : page == 3u ? 92u : page == 4u ? 96u : 84u;
    out_words[0] = box;
    out_words[1] = page;
    out_words[2] = GuestWordAt(base, box + row_at);
    out_words[3] = GuestByteAt(base, box + 31u);
    out_words[4] = GuestByteAt(base, box + 32u);
    out_words[5] = GuestByteAt(base, box + 33u);
    out_words[6] = GuestWordAt(base, box + 104u);
    out_words[7] = GuestWordAt(base, box + 100u);
    out_words[8] = GuestWordAt(base, box + 64u);
    out_words[9] = GuestWordAt(base, box + 60u);
    out_words[10] = GuestWordAt(base, box + 108u);
    out_words[11] = GuestWordAt(base, box + 112u);
    out_words[12] = GuestByteAt(base, box + 34u);
    out_words[13] = GuestByteAt(base, box + 37u);
    out_words[14] = GuestByteAt(base, box + 36u);
    out_values[0] = Float(base, box + 128u);
    out_values[1] = Float(base, box + 116u);
    out_values[2] = Float(base, box + 120u);
    out_values[3] = Float(base, box + 124u);
    return 1u;
}

}  // extern "C"
