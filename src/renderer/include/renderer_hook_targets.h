#pragma once

#include <cstdint>

namespace refii::renderer {

enum class RendererHookConfidence {
  High,
  Tentative,
  Unresolved,
};

struct RendererHookTarget {
  const char* name;
  uint32_t sonic_address;
  uint32_t fable_address;
  RendererHookConfidence confidence;
  const char* note;
};

constexpr RendererHookTarget kRendererHookTargets[] = {
    {"CreateDevice", 0x82BD99B0, 0x82387908,
     RendererHookConfidence::Tentative,
     "Fable high-level renderer setup; generated symbol is rex_lhRenderer_Setup. "
     "This is not a direct D3D9 device ABI twin and is already part of the "
     "startup/VFS lifecycle."},
    {"DestructResource", 0x82BE6230, 0x821FC320,
     RendererHookConfidence::Tentative,
     "Broad Fable GPU resource refcount release/destruct helper. This is a "
     "resource-lifetime choke point, not a narrow Sonic D3D9 ABI twin."},
    {"LockTextureRect", 0x82BE9300, 0x82B98930,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"UnlockTextureRect", 0x82BE7780, 0x82B98950,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"LockVertexBuffer", 0x82BE6B98, 0x8225F980,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"UnlockVertexBuffer", 0x82BE6BE8, 0x82B9EEC0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"GetVertexBufferDesc", 0x82BE61D0, 0,
     RendererHookConfidence::Unresolved,
     "No standalone Fable helper found yet. Generated-code pattern search and "
     "the vertex-buffer table around 0x8210EB90 did not produce a Sonic-style "
     "desc getter; Fable appears to read the resource fields directly or via "
     "engine helpers."},
    {"LockIndexBuffer", 0x82BE6CA8, 0x82196980,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"UnlockIndexBuffer", 0x82BE6CF0, 0x82B9EED0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"GetIndexBufferDesc", 0x82BE6200, 0,
     RendererHookConfidence::Unresolved,
     "No standalone Fable helper found yet. Generated-code pattern search and "
     "the index-buffer table around 0x8210C2A8 did not produce a Sonic-style "
     "desc getter."},
    {"GetSurfaceDesc", 0x82BE96F0, 0x821FA070,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {"GetVertexDeclaration", 0x82BE04B0, 0,
     RendererHookConfidence::Unresolved,
     "Create path stores declaration count at +24 and 12-byte elements at +52, "
     "but no standalone Fable getter has been found; likely inlined or unused."},
    {"HashVertexDeclaration", 0x82BE0530, 0,
     RendererHookConfidence::Unresolved,
     "No standalone Fable hash helper found. Fable seems to materialize/cache "
     "GPU state differently from Sonic's D3D9-style declaration hash path."},
    {"Present", 0x82BDA8C0, 0x82B6F1D0,
     RendererHookConfidence::Tentative,
     "Fable renderer end-scene path; generated symbol is rex_lhRenderer_EndScene "
     "and it reaches the display swap helper after frame work is flushed."},
    {"DisplaySwap", 0, 0x82BA34D8,
     RendererHookConfidence::Tentative,
     "Fable display/swap helper called by the end-scene path after frame work "
     "is flushed. Hooked separately so Plume can suppress native display "
     "handoff while preserving higher-level EndScene bookkeeping."},
    {"GetBackBuffer", 0x82BDD330, 0,
     RendererHookConfidence::Unresolved,
     "No standalone Sonic-style getter found. Current backbuffer/current "
     "surface state is tracked through dword_8331B144/dword_8331B148 and "
     "addref'd in setup/end-scene paths."},
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
    {"StretchRect", 0x82BF6400, 0, RendererHookConfidence::Unresolved,
     "No exact Sonic ABI twin found yet. Fable's render-target resolve/copy "
     "behavior appears split across render-target setup, texture creation, "
     "and end-scene/update paths rather than a direct D3D StretchRect wrapper."},
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
    {"D3DXFillVolumeTexture", 0x82C00910, 0x82D17250,
     RendererHookConfidence::High,
     "D3DX fill table sibling immediately after D3DXFillTexture."},
};

}  // namespace refii::renderer
