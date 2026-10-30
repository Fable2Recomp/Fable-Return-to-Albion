#include "shader.h"
#include "shader_recompiler.h"
#include "dxc_compiler.h"
#include "xenos_recomp_api.h"
#include <atomic>
#include <cassert>
#include <cstdio>
#include <filesystem>
#include <memory>
#include <string>
#include <vector>
#include <map>
#include <thread>

static std::unique_ptr<uint8_t[]> readAllBytes(const char* filePath, size_t& fileSize)
{
    FILE* file = fopen(filePath, "rb");
    if (file == nullptr) {
        fileSize = 0;
        return {};
    }

    if (fseek(file, 0, SEEK_END) != 0) {
        fclose(file);
        fileSize = 0;
        return {};
    }

    const long fileSizeValue = ftell(file);
    if (fileSizeValue < 0) {
        fclose(file);
        fileSize = 0;
        return {};
    }
    fileSize = static_cast<size_t>(fileSizeValue);

    if (fseek(file, 0, SEEK_SET) != 0) {
        fclose(file);
        fileSize = 0;
        return {};
    }

    auto data = std::make_unique<uint8_t[]>(fileSize);
    const size_t readSize = fread(data.get(), 1, fileSize, file);
    fclose(file);
    if (readSize != fileSize) {
        fileSize = 0;
        return {};
    }

    return data;
}

static void writeAllBytes(const char* filePath, const void* data, size_t dataSize)
{
    FILE* file = fopen(filePath, "wb");
    if (file == nullptr) {
        return;
    }
    fwrite(data, 1, dataSize, file);
    fclose(file);
}

static void translateSingleShaderFile(const char* input,
                                      const char* output,
                                      const std::string_view& include,
                                      XenosRecompTranslationStats* stats,
                                      ShaderDialect dialect = ShaderDialect::Hlsl,
                                      const char* title = nullptr)
{
    ShaderRecompiler recompiler;
    recompiler.dialect = dialect;
    recompiler.godotTitle = title != nullptr ? title : "";
    {
        std::string stem = std::filesystem::path(output).stem().string();
        for (char& c : stem)
            c = char(std::tolower(static_cast<unsigned char>(c)));
        recompiler.godotEntryName = stem;
    }
    size_t fileSize = 0;
    auto data = readAllBytes(input, fileSize);
    recompiler.recompile(data.get(), include);
    writeAllBytes(output, recompiler.out.data(), recompiler.out.size());

    if (stats != nullptr)
    {
        stats->memexportEnabled = recompiler.memexportEnabled ? 1u : 0u;
        stats->memexportStreams = static_cast<uint32_t>(recompiler.memexportStreamConstants.size());
        stats->memexportDataRegisterMask = recompiler.memexportEMWrittenMask;
        stats->memexportUnresolvedAddressWrites = recompiler.memexportUnresolvedAddressWrites;
    }
}

struct RecompiledShader
{
    uint8_t* data = nullptr;
    IDxcBlob* dxil = nullptr;
    std::vector<uint8_t> spirv;
    uint32_t specConstantsMask = 0;
};

