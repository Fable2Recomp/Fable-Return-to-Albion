// refii - the shader bank pipeline behind the launcher's C ABI.
#include "shader/refii_shader_build.h"

#include <algorithm>
#include <cstring>
#include <exception>
#include <filesystem>
#include <sstream>
#include <string>

#include "shader/shader_sbk_pipeline.h"
#include "shader_sbk_titles.h"

namespace {

namespace fs = std::filesystem;

/// Raised out of the progress callback when the caller asks to stop. Not a std::exception on
/// purpose: the pipeline's own handlers catch that for filesystem faults and would swallow it.
struct StopRequested {};

void WriteError(char* error, size_t capacity, const std::string& message) {
    if (error == nullptr || capacity == 0) {
        return;
    }
    const size_t take = std::min(message.size(), capacity - 1);
    std::memcpy(error, message.data(), take);
    error[take] = '\0';
}

/// The caller's callback, with a stop request turned into an unwind.
class Reporter {
public:
    Reporter(refii_shader_build_progress_fn progress, void* user)
        : progress_(progress), user_(user) {}

    void Progress(const refii::shader::ShaderSbkProgress& progress) const {
        Emit(REFII_SHADER_BUILD_EVENT_PROGRESS, progress.phase,
             static_cast<int32_t>(progress.completed_items),
             static_cast<int32_t>(progress.total_items));
    }

    void Phase(const char* phase) const { Emit(REFII_SHADER_BUILD_EVENT_PROGRESS, phase, 0, 0); }

    void Log(const std::string& line) const { Emit(REFII_SHADER_BUILD_EVENT_LOG, line, 0, 0); }

private:
    void Emit(int32_t event, const std::string& detail, int32_t index, int32_t total) const {
        if (progress_ != nullptr && progress_(user_, event, detail.c_str(), index, total) != 0) {
            throw StopRequested{};
        }
    }

    refii_shader_build_progress_fn progress_;
    void* user_;
};

/// Every directory the pipeline writes, plus the case variants older runs left behind. A
/// target directory that survives would let stale artifacts pass the coverage gate.
constexpr const char* kStaleOutput[] = {
    "blobs", "logs", "vulkan", "Vulkan", "d3d12", "D3D12", "metal", "Metal",
};

int32_t Run(const std::string& title, const fs::path& bank_dir, const fs::path& output_dir,
            const std::string& targets, const Reporter& reporter, char* error,
            size_t error_capacity) {
    using namespace refii::shader;

    // Before anything is removed: a run for no known title must leave the last output alone.
    if (FindShaderTitle(title) == nullptr) {
        WriteError(error, error_capacity,
                   "Unknown shader title '" + title + "' (expected one of " +
                       DescribeShaderTitles() + ")");
        return REFII_SHADER_BUILD_FAILED;
    }

    if (FindShaderBankFile(bank_dir, "Shaders.sbk").empty()) {
        WriteError(error, error_capacity,
                   "Shaders.sbk is not in " + bank_dir.string() +
                       "; the game data is not installed there.");
        return REFII_SHADER_BUILD_FAILED;
    }

    for (const char* stale : kStaleOutput) {
        fs::remove_all(output_dir / stale);
    }
    fs::create_directories(output_dir);

    ShaderSbkBuildOptions options;
    options.extract.title = title;
    options.translate.title = title;
    options.extract.input_dir = bank_dir;
    options.extract.extract_out_dir = output_dir;
    options.extract.manifest_path = output_dir / "extract_manifest.tsv";
    options.translate.extract_manifest_path = options.extract.manifest_path;
    options.translate.translate_out_dir = output_dir;
    options.translate.report_path = output_dir / "compile_report.tsv";
    options.translate.targets = targets;

    ShaderSbkBuildSummary summary;
    std::string message;
    const bool built = BuildShaderSbkWorkspace(
        options, &summary, &message,
        [&reporter](const ShaderSbkProgress& progress) { reporter.Progress(progress); });
    if (!built) {
        WriteError(error, error_capacity,
                   message.empty() ? "the shader pipeline failed without a message" : message);
        return REFII_SHADER_BUILD_FAILED;
    }

    // The pipeline returns true as long as it wrote a report, so a run in which individual
    // shaders failed lands here. A partial shader set is invisible until a draw is dropped
    // for "shader has no generated artifact", so it is a failure, not a warning.
    if (summary.translate.failed > 0 || summary.translate.timed_out > 0 ||
        summary.translate.backend_missing > 0) {
        std::ostringstream text;
        text << "Shader compile produced " << summary.translate.failed << " failed, "
             << summary.translate.timed_out << " timed-out and "
             << summary.translate.backend_missing << " backend-missing entries (targets "
             << summary.translate.resolved_targets << "). See "
             << options.translate.report_path.string() << ".";
        WriteError(error, error_capacity, text.str());
        return REFII_SHADER_BUILD_FAILED;
    }

    reporter.Phase("verifying");
    ShaderSbkCoverageOptions coverage;
    coverage.shader_root = output_dir;
    coverage.targets = targets;
    ShaderSbkCoverageSummary coverage_summary;
    std::string coverage_error;
    const bool covered = VerifyTranslatedShaderCoverage(coverage, &coverage_summary, &coverage_error);
    reporter.Log("shader coverage: " + DescribeShaderCoverageSummary(coverage_summary));
    if (!covered) {
        WriteError(error, error_capacity,
                   coverage_error.empty() ? "the translated shaders do not cover every blob"
                                          : coverage_error);
        return REFII_SHADER_BUILD_FAILED;
    }

    std::ostringstream text;
    text << "shaders: " << summary.extract.blob_count << " blob(s) from "
         << summary.extract.bank_count << " bank(s); " << summary.translate.succeeded
         << " compiled, " << summary.translate.dedupe_aliased << " shared with an identical blob; targets "
         << summary.translate.resolved_targets;
    reporter.Log(text.str());
    return REFII_SHADER_BUILD_OK;
}

}  // namespace

