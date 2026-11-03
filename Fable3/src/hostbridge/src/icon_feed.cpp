// refii - Fable 3's world icons and emotion choices, read per frame from the guest's own live
// containers: the registry R keeps every icon's lifetime, fade (a scale tween) and anchor, and a
// timed icon expires inside R's Update, never through a Remove call — so the feed is the state,
// as Fable 2's RefiiHostGetWorldSprites is, not Add/Remove statements.
//
// THE REGISTRY R = *(H+0x18), H = *(GUI+0x20), GUI = *0x83556490: the HUD update sub_822597B0
// runs it at 0x82259B7C as `sub_825EC530(*(H+24), time, ctx, 1)`. Inside sub_825EC530:
//   R+4   map head (`v30 = *(R+4)`, 0x825EC890; first node `*v30`, the walk ends back at the head)
//   R+8   map size (0x825EC5C4)       R+20 / R+24  entity-less icons, begin / end of 8-byte
//         {Icon*, control*} entries (`(R+24 - R+20) & ~7`, 0x825EC5C4; the loop steps `v16 += 2`
//         words, 0x825EC840)
//   R+48  the held interaction icon (0x825EC9D8)   R+52 enabled (0x825EC570)
//   R+53  hold filling (0x825ECA10)   R+55 may draw world icons (0x825EC944)
//   R+60  hold-fill progress 0..1 (0x825ECA24..0x825ECA8C, reported to the HUD meter at 0x825ECACC)
//   R+84  the camera (0x825EC61C; the projection sub_82B50AB8 reads it as `*(*(*(GUI+32)+24)+84)`
//         at 0x82B50B00)   R+92 / R+96 pending requests, 64 bytes each (0x825EC668)
// A map node (MSVC red-black tree, 40 bytes): +0 left, +4 parent, +8 right, +12 the entity uid64
// key, +20 the icon vector whose begin is +24 and end +28 (8-byte entries as above), +37 `_Isnil`.
//
// EVERY ICON CLASS SHARES THE BASE (ctor sub_82B52BE0, Init sub_82B52DB0):
//   +16 name CCharString (0x82B52DC8 `sub_822295F0(icon+16, req+0)`)   +20 type (sub_82B512C0
//   `a1[5] = type`, 0x82B512D0)   +24/+28 entity WeakRef {vtable, node*}, node+0 the CThing
//   (sub_829A2650 resolves; sub_8222F580 at 0x8222F5CC..0x8222F608)   +48 seconds left and +52
//   persistent (0x82B52EC4..0x82B52EDC: spaced types get 5.0 timed, the rest persistent)
//   +60..+63 colour as B, G, R, A bytes (sub_82B53EA8 copies the type table's word there,
//   0x82B53EB0..0x82B53EC8; sub_82B50AB8 builds the text colour as (+62, +61, +60, +63)/255)
//   +68 age (0x8222F564)   +100 fading out (0x82226F24, set at age 4 s for spaced types; the
//   remove path sets it and tweens the scale to 0)   +112 cached world position
//   (sub_8221B4F0, `stvx128 v127, r27, r25` with r25 = 112 at 0x8221B990)
// World icon (560 bytes, vtable 0x820029F8) adds: +144 texture id = FNV-1 of
//   `art\<type path>.tex` (0x82B51304, path table sub_82B530E8), +148 the GUI image handle
//   (-1 until the guest's draw created it, 0x82B50D9C; released by hide sub_82B51340 at
//   0x82B5137C), +152 the tweened scale (0x82226EA8..0x82226EF0), +156 primary text LHString,
//   +372 secondary text LHString (0x82B5143C / 0x82B5144C).
// Emotion icon (912 bytes, vtable 0x820C37A8) derives from it and adds the ring: +692 held
//   (0x829791B8), +696 progress 0..300 (0x8297A6A4), +700 complete (0x8297A61C), +701 manual
//   (0x829791FC), +720 manual speed (0x8297A544), +736 the GDB `Texture` id.
// HUD-element icon (192 bytes, vtable 0x820D5EBC; the hold icon 35, the player markers 52/53 and
//   the button prompts 31..34/66..69): drawn on a pooled Gameface element it positions itself
//   (sub_82B51B58, sub_82818580 at 0x82B51FC8). Its world point is the same +112.
//
// THE EMOTION CHOICES E = *(H+0x1C) (the HUD update runs `sub_822951F0(*(H+28))` at 0x82259B84):
// a doubly-linked list whose sentinel is at E+16 and count at E+20; node +8 is the record. The
// record (80 bytes, sub_825E8458 from Lua): +0 action id (0x825E847C), +4 Name (0x825E84BC),
// +8 player uid64 (0x825E86A4/AC), +16 Text LHString (0x825E850C), +20/+24 entity WeakRef
// (0x825E8540/48), +32 position (0x825E8560), +52 RecordName (0x825E859C), +56 icon type
// (0x825E85A8), +60 holdable (0x825E85C0), +61 manual (0x825E85D8), +62 held (0x825E85DC),
// +64 manual speed (0x825E8610). The input sub_825E91E0 walks the same list from `*(E+16)`
// (0x825E928C..0x825E9334) and reads `rec+8` against the pad's hero uid (0x825E92C0).

