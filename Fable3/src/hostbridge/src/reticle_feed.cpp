// refii - Fable 3's aim reticules, read from the records their draw reads.
//
// The setters and the record layout are in Fable3/src/hooks/src/reticle_hooks.cpp, which posts
// `@Reticule(player, slot, object, active)`. The record is player `p`'s array (`*(M+4)` for
// player one, `*(M+0x14)` for player two, M = *(H+0x3C), H = *(GUI+0x20), GUI = *0x83556490)
// plus 48 x slot: slot 0 the spell target, slot 1 the ranged aim.
//
// WHAT THE DRAW MAKES OF IT. sub_822906A8(record, 0) (0x822906D0..0x82290A44), in the guest's
// backbuffer pixels (width 0x83566C84, height 0x83566C88):
//   t      = clamp(counter * 0.25, 0, 1)           counter = +0x28, bumped once per draw
//   m      = min(progress, t)
//   angle  = t * pi/2                               the caller's index is always 0 (0x82287030)
//   r      = (1 - m) * 0                            so the sprite sits on the point
//   s      = (1 - t) * 8 + 8,  k = (1 - t) * 1.25 + 1, half extents k*s by s
//   rot    = angle + pi
//   art    = `Art\GUI\Persistant\target` + "A" for mode 0..2 (0x822907D8..0x822907FC) + ".tex"
//   colour = the record's, or qword_8355D6C0 when mode != 0 (0x82290930..0x8229094C: copied
//            from 0x820FD640 = (0.906, 0.271, 0.251, 0.8) at 0x831E8F60), alpha * t
// The colour is answered as drawn; the geometry is the shell's (Engine_HudReticule's style).

#include <cstdint>
#include <cstring>

#include "host_abi.h"
#include "host_core.h"

namespace {

using refii::hostcore::GuestByteAt;
using refii::hostcore::GuestWordAt;
using refii::hostcore::SafeKernelMemory;

constexpr uint32_t kGuiManager = 0x83556490u;
constexpr uint32_t kLockedColour = 0x8355D6C0u;
constexpr uint32_t kBackBufferHeight = 0x83566C88u;
constexpr uint32_t kRecordBytes = 0x30u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

uint32_t Word(const uint8_t* base, uint32_t address) {
    return Plausible(address) ? GuestWordAt(base, address) : 0u;
}

float Float(const uint8_t* base, uint32_t address) {
    const uint32_t bits = Word(base, address);
    float value = 0.0f;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

}  // namespace

extern "C" {

/// One reticule record, `player` 0 or 1, `slot` 0 (spell target) or 1 (ranged aim). Returns 1 and
/// fills the outputs while the record is active, 0 otherwise.
///   out_values[0..1]  the point, guest pixels from the screen centre, y up (+4, +8)
///   out_values[2]     lock progress (+0x0C)
///   out_values[3..6]  RGBA as the draw colours it (see the header)
///   out_values[7..9]  `object+0x80` when `object` is non-zero — the ranged aimer's world aim
///                     point, the vector sub_82853668 projects (0x82853734); 0 otherwise
///   out_values[10]    the guest's backbuffer height, the pixels +4/+8 are in
///   out_words[0]      mode (+0x20)   out_words[1] frames since the mode changed (+0x28)
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFable3Reticle(uint32_t player, uint32_t slot, uint32_t object,
                                                        float* out_values, uint32_t value_capacity,
                                                        uint32_t* out_words, uint32_t word_capacity) {
    rex::memory::Memory* memory = SafeKernelMemory();
    const uint8_t* base = memory == nullptr ? nullptr : memory->virtual_membase();
    if (base == nullptr || player > 1u || slot > 1u) {
        return 0u;
    }

    const uint32_t manager = Word(base, Word(base, Word(base, kGuiManager) + 0x20u) + 0x3Cu);
    const uint32_t array = Plausible(manager) ? Word(base, manager + (player == 0u ? 0x4u : 0x14u)) : 0u;
    const uint32_t record = array + (slot * kRecordBytes);
    if (!Plausible(array) || GuestByteAt(base, record) == 0u) {
        return 0u;
    }

    const uint32_t mode = Word(base, record + 0x20u);
    const uint32_t colour = mode != 0u ? kLockedColour : record + 0x10u;
    const float height = static_cast<float>(Word(base, kBackBufferHeight));
    const float values[11] = {
        Float(base, record + 0x4u),
        Float(base, record + 0x8u),
        Float(base, record + 0xCu),
        Float(base, colour),
        Float(base, colour + 0x4u),
        Float(base, colour + 0x8u),
        Float(base, colour + 0xCu),
        Plausible(object) ? Float(base, object + 0x80u) : 0.0f,
        Plausible(object) ? Float(base, object + 0x84u) : 0.0f,
        Plausible(object) ? Float(base, object + 0x88u) : 0.0f,
        height > 0.0f ? height : 720.0f,
    };
    const uint32_t words[2] = {mode, Word(base, record + 0x28u)};

    if (out_values != nullptr) {
        const uint32_t count = value_capacity < 11u ? value_capacity : 11u;
        std::memcpy(out_values, values, count * sizeof(float));
    }
    if (out_words != nullptr) {
        const uint32_t count = word_capacity < 2u ? word_capacity : 2u;
        std::memcpy(out_words, words, count * sizeof(uint32_t));
    }
    return 1u;
}

}  // extern "C"
