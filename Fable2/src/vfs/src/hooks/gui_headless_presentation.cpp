// HEADLESS GAMEFACE PRESENTATIONS, Fable 2's hooks. The guest keeps its UI objects; it never
// builds a .bgf scene. The work is the shared headless core's (core/headless_presentation.h,
// src/payload/src/core/headless_presentation.cpp): these hooks are its shims, and Fable2Table()
// is Fable 2's Gameface for it — every address TU1, converted to the running build.
//
// THE CREATE. `CGameface` vtable +28 (TU1 `sub_82BBA588`, disc `sub_82BB3E88`; vtable TU1
// 0x82002F68, disc 0x82002F88) is the only maker of a presentation. Its callers are
// lhGuiPresentation_LoadIfUnloaded (TU1 `sub_823DCB10` 0x823dcbb0, disc `sub_823FE828`) and the
// CGameface's own loader (+16 `sub_82BBA370` 0x82bba47c). Shipped, it makes a CPresentation in the
// kernel (`sub_82BEDBC0`: kernel alloc 876, ctor `sub_82BAFA28`, kernel list push), names it
// (vf +72), gives it its Lua engine (0x82bba624..0x82bba718), deserialises the .bgf (CGameface+528
// vf +12) and adds its scene to the renderer. The core makes, names and gives an engine to the same
// CPresentation with the same calls (GiveEngine below is Fable 2's engine sequence) and does none
// of the rest. The engine is the mods' front-end Lua state (mod_frontend_lua.cpp); no guest chunk
// reaches it (the runner and `sub_82372500` are swallowed in gui_menu_hooks.cpp /
// resource_manager_open.cpp).
//
// ELEMENTS ARE MADE AS THE GUEST NAMES THEM, AS THE FILE HAS THEM. Child-by-name, element service
// vf +84 (TU1 `sub_82BB5658`, disc `sub_82BACC90`; r3 service, r4 name hash, r5 parent or 0), and
// the child walk, vf +92 (TU1 `sub_82BB56A8`, disc `sub_82BACCE0`), make a headless parent's
// children from the .bgf element table, laid out as the guest's deserialiser (TU1 `sub_82BC8038`,
// disc `sub_82BC1968`) would have left them: the file's active bit as flag 0x2000 (the element
// builders' `vf+80(e, 0x2000, file bit 0)`, `sub_82BB2E18` / `sub_82BB2F10`), 0x0001 on a component
// (its own timeline; attributes at +68, else +32), 0x0002 on one with a start or end time, its
// numeric attribute records in the store's word form, and a component's slide base and count
// (`sub_82BB3778`) over the presentation's slide records at Mgr:Slides +8 (pres +628). So:
//  - the attribute lookup (TU1 `sub_822848E0` / `sub_82284948`, disc `sub_82284448` /
//    `sub_822844B0`) and everything through it answers the file's value until the guest writes it,
//    and the store (`sub_82284798`, vf +72) writes into that record — the show/hide helper
//    (`sub_82426260`, disc `sub_823F86F8`) reads the authored opacity at 0x82426294, GetPosition
//    (0x824252E8, disc 0x823F7780) the authored or last-set position (the credits' scroll
//    `sub_826B7D00` 0x826b7d60, the pause minimap's origin 0x826a22b0);
//  - the active bit (vf +76) is the file's until SetActive / the flags writer changes it, and the
//    presentation Update's tree walk (`sub_822849A0`, entered on root 0x2000 at 0x822ac9e0) runs
//    over the tree as over a loaded one;
//  - GotoSlide (`sub_82BB5760`) lands on a component (count +42, base +40) and GetState
//    (`sub_82426D80`, through the timeline-owner walk `sub_82BB9AB0`) answers it. The slide
//    records carry no actions: Godot plays the slides from the statements.
// The root always owns its timeline: the owner walk has no null exit (0x82bb9af0..0x82bb9b48).
// +4 (the render node) stays 0 on every element: the writers that take the node's record null-check
// it, and the quad readers' joins that do not are answered with the core's inert quad record
// (RefiiHeadlessQuadRecord). String attributes are answered at the attribute-string read.
//
// THE SCENE, THE DESTROY, THE PER-FRAME PASS: see the core. `CGameface` vtable +32 (TU1
// `sub_82BBA8D0`, disc `sub_82BB41D0`, named rex_lhItemManager_NotifyAndRemoveItem in the config),
// vtable +40 (TU1 `sub_822BE860`, disc `sub_822BE580`). The scene's vtable is CScene's (TU1
// 0x82003E38, disc 0x82003E58), called unchecked as vf +16 by `sub_823DCF98` 0x823dd010,
// PopulateMainMenu 0x826e21b0, MenuActionDispatch 0x826e0ac8 / 0x826e0bd4 and the message-box fit
// `sub_823CEFA8` 0x823cf1f4.
//
// THE ROOT'S TIME. The co-op screen (TU1 `sub_82436548`, disc `sub_82477768`) gates player two's
// preview on the shared slot's root timeline time, `lwz r11,0x30(r3)` straight after
// GetRootElement: >= 1000 at 0x82436930 and >= 500 at 0x82436988. GetRootElement writes the root's
// +48 = clock − creation clock.
//
// EXISTENCE-CHECKED WRITES. SetPosition (TU1 `sub_824251E0`, disc `sub_823F7678`: r3 element, r4
// presentation, f1..f3; position.x checked through service vf +12 at 0x8242524c, then x/y/z
// `dword_834BE51C/520/524` through the store), SetRotation (TU1 `sub_824254C8`, disc
// `sub_823F7960`: rotation.x `dword_834BE528` checked by `sub_82284948` at 0x82425530, then
// x/y/z 528/52C/530) and SetOpacity (`GuiElement_SetOpacity_p` TU1 0x82426078, disc
// `sub_823F8510`: opacity `dword_834BE518`) write only when the element carries the attribute. A
// file element carries what the file authored, as a loaded one does, and the guest's own write and
// its statement (the store's, gui_element_hooks.cpp) follow from that. A fileless element (no
// table, or a name the file has not got) is given the attribute at the entry (RememberWrite), so
// its write lands too; only when it has no room left is the write stated here from the arguments,
// the opacity raw (SetOpacity scales its argument by 1024.0, `flt_82101240`, when the CURRENT
// opacity is below 0.09765625, `flt_83319B60`, 0x824260e0..0x824260fc) with kScaledOpacity on the
// id.
//
// THE FRONT END'S TEXTURE STAMPS. Its writers store a quad record's +512 inline (no setter) and
// only through +4, so on a headless element they skip. Each is stated where it takes the element,
// from the same element and hash it would store: the Load Game path's blank on every card
// (`GUI_FrontendRoot_MenuActionDispatch` TU1 0x826e06cc / disc 0x826c8afc, and the main menu's
// rebuild PopulateMainMenu 0x826e20f0 / disc 0x826ca550, where the disc build's inlined
// `sub_82426798` returns; hash `dword_8349B600`), each save's screenshot (`sub_826E1408` 0x826e14b0
// / disc `sub_826C9828` 0x826c98d0, r30 the hash), and the dialog's Y glyph (`sub_826E40E0`
// 0x826e421c cancel `dword_8349B694` / 0x826e4284 `dword_8349B690`; disc `sub_826CB158` 0x826cb290 /
// 0x826cb2f8).
//
// TEXTS. SetText (TU1 `sub_82425DD0`: r3 element, r4 presentation, r5 the text's handle) and
// SetTextFromTag (`sub_82425F40`: r3 element, r4 the tag) store only through +4 and bail without
// it. Each is stated at its entry (midasm) for a headless element: SetText's string as given;
// SetTextFromTag's through the guest's own lookup `sub_8230E0E8` (Babel, TEXT_ fallback), as the
// writer would have stored it (0x82425f78..0x82425fb8).

