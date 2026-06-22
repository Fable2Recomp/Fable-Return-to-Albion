#pragma once

#include <cstdint>

namespace refii::renderer {

enum class RendererHookConfidence {
  High,
  Tentative,
  Unresolved,
};

enum class RendererHookReplacement {
  FullReplacement,
  FableAdapter,
  SideEffectOnly,
  Unresolved,
};

constexpr RendererHookReplacement DefaultRendererHookReplacement(
    RendererHookConfidence confidence) {
  switch (confidence) {
  case RendererHookConfidence::High:
    return RendererHookReplacement::FullReplacement;
  case RendererHookConfidence::Tentative:
    return RendererHookReplacement::FableAdapter;
  case RendererHookConfidence::Unresolved:
    return RendererHookReplacement::Unresolved;
  }
  return RendererHookReplacement::Unresolved;
}

struct RendererHookTarget {
  const char* name;
  uint32_t sonic_address;
  uint32_t fable_address;
  RendererHookConfidence confidence;
  RendererHookReplacement replacement;
  const char* note;

  constexpr RendererHookTarget(const char* target_name,
                               uint32_t sonic_target_address,
                               uint32_t fable_target_address,
                               RendererHookConfidence target_confidence,
                               const char* target_note)
      : name(target_name),
        sonic_address(sonic_target_address),
        fable_address(fable_target_address),
        confidence(target_confidence),
        replacement(DefaultRendererHookReplacement(target_confidence)),
        note(target_note) {}

