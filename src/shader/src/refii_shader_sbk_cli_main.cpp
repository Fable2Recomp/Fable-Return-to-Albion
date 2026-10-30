#include "shader/shader_sbk_pipeline.h"

#include <exception>
#include <filesystem>
#include <iostream>
#include <limits>
#include <string>
#include <string_view>

namespace {

struct Options {
    std::string command;
    // The title folder the banks belong to (Engine_Title.Folder: Fable2, Fable3).
    std::string title;
    std::filesystem::path input_dir;
    std::filesystem::path extract_out;
    std::filesystem::path manifest;
    std::filesystem::path extract_manifest;
    std::filesystem::path translate_out;
    std::filesystem::path report;
    std::filesystem::path xenos_report;
    std::filesystem::path translator;
    std::uint64_t translator_timeout_ms = 60000;
    std::string targets = "auto";
    bool dedupe_identical_blobs = true;
    // Post-translation acceptance gate.  On by default: a partially translated
    // shader set is invisible until the runtime silently drops the draw.
    bool verify_coverage = true;
};

bool ParseOnOff(std::string_view text, bool* value) {
    if (value == nullptr) {
        return false;
    }

    if (text == "on" || text == "true" || text == "1") {
        *value = true;
        return true;
    }
    if (text == "off" || text == "false" || text == "0") {
        *value = false;
        return true;
    }
    return false;
}

bool ParseCommonBuildFlags(int argc, char** argv, int start_index, Options* options) {
    if (options == nullptr) {
        return false;
    }

    for (int i = start_index; i < argc; ++i) {
        const std::string_view argument(argv[i]);
        if (argument == "--title" && i + 1 < argc) {
            options->title = argv[++i];
        } else if (argument == "--input-dir" && i + 1 < argc) {
            options->input_dir = std::filesystem::path(argv[++i]);
        } else if (argument == "--extract-out" && i + 1 < argc) {
            options->extract_out = std::filesystem::path(argv[++i]);
        } else if (argument == "--manifest" && i + 1 < argc) {
            options->manifest = std::filesystem::path(argv[++i]);
        } else if (argument == "--extract-manifest" && i + 1 < argc) {
            options->extract_manifest = std::filesystem::path(argv[++i]);
        } else if ((argument == "--translate-out" || argument == "--compile-out") &&
                   i + 1 < argc) {
            options->translate_out = std::filesystem::path(argv[++i]);
        } else if (argument == "--report" && i + 1 < argc) {
            options->report = std::filesystem::path(argv[++i]);
        } else if (argument == "--xenos-report" && i + 1 < argc) {
            options->xenos_report = std::filesystem::path(argv[++i]);
        } else if (argument == "--translator" && i + 1 < argc) {
            options->translator = std::filesystem::path(argv[++i]);
        } else if (argument == "--translator-timeout-ms" && i + 1 < argc) {
            try {
                options->translator_timeout_ms = std::stoull(argv[++i]);
            } catch (const std::exception&) {
                return false;
            }
        } else if (argument == "--targets" && i + 1 < argc) {
            options->targets = argv[++i];
        } else if (argument == "--dedupe-identical-blobs" && i + 1 < argc) {
            if (!ParseOnOff(argv[++i], &options->dedupe_identical_blobs)) {
                return false;
            }
        } else if (argument == "--verify-coverage" && i + 1 < argc) {
            if (!ParseOnOff(argv[++i], &options->verify_coverage)) {
                return false;
            }
        } else {
            return false;
        }
    }

    return true;
}

bool ParseArguments(int argc, char** argv, Options* options) {
    if (options == nullptr || argc < 2) {
        return false;
    }

    options->command = argv[1];
    if (options->command != "extract" && options->command != "translate" &&
        options->command != "compile" &&
        options->command != "build" && options->command != "probe-xenos") {
        return false;
    }

    if (!ParseCommonBuildFlags(argc, argv, 2, options)) {
        return false;
    }

    if (options->extract_manifest.empty() && !options->manifest.empty()) {
        options->extract_manifest = options->manifest;
    }

    if (options->command == "extract") {
        return !options->title.empty() && !options->input_dir.empty() &&
               !options->extract_out.empty() && !options->manifest.empty();
    }

    if (options->command == "translate" || options->command == "compile") {
        return !options->title.empty() && !options->extract_manifest.empty() &&
               !options->translate_out.empty() && !options->report.empty();
    }

    if (options->command == "probe-xenos") {
        return !options->extract_manifest.empty() &&
               (!options->xenos_report.empty() || !options->report.empty());
    }

    return !options->title.empty() && !options->input_dir.empty() &&
           !options->extract_out.empty() &&
           !options->manifest.empty() && !options->extract_manifest.empty() &&
           !options->translate_out.empty() &&
           !options->report.empty();
}

void PrintUsage() {
    std::cerr
        << "usage: refii-shader-sbk extract --title <Fable2|Fable3> --input-dir <dir> --extract-out <dir>\n"
        << "                          --manifest <path>\n"
        << "       refii-shader-sbk compile --title <Fable2|Fable3> --extract-manifest <path>\n"
        << "                          --compile-out <dir> --report <path>\n"
        << "                          [--translator <exe>] [--translator-timeout-ms <ms>]\n"
        << "                          [--targets <vulkan|d3d12|metal|auto>]\n"
        << "                          [--dedupe-identical-blobs <on|off>]\n"
        << "                          [--verify-coverage <on|off>]\n"
        << "       refii-shader-sbk build --title <Fable2|Fable3> --input-dir <dir> --extract-out <dir>\n"
        << "                          --manifest <path>\n"
        << "                          --compile-out <dir> --report <path>\n"
        << "                          [--extract-manifest <path>]\n"
        << "                          [--translator <exe>] [--translator-timeout-ms <ms>]\n"
        << "                          [--targets <vulkan|d3d12|metal|auto>]\n"
        << "                          [--dedupe-identical-blobs <on|off>]\n"
        << "                          [--verify-coverage <on|off>]\n"
        << "       refii-shader-sbk probe-xenos --extract-manifest <path> --xenos-report <path>\n"
        << "                          (or --report <path>)\n"
        << "\n"
        << "notes:\n"
        << "- --title is the title folder the banks belong to; it decides the shaders the\n"
        << "  title's executable adds inline and the title's own per-draw constants.\n"
        << "- `compile` requires --extract-manifest (or --manifest alias).\n"
        << "- `translate` and --translate-out are accepted as legacy aliases.\n"
        << "- `build` defaults --extract-manifest to --manifest when omitted.\n"
        << "- build fails on extraction errors and always writes a compile report.\n"
        << "- compile/build exit non-zero when any entry failed, timed out or had no\n"
        << "  backend, and (unless --verify-coverage off) when the translated set does\n"
        << "  not cover every extracted blob for every target.\n";
}

// Shared by `compile`, `translate` and `build`: a run that produced any failed,
// timed-out or backend-missing entry, or that does not fully cover the extracted
// blob set, must not exit 0.  Historic behaviour returned 0 regardless, which is
// how a report recording 543/588 translator_launch_failed once passed for a
// successful run.
int FinishTranslateCommand(const refii::shader::ShaderSbkTranslateSummary& summary,
                           bool verify_coverage) {
    int exit_code = 0;

    if (summary.failed != 0 || summary.timed_out != 0 || summary.backend_missing != 0) {
        std::cerr << "shader_translation=REJECTED failed=" << summary.failed
                  << " timed_out=" << summary.timed_out
                  << " backend_missing=" << summary.backend_missing
                  << " report=" << summary.report_path << "\n";
        exit_code = 1;
    }

    if (!verify_coverage) {
        return exit_code;
    }

    refii::shader::ShaderSbkCoverageOptions coverage_options;
    coverage_options.shader_root = summary.translate_out_dir;
    coverage_options.extract_manifest_path = summary.extract_manifest_path;
    coverage_options.report_path = summary.report_path;
    coverage_options.targets = summary.resolved_targets;

    refii::shader::ShaderSbkCoverageSummary coverage_summary;
    std::string coverage_error;
    if (!refii::shader::VerifyTranslatedShaderCoverage(coverage_options, &coverage_summary,
                                                       &coverage_error)) {
        std::cerr << coverage_error << "\n";
        return 1;
    }

    std::cout << "Coverage: shader_translation_coverage=ACCEPTED blobs="
              << coverage_summary.blob_count << " targets="
              << coverage_summary.resolved_targets << " covered="
              << refii::shader::DescribeShaderCoverageSummary(coverage_summary) << "\n";
    return exit_code;
}

}  // namespace

