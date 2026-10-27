// Every slide a presentation is TOLD to play, stated to the host.
//
// Every explicit slide change in the Gameface runtime — native screen code (`sub_82426BA8`,
// `sub_82426CA8`), the GUI registries, Lua `goToSlide`, a file's Logic commands, the page
// arrows, a completed go-next slide — reaches one call on the presentation's time interface
// (`pres + 0x238`, `sub_82173E98`; TU1 addresses, disc 0x82BAF540 / 0x82BAFA98):
//
//   sub_82BB5760(timeline, element, int slide, bool stated) — GotoSlide. `stated` is 1 for every
//       caller but the element walk's own entry when a component comes live (`sub_822849A0`
//       0x82284b38 passes 0), which the host's playback reproduces itself.
//   sub_82BB5CB8(timeline, element, bool paused) — SetPaused: `sub_82426CA8`'s play
//       (0x82426cf8), COMMAND_PLAY / COMMAND_PAUSE. The slide entry's own record-pause write
//       (`sub_82BB6408` 0x82bb64f0) happens INSIDE GotoSlide and is not posted.
//
// The other statements an element is told go through the presentation's element store
// (`pres + 0x1EC`, `sub_82284360`, its vtable +72 / +80; disc 0x82284300 / 0x82284750, tracks
// 0x82284218):
//
//   sub_82284798(store, element, attrId, value) — SetAttribute, change-only (`result[1] != a4`):
//       Lua `setAttribute` (`AKElement_setAttribute` 0x82bbb998), `COMMAND_SETPROPERTY`, native
//       writers. Also every active track every frame (`sub_822846B0`) and every slide command
//       inside GotoSlide; both are the host's own playback and are not posted.
//   sub_82284BE8(store, element, mask, on) — the flags writer. Mask 0x2000 is `active` (Lua
//       `setAttribute("active", b)` 0x82bbb774); the walk's 0x1000 / slide-entry writes are not
//       statements.
//
// GotoSlide and the two element-store writers are hooked in gui_element_hooks.cpp (the
// element change feed) and post through the Tell* functions here; SetPaused and the track
// evaluator are hooked here. Pushed after the original through the C ABI (`refii_bridge_told_push`)
// for the host to take (`RefiiHostTakeToldStatements`), the ordinal being the element's index in the
// presentation's depth-first walk (child +20, sibling +16, parent +12 — the order the host
// bridge's element sampler and every `.bgf` ordinal use). The root's name hash lets the host
// drop a statement made to a presentation that has since been rebuilt at the same address.
// Every entry is posted, a restatement included: GotoSlide has no same-slide guard
// (`sub_823FE848` calls `sub_82426CA8` unconditionally), so a restatement re-enters the slide
// at t = 0 in the guest as well.
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <unordered_map>
#include <utility>
#include <vector>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge.h"
#include "gui/gui_told_statements.h"
#include "hooks/ppc_externs.h"