  constexpr RendererHookTarget(const char* target_name,
                               uint32_t sonic_target_address,
                               uint32_t fable_target_address,
                               RendererHookConfidence target_confidence,
                               RendererHookReplacement target_replacement,
                               const char* target_note)
      : name(target_name),
        sonic_address(sonic_target_address),
        fable_address(fable_target_address),
        confidence(target_confidence),
        replacement(target_replacement),
        note(target_note) {}
};

constexpr RendererHookTarget kRendererHookTargets[] = {
    {"CreateDevice", 0x82BD99B0, 0x82387908,
     RendererHookConfidence::Tentative, RendererHookReplacement::SideEffectOnly,
     "Fable high-level renderer setup; generated symbol is rex_lhRenderer_Setup. "
     "This is not a direct D3D9 device ABI twin and is already part of the "
     "startup/VFS lifecycle."},
    {"DestructResource", 0x82BE6230, 0x821FC320,
     RendererHookConfidence::Tentative, RendererHookReplacement::FableAdapter,
     "Broad Fable GPU resource refcount release/destruct helper. This is a "
     "resource-lifetime choke point, not a narrow Sonic D3D9 ABI twin."},
    {"LockTextureRect", 0x82BE9300, 0x82B98930,
     RendererHookConfidence::High, RendererHookReplacement::FullReplacement,
     "Exact opcode-shape match."},
    {"UnlockTextureRect", 0x82BE7780, 0x82B98950,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"LockVertexBuffer", 0x82BE6B98, 0x8225F980,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"UnlockVertexBuffer", 0x82BE6BE8, 0x82B9EEC0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"GetVertexBufferDesc", 0x82BE61D0, 0x82B8B820,
     RendererHookConfidence::Tentative,
     "No standalone Fable ABI twin. Compatibility derives the desc from the "
     "typed vertex-buffer object built by rex_lhGfx_CreateVertexBuffer and "
     "refreshed through LockVertexBuffer/XGSetVertexBufferHeader."},
    {"LockIndexBuffer", 0x82BE6CA8, 0x82196980,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"UnlockIndexBuffer", 0x82BE6CF0, 0x82B9EED0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"GetIndexBufferDesc", 0x82BE6200, 0x82B8D7A8,
     RendererHookConfidence::Tentative,
     "No standalone Fable ABI twin. Compatibility derives the desc from the "
     "typed index-buffer object built by rex_lhGfx_CreateIndexBuffer and "
     "refreshed through LockIndexBuffer/XGSetIndexBufferHeader."},
    {"GetSurfaceDesc", 0x82BE96F0, 0x821FA070,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"GetVertexDeclaration", 0x82BE04B0, 0x822224A8,
     RendererHookConfidence::Tentative,
     "No standalone Fable ABI twin. Current declaration is the device slot "
     "mirrored from SetVertexDeclaration/XGSetVertexDeclaration; declaration "
     "objects store count at +24 and 12-byte elements at +52."},
    {"HashVertexDeclaration", 0x82BE0530, 0x82B99720,
     RendererHookConfidence::Tentative,
     "No standalone Fable ABI twin. Compatibility hashes the element stream "
     "captured from rex_lhGfx_CreateGPUState/CreateVertexDeclaration."},
    {"Present", 0x82BDA8C0, 0x82B6F1D0,
     RendererHookConfidence::Tentative,
     "Fable renderer end-scene path; generated symbol is rex_lhRenderer_EndScene "
     "and it reaches the display swap helper after frame work is flushed."},
    {"DisplaySwap", 0, 0x82BA34D8,
     RendererHookConfidence::Tentative,
     "Fable display/swap helper called by the end-scene path after frame work "
     "is flushed. Hooked separately so Plume can suppress native display "
     "handoff while preserving higher-level EndScene bookkeeping."},
    {"GetBackBuffer", 0x82BDD330, 0x82B981D0,
     RendererHookConfidence::Tentative,
     "No exact Sonic ABI twin. Fable's D3DDevice_GetRenderTarget plus the "
     "0x82B6F040 presentation scratch refresh expose the same current "
     "backbuffer/render-target state for replacement."},
    {"GetRenderTarget", 0, 0x82B981D0,
     RendererHookConfidence::High,
     "Lumen names this exact Fable entry D3DDevice_GetRenderTarget. IDA shows "
     "it reads device + 4 * (slot + 3110), AddRefs the surface if present, and "
     "returns the guest surface pointer. Hooked as a general native-running "
     "getter; Plume mirrors suppressed SetRenderTarget writes into the same "
     "device slot table so this remains coherent without native D3D9 draws."},
    {"CreateTexture", 0x82BE9498, 0x82B98B28,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"CreateVertexBuffer", 0x82BE6AD0, 0x82B8B820,
     RendererHookConfidence::Tentative,
     "Fable engine-level GPU vertex-buffer constructor; generated symbol is "
     "rex_lhGfx_CreateVertexBuffer, not an exact Sonic D3D9 ABI twin."},
    {"CreateIndexBuffer", 0x82BE6BF8, 0x82B8D7A8,
     RendererHookConfidence::Tentative,
     "Fable engine-level GPU index-buffer constructor; generated symbol is "
     "rex_lhGfx_CreateIndexBuffer, not an exact Sonic D3D9 ABI twin."},
    {"CreateSurface", 0x82BE95B8, 0x821F4EC0,
     RendererHookConfidence::High,
     "Lumen names this exact Fable entry D3DDevice_CreateSurface; generated "
     "symbol is rex_lhTexture_Create. Surface-view constructors 0x82B98978 "
     "and 0x82B98A58 are still hooked as Fable-specific view paths, alongside "
     "render-target/depth wrappers 0x821FC8D8, 0x82295380, and constructor "
     "0x82B8AB30."},
    {"StretchRect", 0x82BF6400, 0x82D21C68,
     RendererHookConfidence::Tentative,
     "No exact Sonic ABI twin. Fable splits this across surface-copy and "
     "display-copy choke points including D3DXLoadSurfaceFromSurface, "
     "InternalCopy, and D3DXLoadSurfaceFromMemory; Plume aliases those copies "
     "as the StretchRect compatibility path."},
    {"SetRenderTarget", 0x82BDD9F0, 0x822869A0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"SetDepthStencilSurface", 0x82BDDD38, 0x82264590,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"Clear", 0x82BFE4C8, 0x821E2FE0, RendererHookConfidence::High,
     "Exact opcode-shape match."},
    {"SetViewport", 0x82BDD8C0, 0x821F9F58,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"SetTexture", 0x82BE9818, 0x821B6EE0, RendererHookConfidence::High,
     "Exact opcode-shape match."},
    {"SetScissorRect", 0x82BDCFB0, 0x821F9C08,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"DrawPrimitive", 0x82BE5900, 0x8221C3E8,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"DrawIndexedPrimitive", 0x82BE5CF0, 0x8221DFC0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"DrawPrimitiveUP", 0x82BE52F8, 0x82265CD8,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"CreateVertexDeclaration", 0x82BE0428, 0x82B99720,
     RendererHookConfidence::High,
     "Allocates/finalizes D3D vertex declaration state; generated symbol is "
     "rex_lhGfx_CreateGPUState."},
    {"SetVertexDeclaration", 0x82BE02E0, 0x822224A8,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"CreateVertexShader", 0x82BE1A80, 0x82B994B8,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"SetVertexShader", 0x82BE0110, 0x82232510,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"SetStreamSource", 0x82BDD0F8, 0x821B6C60,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"SetIndices", 0x82BDD218, 0x8219CC30, RendererHookConfidence::High,
     "Exact opcode-shape match."},
    {"CreatePixelShader", 0x82BE1990, 0x82B992B0,
     RendererHookConfidence::High,
     "Pixel-shader object creator; sibling vertex-shader creator is "
     "0x82B994B8."},
    {"SetPixelShader", 0x82BDFE58, 0x82208BB0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"D3DXFillTexture", 0x82C003B8, 0x82D16CF8,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"D3DXFillVolumeTexture", 0x82C00910, 0,
     RendererHookConfidence::Unresolved,
     "No confirmed Fable ABI twin. Lumen identifies 0x82D17250 as "
     "CreateTextureFromFileInMemoryEx, so Refii keeps that as a Fable-specific "
     "texture-file create/decode path instead of treating it as Sonic's volume "
     "texture fill hook."},
    {"MakePictureData", 0x82E43FC8, 0, RendererHookConfidence::Unresolved,
     "No Fable generated-PPC exact-shape match. This is Sonic-specific picture "
     "asset decoding and texture recreation logic, including game-specific DDS "
     "patches/cubemap fixes; Refii uses Fable's own texture-file create/decode "
     "path instead."},
    {"SetResolution", 0x82E9EE38, 0, RendererHookConfidence::Unresolved,
     "No Fable generated-PPC exact-shape match. Sonic's hook only stores "
     "scaled viewport dimensions into Sonic's device structure; Fable's Plume "
     "replacement derives presentation size from its own display/end-scene "
     "bookkeeping."},
    {"ScreenShaderInit", 0x82AE2BF8, 0,
     RendererHookConfidence::Unresolved,
     "No Fable generated-PPC exact-shape match. This installs Sonic movie "
     "screen shaders and a Sonic vertex declaration; Refii keeps Fable's "
     "SBK/XenosRecomp shader artifacts and video/private texture adapters."},
};

}  // namespace refii::renderer