#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/headless_presentation.h"
#include "core/payload_bridge_fable2.h"
#include "gui/gui_told_statements.h"
#include "hooks/ppc_externs.h"

extern "C" uint8_t* RefiiGuestBase(void);

namespace {

using refii::vfs::LoadGuestU16;
using refii::vfs::LoadGuestU32;
using refii::vfs::StoreGuestU32;

// The kernel allocator and the presentation's ctor (`sub_82BEDBC0` 0x82bedbe8..0x82bedc14).
constexpr refii::guest::Addr kKernelAllocator{0x82BAEE60u};  // returns the allocator
constexpr refii::guest::Addr kKernelAlloc{0x82BAF660u};      // (allocator, size, class, file, line)
constexpr refii::guest::Addr kKernelFree{0x82BAF7B0u};       // (allocator, ptr, size)
constexpr refii::guest::Addr kPresentationCtor{0x82BAFA28u};  // (this, name, gameface)
constexpr refii::guest::Addr kPresentationClassName{0x820FFF98u};  // "class CPresentation"
constexpr refii::guest::Addr kKernelSourceName{0x820FFF30u};       // "...AKKernel.cpp"
constexpr uint32_t kKernelSourceLine = 71u;                        // `li r7,0x47`
constexpr uint32_t kPresentationSize = 876u;                       // `li r4,0x36C`

// The create's engine steps, 0x82bba61c..0x82bba718 (disc 0x82bb3f1c..0x82bb4018).
constexpr refii::guest::Addr kEngineFactory{0x83314AC0u};    // -> `sub_82BC4548`(pres)
constexpr refii::guest::Addr kGameplanFactory{0x83314AC4u};  // -> `li r3,0`
constexpr refii::guest::Addr kRegisterDataPort{0x82BC5DA8u};  // (gameface + 0x214, L)
constexpr refii::guest::Addr kLuaSetField{0x82BCC600u};       // (L, index, char*)
constexpr refii::guest::Addr kLuaPushClosure{0x8227B968u};    // (L, fn, 0)
constexpr refii::guest::Addr kFireGameplanEvent{0x82BC5B50u};
constexpr refii::guest::Addr kFireEventOnPresentation{0x82BC5C38u};
constexpr refii::guest::Addr kNameAkGameplan{0x820FF2B8u};         // "AKGameplan"
constexpr refii::guest::Addr kNameFireGameplanEvent{0x820FF2C4u};  // "fireGameplanEvent"
constexpr refii::guest::Addr kNameFireEventOnPresentation{0x820FF2D8u};
constexpr uint32_t kLuaGlobalsIndex = 0xFFFFD8EEu;  // -10002, `li r4,-0x2712`
constexpr uint32_t kGamefaceDataPorts = 0x214u;     // `addi r3,r28,0x214`
constexpr uint32_t kGamefaceGameplan = 0x248u;      // `lwz r9,0x248(r28)`, pushed as light userdata
constexpr uint32_t kLuaTop = 8u;                    // lua_State +8
constexpr uint32_t kLuaLightUserdata = 2u;          // `li r11,2`
constexpr uint32_t kPresEngine = 28u;               // vf +16 `stw r4,0x1C(r3)`
constexpr uint32_t kPresGameplan = 32u;             // `stw r3,0x20(r31)` 0x82bba718
constexpr uint32_t kPresSlotSetEngine = 16u;

constexpr refii::guest::Addr kNameHash{0x82189140u};       // h = 65599*h + c, NUL folded
constexpr refii::guest::Addr kParametersName{0x8349AF78u};  // CCharString "Parameters"
constexpr refii::guest::Addr kSceneVtable{0x82003E38u};     // CScene, ctor `sub_82BC9918`

// SetTextFromTag's own steps (0x82425f78 / 0x82425f84 / 0x82425fb8).
constexpr refii::guest::Addr kCharStringCopy{0x821F0240u};    // (out, CCharString*) -> out
constexpr refii::guest::Addr kTextLookup{0x8230E0E8u};        // (out wide, CCharString*), frees the arg
constexpr refii::guest::Addr kWideStringDtor{0x821C67F8u};    // (wide*)

// CCharString's own steps: GetAttributeString's result (0x824250f8), its dtor, and the texture
// setter's hash of a name (`sub_8286DDC0` 0x8286de1c..0x8286de3c: copy, lower case, hash, dtor).
// The ctor hands r5 straight to its body (`sub_822078A0`, disc `sub_822079D8`): -1 measures the
// text — every guest caller loads `li r5,-1` (0x824250f0).
constexpr refii::guest::Addr kCharStringFromCStr{0x8222CED0u};  // (this, char*, length) -> this
constexpr refii::guest::Addr kCharStringDtor{0x82214DD8u};      // (this)
constexpr refii::guest::Addr kCharStringLower{0x821EEB38u};     // (out, CCharString*) -> out
constexpr refii::guest::Addr kResourceNameHash{0x82C037F0u};    // (CCharString*) -> hash

// An attribute's id: the name hash (`sub_82189140`) in 27 bits (0x82425080..0x8242508c).
constexpr uint32_t kAttributeIdMask = 0x07FFFFFFu;

// The attribute ids' globals (static init 0x83294294 onward; the setters load them).
constexpr refii::guest::Addr kAttrStartTime{0x834BE510u};  // the deserialiser's 0x0002 pair (0x82bc83ec)
constexpr refii::guest::Addr kAttrEndTime{0x834BE514u};
constexpr refii::guest::Addr kAttrOpacity{0x834BE518u};
constexpr refii::guest::Addr kAttrPositionX{0x834BE51Cu};
constexpr refii::guest::Addr kAttrPositionY{0x834BE520u};
constexpr refii::guest::Addr kAttrPositionZ{0x834BE524u};
constexpr refii::guest::Addr kAttrRotationX{0x834BE528u};
constexpr refii::guest::Addr kAttrRotationY{0x834BE52Cu};
constexpr refii::guest::Addr kAttrRotationZ{0x834BE530u};
constexpr uint32_t kScaledOpacity = 0x80000000u;  // the id's top bit: SetOpacity_p's raw argument

constexpr refii::guest::Addr kCardBlankTexture{0x8349B600u};
constexpr refii::guest::Addr kGlyphCancelTexture{0x8349B694u};
constexpr refii::guest::Addr kGlyphOtherTexture{0x8349B690u};

constexpr refii::guest::Addr kQuadRecordCtor{0x8286DAC8u};  // (this) -> this
constexpr uint32_t kQuadRecordSize = 576u;                  // `li r3,0x240` in `sub_829B8480`

constexpr uint32_t kMaxTextUnits = 4096u;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

bool Milestone(uint64_t n) { return n <= 32u || (n & (n - 1u)) == 0u; }

uint32_t Call(PPCContext& ctx, uint8_t* base, uint32_t address, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u,
              uint32_t r6 = 0u) {
    PPCFunc* fn = refii::vfs::FindGuestFunction(address);
    if (fn == nullptr) {
        REXLOG_ERROR("Refii headless presentation: guest function 0x{:08X} is not a recompiled entry", address);
        return 0u;
    }
    PPCContext call = ctx;
    call.r3.u32 = r3;
    call.r4.u32 = r4;
    call.r5.u32 = r5;
    call.r6.u32 = r6;
    call.ctr.u32 = address;
    fn(call, base);
    return call.r3.u32;
}

/// One guest call on a frame below `sp`.
uint32_t CallAt(PPCContext& ctx, uint8_t* base, uint32_t sp, uint32_t address, uint32_t r3, uint32_t r4 = 0u) {
    PPCContext at = ctx;
    at.r1.u32 = sp;
    return Call(at, base, address, r3, r4);
}

uint32_t Slot(uint8_t* base, uint32_t object, uint32_t offset) {
    return LoadGuestU32(base, LoadGuestU32(base, object) + offset);
}

/// Fable 2's create engine steps 0x82bba61c..0x82bba718: the engine (vf +16), the data ports,
/// the AKGameplan light userdata and its two closures, then the gameplan (+32).
void GiveEngine(PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t presentation) {
    const uint32_t engine = Call(ctx, base, LoadGuestU32(base, kEngineFactory), presentation);
    Call(ctx, base, Slot(base, presentation, kPresSlotSetEngine), presentation, engine);
    if (Plausible(engine)) {
        const uint32_t L = LoadGuestU32(base, engine + 4u);
        Call(ctx, base, kRegisterDataPort, gameface + kGamefaceDataPorts, L);
        const uint32_t top = LoadGuestU32(base, L + kLuaTop);
        StoreGuestU32(base, top, LoadGuestU32(base, gameface + kGamefaceGameplan));
        StoreGuestU32(base, top + 4u, kLuaLightUserdata);
        StoreGuestU32(base, L + kLuaTop, top + 8u);
        Call(ctx, base, kLuaSetField, L, kLuaGlobalsIndex, kNameAkGameplan);
        Call(ctx, base, kLuaPushClosure, L, kFireGameplanEvent, 0u);
        Call(ctx, base, kLuaSetField, L, kLuaGlobalsIndex, kNameFireGameplanEvent);
        Call(ctx, base, kLuaPushClosure, L, kFireEventOnPresentation, 0u);
        Call(ctx, base, kLuaSetField, L, kLuaGlobalsIndex, kNameFireEventOnPresentation);
    }
    StoreGuestU32(base, presentation + kPresGameplan,
                  Call(ctx, base, LoadGuestU32(base, kGameplanFactory), presentation));
}

uint32_t GuestAlloc(PPCContext& ctx, uint8_t* base, uint32_t size) { return refii::vfs::CallLhMalloc(ctx, base, size); }

void GuestFree(PPCContext& ctx, uint8_t* base, uint32_t address) { refii::vfs::CallLhFree(ctx, base, address); }

PPCFunc* Find(uint32_t address) { return refii::vfs::FindGuestFunction(address); }

refii::headless::GamefaceTable BuildTable() {
    refii::headless::GamefaceTable t;
    t.title = "Fable 2";
    t.find = &Find;
    t.alloc = &GuestAlloc;
    t.free = &GuestFree;
    t.give_engine = &GiveEngine;
    t.kernel_allocator = kKernelAllocator;
    t.kernel_alloc = kKernelAlloc;
    t.kernel_free = kKernelFree;
    t.presentation_ctor = kPresentationCtor;
    t.presentation_class_name = kPresentationClassName;
    t.kernel_source_name = kKernelSourceName;
    t.kernel_source_line = kKernelSourceLine;
    t.presentation_size = kPresentationSize;
    // CPresentation (ctor `sub_82BAFA28`; vtable TU1 0x82006D48, disc 0x82006D68).
    t.pres_scene = 24u;          // vf +8 sets, vf +12 gets
    t.pres_engine = kPresEngine;
    t.pres_gameplan = kPresGameplan;
    t.pres_service = 492u;       // vf +32 `addi r3,r3,0x1EC`
    t.pres_root = 500u;          // service +8
    t.pres_param_records = 596u; // params (+592) +4
    t.pres_param_count = 600u;   // params +8, slot 1 `lwz r3,8(r3)`
    t.pres_param_size_a = 608u;  // params +16, the dtor's free size
    t.pres_param_size_b = 612u;  // params +20
    t.pres_slide_records = 628u; // Mgr:Slides (vf +40 `addi r3,r3,0x26C`) +8, read by vf +4 / +8
    t.pres_slot_dtor = 0u;       // `sub_82BAFD90`(this, flags)
    t.pres_slot_update = 4u;     // `sub_822AC880`(this, clock)
    t.pres_slot_nullsub = 68u;   // nullsub_1, `blr`
    t.pres_slot_set_name = 72u;  // `sub_82BB0790`(this, char*)
    t.gameplan_slot_release = 12u;  // destroy's `(+32)->vf12` 0x82bba924
    // CGameface: the clock object (+8, vf +16 = ms), the presentation callback (+588, ctx +592).
    t.gameface_clock = 8u;
    t.clock_slot_ms = 16u;
    t.gameface_callback = 588u;
    t.gameface_callback_ctx = 592u;
    // CScene: 16 slots; +16 `sub_821858F8` resets the render lists, +28 `lwz r3,0x6F0(r3)`.
    t.scene_vtable = kSceneVtable;
    t.scene_slots = 16u;
    t.scene_slot_refresh = 4u;
    t.scene_slot_list = 7u;
    t.scene_render_list = 0x6F0u;
    t.name_hash = kNameHash;
    t.attribute_id_mask = kAttributeIdMask;
    t.parameters_name = kParametersName;
    t.char_string_from_cstr = kCharStringFromCStr;
    t.char_string_dtor = kCharStringDtor;
    t.attr_start_time = kAttrStartTime;
    t.attr_end_time = kAttrEndTime;
    t.quad_record_ctor = kQuadRecordCtor;
    t.quad_record_size = kQuadRecordSize;
    return t;
}

/// Fable 2's Gameface for the core: built on first use, once the payload has stated its build.
const refii::headless::GamefaceTable& Table() {
    static const refii::headless::GamefaceTable table = BuildTable();
    return table;
}

/// A wide string handle's units (`rep = *handle`, `*rep` the units, `rep+4` the length — the
/// layout TellText reads at a record's +140).
std::vector<uint16_t> WideUnits(uint8_t* base, uint32_t handle) {
    std::vector<uint16_t> units;
    const uint32_t rep = Plausible(handle) ? LoadGuestU32(base, handle) : 0u;
    if (!Plausible(rep)) {
        return units;
    }
    const uint32_t data = LoadGuestU32(base, rep);
    const uint32_t length = std::min(LoadGuestU32(base, rep + 4u), kMaxTextUnits);
    if (!Plausible(data)) {
        return units;
    }
    units.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
        units.push_back(LoadGuestU16(base, data + i * 2u));
    }
    return units;
}

