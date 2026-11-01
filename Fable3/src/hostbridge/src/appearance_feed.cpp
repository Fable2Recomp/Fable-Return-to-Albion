// refii - Fable 3's character appearance, as its hooks state it (Fable3/src/hooks/src/
// appearance_hooks.cpp; the table in src/runtime/src/core/stated_appearance.cpp): each part's
// morph targets and each original texture's replacement. Both exports answer Fable 2's rows
// (Fable2/src/hostbridge/src/host_bridge.cpp), so the shell reads either title the same way.
//
// WHAT FABLE 3 STATES THAT FABLE 2 DID NOT: the dye is a COLOUR MASK command (type 30: mask
// texture, two colours, two strengths), staged as kind 3; the shell's decoder
// (HostBridge.DecodeTextureLayers) folds it into the original's composite as two tinted masked
// layers. The tint command's lane order is untraced, so no layer carries a tint yet.

#include <cstdint>

#include "core/payload_bridge.h"
#include "host_abi.h"

namespace {

/// Kinds 0 (composite), 1 (overlay), 2 (whole-entity swap) and 3 (colour mask): the ones the
/// shell decodes.
constexpr uint32_t kShellKinds = 0xFu;

}  // namespace

extern "C" {

/// Per morphed part, as the guest handed sub_82C4BB20: [model hash, count, then count x (target
/// model hash, weight float bits)]; the part itself takes 1 - sum. A part that is not listed draws
/// as its own model. `out_statements` low word counts every descriptor constructed with a target
/// list (0 means the hook never ran), high word those that listed a target above the threshold.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPartMorphs(uint32_t entity, uint32_t* out_words,
                                                            uint32_t word_capacity,
                                                            uint32_t* out_generation,
                                                            uint64_t* out_statements) {
    return refii_bridge_part_morphs_read(entity, out_words, word_capacity, out_generation, out_statements);
}

/// Per original texture the guest replaced on the entity: [original texture, 1 | 0 << 16, then
/// {replacement texture, 0, 1.0f bits, 0, 0, 0, 0}] — one opaque layer, the shell's base. Covers
/// the def's TextureMorphs, the skin set, the stat textures, tattoos and hair. Returns the words
/// written; `out_generation` moves when a rebuild changed the set.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityTextureLayers(uint32_t entity, uint32_t* out_words,
                                                               uint32_t word_capacity,
                                                               uint32_t* out_generation,
                                                               uint64_t* out_statements) {
    return refii_bridge_texture_layers_read(entity, out_words, word_capacity, kShellKinds, out_generation,
                                            out_statements);
}

}  // extern "C"
