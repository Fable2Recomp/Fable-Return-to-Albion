// refii - Fable 3's aim reticules, as their setters stated them.
//
// The setters, the record layout and the colour the draw picks are in
// Fable3/src/hooks/src/reticle_hooks.cpp, which states each record as its setter left it into the
// table this reads (core/payload_bridge.h: refii_bridge_reticle_read) and posts
// `@Reticule(player, slot, object, active)`. No guest memory is read here.
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
//   colour = the record's, or qword_8355D6C0 when mode != 0, alpha * t
// The colour is answered as drawn; the geometry is the shell's (Engine_HudReticule's style).

#include <cstdint>
#include <cstring>

#include "core/payload_bridge.h"
#include "host_abi.h"

extern "C" {

/// One reticule record, `player` 0 or 1, `slot` 0 (spell target) or 1 (ranged aim), as its setter
/// stated it. Returns 1 and fills the outputs while the record is active, 0 otherwise.
///   out_values[0..1]  the point, guest pixels from the screen centre, y up (+4, +8)
///   out_values[2]     lock progress (+0x0C)
///   out_values[3..6]  RGBA as the draw colours it
///   out_values[7..9]  the ranged aimer's world aim point aimer+0x80, the vector sub_82853668
///                     projects (0x82853734), as of its setter; 0 for the spell target
///   out_values[10]    the guest's backbuffer height, the pixels +4/+8 are in
///   out_words[0]      mode (+0x20)   out_words[1] frames since the mode changed (+0x28)
/// `object` is the aimer `@Reticule` named; the table already holds the aim point it stated.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFable3Reticle(uint32_t player, uint32_t slot, uint32_t object,
                                                        float* out_values, uint32_t value_capacity,
                                                        uint32_t* out_words, uint32_t word_capacity) {
    (void)object;
    float values[REFII_RETICLE_VALUES] = {};
    uint32_t words[REFII_RETICLE_WORDS] = {};
    if (player > 1u || slot > 1u || refii_bridge_reticle_read(player, slot, values, words) == 0) {
        return 0u;
    }

    if (out_values != nullptr) {
        const uint32_t count = value_capacity < REFII_RETICLE_VALUES ? value_capacity : REFII_RETICLE_VALUES;
        std::memcpy(out_values, values, count * sizeof(float));
    }
    if (out_words != nullptr) {
        const uint32_t count = word_capacity < REFII_RETICLE_WORDS ? word_capacity : REFII_RETICLE_WORDS;
        std::memcpy(out_words, words, count * sizeof(uint32_t));
    }
    return 1u;
}

}  // extern "C"
