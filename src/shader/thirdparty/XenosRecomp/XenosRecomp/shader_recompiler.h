#pragma once

#include "shader.h"
#include "shader_code.h"

#include <algorithm>
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

struct ShaderRecompiler : StringBuffer
{
    uint32_t indentation = 0;
    bool isPixelShader = false;
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