#include <cstdint>
#include <cstring>
#include <string>

#include "host_abi.h"
#include "host_core.h"

namespace {

using refii::hostcore::GuestByteAt;
using refii::hostcore::GuestHalfAt;
using refii::hostcore::GuestWordAt;
using refii::hostcore::SafeKernelMemory;

constexpr uint32_t kGuiManager = 0x83556490u;
constexpr uint32_t kHudOffset = 0x20u;
constexpr uint32_t kRegistryOffset = 0x18u;
constexpr uint32_t kEmotionOffset = 0x1Cu;
constexpr uint32_t kPlayerIconsOffset = 80u;  // `*(H+80)+16`: SetShouldDrawPlayerIcons 0x8251C154

constexpr uint32_t kVtWorldIcon = 0x820029F8u;
constexpr uint32_t kVtEmotionIcon = 0x820C37A8u;
constexpr uint32_t kVtHudElementIcon = 0x820D5EBCu;

constexpr uint32_t kMaxIcons = 256u;
constexpr uint32_t kMaxChoices = 16u;

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

/// The icon registry R, 0 when the HUD is not built.
uint32_t Registry(const uint8_t* base) {
    const uint32_t hud = Word(base, Word(base, kGuiManager) + kHudOffset);
    const uint32_t registry = Word(base, hud + kRegistryOffset);
    return Plausible(registry) ? registry : 0u;
}

uint32_t Hud(const uint8_t* base) {
    const uint32_t hud = Word(base, Word(base, kGuiManager) + kHudOffset);
    return Plausible(hud) ? hud : 0u;
}

/// The CThing a WeakRef at `at` resolves to, or 0 (sub_829A2650: `*(*(wr+4))`, the node's first
/// word nulled on death).
uint32_t ResolveWeakRef(const uint8_t* base, uint32_t at) {
    const uint32_t node = Word(base, at + 4u);
    const uint32_t thing = Word(base, node);
    return Plausible(thing) ? thing : 0u;
}

/// The CCharString whose handle sits at `at` (rep+0 = chars), bounded.
std::string ReadCharString(const uint8_t* base, uint32_t at, uint32_t max_chars = 160u) {
    std::string text;
    const uint32_t rep = Word(base, at);
    const uint32_t chars = Word(base, rep);
    if (!Plausible(chars)) {
        return text;
    }
    for (uint32_t i = 0; i < max_chars; ++i) {
        const char ch = static_cast<char>(GuestByteAt(base, chars + i));
        if (ch == '\0') {
            break;
        }
        text.push_back(ch);
    }
    return text;
}

/// The LHString whose handle sits at `at` as UTF-8 (rep+0 UTF-16BE, rep+4 code units).
std::string ReadWideString(const uint8_t* base, uint32_t at, uint32_t max_units = 512u) {
    std::string text;
    const uint32_t rep = Word(base, at);
    const uint32_t chars = Word(base, rep);
    const uint32_t length = Plausible(rep) ? GuestWordAt(base, rep + 4u) : 0u;
    if (!Plausible(chars) || length == 0u || length > max_units) {
        return text;
    }
    for (uint32_t i = 0; i < length; ++i) {
        uint32_t unit = GuestHalfAt(base, chars + (i * 2u));
        if (unit == 0u) {
            break;
        }
        if (unit >= 0xD800u && unit < 0xDC00u && i + 1u < length) {
            const uint32_t low = GuestHalfAt(base, chars + ((i + 1u) * 2u));
            if (low >= 0xDC00u && low < 0xE000u) {
                unit = 0x10000u + ((unit - 0xD800u) << 10) + (low - 0xDC00u);
                ++i;
            }
        }
        if (unit < 0x80u) {
            text.push_back(static_cast<char>(unit));
        } else if (unit < 0x800u) {
            text.push_back(static_cast<char>(0xC0u | (unit >> 6)));
            text.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
        } else if (unit < 0x10000u) {
            text.push_back(static_cast<char>(0xE0u | (unit >> 12)));
            text.push_back(static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu)));
            text.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
        } else {
            text.push_back(static_cast<char>(0xF0u | (unit >> 18)));
            text.push_back(static_cast<char>(0x80u | ((unit >> 12) & 0x3Fu)));
            text.push_back(static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu)));
            text.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
        }
    }
    return text;
}

