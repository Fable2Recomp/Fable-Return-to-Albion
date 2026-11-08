// refii - Fable 3's engine preloads, answered complete when the guest states them.
//
// THE PRELOAD. Game code asks the 3D Engine to make a resource resident with the engine primitive
// NEngineInterface::CEnginePrimitivePreloadResource (vtable 0x820E6390, type 52; built by
// sub_82C7D9C0(prim, mode, &name_hash, &handle), mode 1 a texture, 0 a mesh) and waits on its
// CEnginePrimitivePreloadHandle: the word at handle+8 (handle = 12 bytes, +4 the count). Every
// address is the Fable 3 TU3 IDB; each offset names its base. The callers:
//   CWorldIcon3D's update sub_82225AC8 (mode 1, 0x82226134) — it polls `*(*(icon+240)+8)` at
//       0x82226010 and only then builds its visual (icon+236 = 1); until it does, the emotion
//       icon's update CEmotionWorldIcon_vf4 skips its ring (`lwz r11,0xF0(r3)` 0x829C20B0), so an
//       interaction choice can never fill and never commit.
//   sub_82533D30 (mode 1), sub_82608918 (mode 0), Lua GraphicAppearance.StartPreloadingMeshes
//       (sub_826B5758, mode 0) and Lua Breakable.PreloadPieces (sub_827C1CC8, mode 0).
//
// WHERE THE GUEST COMPLETES IT. The render twin create (sub_821F0E70) makes the engine-side
// instance (vtable 0x82005498, type 46) and applies the primitive through its slot 5,
// sub_82CB8940(inst, prim), which keeps the handle at inst+0x50 (`stw r28,0x50(r31)` at
// 0x82CB8C48). The only writer of handle+8 is that instance's slot 2, sub_82194F30, which stores
// `status == 2` (`stwcx. r10,0,r7` at 0x82194F84, r7 = handle+8) — and it runs only as the "Main
// Scene" layer renderer's CRenderLayerTraversePrimitives(46) pass over a scene a view draws
// (Render_BuildMainScenePipeline 0x82CDB1E8, `li r4,0x2E` at 0x82CDB2CC). Godot draws; the 3D
// Engine thread takes its no-view branch every frame (sub_821989D8, the counter dword_832E542C
// rises with the frame counter), so that pass never runs and no preload completes — the icon's
// texture is never even requested (texture +76 stays 0x7FFFFFFF, no load outstanding).
//
// THE ANSWER. Godot builds every mesh and texture from the banks itself, so the resource the guest
// asks to have resident is the host's, and it is resident: the completion is the value the
// guest's own pass would have stored once the resource was usable, written as the primitive is
// applied. Fable 2's bridge gives the same answer for its type-46 holders
// (Fable2/src/vfs/src/hooks/boot_lifecycle.cpp, rex_lhRenderer_DrawScene).

#include <atomic>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

REX_EXTERN(__imp__rex_PreloadInstance_Apply);

namespace {

constexpr uint32_t kInstHandle = 0x50u;  // inst+0x50: the CEnginePrimitivePreloadHandle
constexpr uint32_t kHandleDone = 8u;     // handle+8: 1 once the resource is resident

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

void StoreGuestU32(uint8_t* base, uint32_t address, uint32_t value) {
  value = __builtin_bswap32(value);
  std::memcpy(base + address, &value, sizeof(value));
}

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

std::atomic<uint32_t> g_completed{0u};

}  // namespace

// void CEnginePreloadInstance::Apply(inst, const CEnginePrimitivePreloadResource* prim)
REX_EXTERN(rex_PreloadInstance_Apply) {
  const uint32_t inst = ctx.r3.u32;
  __imp__rex_PreloadInstance_Apply(ctx, base);

  const uint32_t handle = Plausible(inst) ? LoadGuestU32(base, inst + kInstHandle) : 0u;
  if (!Plausible(handle)) {
    return;
  }
  StoreGuestU32(base, handle + kHandleDone, 1u);
  const uint32_t n = g_completed.fetch_add(1u, std::memory_order_relaxed) + 1u;
  if (n <= 4u || (n & (n - 1u)) == 0u) {
    REXLOG_INFO("[preload] #{} complete as applied: instance 0x{:08X}, handle 0x{:08X}", n, inst, handle);
  }
}
