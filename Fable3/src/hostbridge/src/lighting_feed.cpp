// refii - Fable 3's lighting feeds: the environment theme stack the guest composes its lighting
// from, stated layer by layer at the weights the guest is blending them at, and the light halos
// it draws this frame.
//
// THE STACK IS A LIST OF LAYER GROUPS, REBUILT EVERY WORLD TICK. The world update sub_821CD4C8(B)
// evaluates the theme manager M = *(B+0x24) (`lwz r3,0x24(r27)` 0x821CD610; B = *(*(Game+0x1C)+4),
// the container RefiiHostGetTimeOfDay reaches) through sub_82268E20(M):
//   - unless byte M+12 holds a frozen copy, sub_82266B98(M) rebuilds the compose list: clear
//     (sub_821747C8), push "Global Layer" {*(M+16), the envthemebase map entry} at weight 1,
//     "Level Layer" {*(M+20), LevelSpecificData.EnvironmentTheme} or the "GUI Level Layer"
//     dword_83598B24 at 1, then the painted layers sub_82260278 keeps at M+60..M+64 (one per map
//     theme the hero's .genv cell names, 0x8226036C..0x82260378; its weight ramps over the theme's
//     own FadeInTime/FadeOutTime, theme+16/+20, 0x822605A8/0x82260480), then the five fixed layers
//     at M+72+28*i (sub_822719C0 pushes each whose weight M+72+28*i+20 is > 0; a script
//     BlendToEnvironmentTheme lands in layer 2: sub_824A46E0 -> sub_824A54E8(M, entry, 2));
//   - sub_824A46E0(M) ticks the pending script blends at M+216..M+220;
//   - sub_822275F0(stack, t) composes, t = the day fraction *(TM+4) (0x82268E7C..0x82268ED0):
//     per registered parameter sub_822279E0 walks the groups at stack+12..stack+16 — 24 bytes
//     each: +0 name, +4 weight (clamped 0..1 by sub_8226D298), +12..+16 the themes as 8-byte
//     {weight, theme} (sub_82278AF0) — sums value x weight over the themes that STATE the
//     parameter (sub_82227B68 on theme+28), divides by the summed weight (vtable +16) and lerps
//     the running value to that average at the GROUP weight (vtable +8, 0x82227B00). The result
//     lands at stack+32+offset (0x82227784): the evaluated block RefiiHostGetWeather reads.
//   stack = *(M+8) while byte M+12 is set, else *(M+4) (0x82268E74..0x82268EA0).
//
// A THEME IS A MAP ENTRY. EnvironmentThemeMap.txt is loaded by sub_824A32A0 into 24-byte entries
// at dword_83598B18..dword_83598B1C: +0 name, +4 path, +8 the loaded .envtheme (set by
// sub_824A48A8 at 0x824A4A0C on first use), +16 the id. A painted cell's word, the level's
// EnvironmentTheme field and the base name all resolve through this table (sub_82176658 matches
// +16), so the id is how the host names a theme (Engine_EnvThemeFile.TryMapTheme). Theme+12 is a
// hash of the name (sub_82C78C48 0x82C78CAC), not the id; the id is found by the entry whose +8 is
// the theme.

#include <cmath>
#include <cstdint>
#include <cstring>

#include "host_abi.h"
#include "host_core.h"
#include "presented_world.h"
#include "../../vfs/include/fable3_guest_build.h"