int XenosRecompEntrypoint(int argc, char** argv)
{
#ifndef XENOS_RECOMP_INPUT
    if (argc < 4)
    {
        printf("Usage: XenosRecomp [input path] [output path] [shader common header file path]");
        return 0;
    }
#endif

    const char* input =
#ifdef XENOS_RECOMP_INPUT 
        XENOS_RECOMP_INPUT
#else
        argv[1]
#endif
    ;

    const char* output =
#ifdef XENOS_RECOMP_OUTPUT 
        XENOS_RECOMP_OUTPUT
#else
        argv[2]
#endif
        ;
    
    const char* includeInput =
#ifdef XENOS_RECOMP_INCLUDE_INPUT
        XENOS_RECOMP_INCLUDE_INPUT
#else
        argv[3]
#endif
        ;

    size_t includeSize = 0;
    auto includeData = readAllBytes(includeInput, includeSize);
    std::string_view include(reinterpret_cast<const char*>(includeData.get()), includeSize);

    if (std::filesystem::is_directory(input))
    {
        std::vector<std::unique_ptr<uint8_t[]>> files;
        std::map<XXH64_hash_t, RecompiledShader> shaders;

        for (auto& file : std::filesystem::recursive_directory_iterator(input))
        {
            if (std::filesystem::is_directory(file))
            {
                continue;
            }
            
            size_t fileSize = 0;
            auto fileData = readAllBytes(file.path().string().c_str(), fileSize);
            bool foundAny = false;

            for (size_t i = 0; fileSize > sizeof(ShaderContainer) && i < fileSize - sizeof(ShaderContainer) - 1;)
            {
                auto shaderContainer = reinterpret_cast<const ShaderContainer*>(fileData.get() + i);
                size_t dataSize = shaderContainer->virtualSize + shaderContainer->physicalSize;

                if ((shaderContainer->flags & 0xFFFFFF00) == 0x102A1100 &&
                    dataSize <= (fileSize - i) &&
                    shaderContainer->field1C == 0 &&
                    shaderContainer->field20 == 0)
                {
                    XXH64_hash_t hash = XXH3_64bits(shaderContainer, dataSize);
                    auto shader = shaders.try_emplace(hash);
                    if (shader.second)
                    {
                        shader.first->second.data = fileData.get() + i;
                        foundAny = true;
                    }

                    i += dataSize;
                }
                else
                {
                    i += sizeof(uint32_t);
                }
            }

            if (foundAny)
                files.emplace_back(std::move(fileData));
        }

        std::atomic<uint32_t> progress = 0;

        std::for_each(std::execution::par_unseq, shaders.begin(), shaders.end(), [&](auto& hashShaderPair)
            {
                auto& shader = hashShaderPair.second;

                thread_local ShaderRecompiler recompiler;
                recompiler = {};
                recompiler.recompile(shader.data, include);

                shader.specConstantsMask = recompiler.specConstantsMask;

                thread_local DxcCompiler dxcCompiler;

#ifdef XENOS_RECOMP_DXIL
                shader.dxil = dxcCompiler.compile(recompiler.out, recompiler.isPixelShader, recompiler.specConstantsMask != 0, false);
                assert(shader.dxil != nullptr);
                assert(*(reinterpret_cast<uint32_t *>(shader.dxil->GetBufferPointer()) + 1) != 0 && "DXIL was not signed properly!");
#endif

                IDxcBlob* spirv = dxcCompiler.compile(recompiler.out, recompiler.isPixelShader, false, true);
                assert(spirv != nullptr);

                bool result = smolv::Encode(spirv->GetBufferPointer(), spirv->GetBufferSize(), shader.spirv, smolv::kEncodeFlagStripDebugInfo);
                assert(result);

                spirv->Release();

                size_t currentProgress = ++progress;
                if ((currentProgress % 10) == 0 || (currentProgress == shaders.size() - 1))
                    fmt::println("Recompiling shaders... {}%", currentProgress / float(shaders.size()) * 100.0f);
            });

        fmt::println("Creating shader cache...");

        StringBuffer f;
        f.println("#include \"shader_cache.h\"");
        f.println("ShaderCacheEntry g_shaderCacheEntries[] = {{");

        std::vector<uint8_t> dxil;
        std::vector<uint8_t> spirv;

        for (auto& [hash, shader] : shaders)
        {
            f.println("\t{{ 0x{:X}, {}, {}, {}, {}, {} }},",
                hash, dxil.size(), (shader.dxil != nullptr) ? shader.dxil->GetBufferSize() : 0, spirv.size(), shader.spirv.size(), shader.specConstantsMask);

            if (shader.dxil != nullptr)
            {
                dxil.insert(dxil.end(), reinterpret_cast<uint8_t *>(shader.dxil->GetBufferPointer()),
                    reinterpret_cast<uint8_t *>(shader.dxil->GetBufferPointer()) + shader.dxil->GetBufferSize());
            }
            
            spirv.insert(spirv.end(), shader.spirv.begin(), shader.spirv.end());
        }

        f.println("}};");

        fmt::println("Compressing DXIL cache...");

        int level = ZSTD_maxCLevel();

#ifdef XENOS_RECOMP_DXIL
        std::vector<uint8_t> dxilCompressed(ZSTD_compressBound(dxil.size()));
        dxilCompressed.resize(ZSTD_compress(dxilCompressed.data(), dxilCompressed.size(), dxil.data(), dxil.size(), level));

        f.print("const uint8_t g_compressedDxilCache[] = {{");

        for (auto data : dxilCompressed)
            f.print("{},", data);

        f.println("}};");
        f.println("const size_t g_dxilCacheCompressedSize = {};", dxilCompressed.size());
        f.println("const size_t g_dxilCacheDecompressedSize = {};", dxil.size());
#endif

        fmt::println("Compressing SPIRV cache...");

        std::vector<uint8_t> spirvCompressed(ZSTD_compressBound(spirv.size()));
        spirvCompressed.resize(ZSTD_compress(spirvCompressed.data(), spirvCompressed.size(), spirv.data(), spirv.size(), level));

        f.print("const uint8_t g_compressedSpirvCache[] = {{");

        for (auto data : spirvCompressed)
            f.print("{},", data);

        f.println("}};");

        f.println("const size_t g_spirvCacheCompressedSize = {};", spirvCompressed.size());
        f.println("const size_t g_spirvCacheDecompressedSize = {};", spirv.size());
        f.println("const size_t g_shaderCacheEntryCount = {};", shaders.size());

        writeAllBytes(output, f.out.data(), f.out.size());
    }
    else
    {
        translateSingleShaderFile(input, output, include, nullptr);
    }

    return 0;
}

