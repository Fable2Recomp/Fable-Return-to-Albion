// refii - Fable 3's HUD, as the guest states it: its typed element registries (texts, meters,
// pulsers) and its screen fade.
//
// THE REGISTRIES ARE FABLE 2'S, UNDER A NEW ROOT. GUI = `*0x83556490`, the HUD H = `*(GUI+0x20)`,
// and H+0x24 holds the registry set: the HUD.bgf load sub_825C18C0 hands it to the builder
// (`lwz r3,0x24(r31)` / `bl sub_82675288` at 0x825C1914). The builder keeps fourteen registry
// pointers in a vector at holder+0x18 (begin) / +0x1C (end), [0]..[12] in Fable 2's order, each
// vtable's name slot returning its .bgf attribute: [1] MeterName, [4] PulserName, [6] TextName.
// A registry is a map at +4 whose head is at +8; a node keys the element name's FNV-1 at +12,
// its record at +16, `_Isnil` at +21; a record's element vector is +4..+8. The element writers
// are Fable 2's byte for byte: text sub_82733F30 (LHString at element+0x10, element+12 = 1 —
// 0x82733F70/0x82733F78), meter sub_8291F988 (current at element+0x14).
//
// THE PULSERS are registry [4] (`PulserName`, vtable off_820A49B0 stored at 0x82675460). Its
// factory slot sub_82674CE8 makes a 28-byte element: +4/+8 the Gameface element it scrubs, +12 the
// mode, created 2 (`*(e+12) = 2` 0x82674d58), +16 the time it last advanced at, +20 the phase,
// +24 the rate (all three 0.0, 0x82674d64..0x82674d6c). Its update, element vtable off_820DB4D0
// slot 3 sub_82B9A300(e, f1 now), runs only while mode != 2 and time has moved
// (0x82b9a314..0x82b9a340): phase += rate * (now - +16) (0x82b9a354); past 1 a mode-1 pulser
// zeroes the phase and stops at mode 2 (0x82b9a36c / 0x82b9a370), a mode-0 one wraps
// (0x82b9a37c..0x82b9a38c); then GuiElement_SetTime(+4, +8, phase * 1000) (0x82b9a3c4). Fable 2's
// layout and contract, the clock absolute instead of a delta.
//
// The visible subtitle is one of these texts: the subtitler (`*(H+0x38)`) writes each page through
// sub_82920278 into the text registry under FNV-1("Subtitles") (`dword_8355906C`), as Fable 2's does.
//
// THE TOPBOX writes its strings the same way: its publisher sub_8217D880 (run from the manager's
// tick sub_82274950 at 0x82274A4C) composes `TopTitle` and `TopMain` as paragraphs and writes each
// into the text registry (sub_82920BC8 -> sub_82920278 -> sub_82733F30), and the button labels
// `RightTopButton` / `LeftTopButton` straight into it (0x8217DD30 / 0x8217DE14). Its visibility
// goes to the toggle and slide registries [9] and [10]; the shared HUD view draws it from the
// element statements (Titles/Fable3/Fable3Hud.cs), as it does every other hud.bgf widget.

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
constexpr uint32_t kRegistryMeter = 1u;
constexpr uint32_t kRegistryPulser = 4u;
constexpr uint32_t kRegistryText = 6u;
constexpr uint32_t kMaxNames = 512u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

const uint8_t* GuestBase() {
    rex::memory::Memory* memory = SafeKernelMemory();
    return memory == nullptr ? nullptr : memory->virtual_membase();
}

uint32_t Word(const uint8_t* base, uint32_t address) {
    return Plausible(address) ? GuestWordAt(base, address) : 0u;
}

