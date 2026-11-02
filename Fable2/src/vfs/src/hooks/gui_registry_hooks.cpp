// The GUI registries' SLIDE writers, stated to the host.
//
// A `.bgf` element binds to a name in one of the GUI manager's typed registries (the vector at
// `*(*(GUI+0x20)+0x20)+28`: [2] SlideFaderName, [3] SlideFader2Name, [9] SlideToggleName,
// [10] SlideSetName, …) and the guest drives it BY NAME. Three writers play slides on every element
// bound to a name, write-through, so the registry holds no value a host could read back (TU1
// addresses; disc 0x8257B408, 0x8257C9C8, 0x82845CB8):
//
//   sub_823FE848(registry, nameHash, bool on)   — toggle: slide 2 on, 1 off (`sub_82426CA8`)
//   sub_828421F8(registry, nameHash, int slide) — fader / slide set, entered (`sub_82426BA8`)
//   sub_827FA600(registry, nameHash, int slide) — the same, entered and played (`sub_82426CA8`);
//                                                  the fade natives' path
//
// All three are hooked AFTER the original and post `@Gui.Slide registry hash slide played` on the
// GUI call feed, so a Godot-drawn screen takes the slide from the guest's statement by name rather
// than from the presentation it plays on. `played` is 1 for the writers that go through
// `sub_82426CA8` (slide entry, then `SetPaused(0)` 0x82426cf8) and 0 for `sub_82426BA8`, which
// leaves an authored-paused slide waiting at t = 0. Posted on CHANGE only: the TopBox publisher
// `sub_821787D0` restates its toggles every frame.
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <string>
#include <unordered_map>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "hooks/ppc_externs.h"

namespace {

bool Plausible(uint32_t value) { return value >= 0x10000u && value < 0xC0000000u; }

/// The registry's index in the manager's vector, or -1 when it is not one of them.
int RegistryIndex(uint8_t* base, uint32_t registry) {
    const uint32_t gui = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496B08u));
    if (!Plausible(gui)) return -1;
    const uint32_t manager = refii::vfs::LoadGuestU32(base, gui + 0x20u);
    if (!Plausible(manager)) return -1;
    const uint32_t vector = refii::vfs::LoadGuestU32(base, manager + 0x20u);
    if (!Plausible(vector)) return -1;
    const uint32_t begin = refii::vfs::LoadGuestU32(base, vector + 28u);
    const uint32_t end = refii::vfs::LoadGuestU32(base, vector + 32u);
    if (!Plausible(begin) || end <= begin || (end - begin) > 0x400u) return -1;
    for (uint32_t at = begin, index = 0; at < end; at += 4u, ++index) {
        if (refii::vfs::LoadGuestU32(base, at) == registry) return static_cast<int>(index);
    }
    return -1;
}

std::mutex g_said_lock;
std::unordered_map<uint64_t, uint32_t> g_said;

void State(uint8_t* base, uint32_t registry, uint32_t hash, uint32_t slide, bool played) {
    const int index = RegistryIndex(base, registry);
    if (index < 0) return;
    {
        const uint64_t key = (static_cast<uint64_t>(index) << 32) | hash;
        std::lock_guard<std::mutex> lock(g_said_lock);
        auto found = g_said.find(key);
        if (found != g_said.end() && found->second == slide) return;
        g_said[key] = slide;
    }

    refii::vfs::GuiNativeCall call;
    call.name = "@Gui.Slide";
    for (const uint32_t value : {static_cast<uint32_t>(index), hash, slide, played ? 1u : 0u}) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::to_string(value);
        call.arguments.push_back(std::move(argument));
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

// Toggle: TU1 0x823FE848.
REX_EXTERN(rex_lhGuiRegistry_SetToggle) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const uint32_t slide = (ctx.r5.u32 & 0xFFu) != 0u ? 2u : 1u;
    __imp__rex_lhGuiRegistry_SetToggle(ctx, base);
    State(base, registry, hash, slide, true);
}

// Fader / slide set: TU1 0x828421F8.
REX_EXTERN(rex_lhGuiRegistry_SetSlide) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const uint32_t slide = ctx.r5.u32;
    __imp__rex_lhGuiRegistry_SetSlide(ctx, base);
    State(base, registry, hash, slide, false);
}

// Fader / slide set, jumping: TU1 0x827FA600.
REX_EXTERN(rex_lhGuiRegistry_JumpSlide) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const uint32_t slide = ctx.r5.u32;
    __imp__rex_lhGuiRegistry_JumpSlide(ctx, base);
    State(base, registry, hash, slide, true);
}

// Image: TU1 0x82804C28, `(registry, nameHash, const u32* textureHash)` — writes the resource
// hash onto every bound quad (`+0x200`, dirty `+0x224`). Posted `@Gui.Image hash texture` on
// change. The disc build has no such function: its one publisher, the epilogue `sub_824F23A0`
// (TU1 `sub_823D3FF0`), inlines the loop, so the original is weak there and the disc states the
// same pair through RefiiGuiImageInlined below. TU1's other caller, the episodic screen
// `sub_823D02D8` ("EpisodeImage"), is DLC the disc build does not contain.
extern "C" __attribute__((weak)) REX_FUNC(__imp__rex_lhGuiRegistry_SetImage);

namespace {

std::unordered_map<uint32_t, uint32_t> g_images;

void StateImage(uint32_t hash, uint32_t texture) {
    {
        std::lock_guard<std::mutex> lock(g_said_lock);
        auto found = g_images.find(hash);
        if (found != g_images.end() && found->second == texture) return;
        g_images[hash] = texture;
    }

    refii::vfs::GuiNativeCall call;
    call.name = "@Gui.Image";
    for (const uint32_t number : {hash, texture}) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::to_string(number);
        call.arguments.push_back(std::move(argument));
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

REX_EXTERN(rex_lhGuiRegistry_SetImage) {
    if (&__imp__rex_lhGuiRegistry_SetImage == nullptr) {
        return;
    }

    const uint32_t hash = ctx.r4.u32;
    const uint32_t value = ctx.r5.u32;
    const uint32_t texture = Plausible(value) ? refii::vfs::LoadGuestU32(base, value) : 0u;
    __imp__rex_lhGuiRegistry_SetImage(ctx, base);
    StateImage(hash, texture);
}

extern "C" uint8_t* RefiiGuestBase(void);

// The disc epilogue's inlined image write (disc config only). At 0x824F283C `bl sub_822A9A20`
// finds the registry entry: r4 = the name hash (`lwz r4, dword_834983D4@l(r10)` 0x824F2838) and
// the texture is the handle at r1+0x58 (`lwz r9, 0xA0+var_48(r1)` 0x824F2858, stored to every
// quad's +0x200 at 0x824F288C) — the arguments TU1 passes SetImage.
void RefiiGuiImageInlined(PPCRegister& r1, PPCRegister& r4) {
    uint8_t* base = RefiiGuestBase();
    if (base == nullptr) {
        return;
    }
    StateImage(r4.u32, refii::vfs::LoadGuestU32(base, r1.u32 + 0x58u));
}