namespace {

using refii::hostcore::GuestByteAt;
using refii::hostcore::GuestWordAt;
using refii::hostcore::SafeKernelMemory;

const refii::guest::Addr kThemeMapBegin{0x83598B18u};
const refii::guest::Addr kThemeMapEnd{0x83598B1Cu};
constexpr uint32_t kThemeMapEntry = 24u;
constexpr uint32_t kThemeMapTheme = 8u;
constexpr uint32_t kThemeMapId = 16u;
constexpr uint32_t kMaxMapEntries = 1024u;

constexpr uint32_t kGroupBytes = 24u;
constexpr uint32_t kGroupWeight = 4u;
// A group's theme vector is the 16-byte object sub_8226D298 builds at group+8 (sub_82C78818): the
// themes begin at +12 and end at +16 — the words sub_822279E0 walks (`*(v7+12)`, `*(v7+16)`,
// 0x82227A3C/0x82227A44).
constexpr uint32_t kGroupThemesBegin = 12u;
constexpr uint32_t kGroupThemesEnd = 16u;
constexpr uint32_t kThemeEntryBytes = 8u;
constexpr uint32_t kMaxGroups = 32u;
constexpr uint32_t kMaxThemesPerGroup = 16u;

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

uint32_t Bits(float value) {
    uint32_t bits = 0u;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

/// The compose object of the presented world's theme manager, else 0.
uint32_t ThemeStack(const uint8_t* base) {
    const uint32_t game = refii::fable3::PresentedGame(base);
    if (game == 0u) {
        return 0u;
    }

    const uint32_t container = Word(base, Word(base, game + 0x1Cu) + 4u);
    const uint32_t manager = Word(base, container + 0x24u);
    if (!Plausible(manager)) {
        return 0u;
    }

    const uint32_t stack = Word(base, manager + (GuestByteAt(base, manager + 12u) != 0u ? 8u : 4u));
    return Plausible(stack) ? stack : 0u;
}

/// The map id of a loaded theme: the entry at dword_83598B18.. whose +8 is it, else 0.
uint32_t MapIdOf(const uint8_t* base, uint32_t theme) {
    const uint32_t begin = Word(base, kThemeMapBegin);
    const uint32_t end = Word(base, kThemeMapEnd);
    if (!Plausible(begin) || end <= begin) {
        return 0u;
    }

    const uint32_t count = (end - begin) / kThemeMapEntry;
    for (uint32_t i = 0; i < count && i < kMaxMapEntries; ++i) {
        const uint32_t entry = begin + i * kThemeMapEntry;
        if (Word(base, entry + kThemeMapTheme) == theme) {
            return Word(base, entry + kThemeMapId);
        }
    }

    return 0u;
}

uint32_t WalkGroups(const uint8_t* base, uint32_t groups_begin, uint32_t groups_end, uint32_t* out_words,
                    uint32_t max_entries);

}  // namespace

extern "C" {

/// The theme layers the guest is composing this tick, in its fold order, 3 words per row: the
/// theme's map id, the weight it reaches the fold at, and its group's weight.
///
/// CONTRACT (Fable 3). One row per {group, theme} of the compose list above, groups in order —
/// Global, Level, painted, fixed 0..4 — each theme at `group weight x its weight / the group's
/// summed weight`, so a reader folding the rows in order at their weights reproduces
/// sub_822279E0 exactly for a one-theme group (every painted and level layer) and takes a
/// multi-theme group as its themes folded one after another instead of averaged first. The
/// third word is the group's weight as float bits, which is also the target every theme in a
/// one-theme group is heading for: the guest ramps the group, not the theme. A theme not in the
/// map table (none shipped) is left out. Rows beyond `max_entries` are dropped; the count written
/// is returned. 0 while no world is presented or the compose list is empty.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetThemeStack(uint32_t* out_words, uint32_t max_entries) {
    if (out_words == nullptr || max_entries == 0u) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    const uint32_t stack = ThemeStack(base);
    if (stack == 0u) {
        return 0u;
    }

    // THE LIST IS REBUILT EVERY GUEST TICK (sub_821747C8 clears it, the pushes follow), so a walk
    // that straddles the rebuild sees part of it. The group range is read again after the walk
    // and the walk repeated while it moved; a walk that keeps moving answers nothing, which a
    // reader takes as "unchanged".
    for (uint32_t attempt = 0; attempt < 4u; ++attempt) {
        const uint32_t groups_begin = Word(base, stack + 12u);
        const uint32_t groups_end = Word(base, stack + 16u);
        if (!Plausible(groups_begin) || groups_end < groups_begin) {
            return 0u;
        }

        const uint32_t written = WalkGroups(base, groups_begin, groups_end, out_words, max_entries);
        if (Word(base, stack + 12u) == groups_begin && Word(base, stack + 16u) == groups_end) {
            return written;
        }
    }

    return 0u;
}

}  // extern "C"

