// refii - HEADLESS GAMEFACE PRESENTATIONS, Fable 3's hooks. The guest keeps its UI objects; it
// never builds a .bgf scene and never runs a presentation's Lua. The work is the shared headless
// core's (core/headless_presentation.h, src/payload/src/core/headless_presentation.cpp), as Fable
// 2's (Fable2/src/vfs/src/hooks/gui_headless_presentation.cpp); these hooks are its shims and
// headless_table.h is Fable 3's Gameface for it. Every address is the Fable 3 360 disc IDB (port
// 13339); each offset names its base.
//
// THE CREATE. CGameface (K, vtable 0x82002B00) vf +28 `sub_82C211F8`(K, char* path) is the only
// maker: its one caller is the load `sub_825C7A90` (`lwz r10,0x1C(r11)` / `bctrl` 0x825c7b24..
// 0x825c7b2c, the path `Data\Art\GUI\Gameface\<name>` built from `dword_83557120` by the static
// initialiser `sub_831A2500`). Shipped, it makes a CPresentation in the kernel heap, pushes it on
// K's list (0x82c212e8..0x82c212f4), names it (vf +72, 0x82c21314), gives it its engine
// (0x82c21320..0x82c213a0, GiveEngine below), deserialises the .bgf (CBGFDeserializer vf +12,
// 0x82c213f4) and adds its scene (CSceneManager vf +4, 0x82c21454). The core makes, names and gives
// an engine to the same CPresentation with the same calls and does none of the rest; its elements
// are made from the file's element table as the guest names them. The object must exist: some 140
// box Show / HandleInput sites null-gate on it (the box loader `sub_8291F4C8`).
//
// ELEMENTS. Child-by-name, element service (pres +492, vtable 0x82006F38) vf +84 0x82C3A420(svc,
// hash, parent or 0) — every resolver step ends there (GuiElement_Resolve `lwz r10,0x54(r11)` /
// `bctrl` 0x8281a014..0x8281a01c) — answers 0 on a miss (`li r3,0` 0x82c3a464); a headless
// presentation then makes the element. The child walk vf +92 0x82C3A478(svc, parent, out array,
// all) reads `lwz r30,0x14(r4)` (0x82c3a484) and follows +16; a headless parent's file children are
// made first, so the HUD's registry build (`sub_82675848` through `sub_8281A138` 0x8281a1b0) and
// the world-icon pool's child count (0x82b52310 `bl sub_8281A138` / `cmpwi r3,0x10` 0x82b52318)
// see the file's tree.
//
// THE FILE'S STRING ATTRIBUTES. The attribute-string read `sub_828183C8`(out, element, pres,
// &name) answers "" for an element without the record (0x8281848c); the fourteen HUD registries'
// filer `sub_82B9A108` files an element only under a non-empty answer (`bl sub_821C3AC8` against
// "" 0x82b9a160 / `beq` 0x82b9a16c). Its hook (gui_element_hooks.cpp) has the core answer the
// FILE's attribute (HeadlessAnswerAttributeString).
//
// THE DESTROY. K vf +32 0x82C21540(K, pres) frees only a presentation on K's list (`bl
// sub_82C36828` 0x82c215b8: dtor `(**a2)(a2, 0)` 0x82c368a8, kernel free 0x36C 0x82c368b8, both
// only for a listed one); the core frees a headless one itself.
//
// THE PER-FRAME PASS. K vf +40 `sub_8227B7C0`(K, render) updates the listed presentations
// (`lwz r10,4(r11)` / `bctrl` 0x8227b86c..0x8227b874, clock from K +8 vf +16 0x8227b830..
// 0x8227b83c); the headless ones are updated after it with the same clock: their command / event
// ring (pres +324, `sub_82183C00`) drains and their timelines step.
//
// THE PARAMETERS. SetParameterString `sub_82B3F470`(CCharString* name, CCharString* value, pres
// or 0 = the loader's current, `bl sub_825C7B60` 0x82b3f49c) looks the named string attribute up
// on the Parameters element (service vf +12, `lwz r10,0xC(r11)` / `bctrl` 0x82b3f52c..0x82b3f534)
// and assigns through the lookup's out word whether or not it was found (`lwz r3,0x80+var_2C(r1)`
// / `bl sub_82C33CC8` 0x82b3f550..0x82b3f554); a headless element holds no string attribute, so
// the call is skipped. Callers: RunInitScripts (`String1` = the scripts directory, 0x825c7c74) and
// the mini-game bar (`String1` = the button colour 0x826ec7cc, the background 0x826eab90).
// SetParameterFloat `sub_82B3F3A8`(CCharString* name, f1 value, r5 pres or 0 = the loader's
// current, `mr r31,r5` 0x82b3f3c4 / `bl sub_825C7B60` 0x82b3f3d8) stores through the store's
// vt[18] (0x82b3f460) and is left to run. Both post `@Lua.Param(pres, name, value)` for a headless
// presentation: the Parameters element (`g_Param`) is the shipped Lua's only input besides the
// chunk, so the pair carries what the scripts would have read (Fable3LuaContracts in Godot).
//
// THE LUA. Every chunk the C++ runs in a presentation passes `sub_82819D40`(CCharString* script,
// pres): g_HUD:Enable/Disable, RunInitScripts' three, GUI:OnEnter/OnExit, the thumbstick and
// direction stores, g_MiniGameBar:*, g_DisplayBox:* and the Execute{Hud,Screen,Interaction}Script
// natives. Read from the shipped scripts (F3 phase B, B6): none posts anything the C++ waits on —
// each sets elements or Lua state; the box close, the mini-game counts and the HUD's state are the
// C++'s own. A headless presentation's engine holds no script, so each chunk is skipped at the
// runner rather than run into an empty state (which the runner's pcall `sub_8232CEB8` would
// swallow) or queued in GUI +116 with a raw presentation pointer. Each skipped chunk is posted as
// `@Lua.Chunk(pres, text)`: the text is the call the guest made (e.g. `g_MiniGameBar:SpawnButton()`
// at 0x826ec7fc, `g_MiniGameBar:SetPipTravel(<t>)` at 0x826eb228), and Godot runs the script's
// layout rule for it.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "gui_statements.h"
#include "headless_table.h"