std::atomic<uint64_t> g_passes{0};
std::atomic<uint64_t> g_texts{0};
std::atomic<uint64_t> g_absent_writes{0};
std::atomic<uint64_t> g_inert_answers{0};
std::atomic<uint64_t> g_textures_named{0};
std::atomic<uint64_t> g_stated_writes{0};

void StateText(uint32_t element, const std::vector<uint16_t>& units) {
    if (!refii::headless::TellText(element, units)) {
        return;
    }
    const uint64_t n = ++g_texts;
    if (Milestone(n)) {
        REXLOG_INFO("Refii headless presentation: text of {} unit(s) told to element 0x{:08X} (#{})", units.size(),
                    element, n);
    }
}

/// The ctx a midasm's register belongs to: generated code passes `ctx.rN` (codegen
/// builders/context.cpp `BuilderContext::r`), and r3 is PPCContext's first member.
PPCContext& ContextOf(PPCRegister& r3) {
    static_assert(offsetof(PPCContext, r3) == 0u, "r3 is PPCContext's first member");
    return reinterpret_cast<PPCContext&>(r3);
}

}  // namespace

namespace refii::vfs {

bool HeadlessElementKey(uint32_t element, uint32_t* presentation, uint32_t* serial, uint32_t* id) {
    return refii::headless::ElementKey(element, presentation, serial, id);
}

}  // namespace refii::vfs

