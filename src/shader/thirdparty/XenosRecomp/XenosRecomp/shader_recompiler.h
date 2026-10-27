#pragma once

#include "shader.h"
#include "shader_code.h"

#include <algorithm>
#include <map>
#include <set>
#include <string>
#include <vector>

struct StringBuffer
{
    std::string out;

    template<class... Args>
    void print(fmt::format_string<Args...> fmt, Args&&... args)
    {
        fmt::vformat_to(std::back_inserter(out), fmt.get(), fmt::make_format_args(args...));
    }

    template<class... Args>
    void println(fmt::format_string<Args...> fmt, Args&&... args)
    {
        fmt::vformat_to(std::back_inserter(out), fmt.get(), fmt::make_format_args(args...));
        out += '\n';
    }
};

// What the recompiler writes. HLSL is the original output, compiled by dxc into SPIR-V, DXIL or
// metallib for the reference renderer. Godot is Godot's shading language: one .gdshaderinc per
// blob holding a vertex() or fragment() over the helpers in
// Godot/src/Engine/material/xenos_common.gdshaderinc, for the classic renderer, where the
// title's own shaders draw as Godot materials.
enum class ShaderDialect
{
    Hlsl,
    Godot,
};

struct ShaderRecompiler : StringBuffer
{
    ShaderDialect dialect = ShaderDialect::Hlsl;
    // The Godot dialect's output stem (e.g. "0248_vshader_standardmaterial_blt1_glt1_mist0_amb2"):
    // the family and permutation tokens, which decide a few input mappings.
    std::string godotEntryName;
    bool godotTransferShader() const { return godotEntryName.find("amb2") != std::string::npos; }
    bool godotRepeatedShader() const { return godotEntryName.find("repeated") != std::string::npos; }
    bool godotTreeShader() const { return godotEntryName.find("tree") != std::string::npos; }
    bool godotLandscapeShader() const { return godotEntryName.find("landscape") != std::string::npos; }
    // The tree billboard: its stream is one FLOAT16_4 record per tree, {guest-world position,
    // world radius}, repeated on the quad's four vertices (`sub_822DB218`).
    bool godotBillboardShader() const { return godotEntryName.find("treebillboard") != std::string::npos; }
    // The fur shells: TEXCOORD2 is the fur block's per-vertex float4 (`sub_82B8FD28`, stream 1),
    // which the host keeps whole in CUSTOM0 (Engine_ClassicFur).
    bool godotFurShellShader() const { return godotEntryName.find("shell_fur") != std::string::npos; }
    // The particle family: the FACE_ME vertex shaders and the PARTICLEMATERIAL pixel shaders,
    // drawn from the title's 48-byte particle records (position, TEXCOORD0, TEXCOORD1), not
    // from an imported model.
    // World-space geometry: the title's water meshes carry guest-world positions and the
    // shaders use them as such (eye vectors from POSITION), and the landscape computes its
    // positions in guest world from constants, so no file rotation or object matrix applies.
    bool godotWorldSpaceShader() const
    {
        return godotEntryName.find("ocean_water") != std::string::npos ||
            godotEntryName.find("oceanwater") != std::string::npos ||
            godotEntryName.find("waterpatch") != std::string::npos ||
            godotEntryName.find("water_tessellator") != std::string::npos ||
            godotEntryName.find("landscapematerial") != std::string::npos;
    }
    // A constant's uniform name: the stage prefix and the table's name with struct members
    // flattened (`g_WaterConstants.m_FresnelBias` -> `g_WaterConstants_m_FresnelBias`).
    std::string godotUniformName(const char* constantName) const;
    bool godotParticleShader() const
    {
        return godotEntryName.find("face_me") != std::string::npos ||
            godotEntryName.find("particlematerial") != std::string::npos;
    }
    // The trail family: TRAILS / DOUBLE_TRAILS vertex shaders fetch their Catmull-Rom control
    // points by a COMPUTED index (the point ring the CPU writes, POSITION0 at +0 and POSITION1 at
    // +16 of 32 bytes), which no per-vertex attribute can carry. Those fetches read the host's
    // control-point texture instead (xr_stream_fetch, one column a point, one row an element).
    bool godotTrailShader() const { return godotEntryName.find("trails") != std::string::npos; }
    // The post-processing family (PSHADER_POSTPROCESSING_*, PSHADER_POSTPROCESSINGMATERIAL_*): the
    // title binds every constant of these by binding id per pass (`sub_821E5DB8`, `sub_822767E0`),
    // so a name lands on a different register in each variant (g_BloomFactor is c31 in 0500 and
    // c77 in 0516). All of them are the pass's own, by name; the host runs these as compute passes
    // (Engine_ClassicPostPass).
    bool godotPostShader() const { return godotEntryName.find("postprocessing") != std::string::npos; }
    bool godotUsesVertexStream = false;
    bool godotWritesPointSize = false;
    uint32_t indentation = 0;
    bool isPixelShader = false;

