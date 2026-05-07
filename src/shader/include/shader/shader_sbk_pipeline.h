#pragma once

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>
#include <vector>

namespace refii::shader {

enum class ShaderStage {
    kUnknown,
    kVertex,
    kPixel,
};

struct ShaderSbkEntryMetadata {
    std::string bank_name;
    std::size_t entry_index = 0;
    std::uint32_t entry_id = 0;
    std::uint32_t stage_raw = 0;
    ShaderStage stage = ShaderStage::kUnknown;
    std::string entry_name;
    std::string entry_hash_hex;
};

struct ShaderSbkBlobMetadata {
    std::string bank_name;
    std::size_t blob_index = 0;
    std::uint32_t source_offset = 0;
    std::uint32_t source_size = 0;
    ShaderStage stage = ShaderStage::kUnknown;
    std::string name_hint;
    std::filesystem::path output_path;
    std::string sha256;
};

struct ShaderSbkExtractOptions {
    std::filesystem::path input_dir;
    std::filesystem::path extract_out_dir;
    std::filesystem::path manifest_path;
};

struct ShaderSbkExtractSummary {
    std::filesystem::path input_dir;
    std::filesystem::path extract_out_dir;
    std::filesystem::path manifest_path;
    std::size_t bank_count = 0;
    std::size_t entry_count = 0;
    std::size_t blob_count = 0;
};

struct ShaderSbkTranslateOptions {
    std::filesystem::path extract_manifest_path;
    std::filesystem::path translate_out_dir;
    std::filesystem::path report_path;
    std::filesystem::path translator_path;
    std::uint64_t translator_timeout_ms = 60000;
    std::string targets = "auto";
    bool dedupe_identical_blobs = true;
};

struct ShaderSbkTranslateSummary {
    std::filesystem::path extract_manifest_path;
    std::filesystem::path translate_out_dir;
    std::filesystem::path report_path;
    std::filesystem::path translator_path;
    std::string resolved_targets;
    std::size_t blob_count = 0;
    std::size_t target_count = 0;
    std::size_t attempted = 0;
    std::size_t succeeded = 0;
    std::size_t failed = 0;
    std::size_t timed_out = 0;
    std::size_t backend_missing = 0;
    std::size_t dedupe_executed = 0;
    std::size_t dedupe_aliased = 0;
};

struct ShaderSbkBuildOptions {
    ShaderSbkExtractOptions extract;
    ShaderSbkTranslateOptions translate;
};

struct ShaderSbkBuildSummary {
    ShaderSbkExtractSummary extract;
    ShaderSbkTranslateSummary translate;
};

struct ShaderSbkProgress {
    std::string phase;
    std::string bank_name;
    std::size_t bank_index = 0;
    std::size_t total_banks = 0;
    std::size_t blob_index = 0;
    std::size_t total_blobs = 0;
    std::size_t completed_items = 0;
    std::size_t total_items = 0;
    std::string target;
    std::string current_input;
    std::string stage;
};

using ShaderSbkProgressCallback = std::function<void(const ShaderSbkProgress&)>;

struct ShaderSbkXenosProbeOptions {
    std::filesystem::path extract_manifest_path;
    std::filesystem::path report_path;
};

struct ShaderSbkXenosProbeSummary {
    std::filesystem::path extract_manifest_path;
    std::filesystem::path report_path;
    std::size_t blob_count = 0;
    std::size_t blobs_with_candidates = 0;
    std::size_t total_candidates = 0;
};

const char* DescribeShaderStage(ShaderStage stage);

bool ExtractShaderSbkWorkspace(const ShaderSbkExtractOptions& options,
                               ShaderSbkExtractSummary* summary = nullptr,
                               std::string* error = nullptr,
                               const ShaderSbkProgressCallback& progress_callback = {});

bool TranslateExtractedShaderSbk(const ShaderSbkTranslateOptions& options,
                                 ShaderSbkTranslateSummary* summary = nullptr,
                                 std::string* error = nullptr,
                                 const ShaderSbkProgressCallback& progress_callback = {});

bool BuildShaderSbkWorkspace(const ShaderSbkBuildOptions& options,
                             ShaderSbkBuildSummary* summary = nullptr,
                             std::string* error = nullptr,
                             const ShaderSbkProgressCallback& progress_callback = {});

bool ProbeExtractedShaderSbkForXenos(const ShaderSbkXenosProbeOptions& options,
                                     ShaderSbkXenosProbeSummary* summary = nullptr,
                                     std::string* error = nullptr);

}  // namespace refii::shader
