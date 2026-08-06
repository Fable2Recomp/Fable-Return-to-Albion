#pragma once

#include <cstdint>

namespace refii::renderer {

inline constexpr uint32_t kFableTitleSceneRenderTarget = 0x4012A490u;
inline constexpr uint32_t kFableTitleVisibleRenderTarget = 0x4011F370u;
inline constexpr uint32_t kFableTitleSceneVertexShader = 0x4C1AC790u;
inline constexpr uint32_t kFableTitleSceneMenuVertexShader = 0x4C271010u;
inline constexpr uint32_t kFableTitleSceneTextVertexShader = 0x4C2710C0u;
inline constexpr uint32_t kFableTitleSceneTextPixelShader = 0x42151550u;
inline constexpr uint32_t kFableTitleSceneScalerDepthTexture = 0x40131610u;
inline constexpr uint32_t kFableTitleSceneScalerTexture = 0x40131650u;
inline constexpr uint32_t kFableTitleSceneScalerUploadBase = 0x19580000u;

inline constexpr uint32_t kFableCanonicalTitleBillboardBase = 0x16C40000u;
inline constexpr uint32_t kFableTitleBillboardTileFirstBase = 0x14520000u;
inline constexpr uint32_t kFableTitleBillboardTileLastBase = 0x14840000u;
inline constexpr uint32_t kFableTitleBillboardTileBaseStride = 0x00020000u;
inline constexpr uint32_t kFableTitleBillboardTileAltFirstBase = 0x15800000u;
inline constexpr uint32_t kFableTitleBillboardTileAltLastBase = 0x16040000u;
inline constexpr uint32_t kFableTitleBillboardTileAltBaseStride = 0x00060000u;
inline constexpr uint32_t kFableTitleBillboardTileHeaderFirstBase =
    0x16C40000u;
inline constexpr uint32_t kFableTitleBillboardTileHeaderLastBase =
    0x16DA0000u;
inline constexpr uint32_t kFableTitleBillboardTileHeaderBaseStride =
    0x000B0000u;
inline constexpr uint32_t kFableTitleBillboardTileFirstObject = 0x4C12BAE0u;
inline constexpr uint32_t kFableTitleBillboardTileLastObject = 0x4C12C508u;
inline constexpr uint32_t kFableTitleBillboardTileAltFirstObject =
    0x4C130E00u;
inline constexpr uint32_t kFableTitleBillboardTileAltLastObject =
    0x4C1316F0u;
inline constexpr uint32_t kFableTitleBillboardTileHeaderFirstObject =
    0x4C132C70u;
inline constexpr uint32_t kFableTitleBillboardTileHeaderLastObject =
    0x4C132D40u;
inline constexpr uint32_t kFableTitleBillboardTileObjectStride = 0x68u;
inline constexpr uint32_t kFableTitleBillboardTileHeaderOffset = 0x34u;
inline constexpr uint32_t kFableTitleBillboardCommandTexture = 0x4C12C230u;
inline constexpr uint32_t kFableTitleBillboardCommandTextureAlias =
    0x4C12C264u;
inline constexpr uint32_t kFableTitleBillboardCommandSource = 0x4C12C1C8u;
inline constexpr uint32_t kFableTitleBillboardCommandSourceAlias =
    0x4C12C1FCu;
inline constexpr uint32_t kFableTitleBillboardCommandSourceBase = 0x14740000u;
inline constexpr uint32_t kFableTitlePrivateIndirectionCommandBase =
    0x14760000u;
inline constexpr uint32_t kFableTitlePrivateIndirectionHeader0Base =
    0x14780000u;
inline constexpr uint32_t kFableTitlePrivateIndirectionHeader1Base =
    0x147A0000u;
inline constexpr uint32_t kFableTitlePrivateIndirectionSourceBase =
    0x15FE0000u;
inline constexpr uint32_t kFableTitlePrivateIndirectionHeader0 = 0x4C12C298u;
inline constexpr uint32_t kFableTitlePrivateIndirectionHeader1 = 0x4C12C300u;
inline constexpr uint32_t kFableTitlePrivateIndirectionUpload = 0x4C12C368u;
inline constexpr uint32_t kFableTitlePrivateIndirectionSource = 0x4C131688u;
inline constexpr uint32_t kFableTitlePrivateIndirectionSourceAlias =
    0x4C1316BCu;

inline constexpr uint64_t kFableGuiQuadDeclarationHash =
    0xA42BBBED372F82C1ull;
inline constexpr uint64_t kFableSingleStreamQuadDeclarationHash =
    0x61A3487129E6884Cull;
inline constexpr uint64_t kFablePrivateRectDeclarationHash =
    0x464D723052FFA794ull;
inline constexpr uint64_t kFableTitleIndexedDeclarationHash =
    0xFF8CE3C356AE9480ull;
inline constexpr uint64_t kFableRepeatedMeshDeclarationHash =
    0xA4C4DA765908BDBFull;
inline constexpr uint64_t kFableTitleBillboardMeshDeclarationHash =
    0xDB59E7430E188E25ull;

inline constexpr uint64_t kFableTitleGuiText3DVertexShaderHash =
    0x846EA8260C648669ull;
inline constexpr uint64_t kFableTitleGuiText3DPixelShaderHash =
    0x3E7D60D39290F13Dull;
inline constexpr uint64_t kFableTitleToneMapPixelShaderHash =
    0xA640C5C0084248F0ull;
inline constexpr uint64_t kFableTitleWaterTessellatorZPassVertexHash =
    0x3A30943D5ED210CBull;
inline constexpr uint64_t kFableTitleTreeBillboardPixelHash =
    0x82B98AA62B081948ull;
inline constexpr uint64_t kFableTitleScreenFacingAtlasSpritePixelHash =
    0x824604B7737F75AFull;
inline constexpr uint64_t kFableTitleWaterTessellatorMistedZPassVertexHash =
    0x567659B855B9CAB5ull;
inline constexpr uint64_t kFableTitleOverdrawVisualisePixelHash =
    0x65E4BB2BA429A887ull;
inline constexpr uint64_t kFableTitleGuiText3DVertexHash =
    0x846EA8260C648669ull;
inline constexpr uint64_t kFablePostprocessingDownscale4PixelHash =
    0xCC68DDB7EE5049EBull;
inline constexpr uint64_t kFablePostprocessingGaussBlurPixelHash =
    0xDBF69BA3792D00A9ull;
inline constexpr uint64_t kFableScreenFacingSpritePixelHash =
    0x940E12CB0ED381C2ull;
inline constexpr uint64_t kFableDecalPixelHash = 0xAE53D52D642B100Aull;
inline constexpr uint64_t kFableTexturedQuadPreserveStencilPixelHash =
    0x9CEFC0421AC2A68Cull;
inline constexpr uint64_t kFableStandardMaterialBlt1Glt1Mist0Amb1Nrm1Scr1PixelHash =
    0xA640C5C0084248F0ull;
inline constexpr uint64_t kFableTitleOceanWaterVertexHash =
    0x57305D197842508Bull;
inline constexpr uint64_t kFableTitleOceanWaterMistedVertexHash =
    0x85922739383C71C8ull;
inline constexpr uint64_t kFableTitleTextureMorphPixelHash =
    0x0CDA3675B0635F7Cull;
inline constexpr uint64_t kFableRepeatedMeshesAlphaNoStippleVertexHash =
    0xBC4C9E1A27CB9012ull;
inline constexpr uint64_t kFableParticleMaterialAnim1DepthPixelHash =
    0x140ED469317CD219ull;
inline constexpr uint64_t kFableLandscapeMaterialBlt2Glt1Mist1Amb1Clf1PixelHash =
    0x09490EF2F6286C28ull;
inline constexpr uint64_t kFableLandscapeMaterialBlt7Glt1Mist0Amb1Clf1PixelHash =
    0x93E12CC401D11D3Aull;

}  // namespace refii::renderer