REX_EXTERN(__imp__rex_CGameface_DestroyPresentation);
REX_EXTERN(__imp__rex_CGameface_Update);
REX_EXTERN(__imp__rex_GfService_ChildByName);
REX_EXTERN(__imp__rex_GfService_CollectChildren);
REX_EXTERN(__imp__rex_GuiPresentation_SetParameterString);
REX_EXTERN(__imp__rex_GuiPresentation_SetParameterFloat);
REX_EXTERN(__imp__rex_GuiPresentation_RunScript);

namespace {

using namespace refii::fable3;

// The create's engine steps (0x82c21320..0x82c213a0).
constexpr uint32_t kEngineFactory = 0x8329DB78u;    // -> `sub_82C2BAE8`(pres): "class CLuaEngine"
constexpr uint32_t kGameplanFactory = 0x8329DB7Cu;  // -> `sub_82CE17A8`(pres): `li r3,0`
constexpr uint32_t kRegisterDataPorts = 0x82C2D680u;  // AK_RegisterDataPortAPI(K + 0x214, L)
constexpr uint32_t kRegisterGameplan = 0x82C2D2B8u;   // (*(K + 0x248), L)
constexpr uint32_t kGamefaceDataPorts = 0x214u;       // `addi r3,r29,0x214` 0x82c21374
constexpr uint32_t kGamefaceGameplan = 0x248u;        // `lwz r3,0x248(r29)` 0x82c21384
constexpr uint32_t kEngineLua = 4u;                   // `lwz r4,4(r30)` 0x82c21378
constexpr uint32_t kPresEngine = 28u;                 // pres vf +16 0x82C35488 `stw r4,0x1C(r3)`
constexpr uint32_t kPresGameplan = 32u;               // `stw r3,0x20(r31)` 0x82c213a0

// The kernel heap the deserialiser's own element block comes from ("Mgr:Elements",
// 0x82c2f880..0x82c2f8a4: allocator, size, class, file, line 0x57).
constexpr uint32_t kKernelAllocator = 0x82C33990u;
constexpr uint32_t kKernelAlloc = 0x82C34350u;
constexpr uint32_t kKernelFree = 0x82C344A0u;
constexpr uint32_t kElementsClassName = 0x820E2334u;  // "Mgr:Elements"
constexpr uint32_t kElementsSourceName = 0x820E22C0u;
constexpr uint32_t kElementsSourceLine = 0x57u;

// SetParameterString's current presentation (`lwz r3,0x38(r11)` of the GUI manager, then
// `bl sub_825C7B60` 0x82b3f494..0x82b3f49c).
constexpr uint32_t kGuiManager = 0x83556490u;
constexpr uint32_t kGuiLoader = 0x38u;
constexpr uint32_t kLoaderCurrent = 0x825C7B60u;

// `opacity`'s attribute id (SetOpacity `lwz r5,dword_8355EE48@l(r9)` 0x8281946c).
constexpr uint32_t kAttrOpacity = 0x8355EE48u;

bool Milestone(uint64_t n) { return n <= 32u || (n & (n - 1u)) == 0u; }

PPCFunc* Find(uint32_t address) {
  if (address == 0u) {
    return nullptr;
  }
  for (PPCFuncMapping* mapping = PPCFuncMappings; mapping != nullptr && mapping->host != nullptr; ++mapping) {
    if (static_cast<uint32_t>(mapping->guest) == address) {
      return mapping->host;
    }
  }
  return nullptr;
}

PPCFunc* Resolved(uint32_t address) {
  static std::mutex m;
  static std::unordered_map<uint32_t, PPCFunc*> cache;
  std::lock_guard<std::mutex> lock(m);
  const auto it = cache.find(address);
  if (it != cache.end()) {
    return it->second;
  }
  PPCFunc* fn = Find(address);
  cache.emplace(address, fn);
  if (fn == nullptr) {
    REXLOG_ERROR("Refii headless presentation (Fable 3): guest function 0x{:08X} is not a recompiled entry", address);
  }
  return fn;
}

uint32_t Call(PPCContext& ctx, uint8_t* base, uint32_t address, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u,
              uint32_t r6 = 0u, uint32_t r7 = 0u) {
  PPCFunc* fn = Resolved(address);
  if (fn == nullptr) {
    return 0u;
  }
  PPCContext call = ctx;
  call.r3.u32 = r3;
  call.r4.u32 = r4;
  call.r5.u32 = r5;
  call.r6.u32 = r6;
  call.r7.u32 = r7;
  call.ctr.u32 = address;
  fn(call, base);
  return call.r3.u32;
}

void StoreU32(uint8_t* base, uint32_t at, uint32_t value) {
  const uint32_t v = __builtin_bswap32(value);
  std::memcpy(base + at, &v, sizeof v);
}

/// Every block the core takes from the kernel heap, with its size: the kernel free files the
/// block back by size (`sub_82C344A0`'s size class, `16 * (size + 196)` and the pool walk).
std::mutex g_blocks_mutex;
std::unordered_map<uint32_t, uint32_t> g_blocks;

uint32_t GuestAlloc(PPCContext& ctx, uint8_t* base, uint32_t size) {
  const uint32_t allocator = Call(ctx, base, kKernelAllocator, 0u);
  const uint32_t block =
      Call(ctx, base, kKernelAlloc, allocator, size, kElementsClassName, kElementsSourceName, kElementsSourceLine);
  if (block != 0u) {
    std::lock_guard<std::mutex> lock(g_blocks_mutex);
    g_blocks[block] = size;
  }
  return block;
}

void GuestFree(PPCContext& ctx, uint8_t* base, uint32_t address) {
  uint32_t size = 0u;
  {
    std::lock_guard<std::mutex> lock(g_blocks_mutex);
    const auto it = g_blocks.find(address);
    if (it == g_blocks.end()) {
      REXLOG_WARN("Refii headless presentation (Fable 3): 0x{:08X} is not a block the core took; not freed", address);
      return;
    }
    size = it->second;
    g_blocks.erase(it);
  }
  const uint32_t allocator = Call(ctx, base, kKernelAllocator, 0u);
  Call(ctx, base, kKernelFree, allocator, address, size);
}

/// Fable 3's create engine steps 0x82c21320..0x82c213a0: the engine (factory, then pres +28 as vf
/// +16 stores it), the data ports and gameplan natives on its lua_State, then the gameplan word.
/// The K +588 callback (2, made) the core sends after.
void GiveEngine(PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t presentation) {
  const uint32_t engine = Call(ctx, base, LoadGuestU32(base, kEngineFactory), presentation);
  StoreU32(base, presentation + kPresEngine, engine);
  if (Plausible(engine)) {
    const uint32_t lua = LoadGuestU32(base, engine + kEngineLua);
    Call(ctx, base, kRegisterDataPorts, gameface + kGamefaceDataPorts, lua);
    Call(ctx, base, kRegisterGameplan, LoadGuestU32(base, gameface + kGamefaceGameplan), lua);
  } else {
    REXLOG_ERROR("Refii headless presentation (Fable 3): no engine for 0x{:08X} — its chunks are skipped, its "
                 "Lua element calls would fault",
                 presentation);
  }
  StoreU32(base, presentation + kPresGameplan, Call(ctx, base, LoadGuestU32(base, kGameplanFactory), presentation));
}

const refii::headless::GamefaceTable& Table() {
  static const refii::headless::GamefaceTable table = [] {
    refii::headless::GamefaceTable t = HeadlessTableAddresses();
    t.find = &Find;
    t.alloc = &GuestAlloc;
    t.free = &GuestFree;
    t.give_engine = &GiveEngine;
    return t;
  }();
  return table;
}

std::atomic<uint64_t> g_passes{0};
std::atomic<uint64_t> g_parameters{0};
std::atomic<uint64_t> g_scripts{0};

}  // namespace