/// The first element registered under each name in registry `index`, as parallel arrays. Returns
/// the number of names, which may exceed `capacity`. An explicit stack bounded at a red-black
/// tree's depth: another thread mutates the map, so a torn pointer truncates the answer.
uint32_t WalkRegistry(const uint8_t* base, uint32_t index, uint32_t* out_hashes, uint32_t* out_elements,
                      uint32_t capacity) {
    const uint32_t hud = Word(base, Word(base, kGuiManager) + 0x20u);
    const uint32_t holder = Word(base, hud + 0x24u);
    const uint32_t begin = Word(base, holder + 0x18u);
    const uint32_t end = Word(base, holder + 0x1Cu);
    if (!Plausible(begin) || end <= begin || (end - begin) > 0x400u || ((end - begin) / 4u) <= index) {
        return 0u;
    }

    const uint32_t registry = Word(base, begin + (index * 4u));
    const uint32_t head = Word(base, registry + 8u);
    const uint32_t root = Word(base, head + 4u);
    if (!Plausible(root)) {
        return 0u;
    }

    constexpr uint32_t kMaxDepth = 64u;
    uint32_t stack[kMaxDepth];
    uint32_t depth = 0u;
    uint32_t found = 0u;
    stack[depth++] = root;
    while (depth > 0u && found < kMaxNames) {
        const uint32_t node = stack[--depth];
        if (!Plausible(node) || GuestByteAt(base, node + 21u) != 0u) {
            continue;
        }

        const uint32_t record = Word(base, node + 16u);
        const uint32_t elements = Word(base, record + 4u);
        if (Plausible(elements) && Word(base, record + 8u) > elements) {
            const uint32_t element = Word(base, elements);
            if (Plausible(element)) {
                if (out_hashes != nullptr && out_elements != nullptr && found < capacity) {
                    out_hashes[found] = GuestWordAt(base, node + 12u);
                    out_elements[found] = element;
                }
                ++found;
            }
        }

        if (depth + 2u <= kMaxDepth) {
            stack[depth++] = Word(base, node + 0u);
            stack[depth++] = Word(base, node + 8u);
        }
    }

    return found;
}

/// The LHString whose handle sits at `at` as NUL-terminated UTF-8 (Rep: +0 big-endian UTF-16, +4
/// length in code units). Returns the bytes written including the terminator, one entry per name
/// even when the text is absent.
uint32_t WriteWideString(const uint8_t* base, uint32_t at, char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0u) {
        return 0u;
    }

    uint32_t written = 0u;
    const uint32_t rep = Word(base, at);
    const uint32_t chars = Word(base, rep);
    const uint32_t length = Plausible(rep) ? GuestWordAt(base, rep + 4u) : 0u;
    if (Plausible(chars) && length > 0u && length < 512u) {
        for (uint32_t c = 0; c < length; ++c) {
            const uint16_t unit = GuestHalfAt(base, chars + (c * 2u));
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
    }

    out[written++] = '\0';
    return written;
}

}  // namespace

extern "C" {

/// Every HUD text by its element name's FNV-1, the finished string the guest wrote (registry [6]).
/// Strings go to `text` NUL-separated in name order; returns the number of names.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudTexts(uint32_t* out_hashes, uint32_t capacity, char* text,
                                                   uint32_t text_capacity) {
    if (text != nullptr && text_capacity > 0u) {
        text[0] = '\0';
    }

    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    uint32_t elements[kMaxNames];
    const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;
    const uint32_t found = WalkRegistry(base, kRegistryText, out_hashes, elements, want);
    const uint32_t stored = found < want ? found : want;
    uint32_t written = 0u;
    for (uint32_t i = 0; i < stored && text != nullptr; ++i) {
        written += WriteWideString(base, elements[i] + 0x10u, text + written,
                                   text_capacity > written ? text_capacity - written : 0u);
    }

    return found;
}

/// Every HUD meter by name, its current value 0..100 (registry [1], element+0x14 — sub_8291F988
/// steps it toward the target at +0x1C at the rate it writes to +0x18, 0x8291FA68).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudMeters(uint32_t* out_hashes, float* out_values, uint32_t capacity) {
    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    uint32_t elements[kMaxNames];
    const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;
    const uint32_t found = WalkRegistry(base, kRegistryMeter, out_hashes, elements, want);
    const uint32_t stored = found < want ? found : want;
    for (uint32_t i = 0; i < stored && out_values != nullptr; ++i) {
        const uint32_t raw = GuestWordAt(base, elements[i] + 0x14u);
        std::memcpy(&out_values[i], &raw, sizeof(float));
    }

    return found;
}