/// CGameface vtable +28 — (r3 gameface, r4 path) → a headless presentation, or 0.
REX_EXTERN(rex_CGameface_CreatePresentation) {
    REX_FUNC_PROLOGUE();
    ctx.r3.u32 = refii::headless::Create(Table(), ctx, base, ctx.r3.u32, ctx.r4.u32);
}

/// CGameface vtable +32 — (r3 gameface, r4 presentation).
REX_EXTERN(rex_lhItemManager_NotifyAndRemoveItem) {
    REX_FUNC_PROLOGUE();
    if (!refii::headless::Destroy(Table(), ctx, base, ctx.r3.u32, ctx.r4.u32)) {
        __imp__rex_lhItemManager_NotifyAndRemoveItem(ctx, base);
        return;
    }
    ctx.r3.u32 = 0u;
}

/// Element service vf +84 — (r3 service, r4 name hash, r5 parent or 0). The original answers;
/// a headless presentation makes the element it lacks.
REX_EXTERN(rex_lhGuiAttributeService_ChildByName) {
    REX_FUNC_PROLOGUE();
    const uint32_t service = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const uint32_t parent = ctx.r5.u32;
    __imp__rex_lhGuiAttributeService_ChildByName(ctx, base);
    if (ctx.r3.u32 != 0u) {
        return;
    }
    const uint32_t element = refii::headless::MakeNamed(Table(), ctx, base, service, hash, parent);
    if (element != 0u) {
        ctx.r3.u32 = element;
    }
}