bool refii::fable3::HeadlessAnswerAttributeString(PPCContext& ctx, uint8_t* base, uint32_t out, uint32_t element,
                                                   uint32_t presentation, uint32_t name) {
  return refii::headless::AnswerAttributeString(Table(), ctx, base, out, element, presentation, name);
}

/// K vf +28 — (r3 K, r4 path) -> a headless presentation, or 0.
REX_EXTERN(rex_CGameface_CreatePresentation) {
  ctx.r3.u32 = refii::headless::Create(Table(), ctx, base, ctx.r3.u32, ctx.r4.u32);
}

/// K vf +32 — (r3 K, r4 presentation).
REX_EXTERN(rex_CGameface_DestroyPresentation) {
  if (!refii::headless::Destroy(Table(), ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    __imp__rex_CGameface_DestroyPresentation(ctx, base);
  }
}

/// K vf +40 — the per-frame pass, then each headless presentation's Update(pres, clock).
REX_EXTERN(rex_CGameface_Update) {
  const uint32_t gameface = ctx.r3.u32;
  __imp__rex_CGameface_Update(ctx, base);
  int32_t clock = 0;
  const size_t updated = refii::headless::UpdateAll(Table(), ctx, base, gameface, &clock);
  if (updated == 0u) {
    return;
  }
  // THE STORE'S OPACITIES. Each Update ran Mgr:Animations vf +4 (`sub_82274B08` ->
  // `sub_821CA070`): every active track on a visible element stored its value at the element's
  // time through the store (`lwz r10,0x48(r11)` / `bctrl`, 0x821ca18c and the interpolated paths),
  // and a slide's enter wrote its entries' commands (`sub_821CA668`). No writer hook sees either,
  // so the opacities they left are stated here, on change, for every element a track drives and
  // every one a writer stated — a list slide's `opacity=100` over the 0 its Hide track left
  // (interactionmode.bgf `Buttons` #18, slides 265/266) is otherwise never heard.
  {
    static thread_local std::vector<std::pair<uint32_t, uint32_t>> changed;
    changed.clear();
    refii::headless::ChangedStoredValues(base, LoadGuestU32(base, kAttrOpacity) & 0x07FFFFFFu, &changed);
    for (const auto& [element, bits] : changed) {
      StateStoredOpacity(element, 0u, bits);
    }
  }
  const uint64_t passes = ++g_passes;
  if (Milestone(passes) && passes < 4096u) {
    REXLOG_INFO("Refii headless presentation (Fable 3): gameface 0x{:08X} pass #{} updated {} headless "
                "presentation(s) at clock {} ms",
                gameface, passes, updated, clock);
  }
}

/// Service vf +84 — (r3 service, r4 name hash, r5 parent or 0): the original answers; a headless
/// presentation makes the element it lacks.
REX_EXTERN(rex_GfService_ChildByName) {
  const uint32_t service = ctx.r3.u32;
  const uint32_t hash = ctx.r4.u32;
  const uint32_t parent = ctx.r5.u32;
  __imp__rex_GfService_ChildByName(ctx, base);
  if (ctx.r3.u32 != 0u) {
    return;
  }
  const uint32_t element = refii::headless::MakeNamed(Table(), ctx, base, service, hash, parent);
  if (element != 0u) {
    ctx.r3.u32 = element;
  }
}

/// Service vf +92 — (r3 service, r4 parent, r5 out array, r6 all): a headless parent's file
/// children are made first.
REX_EXTERN(rex_GfService_CollectChildren) {
  refii::headless::ExpandBeforeWalk(Table(), ctx, base, ctx.r3.u32, ctx.r4.u32);
  __imp__rex_GfService_CollectChildren(ctx, base);
}

/// SetParameterString — (r3 name, r4 value, r5 presentation or 0): skipped on a headless one.
REX_EXTERN(rex_GuiPresentation_SetParameterString) {
  uint32_t presentation = ctx.r5.u32;
  if (presentation == 0u && base != nullptr) {
    const uint32_t gui = LoadGuestU32(base, kGuiManager);
    if (Plausible(gui)) {
      presentation = Call(ctx, base, kLoaderCurrent, LoadGuestU32(base, gui + kGuiLoader));
    }
  }
  if (presentation == 0u || !refii::headless::IsHeadless(presentation)) {
    __imp__rex_GuiPresentation_SetParameterString(ctx, base);
    return;
  }
  const std::string name = ReadCharString(base, ctx.r3.u32, 64u);
  const std::string value = ReadCharString(base, ctx.r4.u32, 160u);
  Statement("@Lua.Param").Add(Text(PresentationKey(presentation))).Add(Text(name)).Add(Text(value)).Post();
  const uint64_t n = ++g_parameters;
  if (Milestone(n)) {
    REXLOG_INFO("Refii headless presentation (Fable 3): parameter '{}' = '{}' of 0x{:08X} stated, not stored — a "
                "headless Parameters element holds no string attribute (#{})",
                name, value, presentation, n);
  }
}

/// SetParameterFloat — (r3 name, f1 value, r5 presentation or 0): runs as given; a headless one's
/// value is also stated.
REX_EXTERN(rex_GuiPresentation_SetParameterFloat) {
  uint32_t presentation = ctx.r5.u32;
  const uint32_t name_at = ctx.r3.u32;
  const float value = static_cast<float>(ctx.f1.f64);
  if (presentation == 0u && base != nullptr) {
    const uint32_t gui = LoadGuestU32(base, kGuiManager);
    if (Plausible(gui)) {
      presentation = Call(ctx, base, kLoaderCurrent, LoadGuestU32(base, gui + kGuiLoader));
    }
  }
  if (presentation != 0u && base != nullptr && refii::headless::IsHeadless(presentation)) {
    Statement("@Lua.Param")
        .Add(Text(PresentationKey(presentation)))
        .Add(Text(ReadCharString(base, name_at, 64u)))
        .Add(Number(value))
        .Post();
  }
  __imp__rex_GuiPresentation_SetParameterFloat(ctx, base);
}

/// The chunk runner — (r3 CCharString* script, r4 presentation): skipped on a headless one.
REX_EXTERN(rex_GuiPresentation_RunScript) {
  const uint32_t presentation = ctx.r4.u32;
  if (presentation == 0u || !refii::headless::IsHeadless(presentation)) {
    __imp__rex_GuiPresentation_RunScript(ctx, base);
    return;
  }
  const uint64_t n = ++g_scripts;
  if (base != nullptr) {
    const std::string script = ReadCharString(base, ctx.r3.u32, 256u);
    Statement("@Lua.Chunk").Add(Text(PresentationKey(presentation))).Add(Text(script)).Post();
    if (Milestone(n)) {
      REXLOG_INFO("Refii headless presentation (Fable 3): chunk '{}' for 0x{:08X} not run — stated, Godot "
                  "draws the presentation (#{})",
                  script, presentation, n);
    }
  }
  // r3 left as given, as the runner's own exit for no presentation leaves it (`beq` 0x82819d58 to
  // the epilogue 0x82819da4).
}