extern "C" bool XenosRecompTranslateEx(
    const char* inputPath,
    const char* outputPath,
    const char* shaderCommonPath,
    uint32_t dialect,
    const char* title,
    XenosRecompTranslationStats* stats,
    const char** errorMessage) {
    thread_local std::string lastError;
    if (errorMessage != nullptr) {
        *errorMessage = nullptr;
    }
    if (stats != nullptr) {
        *stats = XenosRecompTranslationStats{};
    }

    if (dialect == XENOSRECOMP_DIALECT_HLSL) {
        return XenosRecompTranslateToHlslEx(inputPath, outputPath, shaderCommonPath, stats, errorMessage);
    }
    if (dialect != XENOSRECOMP_DIALECT_GODOT) {
        if (errorMessage != nullptr) {
            lastError = "unknown dialect " + std::to_string(dialect);
            *errorMessage = lastError.c_str();
        }
        return false;
    }
    if (inputPath == nullptr || outputPath == nullptr || std::filesystem::is_directory(inputPath)) {
        if (errorMessage != nullptr) {
            lastError = "the Godot dialect translates one blob to one file";
            *errorMessage = lastError.c_str();
        }
        return false;
    }

    XenosRecompTranslationStats localStats{};
    translateSingleShaderFile(inputPath, outputPath, std::string_view{},
                              stats != nullptr ? stats : &localStats, ShaderDialect::Godot, title);
    return true;
}

extern "C" bool XenosRecompTranslateToHlslEx(
    const char* inputPath,
    const char* outputPath,
    const char* shaderCommonPath,
    XenosRecompTranslationStats* stats,
    const char** errorMessage) {
    thread_local std::string lastError;
    if (errorMessage != nullptr) {
        *errorMessage = nullptr;
    }
    if (stats != nullptr) {
        *stats = XenosRecompTranslationStats{};
    }

    if (inputPath == nullptr || outputPath == nullptr || shaderCommonPath == nullptr) {
        if (errorMessage != nullptr) {
            lastError = "invalid input, output, or include path";
            *errorMessage = lastError.c_str();
        }
        return false;
    }

    // Single-blob translation is the path refii drives, and the only one that
    // can report per-shader memexport facts. Directories still go through the
    // shader-cache entrypoint.
    if (stats != nullptr && !std::filesystem::is_directory(inputPath)) {
        size_t includeSize = 0;
        auto includeData = readAllBytes(shaderCommonPath, includeSize);
        if (includeData == nullptr) {
            if (errorMessage != nullptr) {
                lastError = std::string("failed to read shader common header: ") + shaderCommonPath;
                *errorMessage = lastError.c_str();
            }
            return false;
        }
        std::string_view include(reinterpret_cast<const char*>(includeData.get()), includeSize);
        translateSingleShaderFile(inputPath, outputPath, include, stats);
        return true;
    }

    char* args[4];
    args[0] = const_cast<char*>("XenosRecomp");
    args[1] = const_cast<char*>(inputPath);
    args[2] = const_cast<char*>(outputPath);
    args[3] = const_cast<char*>(shaderCommonPath);

    const int exitCode = XenosRecompEntrypoint(4, args);
    if (exitCode != 0) {
        if (errorMessage != nullptr) {
            lastError = "XenosRecomp exited with code " + std::to_string(exitCode);
            *errorMessage = lastError.c_str();
        }
        return false;
    }

    return true;
}

extern "C" bool XenosRecompTranslateToHlsl(
    const char* inputPath,
    const char* outputPath,
    const char* shaderCommonPath,
    const char** errorMessage) {
    return XenosRecompTranslateToHlslEx(
        inputPath, outputPath, shaderCommonPath, nullptr, errorMessage);
}

int main(int argc, char** argv) {
    return XenosRecompEntrypoint(argc, argv);
}