/// Element service vf +92 — (r3 service, r4 parent, r5 the out array, r6 every child or only those
/// vf +4 passes): a headless parent's file children are made first.
REX_EXTERN(rex_lhGuiAttributeService_CollectChildren) {
    REX_FUNC_PROLOGUE();
    refii::headless::ExpandBeforeWalk(Table(), ctx, base, ctx.r3.u32, ctx.r4.u32);
    __imp__rex_lhGuiAttributeService_CollectChildren(ctx, base);
}

/// GuiElement_AttributeString_p (TU1 `sub_82425038`, disc `sub_823F74D0`) — (r3 the out
/// CCharString, r4 element, r5 presentation, r6 the attribute's name as a CCharString): the
/// string attribute whose id is the name's hash in 27 bits, "" when the element has none. A
/// headless element carries no string attribute, so its answer is the FILE's, constructed by the
/// guest's own CCharString ctor in place of the empty one — how every typed registry's file
/// (`GuiRegistry_File` 0x82846148, vf +12 the attribute: MeterName, TextName, SlideToggleName, …)
/// binds a headless HUD element to its name.
REX_EXTERN(rex_GuiElement_GetAttributeString) {
    REX_FUNC_PROLOGUE();
    const uint32_t out = ctx.r3.u32;
    const uint32_t element = ctx.r4.u32;
    const uint32_t presentation = ctx.r5.u32;
    const uint32_t name = ctx.r6.u32;
    __imp__rex_GuiElement_GetAttributeString(ctx, base);
    if (refii::headless::AnswerAttributeString(Table(), ctx, base, out, element, presentation, name)) {
        ctx.r3.u32 = out;
    }
}

namespace {

/// THE HUD REGISTRIES' NAMES, counted. The GUI's typed registries (`*(*(GUI+32)+32)` +28..+32,
/// `sub_827F0F00`) each hold a map at +4 whose size is +12; the filer (`sub_827F1880`) makes a
/// name per bound element, a screen's unfile (`sub_827F1A48`) erases the names its elements
/// carry, and every writer and read-back (`sub_822A9C60`) answers nothing for a name the map does
/// not hold. Logged when any count changes, so a run says when names leave and how many.
void CensusHudRegistries(uint8_t* base) {
    static std::mutex m;
    static std::vector<uint32_t> said;
    const uint32_t gui = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
    const uint32_t hud = Plausible(gui) ? LoadGuestU32(base, gui + 32u) : 0u;
    const uint32_t set = Plausible(hud) ? LoadGuestU32(base, hud + 32u) : 0u;
    if (!Plausible(set)) {
        return;
    }
    const uint32_t begin = LoadGuestU32(base, set + 28u);
    const uint32_t end = LoadGuestU32(base, set + 32u);
    if (!Plausible(begin) || end < begin || (end - begin) > 64u * 4u) {
        return;
    }
    std::vector<uint32_t> counts;
    for (uint32_t at = begin; at < end; at += 4u) {
        const uint32_t registry = LoadGuestU32(base, at);
        counts.push_back(Plausible(registry) ? LoadGuestU32(base, registry + 12u) : 0xFFFFFFFFu);
    }
    std::string was;
    {
        std::lock_guard<std::mutex> lock(m);
        if (counts == said) {
            return;
        }
        for (const uint32_t n : said) {
            was += std::to_string(n) + ' ';
        }
        said = counts;
    }
    std::string now;
    for (const uint32_t n : counts) {
        now += std::to_string(n) + ' ';
    }
    REXLOG_INFO("Refii HUD registries 0x{:08X}: names per registry [ {}] (was [ {}])", set, now, was);
}

}  // namespace