namespace {

bool Plausible(uint32_t value) { return value >= 0x10000u && value < 0xC0000000u; }

constexpr uint32_t kTimelineInPresentation = 0x238u;
constexpr uint32_t kRootInPresentation = 492u + 8u;

/// One presentation's element → ordinal map, rebuilt when its tree is not the one it was built from.
struct Ordinals {
    uint32_t root = 0;
    uint32_t root_hash = 0;
    std::unordered_map<uint32_t, uint32_t> by_element;
};

std::mutex g_lock;
std::unordered_map<uint32_t, Ordinals> g_ordinals;

thread_local int t_inside_goto = 0;
thread_local int t_inside_tracks = 0;

constexpr uint32_t kStoreInPresentation = 0x1ECu;

void Build(uint8_t* base, uint32_t root, Ordinals& map) {
    map.root = root;
    map.root_hash = refii::vfs::LoadGuestU32(base, root);
    map.by_element.clear();
    uint32_t ordinal = 0;
    uint32_t element = root;
    while (element != 0 && ordinal < 16384u && Plausible(element)) {
        map.by_element[element] = ordinal++;
        const uint32_t child = refii::vfs::LoadGuestU32(base, element + 20u);
        if (Plausible(child)) {
            element = child;
            continue;
        }

        int climbed = 0;
        while (element != 0) {
            if (element == root || ++climbed > 16384) {
                element = 0;
                break;
            }

            const uint32_t sibling = refii::vfs::LoadGuestU32(base, element + 16u);
            if (Plausible(sibling)) {
                element = sibling;
                break;
            }

            const uint32_t parent = refii::vfs::LoadGuestU32(base, element + 12u);
            element = Plausible(parent) ? parent : 0;
        }
    }
}

/// The element's ordinal in its presentation, or false when it is not in the tree.
bool OrdinalOf(uint8_t* base, uint32_t presentation, uint32_t element, uint32_t& root_hash,
               uint32_t& ordinal) {
    const uint32_t root = refii::vfs::LoadGuestU32(base, presentation + kRootInPresentation);
    if (!Plausible(root) || !Plausible(element)) return false;

    std::lock_guard<std::mutex> lock(g_lock);
    Ordinals& map = g_ordinals[presentation];
    const uint32_t hash = refii::vfs::LoadGuestU32(base, root);
    auto found = map.by_element.find(element);
    if (map.root != root || map.root_hash != hash || found == map.by_element.end()) {
        Build(base, root, map);
        found = map.by_element.find(element);
        if (found == map.by_element.end()) return false;
    }

    root_hash = map.root_hash;
    ordinal = found->second;
    return true;
}

/// Pushes one statement for the host through the C ABI (payload_bridge.h); the queue lives in
/// refii_vfs (gui/gui_told_statements.cpp), which the bridge drains.
void Queue(uint8_t* base, refii::vfs::ToldStatement::Kind kind, uint32_t presentation, uint32_t element,
           uint32_t a, uint32_t b = 0u) {
    uint32_t root_hash = 0;
    uint32_t ordinal = 0;
    if (!OrdinalOf(base, presentation, element, root_hash, ordinal)) return;
    refii_bridge_told_push(static_cast<uint32_t>(kind), presentation, root_hash, ordinal, a, b);
}

/// The texture each front-end element last showed, by presentation then ordinal — what a writer changed.
std::unordered_map<uint32_t, std::unordered_map<uint32_t, uint32_t>> g_textures;

constexpr uint32_t kMaxTextUnits = 4096u;

/// The front end's active screen presentation (`GUI+188` → `+20`), when its root is `root`. The
/// field exists only on the derived GUI_FRONT_END object (`sub_82378230`); in game the GUI is the
/// 176-byte base, so the exact root match is what keeps a stale word from answering.
uint32_t FrontEndPresentation(uint8_t* base, uint32_t root) {
    const uint32_t gui = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496B08u));
    if (!Plausible(gui)) return 0;
    const uint32_t active = refii::vfs::LoadGuestU32(base, gui + 188u);
    if (!Plausible(active)) return 0;
    const uint32_t presentation = refii::vfs::LoadGuestU32(base, active + 20u);
    return Plausible(presentation) &&
                   (root == 0 || refii::vfs::LoadGuestU32(base, presentation + kRootInPresentation) == root)
               ? presentation
               : 0;
}

/// The presentation whose tree holds `element`: its root, climbed to through the parent links,
/// matched against the GUI player's presentation list (`GUI+16`, array +72, count +80).
uint32_t PresentationOf(uint8_t* base, uint32_t element) {
    uint32_t root = element;
    for (int depth = 0; depth < 256; ++depth) {
        const uint32_t parent = refii::vfs::LoadGuestU32(base, root + 12u);
        if (!Plausible(parent)) break;
        root = parent;
    }

    const uint32_t gui = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496B08u));
    if (!Plausible(gui)) return 0;
    const uint32_t player = refii::vfs::LoadGuestU32(base, gui + 16u);
    if (!Plausible(player)) return 0;
    const uint32_t array = refii::vfs::LoadGuestU32(base, player + 72u);
    const uint32_t count = refii::vfs::LoadGuestU32(base, player + 80u);
    if (!Plausible(array)) return 0;
    for (uint32_t i = 0; i < count && i < 256u; ++i) {
        const uint32_t presentation = refii::vfs::LoadGuestU32(base, array + i * 4u);
        if (Plausible(presentation) &&
            refii::vfs::LoadGuestU32(base, presentation + kRootInPresentation) == root) {
            return presentation;
        }
    }
    return FrontEndPresentation(base, root);
}

}  // namespace

