#pragma once

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>
#include <string_view>
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
    // Payload id: the selector stored on every shader entry that uses this
    // microcode. Resolves to (chunk = id / 4, slot = id % 4) inside the bank.
    std::size_t blob_index = 0;
    // Byte offset and length of the microcode inside the inflated chunk.
    std::uint32_t source_offset = 0;
    std::uint32_t source_size = 0;
    ShaderStage stage = ShaderStage::kUnknown;
    std::string name_hint;
    std::filesystem::path output_path;
    std::string sha256;
    std::uint32_t chunk_index = 0;
    // Number of shader entries that select this payload. Values above one are
    // normal: Fable II folds shader permutations into boolean-constant
    // branches, so many named bindings share one microcode blob.
    std::uint32_t alias_count = 0;
};

struct ShaderSbkExtractOptions {
    // The title folder the banks belong to ("Fable2", "Fable3"; Engine_Title.Folder). Decides
    // the shaders the title's executable adds inline. Required.
    std::string title;
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
    // The title folder the blobs came from; the godot target classifies the title's own
    // per-draw constants by it. Required (BuildShaderSbkWorkspace takes the extract's when empty).
    std::string title;
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

// Post-translation acceptance gate.  Mirrors
// tools/check_refii_shader_translation_coverage.py so that every driver of the
// pipeline (the refii-shader-sbk CLI, the launcher's "Compile Shaders" flow and
// the build) enforces the identical bar.  Without this the loss is silent: a
// blob with no usable artifact is only visible at runtime as a dropped draw
// ("shader has no generated artifact").
struct ShaderSbkCoverageOptions {
    // Root holding blobs/, <target>/ and the reports.  Equivalent to the
    // translate_out_dir used for the run being verified.
    std::filesystem::path shader_root;
    // Defaults to <shader_root>/extract_manifest.tsv when empty.
    std::filesystem::path extract_manifest_path;
    // Defaults to <shader_root>/compile_report.tsv when empty.
    std::filesystem::path report_path;
    // Same syntax as ShaderSbkTranslateOptions::targets ("auto" resolves to the
    // host's default backend set).
    std::string targets = "auto";
};

struct ShaderSbkCoverageTargetSummary {
    std::string target;
    std::size_t covered = 0;
};

struct ShaderSbkCoverageSummary {
    std::filesystem::path shader_root;
    std::filesystem::path extract_manifest_path;
    std::filesystem::path report_path;
    std::string resolved_targets;
    std::size_t blob_count = 0;
    std::size_t report_row_count = 0;
    std::vector<ShaderSbkCoverageTargetSummary> per_target;
    // Every individual defect found, in discovery order.
    std::vector<std::string> failures;
};

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

// `bank_file` ("Shaders.sbk", "ShadersRelease.sbk") in `directory`, matched ignoring ASCII case:
// Fable 2 ships the banks as named, Fable 3 in lower case. Empty when there is none.
std::filesystem::path FindShaderBankFile(const std::filesystem::path& directory,
                                         std::string_view bank_file);

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

// Returns true only when every extracted blob has a usable translated artifact
// for every requested target.  `error` receives a single-line verdict plus the
// leading failures so callers can surface it verbatim.
bool VerifyTranslatedShaderCoverage(const ShaderSbkCoverageOptions& options,
                                    ShaderSbkCoverageSummary* summary = nullptr,
                                    std::string* error = nullptr);

// "vulkan=589/589 d3d12=589/589" - shared by the CLI and the launcher so both
// report coverage the same way.
std::string DescribeShaderCoverageSummary(const ShaderSbkCoverageSummary& summary);

}  // namespace refii::shader