/// CGameface vtable +40 — the per-frame pass; then this gameface's headless presentations'
/// Update(pres, clock), as the pass's own loop (0x822be8e8..0x822be930).
REX_EXTERN(rex_CGameface_Update) {
    REX_FUNC_PROLOGUE();
    const uint32_t gameface = ctx.r3.u32;
    __imp__rex_CGameface_Update(ctx, base);
    int32_t clock = 0;
    const size_t updated = refii::headless::UpdateAll(Table(), ctx, base, gameface, &clock);
    if (updated == 0u) {
        return;
    }
    const uint64_t passes = ++g_passes;
    if ((passes % 30u) == 0u) {
        CensusHudRegistries(base);
    }
    if (Milestone(passes) && passes < 4096u) {
        REXLOG_INFO("Refii headless presentation: gameface 0x{:08X} pass #{} updated {} headless presentation(s) "
                    "at clock {} ms",
                    gameface, passes, updated, clock);
    }
}

/// lhGuiPresentation_GetRootElement (TU1 `sub_82426518`, disc `sub_823F89B0`): a headless root
/// leaves with its timeline time.
REX_EXTERN(rex_lhGuiPresentation_GetRootElement) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhGuiPresentation_GetRootElement(ctx, base);
    refii::headless::NoteRootRead(Table(), ctx, base, ctx.r3.u32);
}

// SetStringParam (TU1 `sub_826B9510`, disc `sub_8259D6C8`) assigns the string at the attribute
// lookup's out word (`lwz r3,0x54(r1)`, `bl sub_82BAEF98` 0x826b95fc) whether or not the lookup
// (service vf +12, `sub_82BB52E0`) found the attribute — it writes that word only when it does.
// Before `lwz r11,0(r28)` (0x826b95e0, disc 0x8259d798), r3 = found: true jumps to the epilogue
// (0x826b9600, disc 0x8259d7b8). A headless element carries no string attribute.
bool RefiiPresentationAttributeAbsent(PPCRegister& r3) {
    if ((r3.u32 & 0xFFu) != 0u) {
        return false;
    }
    const uint64_t n = ++g_absent_writes;
    if (Milestone(n)) {
        REXLOG_INFO("Refii headless presentation: SetStringParam onto an absent attribute skipped (#{})", n);
    }
    return true;
}

// SetText's entry (TU1 0x82425DD0, disc 0x823F8268): r3 element, r4 presentation, r5 the text's
// handle (`mr r4,r5` 0x82425e18 into the record's assign).
void RefiiHeadlessSetText(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) {
    (void)r4;
    uint8_t* base = RefiiGuestBase();
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (base == nullptr || !Plausible(r3.u32) || r5.u32 == 0u ||
        !refii::headless::ElementKey(r3.u32, &presentation, &serial, &id)) {
        return;
    }
    StateText(r3.u32, WideUnits(base, r5.u32));
}

// SetTextFromTag's entry (TU1 0x82425F40, disc 0x823F83D8): r3 element, r4 the tag. The text is
// the guest's own lookup of the tag, on a scratch frame below the caller's sp (the writer's
// prologue has not run): copy (`sub_821F0240`), lookup (`sub_8230E0E8`, which frees the copy),
// then the wide string's dtor (`LHString_CCharString_CCharString_0`).
void RefiiHeadlessSetTextFromTag(PPCRegister& r3, PPCRegister& r4) {
    uint8_t* base = RefiiGuestBase();
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (base == nullptr || !Plausible(r3.u32) || !Plausible(r4.u32) ||
        !refii::headless::ElementKey(r3.u32, &presentation, &serial, &id)) {
        return;
    }
    PPCContext& ctx = ContextOf(r3);
    const uint32_t element = r3.u32;
    const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
    const uint32_t copy = frame + 0x80u;
    const uint32_t text = frame + 0x88u;
    StoreGuestU32(base, copy, 0u);
    StoreGuestU32(base, text, 0u);
    const uint32_t copied = CallAt(ctx, base, frame, kCharStringCopy, copy, r4.u32);
    CallAt(ctx, base, frame, kTextLookup, text, copied);
    const std::vector<uint16_t> units = WideUnits(base, text);
    CallAt(ctx, base, frame, kWideStringDtor, text);
    StateText(element, units);
}