/// Every live icon of R, entity-less vector first, then the map in tree order. Returns the
/// count, which may exceed `capacity`. Bounded: a torn pointer truncates, never walks the heap.
uint32_t CollectIcons(const uint8_t* base, uint32_t registry, uint32_t* out, uint32_t capacity) {
    uint32_t found = 0u;
    const auto take_vector = [&](uint32_t begin, uint32_t end) {
        if (!Plausible(begin) || end < begin || (end - begin) > 8u * kMaxIcons) {
            return;
        }
        for (uint32_t at = begin; at < end; at += 8u) {
            const uint32_t icon = Word(base, at);
            if (Plausible(icon) && found < capacity) {
                out[found] = icon;
            }
            if (Plausible(icon)) {
                ++found;
            }
        }
    };

    take_vector(Word(base, registry + 20u), Word(base, registry + 24u));

    const uint32_t head = Word(base, registry + 4u);
    const uint32_t root = Word(base, head + 4u);
    if (!Plausible(head) || !Plausible(root) || root == head) {
        return found;
    }

    constexpr uint32_t kMaxDepth = 64u;
    uint32_t stack[kMaxDepth];
    uint32_t depth = 0u;
    uint32_t visited = 0u;
    stack[depth++] = root;
    while (depth > 0u && visited < kMaxIcons) {
        const uint32_t node = stack[--depth];
        if (!Plausible(node) || node == head || GuestByteAt(base, node + 37u) != 0u) {
            continue;
        }
        ++visited;
        take_vector(Word(base, node + 24u), Word(base, node + 28u));
        if (depth + 2u <= kMaxDepth) {
            stack[depth++] = Word(base, node + 0u);
            stack[depth++] = Word(base, node + 8u);
        }
    }
    return found;
}

struct TextWriter {
    char* text;
    uint32_t capacity;
    uint32_t at = 0u;

    void Put(const std::string& value) {
        if (text == nullptr || capacity == 0u) {
            return;
        }
        for (char c : value) {
            if (at + 1u >= capacity) {
                break;
            }
            text[at++] = c;
        }
        if (at < capacity) {
            text[at++] = '\0';
        }
    }
};

}  // namespace

