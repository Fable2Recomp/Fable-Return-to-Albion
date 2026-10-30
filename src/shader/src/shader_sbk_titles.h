// refii - what each title adds to the shared shader bank pipeline.
//
// The pipeline reads any ShaderBankFile v3 bank; this table holds the little a title states
// beyond its banks. Keyed by the shell's title folder (Engine_Title.Folder), which every driver
// of the pipeline passes.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

#include "shader/shader_sbk_pipeline.h"

namespace refii::shader {

// A shader the title draws with that is not in its banks: microcode its executable carries
// inline. Extracted with the named bank's blobs as payload `blob_id`, so the manifest and the
// translator treat it like any other blob.
struct ShaderTitleInlineBlob {
    std::string_view bank;
    std::size_t blob_id = 0;
    std::string_view name;
    ShaderStage stage = ShaderStage::kUnknown;
    // Where the executable holds it; recorded as the blob's source_offset.
    std::uint32_t source_address = 0;
    const std::uint8_t* bytes = nullptr;
    std::size_t size = 0;
};

struct ShaderTitle {
    std::string_view folder;
    const ShaderTitleInlineBlob* inline_blobs = nullptr;
    std::size_t inline_blob_count = 0;
};

// The title whose folder is `folder` (ASCII case ignored), or null when none is.
const ShaderTitle* FindShaderTitle(std::string_view folder);

// "Fable2, Fable3": the folders FindShaderTitle accepts, for an error message.
std::string DescribeShaderTitles();

}  // namespace refii::shader