// The quad readers' joins: `r` is the record a kind-2 node gave, 0 when the element has none — a
// headless element — answered with the core's inert record (identity transforms, no render index:
// +544 and +560 = -1, so `sub_8286DBC8` queues nothing). The sixteen readers in each build that
// take a kind-2 node's record, and the seven that use it unchecked (the transform copies
// `sub_823D0D20` in the pause minimap's rect `sub_826A3D98` 0x826a3e28 / 0x826a3e58 and its
// projection `sub_826A3FD8` 0x826a404c, the HUD minimap `sub_826DCAA0` 0x826dcb40, CGameModeBase
// vf6 `sub_826E42C8` 0x826e43ac; the transform getter `sub_823D0E10` 0x823d0ee4; the HUD icon table
// `sub_826DC138` 0x826dc548; the notification icon's texture write `sub_8281F010` 0x8281f108) are
// listed in the configs.
void RefiiHeadlessQuadRecord(PPCRegister& r) {
    if (r.u32 != 0u) {
        return;
    }
    const uint32_t record = refii::headless::InertQuadRecord();
    if (record == 0u) {
        return;
    }
    r.u32 = record;
    const uint64_t n = ++g_inert_answers;
    if (Milestone(n) && n < 4096u) {
        REXLOG_INFO("Refii headless presentation: a quad reader took the inert record (#{})", n);
    }
}

namespace {

/// One attribute write stated for a headless element from a setter's arguments (only when the
/// element had no room to carry it — see EXISTENCE-CHECKED WRITES).
void StateAttribute(uint8_t* base, uint32_t element, uint32_t attribute_global, uint32_t flags, float value) {
    uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof bits);
    if (refii::headless::TellAttribute(element, LoadGuestU32(base, attribute_global) | flags, bits)) {
        const uint64_t n = ++g_stated_writes;
        if (Milestone(n)) {
            REXLOG_INFO("Refii headless presentation: element 0x{:08X} had no room for attribute 0x{:07X}; the "
                        "write is stated from the setter's arguments (#{})",
                        element, LoadGuestU32(base, attribute_global), n);
        }
    }
}

/// An existence-checked setter's entry: a fileless headless element is given the attributes the
/// setter names (its own write then lands, and the store states it); with no room, the write is
/// stated here.
void RememberOrState(uint32_t element, const refii::guest::Addr (&globals)[3], const float (&values)[3],
                     size_t count, uint32_t flags) {
    uint8_t* base = RefiiGuestBase();
    if (base == nullptr || !Plausible(element)) {
        return;
    }
    uint32_t ids[3] = {};
    for (size_t i = 0; i < count; ++i) {
        ids[i] = LoadGuestU32(base, globals[i]) & kAttributeIdMask;
    }
    if (refii::headless::RememberWrite(base, element, ids, values, count) != refii::headless::Memory::kNoRoom) {
        return;
    }
    for (size_t i = 0; i < count; ++i) {
        StateAttribute(base, element, globals[i], flags, values[i]);
    }
}

}  // namespace

// SetPosition's entry (TU1 0x824251E0, disc 0x823F7678): r3 element, r4 presentation, f1..f3.
void RefiiHeadlessSetPosition(PPCRegister& r3, PPCRegister& r4, PPCRegister& f1, PPCRegister& f2, PPCRegister& f3) {
    if (r4.u32 == 0u) {
        return;
    }
    static const refii::guest::Addr kIds[3] = {kAttrPositionX, kAttrPositionY, kAttrPositionZ};
    const float values[3] = {static_cast<float>(f1.f64), static_cast<float>(f2.f64), static_cast<float>(f3.f64)};
    RememberOrState(r3.u32, kIds, values, 3u, 0u);
}

// SetRotation's entry (TU1 0x824254C8, disc 0x823F7960): r3 element, r4 presentation, f1..f3.
void RefiiHeadlessSetRotation(PPCRegister& r3, PPCRegister& r4, PPCRegister& f1, PPCRegister& f2, PPCRegister& f3) {
    if (r4.u32 == 0u) {
        return;
    }
    static const refii::guest::Addr kIds[3] = {kAttrRotationX, kAttrRotationY, kAttrRotationZ};
    const float values[3] = {static_cast<float>(f1.f64), static_cast<float>(f2.f64), static_cast<float>(f3.f64)};
    RememberOrState(r3.u32, kIds, values, 3u, 0u);
}

// SetOpacity's entry (TU1 0x82426078, disc 0x823F8510): r3 element, r4 presentation, f1.
void RefiiHeadlessSetOpacity(PPCRegister& r3, PPCRegister& r4, PPCRegister& f1) {
    if (r4.u32 == 0u) {
        return;
    }
    static const refii::guest::Addr kIds[3] = {kAttrOpacity, kAttrOpacity, kAttrOpacity};
    const float values[3] = {static_cast<float>(f1.f64), 0.0f, 0.0f};
    RememberOrState(r3.u32, kIds, values, 1u, kScaledOpacity);
}

// SetActive (TU1 `sub_82284210`, disc `sub_82283D80`): r3 element, r4 presentation, r5 the bool.
// It writes `active` (flags 0x2000, vf +80) only when the element's bit differs (vf +76,
// 0x82284268..0x82284274). A headless element's bit starts as the file's; the write the guest
// meant is stated at the entry from its argument whether or not it changes the bit.
void RefiiHeadlessSetActive(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) {
    if (!Plausible(r3.u32) || r4.u32 == 0u) {
        return;
    }
    refii::headless::TellActive(r3.u32, (r5.u32 & 0xFFu) != 0u);
}

namespace {

void StateTexture(uint32_t element, uint32_t hash) {
    if (Plausible(element)) {
        refii::headless::TellTexture(element, hash);
    }
}

}  // namespace