extern "C" {

/// Fable 2's layout, export for export. Words per icon: type, texture id, rgba, entity,
/// persistent, flags, pool slot, OBJECT. Values: anchor x y z, scale, seconds left, age. Text:
/// title NUL opinion NUL. Flags: bit0 fading out (+100), bit1 a HUD-element icon (no texture of
/// its own; the type names its button or marker), bit3 emotion ring on (+692), bit4 emotion ring
/// complete (+700), bit5 the guest holds a live image for it (+148 != -1: it is drawing it now —
/// clear while hidden in a menu or while its texture loads). rgba: the type table's colour,
/// alpha 255 — the guest fades by SCALE, which is what `scale` carries.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWorldSprites(uint32_t* words, uint32_t word_capacity, float* values,
                                                        uint32_t value_capacity, char* text, uint32_t text_capacity,
                                                        uint32_t max_icons) {
    constexpr uint32_t kWords = 8u;
    constexpr uint32_t kValues = 6u;
    if (text != nullptr && text_capacity > 0u) {
        text[0] = '\0';
    }

    const uint8_t* base = GuestBase();
    if (base == nullptr || words == nullptr || values == nullptr) {
        return 0u;
    }
    const uint32_t registry = Registry(base);
    if (registry == 0u) {
        return 0u;
    }

    uint32_t icons[kMaxIcons];
    const uint32_t found = CollectIcons(base, registry, icons, kMaxIcons);
    const uint32_t count = found < kMaxIcons ? found : kMaxIcons;

    TextWriter writer{text, text_capacity};
    uint32_t written = 0u;
    for (uint32_t i = 0; i < count && written < max_icons; ++i) {
        const uint32_t icon = icons[i];
        const uint32_t vtable = Word(base, icon);
        const bool world = vtable == kVtWorldIcon;
        const bool emotion = vtable == kVtEmotionIcon;
        const bool pooled = vtable == kVtHudElementIcon;
        if (!world && !emotion && !pooled) {
            continue;
        }
        if ((written + 1u) * kWords > word_capacity || (written + 1u) * kValues > value_capacity) {
            break;
        }

        uint32_t* w = words + (written * kWords);
        float* v = values + (written * kValues);
        w[0] = Word(base, icon + 20u);
        w[1] = pooled ? 0u : Word(base, icon + 144u);
        w[2] = (static_cast<uint32_t>(GuestByteAt(base, icon + 62u)) << 24) |
               (static_cast<uint32_t>(GuestByteAt(base, icon + 61u)) << 16) |
               (static_cast<uint32_t>(GuestByteAt(base, icon + 60u)) << 8) | 0xFFu;
        w[3] = ResolveWeakRef(base, icon + 24u);
        w[4] = GuestByteAt(base, icon + 52u);
        uint32_t flags = GuestByteAt(base, icon + 100u) != 0u ? 1u : 0u;
        if (pooled) {
            flags |= 2u;
        } else {
            if (Word(base, icon + 148u) != 0xFFFFFFFFu) {
                flags |= 32u;
            }
            if (emotion) {
                if (GuestByteAt(base, icon + 692u) != 0u) flags |= 8u;
                if (GuestByteAt(base, icon + 700u) != 0u) flags |= 16u;
            }
        }
        w[5] = flags;
        w[6] = 0xFFFFFFFFu;
        w[7] = icon;

        v[0] = Float(base, icon + 112u);
        v[1] = Float(base, icon + 116u);
        v[2] = Float(base, icon + 120u);
        v[3] = pooled ? 1.0f : Float(base, icon + 152u);
        v[4] = Float(base, icon + 48u);
        v[5] = Float(base, icon + 68u);

        writer.Put(pooled ? std::string() : ReadWideString(base, icon + 156u));
        writer.Put(pooled ? std::string() : ReadWideString(base, icon + 372u));
        ++written;
    }
    return written;
}

/// The registry's gates and its HUD-element icons. `out_flags`: bit0 R+52 enabled, bit1
/// SetShouldDrawPlayerIcons' byte `*(H+80)+16` (0x8251C154), bit2 R+55 may draw world icons,
/// bit3 R+56 and bit4 R+57 (SetCanShowHintWorldIcons, 0x8251C328) opinion/hint gates, bits 8..15
/// the hold-fill progress R+60 as a percentage, bit 16 hold filling (R+53). Types and entities:
/// the HUD-element icons (the hold icon, the player markers, the button prompts) in walk order.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWorldIcons(uint32_t* out_flags, uint32_t* out_types, uint32_t* out_entities,
                                                      uint32_t max_icons) {
    if (out_flags != nullptr) {
        *out_flags = 0u;
    }
    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }
    const uint32_t hud = Hud(base);
    const uint32_t registry = Registry(base);
    if (hud == 0u || registry == 0u) {
        return 0u;
    }

    uint32_t flags = 0u;
    if (GuestByteAt(base, registry + 52u) != 0u) flags |= 1u;
    const uint32_t player_icons = Word(base, hud + kPlayerIconsOffset);
    if (Plausible(player_icons) && GuestByteAt(base, player_icons + 16u) != 0u) flags |= 2u;
    if (GuestByteAt(base, registry + 55u) != 0u) flags |= 4u;
    if (GuestByteAt(base, registry + 56u) != 0u) flags |= 8u;
    if (GuestByteAt(base, registry + 57u) != 0u) flags |= 16u;
    float fill = Float(base, registry + 60u);
    fill = !(fill > 0.0f) ? 0.0f : (fill > 1.0f ? 1.0f : fill);
    flags |= (static_cast<uint32_t>(fill * 100.0f) & 0xFFu) << 8;
    if (GuestByteAt(base, registry + 53u) != 0u) flags |= 1u << 16;
    if (out_flags != nullptr) {
        *out_flags = flags;
    }

    uint32_t icons[kMaxIcons];
    const uint32_t found = CollectIcons(base, registry, icons, kMaxIcons);
    const uint32_t count = found < kMaxIcons ? found : kMaxIcons;
    uint32_t written = 0u;
    for (uint32_t i = 0; i < count && written < max_icons; ++i) {
        const uint32_t icon = icons[i];
        if (Word(base, icon) != kVtHudElementIcon) {
            continue;
        }
        if (out_types != nullptr) out_types[written] = Word(base, icon + 20u);
        if (out_entities != nullptr) out_entities[written] = ResolveWeakRef(base, icon + 24u);
        ++written;
    }
    return written;
}