int main(int argc, char** argv) {
    Options options;
    if (!ParseArguments(argc, argv, &options)) {
        PrintUsage();
        return 1;
    }

    if (options.command == "extract") {
        refii::shader::ShaderSbkExtractOptions extract_options;
        extract_options.title = options.title;
        extract_options.input_dir = options.input_dir;
        extract_options.extract_out_dir = options.extract_out;
        extract_options.manifest_path = options.manifest;

        refii::shader::ShaderSbkExtractSummary summary;
        std::string error;
        if (!refii::shader::ExtractShaderSbkWorkspace(extract_options, &summary, &error)) {
            std::cerr << error << "\n";
            return 1;
        }

        std::cout << "Input:    " << summary.input_dir << "\n";
        std::cout << "Extract:  " << summary.extract_out_dir << "\n";
        std::cout << "Manifest: " << summary.manifest_path << "\n";
        std::cout << "Banks:    " << summary.bank_count << "\n";
        std::cout << "Entries:  " << summary.entry_count << "\n";
        std::cout << "Blobs:    " << summary.blob_count << "\n";
        return 0;
    }

    if (options.command == "translate" || options.command == "compile") {
        refii::shader::ShaderSbkTranslateOptions translate_options;
        translate_options.title = options.title;
        translate_options.extract_manifest_path = options.extract_manifest;
        translate_options.translate_out_dir = options.translate_out;
        translate_options.report_path = options.report;
        translate_options.translator_path = options.translator;
        translate_options.translator_timeout_ms = options.translator_timeout_ms;
        translate_options.targets = options.targets;
        translate_options.dedupe_identical_blobs = options.dedupe_identical_blobs;

        refii::shader::ShaderSbkTranslateSummary summary;
        std::string error;
        if (!refii::shader::TranslateExtractedShaderSbk(translate_options, &summary, &error)) {
            std::cerr << error << "\n";
            return 1;
        }

        std::cout << "Manifest: " << summary.extract_manifest_path << "\n";
        std::cout << "Compile:  " << summary.translate_out_dir << "\n";
        std::cout << "Report:   " << summary.report_path << "\n";
        std::cout << "Targets:  " << summary.resolved_targets << "\n";
        std::cout << "Blobs:    " << summary.blob_count << "\n";
        std::cout << "Attempted:" << summary.attempted << "\n";
        std::cout << "Success:  " << summary.succeeded << "\n";
        std::cout << "Failed:   " << summary.failed << "\n";
        std::cout << "Timeout:  " << summary.timed_out << "\n";
        std::cout << "Missing:  " << summary.backend_missing << "\n";
        std::cout << "ExecJobs: " << summary.dedupe_executed << "\n";
        std::cout << "AliasRows:" << summary.dedupe_aliased << "\n";
        return FinishTranslateCommand(summary, options.verify_coverage);
    }

    if (options.command == "probe-xenos") {
        refii::shader::ShaderSbkXenosProbeOptions probe_options;
        probe_options.extract_manifest_path = options.extract_manifest;
        probe_options.report_path =
            options.xenos_report.empty() ? options.report : options.xenos_report;

        refii::shader::ShaderSbkXenosProbeSummary summary;
        std::string error;
        if (!refii::shader::ProbeExtractedShaderSbkForXenos(
                probe_options, &summary, &error)) {
            std::cerr << error << "\n";
            return 1;
        }

        std::cout << "Manifest: " << summary.extract_manifest_path << "\n";
        std::cout << "Report:   " << summary.report_path << "\n";
        std::cout << "Blobs:    " << summary.blob_count << "\n";
        std::cout << "WithHits: " << summary.blobs_with_candidates << "\n";
        std::cout << "Candidates:" << summary.total_candidates << "\n";
        return 0;
    }

    refii::shader::ShaderSbkBuildOptions build_options;
    build_options.extract.title = options.title;
    build_options.translate.title = options.title;
    build_options.extract.input_dir = options.input_dir;
    build_options.extract.extract_out_dir = options.extract_out;
    build_options.extract.manifest_path = options.manifest;
    build_options.translate.extract_manifest_path = options.extract_manifest;
    build_options.translate.translate_out_dir = options.translate_out;
    build_options.translate.report_path = options.report;
    build_options.translate.translator_path = options.translator;
    build_options.translate.translator_timeout_ms = options.translator_timeout_ms;
    build_options.translate.targets = options.targets;
    build_options.translate.dedupe_identical_blobs = options.dedupe_identical_blobs;

    refii::shader::ShaderSbkBuildSummary summary;
    std::string error;
    if (!refii::shader::BuildShaderSbkWorkspace(build_options, &summary, &error)) {
        std::cerr << error << "\n";
        return 1;
    }

    std::cout << "Input:    " << summary.extract.input_dir << "\n";
    std::cout << "Extract:  " << summary.extract.extract_out_dir << "\n";
    std::cout << "Manifest: " << summary.extract.manifest_path << "\n";
    std::cout << "Blobs:    " << summary.extract.blob_count << "\n";
    std::cout << "Compile:  " << summary.translate.translate_out_dir << "\n";
    std::cout << "Report:   " << summary.translate.report_path << "\n";
    std::cout << "Targets:  " << summary.translate.resolved_targets << "\n";
    std::cout << "Attempted:" << summary.translate.attempted << "\n";
    std::cout << "Success:  " << summary.translate.succeeded << "\n";
    std::cout << "Failed:   " << summary.translate.failed << "\n";
    std::cout << "Timeout:  " << summary.translate.timed_out << "\n";
    std::cout << "Missing:  " << summary.translate.backend_missing << "\n";
    std::cout << "ExecJobs: " << summary.translate.dedupe_executed << "\n";
    std::cout << "AliasRows:" << summary.translate.dedupe_aliased << "\n";
    return FinishTranslateCommand(summary.translate, options.verify_coverage);
}