// A save's screenshot (`sub_826E1408`): r3 the element, r30 the hash.
void RefiiHeadlessStampTexture(PPCRegister& element, PPCRegister& hash) { StateTexture(element.u32, hash.u32); }

// The load cards' blank (the dispatch's Load Game path): r3 the element.
void RefiiHeadlessStampCardBlank(PPCRegister& element) {
    uint8_t* base = RefiiGuestBase();
    if (base != nullptr) {
        StateTexture(element.u32, LoadGuestU32(base, kCardBlankTexture));
    }
}

// The dialog's Y glyph (`sub_826E40E0`), cancel and other: r30 the element.
void RefiiHeadlessStampGlyphCancel(PPCRegister& element) {
    uint8_t* base = RefiiGuestBase();
    if (base != nullptr) {
        StateTexture(element.u32, LoadGuestU32(base, kGlyphCancelTexture));
    }
}

void RefiiHeadlessStampGlyphOther(PPCRegister& element) {
    uint8_t* base = RefiiGuestBase();
    if (base != nullptr) {
        StateTexture(element.u32, LoadGuestU32(base, kGlyphOtherTexture));
    }
}

namespace {

/// The resource hash the texture setter stores for the CCharString at `name` (`sub_8286DDC0`
/// 0x8286de1c..0x8286de3c: copy, GetAsLowerCase, ComputeHash), on a scratch frame below sp.
uint32_t TextureHashOfName(PPCContext& ctx, uint8_t* base, uint32_t name) {
    const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
    const uint32_t copy = frame + 0x80u;
    const uint32_t lower = frame + 0x88u;
    StoreGuestU32(base, copy, 0u);
    StoreGuestU32(base, lower, 0u);
    CallAt(ctx, base, frame, kCharStringCopy, copy, name);
    CallAt(ctx, base, frame, kCharStringLower, lower, copy);
    const uint32_t hash = CallAt(ctx, base, frame, kResourceNameHash, lower);
    CallAt(ctx, base, frame, kCharStringDtor, lower);
    CallAt(ctx, base, frame, kCharStringDtor, copy);
    return hash;
}

/// A texture named onto a headless element, stated as the hash its quad would carry.
void StateNamedTexture(PPCContext& ctx, uint8_t* base, uint32_t element, uint32_t name) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (base == nullptr || !Plausible(element) || !Plausible(name) ||
        !refii::headless::ElementKey(element, &presentation, &serial, &id)) {
        return;
    }
    StateTexture(element, TextureHashOfName(ctx, base, name));
    const uint64_t n = ++g_textures_named;
    if (Milestone(n)) {
        REXLOG_INFO("Refii headless presentation: texture named onto element {} of 0x{:08X} (#{})", id, presentation,
                    n);
    }
}

/// The three record-level sites name their texture in a CCharString at sp+0x50.
constexpr uint32_t kStackName = 0x50u;

}  // namespace

// GuiElement_SetTexture's entry (TU1 0x82425BB8, disc 0x823F8050): r3 the name (CCharString*),
// r4 the element; it bails on the element's missing node (0x82425bc4..cc), so the texture is
// stated here — the world icons' art (`sub_82825358` 0x828253d4).
void RefiiHeadlessSetTextureByName(PPCRegister& r3, PPCRegister& r4) {
    StateNamedTexture(ContextOf(r3), RefiiGuestBase(), r4.u32, r3.u32);
}

// The expression wheel's item (`sub_826AC5F0` 0x826AC708, disc `sub_8258FD30` 0x8258FE48): the
// record setter on r29's node with the name in r28.
void RefiiHeadlessTextureNamed(PPCRegister& r3, PPCRegister& element, PPCRegister& name) {
    StateNamedTexture(ContextOf(r3), RefiiGuestBase(), element.u32, name.u32);
}

// The d-pad quick slots (`sub_823D2C98` 0x823D30D4, disc `sub_824069F0` 0x82406E2C): the slot
// element `sub_823D34B8` returned in r3, the name at sp+0x50.
void RefiiHeadlessTextureOnStack(PPCRegister& r3, PPCRegister& r1) {
    StateNamedTexture(ContextOf(r3), RefiiGuestBase(), r3.u32, r1.u32 + kStackName);
}

// The spell wheel's icon (`sub_826B5D30` 0x826B5E34 r31, disc `sub_8259AFC8` 0x8259B0CC) and the
// spell charges' registry walk (`sub_825E9EA0` 0x825EA1F4 r10, disc `sub_82405C60` 0x82405FB4):
// the element in a register, the name at sp+0x50.
void RefiiHeadlessTextureOnStackOf(PPCRegister& r3, PPCRegister& element, PPCRegister& r1) {
    StateNamedTexture(ContextOf(r3), RefiiGuestBase(), element.u32, r1.u32 + kStackName);
}

void RefiiGuiStateImage(uint32_t hash, uint32_t texture);

// The spell charges' icons (`sub_825E9EA0` 0x825EA1C8, disc `sub_82405C60` 0x82405F88): r4 the
// image name's hash, the texture's name at sp+0x50, just before the image registry's record
// lookup — stated as the call names them, whether or not the name has a record.
void RefiiHudChargeImage(PPCRegister& r3, PPCRegister& r4, PPCRegister& r1) {
    uint8_t* base = RefiiGuestBase();
    if (base == nullptr || !Plausible(r1.u32)) {
        return;
    }
    RefiiGuiStateImage(r4.u32, TextureHashOfName(ContextOf(r3), base, r1.u32 + kStackName));
}