/// The emotion choices E holds, one per record, with the ring of the emotion icon R keeps for it
/// (matched by name, +16, and type 77). Words per choice: action id, player uid low, player uid
/// high, entity, icon type (+56), flags, ICON (0 when R has none yet), 0. Flags: bit0 holdable,
/// bit1 manual power, bit2 held (+62), bit3 the icon's ring is on (+692), bit4 the ring is
/// complete (+700), bit5 the icon is drawn (+148 != -1). Values: anchor x y z (the icon's +112,
/// else the record's +32), ring progress 0..1 (+696 / 300), manual speed (+720), the icon's
/// scale (+152). Text: Name NUL Text NUL RecordName NUL.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFable3EmotionChoices(uint32_t* words, uint32_t word_capacity, float* values,
                                                                uint32_t value_capacity, char* text,
                                                                uint32_t text_capacity, uint32_t max_choices) {
    constexpr uint32_t kWords = 8u;
    constexpr uint32_t kValues = 6u;
    if (text != nullptr && text_capacity > 0u) {
        text[0] = '\0';
    }

    const uint8_t* base = GuestBase();
    if (base == nullptr || words == nullptr || values == nullptr) {
        return 0u;
    }
    const uint32_t hud = Hud(base);
    if (hud == 0u) {
        return 0u;
    }
    const uint32_t emotions = Word(base, hud + kEmotionOffset);
    const uint32_t sentinel = Word(base, emotions + 16u);
    if (!Plausible(emotions) || !Plausible(sentinel)) {
        return 0u;
    }

    // The emotion icons R holds now, for the per-record match.
    uint32_t icons[kMaxIcons];
    uint32_t icon_count = 0u;
    if (const uint32_t registry = Registry(base); registry != 0u) {
        const uint32_t found = CollectIcons(base, registry, icons, kMaxIcons);
        icon_count = found < kMaxIcons ? found : kMaxIcons;
    }

    TextWriter writer{text, text_capacity};
    uint32_t written = 0u;
    uint32_t node = Word(base, sentinel);
    for (uint32_t guard = 0; Plausible(node) && node != sentinel && guard < kMaxChoices && written < max_choices;
         ++guard, node = Word(base, node)) {
        const uint32_t record = Word(base, node + 8u);
        if (!Plausible(record)) {
            continue;
        }
        if ((written + 1u) * kWords > word_capacity || (written + 1u) * kValues > value_capacity) {
            break;
        }

        const std::string name = ReadCharString(base, record + 4u);
        uint32_t icon = 0u;
        for (uint32_t i = 0; i < icon_count && icon == 0u; ++i) {
            if (Word(base, icons[i]) == kVtEmotionIcon && ReadCharString(base, icons[i] + 16u) == name) {
                icon = icons[i];
            }
        }

        uint32_t* w = words + (written * kWords);
        float* v = values + (written * kValues);
        w[0] = Word(base, record + 0u);
        w[1] = Word(base, record + 8u);
        w[2] = Word(base, record + 12u);
        w[3] = ResolveWeakRef(base, record + 20u);
        w[4] = Word(base, record + 56u);
        uint32_t flags = 0u;
        if (GuestByteAt(base, record + 60u) != 0u) flags |= 1u;
        if (GuestByteAt(base, record + 61u) != 0u) flags |= 2u;
        if (GuestByteAt(base, record + 62u) != 0u) flags |= 4u;
        if (icon != 0u) {
            if (GuestByteAt(base, icon + 692u) != 0u) flags |= 8u;
            if (GuestByteAt(base, icon + 700u) != 0u) flags |= 16u;
            if (Word(base, icon + 148u) != 0xFFFFFFFFu) flags |= 32u;
        }
        w[5] = flags;
        w[6] = icon;
        w[7] = 0u;

        const uint32_t anchor = icon != 0u ? icon + 112u : record + 32u;
        v[0] = Float(base, anchor);
        v[1] = Float(base, anchor + 4u);
        v[2] = Float(base, anchor + 8u);
        v[3] = icon != 0u ? static_cast<float>(static_cast<int32_t>(Word(base, icon + 696u))) / 300.0f : 0.0f;
        v[4] = icon != 0u ? Float(base, icon + 720u) : Float(base, record + 64u);
        v[5] = icon != 0u ? Float(base, icon + 152u) : 1.0f;

        writer.Put(name);
        writer.Put(ReadWideString(base, record + 16u));
        writer.Put(ReadCharString(base, record + 52u));
        ++written;
    }
    return written;
}

}  // extern "C"