namespace refii::vfs {

void TellText(uint8_t* base, uint32_t element) {
    if (!Plausible(element) || refii::vfs::LoadGuestU8(base, element + 30u) != 4u) return;
    const uint32_t holder = refii::vfs::LoadGuestU32(base, element + 4u);
    if (!Plausible(holder)) return;
    const uint32_t record = refii::vfs::LoadGuestU32(base, holder);
    if (!Plausible(record)) return;
    const uint32_t rep = refii::vfs::LoadGuestU32(base, record + 140u);
    if (!Plausible(rep)) return;
    const uint32_t data = refii::vfs::LoadGuestU32(base, rep);
    const uint32_t length = refii::vfs::LoadGuestU32(base, rep + 4u);

    const uint32_t presentation = PresentationOf(base, element);
    if (presentation == 0) return;
    uint32_t root_hash = 0;
    uint32_t ordinal = 0;
    if (!OrdinalOf(base, presentation, element, root_hash, ordinal)) return;
    std::vector<uint16_t> units;
    if (Plausible(data)) {
        const uint32_t count = std::min(length, kMaxTextUnits);
        units.reserve(count);
        for (uint32_t i = 0; i < count; ++i) {
            units.push_back(refii::vfs::LoadGuestU16(base, data + i * 2u));
        }
    }
    refii_bridge_told_text_push(presentation, root_hash, ordinal, units.data(),
                                static_cast<uint32_t>(units.size()));
}

void TellFrontEndTextures(uint8_t* base) {
    const uint32_t presentation = ::FrontEndPresentation(base, 0u);
    if (presentation == 0) return;
    const uint32_t root = refii::vfs::LoadGuestU32(base, presentation + kRootInPresentation);
    if (!Plausible(root)) return;

    std::vector<std::pair<uint32_t, uint32_t>> elements;
    uint32_t root_hash = 0;
    {
        std::lock_guard<std::mutex> lock(g_lock);
        Ordinals& map = g_ordinals[presentation];
        if (map.root != root || map.root_hash != refii::vfs::LoadGuestU32(base, root)) {
            Build(base, root, map);
            g_textures.erase(presentation);
        }
        root_hash = map.root_hash;
        elements.assign(map.by_element.begin(), map.by_element.end());
    }

    std::unordered_map<uint32_t, uint32_t>& shown = g_textures[presentation];
    for (const auto& [element, ordinal] : elements) {
        const uint32_t holder = refii::vfs::LoadGuestU32(base, element + 4u);
        if (!Plausible(holder)) continue;
        const uint32_t record = refii::vfs::LoadGuestU32(base, holder);
        if (!Plausible(record)) continue;
        const uint32_t hash = refii::vfs::LoadGuestU32(base, record + 512u);
        auto found = shown.find(ordinal);
        if (found != shown.end() ? found->second == hash : hash == 0u) continue;
        shown[ordinal] = hash;

        refii_bridge_told_push(static_cast<uint32_t>(ToldStatement::Kind::kTexture), presentation,
                               root_hash, ordinal, hash, 0u);
    }
}

void TellGotoSlideBegin() { ++t_inside_goto; }

void TellGotoSlideEnd(uint8_t* base, uint32_t timeline, uint32_t element, uint32_t slide, bool stated) {
    --t_inside_goto;
    if (stated && Plausible(timeline)) {
        Queue(base, ToldStatement::Kind::kGotoSlide, timeline - kTimelineInPresentation, element, slide);
    }
}

void TellAttribute(uint8_t* base, uint32_t store, uint32_t element, uint32_t attribute, uint32_t value) {
    if (t_inside_goto == 0 && t_inside_tracks == 0 && Plausible(store)) {
        Queue(base, ToldStatement::Kind::kAttribute, store - kStoreInPresentation, element, attribute, value);
    }
}

void TellFlags(uint8_t* base, uint32_t store, uint32_t element, uint32_t mask, bool on) {
    if (mask == 0x2000u && t_inside_goto == 0 && t_inside_tracks == 0 && Plausible(store)) {
        Queue(base, ToldStatement::Kind::kActive, store - kStoreInPresentation, element, on ? 1u : 0u);
    }
}

}  // namespace refii::vfs

// SetPaused: TU1 0x82BB5CB8.
REX_EXTERN(rex_lhGfxTimeline_SetPaused) {
    const uint32_t timeline = ctx.r3.u32;
    const uint32_t element = ctx.r4.u32;
    const uint32_t paused = (ctx.r5.u32 & 0xFFu) != 0u ? 1u : 0u;
    __imp__rex_lhGfxTimeline_SetPaused(ctx, base);
    if (t_inside_goto == 0 && Plausible(timeline)) {
        Queue(base, refii::vfs::ToldStatement::Kind::kPaused, timeline - kTimelineInPresentation, element, paused);
    }
}

// Track evaluation: TU1 0x822846B0 — every active track's write is the host's own playback.
REX_EXTERN(rex_lhGfxElements_EvaluateTracks) {
    ++t_inside_tracks;
    __imp__rex_lhGfxElements_EvaluateTracks(ctx, base);
    --t_inside_tracks;
}

// The front end's menu dispatch: TU1 0x826DFD58. Its Load Game path stamps blank.tex on every card
// (0x826e06f4) and each save's portrait (`sub_826E1408`, from the save's QuestName through the
// `<quest>_Epilogue_Screen_Image` GDB record's ScreenShot); its dialogs set the Y glyph
// (`sub_826E40E0`). The stamped hashes are the guest's answer, taken as it returns.
PPC_EXTERN_FUNC(__imp__rex_lhGui_FrontendRoot_MenuActionDispatch);
REX_EXTERN(rex_lhGui_FrontendRoot_MenuActionDispatch) {
    __imp__rex_lhGui_FrontendRoot_MenuActionDispatch(ctx, base);
    refii::vfs::TellFrontEndTextures(base);
}