extern "C" {

REFII_SHADER_BUILD_EXPORT int32_t refii_shader_build_v1_abi_version(void) {
    return REFII_SHADER_BUILD_ABI_VERSION;
}

REFII_SHADER_BUILD_EXPORT int32_t refii_shader_build_v2_run(
    const char* title, const char* bank_dir, const char* output_dir, const char* targets,
    refii_shader_build_progress_fn progress, void* user, char* error, size_t error_capacity) {
    if (title == nullptr || *title == '\0') {
        WriteError(error, error_capacity, "no title was given for the shader banks");
        return REFII_SHADER_BUILD_FAILED;
    }
    if (bank_dir == nullptr || *bank_dir == '\0') {
        WriteError(error, error_capacity, "no shader bank directory was given");
        return REFII_SHADER_BUILD_FAILED;
    }
    if (output_dir == nullptr || *output_dir == '\0') {
        WriteError(error, error_capacity, "no output directory was given");
        return REFII_SHADER_BUILD_FAILED;
    }

    // Exceptions stop here. The caller is managed code reached through P/Invoke, and a C++
    // exception crossing that boundary is a process abort with no message.
    try {
        const Reporter reporter(progress, user);
        return Run(title, fs::absolute(bank_dir), fs::absolute(output_dir),
                   (targets != nullptr && *targets != '\0') ? targets : "auto", reporter, error,
                   error_capacity);
    } catch (const StopRequested&) {
        WriteError(error, error_capacity, "stopped");
        return REFII_SHADER_BUILD_STOPPED;
    } catch (const std::exception& exception) {
        WriteError(error, error_capacity, exception.what());
        return REFII_SHADER_BUILD_FAILED;
    } catch (...) {
        WriteError(error, error_capacity, "the shader pipeline raised an unknown exception");
        return REFII_SHADER_BUILD_FAILED;
    }
}

}  // extern "C"