namespace {

uint32_t WalkGroups(const uint8_t* base, uint32_t groups_begin, uint32_t groups_end, uint32_t* out_words,
                    uint32_t max_entries) {
    const uint32_t group_count = (groups_end - groups_begin) / kGroupBytes;
    uint32_t written = 0u;
    for (uint32_t g = 0; g < group_count && g < kMaxGroups; ++g) {
        const uint32_t group = groups_begin + g * kGroupBytes;
        const float group_weight = Float(base, group + kGroupWeight);
        if (!std::isfinite(group_weight) || group_weight <= 0.0f) {
            continue;
        }

        const uint32_t themes_begin = Word(base, group + kGroupThemesBegin);
        const uint32_t themes_end = Word(base, group + kGroupThemesEnd);
        if (!Plausible(themes_begin) || themes_end < themes_begin) {
            continue;
        }

        const uint32_t theme_count = (themes_end - themes_begin) / kThemeEntryBytes;
        if (theme_count == 0u || theme_count > kMaxThemesPerGroup) {
            continue;
        }

        // The group's summed weight is over the themes present, as sub_822279E0 sums it
        // (0x82227AB4) for a parameter every theme states.
        float summed = 0.0f;
        for (uint32_t t = 0; t < theme_count; ++t) {
            const uint32_t entry = themes_begin + t * kThemeEntryBytes;
            const float weight = Float(base, entry);
            if (Word(base, entry + 4u) != 0u && std::isfinite(weight) && weight > 0.0f) {
                summed += weight;
            }
        }

        if (summed <= 0.0f) {
            continue;
        }

        for (uint32_t t = 0; t < theme_count; ++t) {
            const uint32_t entry = themes_begin + t * kThemeEntryBytes;
            const float weight = Float(base, entry);
            const uint32_t theme = Word(base, entry + 4u);
            if (theme == 0u || !std::isfinite(weight) || weight <= 0.0f) {
                continue;
            }

            const uint32_t id = MapIdOf(base, theme);
            if (id == 0u) {
                continue;
            }

            if (written >= max_entries) {
                return written;
            }

            out_words[written * 3u] = id;
            out_words[(written * 3u) + 1u] = Bits(group_weight * (weight / summed));
            out_words[(written * 3u) + 2u] = Bits(group_weight);
            ++written;
        }
    }

    return written;
}

}  // namespace