    // Godot dialect bookkeeping. The body is written first and refers to uniforms, varyings and
    // samplers by name; the header that declares them is assembled afterwards from what the
    // body used, so a shader declares only what it touches.
    std::set<std::string> godotVaryings;
    // uniform name -> sampler type ("sampler2D", "sampler3D", "samplerCube").
    std::map<std::string, std::string> godotSamplers;
    // uniform name -> the filter its fetches state ("nearest", "linear", with "_mipmap" when the
    // mip filter blends), or "mixed" when two fetches disagree. Absent = the fetch constant's.
    std::map<std::string, std::string> godotSamplerFilters;
    // The per-material constants the body read; frame constants come by register from the frame
    // texture and aliased matrices from Godot's built-ins, so neither is declared.
    std::set<const ConstantInfo*> godotConstants;
    std::set<uint32_t> godotLiteralRegisters;
    std::set<uint32_t> godotLoopIds;
    // The integer constants' defaults from the blob's definition table, for the loop-count
    // uniforms the header declares.
    int32_t godotIntDefaults[32][4] = {};
    // Vertex inputs Godot has no attribute for (blend weights and indices, a second colour):
    // recorded for the translator report, read as zero in the shader.
    std::vector<std::string> godotUnsupportedInputs;
    bool godotWritesColor0 = false;
    bool godotWritesDepth = false;
    bool godotWritesExtraTargets = false;
    // The vertex shader's clip position came through the aliased world-view-projection, so it
    // IS Godot's own transform and POSITION takes it outright.
    bool godotAliasedPosition = false;
    bool godotTreeAmbient = false;

    bool godot() const { return dialect == ShaderDialect::Godot; }
    // "vs_" or "ps_": the two stages have separate constant and sampler registers on Xenos, and a
    // Godot shader holds both stages, so every stage-owned uniform carries its stage.
    const char* stagePrefix() const { return isPixelShader ? "ps_" : "vs_"; }
    std::string godotVaryingName(uint32_t usage, uint32_t usageIndex) const;
    std::string godotVertexInput(uint32_t usage, uint32_t usageIndex);
    // How a named constant reaches the Godot shader: a Godot built-in standing in for it, a
    // per-material uniform, or a register of the frame's constant texture.
    enum class GodotConstantSource { Alias, Material, Frame };
    GodotConstantSource godotConstantSource(const char* name) const;
    std::string godotAliasedConstant(const char* name, uint32_t index, uint32_t reg) const;
    std::string godotFrameConstant(const std::string& registerExpression) const;
    std::string godotHeader() const;
    void recompileGodotUnsupported(const char* reason);
    const uint8_t* constantTableData = nullptr;
    std::unordered_map<uint32_t, VertexElement> vertexElements;
    std::unordered_map<uint32_t, std::string> interpolators;
    std::unordered_map<uint32_t, const ConstantInfo*> float4Constants;
    std::unordered_map<uint32_t, const char*> boolConstants;
    std::unordered_map<uint32_t, const char*> samplers;
    std::unordered_map<uint32_t, uint32_t> ifEndLabels;
    uint32_t specConstantsMask = 0;

    // Xenos memory export (memexport) analysis, filled in by
    // analyzeMemoryExports() before any code is emitted.
    //
    // memexportStreamConstants holds the float4 constant register indices
    // recovered from every `mad eA, r#, const0100, c#` idiom, in first-seen
    // order. memexportUnresolvedAddressWrites counts eA writes that did NOT
    // match that idiom - those still export at runtime (eA is decoded in the
    // shader) but their destination cannot be validated ahead of time, so they
    // are surfaced in the translator report rather than passed over silently.
    std::vector<uint32_t> memexportStreamConstants;
    uint32_t memexportEMWrittenMask = 0;
    uint32_t memexportUnresolvedAddressWrites = 0;
    bool memexportEnabled = false;

#ifdef UNLEASHED_RECOMP
    bool hasMtxProjection = false;
    bool hasMtxPrevInvViewProjection = false;
#endif

    void indent()
    {
        for (uint32_t i = 0; i < indentation; i++)
            out += '\t';
    }

    void printDstSwizzle(uint32_t dstSwizzle, bool operand);
    void printDstSwizzle01(uint32_t dstRegister, uint32_t dstSwizzle);

    void analyzeMemoryExports(const uint8_t* shaderData);
    void emitMemoryExportFlush();

    void recompile(const VertexFetchInstruction& instr, uint32_t address);
    void recompile(const TextureFetchInstruction& instr, bool bicubic);
    void recompile(const AluInstruction& instr);

    void recompile(const uint8_t* shaderData, const std::string_view& include);
};