/// Every HUD pulser by name (registry [4]), its PHASE 0..1 (element+0x14) and its MODE
/// (element+0x0C: 0 looping, 1 finishing its loop, 2 stopped — the stop zeroes the phase and
/// applies tick 0, 0x82b9a36c..0x82b9a3c4). The shell runs a running pulser on its own clock
/// between reads and holds a stopped one; see the header for the element.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudPulserStates(uint32_t* out_hashes, float* out_phases,
                                                           uint32_t* out_modes, uint32_t capacity) {
    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    uint32_t elements[kMaxNames];
    const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;
    const uint32_t found = WalkRegistry(base, kRegistryPulser, out_hashes, elements, want);
    const uint32_t stored = found < want ? found : want;
    for (uint32_t i = 0; i < stored; ++i) {
        if (out_phases != nullptr) {
            const uint32_t raw = GuestWordAt(base, elements[i] + 0x14u);
            std::memcpy(&out_phases[i], &raw, sizeof(float));
        }
        if (out_modes != nullptr) {
            out_modes[i] = GuestWordAt(base, elements[i] + 0x0Cu);
        }
    }

    return found;
}

/// The phases alone, the shape the shell binds when the states export is missing.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudPulsers(uint32_t* out_hashes, float* out_phases, uint32_t capacity) {
    return RefiiHostGetHudPulserStates(out_hashes, out_phases, nullptr, capacity);
}

/// The screen fade: fader = `*(GUI+0x3C)`, Fable 2's layout — +0 progress t, +8 the colour it
/// started from and +12 the target, both B,G,R,A bytes (sub_825D5700 stores them, sub_82182CB8
/// lerps `src + (dst - src) * t`, the draw sub_82181B30 reads them as BGRA), +0x21 the lock
/// (LockScreenFade 0x8251BC20). Returns the fader's address, 0 when there is none.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetScreenFade(float* out_rgba, float* out_progress, uint32_t* out_locked) {
    if (out_rgba != nullptr) {
        out_rgba[0] = out_rgba[1] = out_rgba[2] = out_rgba[3] = 0.0f;
    }
    if (out_progress != nullptr) *out_progress = 0.0f;
    if (out_locked != nullptr) *out_locked = 0u;

    const uint8_t* base = GuestBase();
    const uint32_t fade = base == nullptr ? 0u : Word(base, Word(base, kGuiManager) + 0x3Cu);
    if (!Plausible(fade)) {
        return 0u;
    }

    const uint32_t bits = GuestWordAt(base, fade);
    float t = 0.0f;
    std::memcpy(&t, &bits, sizeof(t));
    t = !(t > 0.0f) ? 0.0f : (t > 1.0f ? 1.0f : t);

    uint8_t lerped[4] = {0, 0, 0, 0};
    for (uint32_t i = 0; i < 4u; ++i) {
        const float from = static_cast<float>(GuestByteAt(base, fade + 8u + i));
        const float to = static_cast<float>(GuestByteAt(base, fade + 12u + i));
        lerped[i] = static_cast<uint8_t>(from + (t * (to - from)));
    }

    if (out_rgba != nullptr) {
        out_rgba[0] = static_cast<float>(lerped[2]) * (1.0f / 255.0f);
        out_rgba[1] = static_cast<float>(lerped[1]) * (1.0f / 255.0f);
        out_rgba[2] = static_cast<float>(lerped[0]) * (1.0f / 255.0f);
        out_rgba[3] = static_cast<float>(lerped[3]) * (1.0f / 255.0f);
    }
    if (out_progress != nullptr) *out_progress = t;
    if (out_locked != nullptr) *out_locked = GuestByteAt(base, fade + 0x21u);
    return fade;
}

}  // extern "C"