extern "C" {

/// THE LIGHT HALOS the guest draws this frame, Fable 2's layout (Fable2 host_bridge.cpp
/// RefiiHostGetLightHalos), 12 floats per halo:
///   [0..2] centre L+288, [3] half-extent L+236, [4..6] colour, [7] the second pass's extra
///   factor L+244, [8..9] its quad aspect L+248 / L+252, [10] the halo texture's resource hash as
///   raw bits, [11] the light pointer as raw bits.
///
/// THE DRAWER is sub_821DF120: it binds VSHADER_LIGHTHALO / PSHADER_LIGHTHALO (bank entries 0x8A /
/// 0x8B, `sub_821F9D48(0x8A)` 0x821DF558, `sub_821FC0A8(0x8B)` 0x821DF560) and walks the global
/// light vector dword_835A5840 (begin) .. dword_835A5844 (end) (0x821DF65C..0x821DF69C), calling
/// sub_8222E6D0(light, scene, pass) for each. That draw needs L+240 >= 0.01 and L+236 >= 0.01
/// (0x8222E6E4..0x8222E710) and a loaded texture object at L+460 whose +76 is not 0x7FFFFFFF
/// (0x8222E714..0x8222E744); it places the quad at L+288 (`lvx128 v0,r26,r11` r26 = L+192,
/// r11 = 96, 0x8222EA34), colours it L+208 x L+444 x L+240 (`lvlx v9,r27,r4` r4 = 444,
/// `lvx128 v7,r27,r3` r3 = 208, 0x8222E8EC..0x8222E93C, x v80 = L+240 at 0x8222E968) and, in
/// pass 1 only, x L+244 with the half-extents L+236 x L+248 / L+236 x L+252 (0x8222E990..0x8222E9C8);
/// pass 0 uses aspect 1.
///
/// THE FIELDS ARE THE RECORD'S. CECGraphicAppearanceLight (component 8) reads HaloSize -> +76,
/// HaloBrightnessScale -> +80, HaloVerticalStreak Brightness/Length/Width/FadeoutDistance ->
/// +84/+88/+92/+96 and HaloTexture -> +100 (sub_827CE880 0x827CE9C4..0x827CEA50; the texture
/// word is sub_82723680 -> sub_82723580 -> sub_83165FC0: the FNV-1 of the name string,
/// sub_822D5DE8 0x822D5E98..0x822D5EC4). The light descriptor copies comp+76..+100 to its
/// +60..+84 (point sub_82C7F2A0 0x82C7F350..0x82C7F380, spot sub_82C7DF18 0x82C7E010..0x82C7E040)
/// and the render light's constructor sub_82D13170 copies descriptor+16.. onto L+192..
/// (sub_82299C30, +44..+80 at 0x82299CA8..0x82299CF0), so L+236 = HaloSize, L+240 =
/// HaloBrightnessScale, L+244..+256 the streak terms, L+260 = the HaloTexture hash.
/// [10] is that stated hash (L+260): the resolved object L+460 is only a load gate here.
/// Returns the halos written (at most capacity / 12), 0 when the list is empty.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLightHalos(float* out, uint32_t capacity) {
    constexpr uint32_t kFloats = 12u;
    const refii::guest::Addr kHaloListBegin{0x835A5840u};
    const refii::guest::Addr kHaloListEnd{0x835A5844u};
    if (out == nullptr || capacity < kFloats) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    const uint32_t begin = Word(base, kHaloListBegin);
    const uint32_t end = Word(base, kHaloListEnd);
    if (!Plausible(begin) || end < begin || end - begin > 4u * 4096u) {
        return 0u;
    }

    uint32_t written = 0u;
    for (uint32_t at = begin; at < end && (written + 1u) * kFloats <= capacity; at += 4u) {
        const uint32_t light = Word(base, at);
        if (!Plausible(light)) {
            continue;
        }

        const float brightness = Float(base, light + 240u);
        const float size = Float(base, light + 236u);
        const uint32_t texture = Word(base, light + 460u);
        if (!(brightness >= 0.01f) || !(size >= 0.01f) || !Plausible(texture) ||
            Word(base, texture + 76u) == 0x7FFFFFFFu) {
            continue;
        }

        const float scale = Float(base, light + 444u) * brightness;
        float* o = out + written * kFloats;
        o[0] = Float(base, light + 288u);
        o[1] = Float(base, light + 292u);
        o[2] = Float(base, light + 296u);
        o[3] = size;
        o[4] = Float(base, light + 208u) * scale;
        o[5] = Float(base, light + 212u) * scale;
        o[6] = Float(base, light + 216u) * scale;
        o[7] = Float(base, light + 244u);
        o[8] = Float(base, light + 248u);
        o[9] = Float(base, light + 252u);
        const uint32_t hash = Word(base, light + 260u);
        std::memcpy(o + 10, &hash, sizeof(float));
        std::memcpy(o + 11, &light, sizeof(float));
        ++written;
    }

    return written;
}

/// Fable 3 keeps no separate script blend list: BlendToEnvironmentTheme (sub_824A7CF0 ->
/// sub_824A53A8 -> sub_824A54E8 layer 2) is a group of the one compose list RefiiHostGetThemeStack
/// walks, folded in its place among the others. Always 0, so a reader composing the two lists
/// takes every layer once.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetThemeBlends(uint32_t* /*out_words*/, uint32_t /*max_entries*/) {
    return 0u;
}

}  // extern "C"
