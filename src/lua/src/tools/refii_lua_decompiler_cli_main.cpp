// refii-lua-decompiler: command-line front end of the Lua workspace.
//
//   refii-lua-decompiler build <game-root> [--workers <n>]
//   refii-lua-decompiler <workspace> [--out <dir>] [--workers <n>]
//   refii-lua-decompiler verify <workspace>
//   refii-lua-decompiler reset-all <workspace>
//   refii-lua-decompiler reset-file <workspace> <relative-path>
//   refii-lua-decompiler single <chunk> [-o <out.lua>] [--dis]
#include "tools/native/native_lua_decompiler.h"

#include "luadec.h"
#include "luadec_chunk.h"
#include "luadec_oracle.h"

#include <cstddef>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <iterator>
#include <mutex>
#include <string>
#include <vector>

namespace {

struct Options {
    std::string command = "decompile";
    std::filesystem::path workspace;
    std::filesystem::path output;
    std::filesystem::path relative_path;
    std::filesystem::path chunk;
    bool disassemble = false;
    std::size_t worker_count = 0;
};

int Usage() {
    std::cerr << "usage: refii-lua-decompiler build <game-root> [--workers <n>]\n"
              << "       refii-lua-decompiler <workspace> [--out <dir>] [--workers <n>]\n"
              << "       refii-lua-decompiler verify <workspace>\n"
              << "       refii-lua-decompiler reset-all <workspace>\n"
              << "       refii-lua-decompiler reset-file <workspace> <relative-path>\n"
              << "       refii-lua-decompiler single <chunk> [-o <out.lua>] [--dis]\n"
              << "       (--workers omitted or 0 keeps some cores free)\n";
    return 1;
}

bool ParseArguments(int argc, char** argv, Options& options) {
    std::vector<std::string> args(argv + 1, argv + argc);
    if (args.empty()) return false;
    std::size_t i = 0;
    if (args[0] == "build" || args[0] == "verify" || args[0] == "reset-all" || args[0] == "reset-file" ||
        args[0] == "single") {
        options.command = args[0];
        ++i;
    }
    std::vector<std::string> positional;
    for (; i < args.size(); ++i) {
        const std::string& argument = args[i];
        if (argument == "--out" || argument == "-o") {
            if (i + 1 >= args.size()) return false;
            options.output = args[++i];
        } else if (argument == "--workers") {
            if (i + 1 >= args.size()) return false;
            options.worker_count = static_cast<std::size_t>(std::strtoull(args[++i].c_str(), nullptr, 10));
        } else if (argument == "--dis") {
            options.disassemble = true;
        } else if (!argument.empty() && argument[0] == '-') {
            return false;
        } else {
            positional.push_back(argument);
        }
    }
    if (options.command == "single") {
        if (positional.size() != 1) return false;
        options.chunk = positional[0];
        return true;
    }
    if (options.command == "reset-file") {
        if (positional.size() != 2) return false;
        options.workspace = positional[0];
        options.relative_path = positional[1];
        return true;
    }
    if (positional.size() != 1) return false;
    options.workspace = positional[0];
    return true;
}

std::function<void(const refii::lua::LuaWorkspaceDecompileProgress&)> MakeProgressCallback() {
    return [](const refii::lua::LuaWorkspaceDecompileProgress& progress) {
        static std::mutex progress_mutex;
        std::lock_guard<std::mutex> lock(progress_mutex);
        std::cerr << "[" << progress.completed_files << "/" << progress.total_files << "] " << progress.phase;
        if (progress.worker_count != 0) {
            std::cerr << " worker=" << (progress.worker_slot + 1) << "/" << progress.worker_count;
        }
        if (!progress.current_input.empty()) {
            std::cerr << " " << progress.current_input.generic_string();
        }
        std::cerr << "\n";
    };
}

void PrintDecompileSummary(const refii::lua::LuaWorkspaceDecompileSummary& summary) {
    std::cout << "Files:      " << summary.total_files << "\n";
    std::cout << "Exact:      " << summary.decompiled << "\n";
    std::cout << "Aliased:    " << summary.aliased << "\n";
    std::cout << "Nonexact:   " << summary.nonexact << "\n";
    std::cout << "AliasNonx:  " << summary.aliased_nonexact << "\n";
    std::cout << "Mismatched: " << summary.mismatched << "\n";
    std::cout << "Disasm:     " << summary.disassembled << "\n";
    std::cout << "Invalid:    " << summary.invalid << "\n";
    std::cout << "Copied:     " << summary.copied << "\n";
    std::cout << "Skipped:    " << summary.skipped << "\n";
    std::cout << "Manifest:   " << summary.manifest_path.generic_string() << "\n";
}

int RunSingle(const Options& options) {
    std::ifstream stream(options.chunk, std::ios::binary);
    if (!stream) {
        std::cerr << "cannot read " << options.chunk << "\n";
        return 1;
    }
    const std::string bytes((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
    refii::luadec::Chunk chunk;
    std::string error;
    if (!refii::luadec::LoadChunk(bytes, &chunk, &error)) {
        std::cerr << "not a Lua chunk: " << error << "\n";
        return 1;
    }
    std::string text;
    if (options.disassemble) {
        refii::luadec::Disassemble(chunk, &text);
    } else if (!refii::luadec::Decompile(chunk, refii::luadec::DecompileOptions{}, &text, &error)) {
        std::cerr << "decompile failed: " << error << "\n";
        return 1;
    }
    if (!options.disassemble) {
        const std::vector<unsigned char> original(bytes.begin(), bytes.end());
        const refii::luadec::OracleResult verify = refii::luadec::VerifySourceAgainstChunk(original, text);
        std::cerr << refii::luadec::DescribeOracleStatus(verify.status);
        if (!verify.detail.empty()) std::cerr << " " << verify.detail;
        std::cerr << "\n";
    }
    if (options.output.empty()) {
        std::cout << text;
        return 0;
    }
    std::ofstream out(options.output, std::ios::binary | std::ios::trunc);
    out << text;
    if (!out) {
        std::cerr << "cannot write " << options.output << "\n";
        return 1;
    }
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    Options options;
    if (!ParseArguments(argc, argv, options)) return Usage();

    if (options.command == "single") return RunSingle(options);

    if (options.command == "build") {
        refii::lua::LuaWorkspaceBuildOptions build_options;
        build_options.game_install_root = options.workspace;
        build_options.worker_count = options.worker_count;
        build_options.progress_callback = MakeProgressCallback();
        refii::lua::LuaWorkspaceBuildSummary summary;
        std::string error;
        if (!refii::lua::BuildLuaWorkspace(build_options, &summary, &error)) {
            std::cerr << error << "\n";
            return 1;
        }
        std::cout << "Workspace:  " << summary.workspace_root.generic_string() << "\n";
        std::cout << "Source:     " << summary.source_root.generic_string() << "\n";
        std::cout << "Output:     " << summary.output_root.generic_string() << "\n";
        std::cout << "Baseline:   " << summary.baseline_manifest_path.generic_string() << "\n";
        std::cout << "Banks:      " << summary.extracted_banks << "\n";
        std::cout << "Extracted:  " << summary.extracted_files << "\n";
        std::cout << "Loose:      " << summary.copied_loose_files << "\n";
        std::cout << "Tracked:    " << summary.baseline_files << "\n";
        PrintDecompileSummary(summary.decompile_summary);
        return 0;
    }

    if (options.command == "verify") {
        refii::lua::LuaWorkspaceVerifyOptions verify_options;
        verify_options.workspace_root = options.workspace;
        verify_options.include_ok_entries = true;
        verify_options.progress_callback = MakeProgressCallback();
        refii::lua::LuaWorkspaceVerifySummary summary;
        std::string error;
        if (!refii::lua::VerifyLuaWorkspace(verify_options, &summary, &error)) {
            std::cerr << error << "\n";
            return 1;
        }
        std::cout << "Workspace:  " << summary.workspace_root.generic_string() << "\n";
        std::cout << "Baseline:   " << summary.baseline_manifest_path.generic_string() << "\n";
        std::cout << "Tracked:    " << summary.tracked_files << "\n";
        std::cout << "Ok:         " << summary.ok << "\n";
        std::cout << "Modified:   " << summary.modified << "\n";
        std::cout << "Missing:    " << summary.missing << "\n";
        std::cout << "Extra:      " << summary.extra << "\n";
        std::cout << "BaseMiss:   " << summary.baseline_missing << "\n";
        std::cout << "BaseBad:    " << summary.baseline_corrupt << "\n";
        for (const refii::lua::LuaWorkspaceVerifyEntry& entry : summary.entries) {
            if (entry.relative_path.empty()) continue;
            std::cout << refii::lua::DescribeLuaWorkspaceVerifyStatus(entry.status) << '\t'
                      << entry.relative_path.generic_string() << "\n";
        }
        return 0;
    }

    if (options.command == "reset-all" || options.command == "reset-file") {
        refii::lua::LuaWorkspaceResetOptions reset_options;
        reset_options.workspace_root = options.workspace;
        reset_options.reset_all = options.command == "reset-all";
        reset_options.relative_path = options.relative_path;
        reset_options.progress_callback = MakeProgressCallback();
        refii::lua::LuaWorkspaceResetSummary summary;
        std::string error;
        if (!refii::lua::ResetLuaWorkspace(reset_options, &summary, &error)) {
            std::cerr << error << "\n";
            return 1;
        }
        std::cout << "Workspace:  " << summary.workspace_root.generic_string() << "\n";
        std::cout << "Baseline:   " << summary.baseline_root.generic_string() << "\n";
        std::cout << "Restored:   " << summary.restored << "\n";
        std::cout << "Removed:    " << summary.removed << "\n";
        std::cout << "Failed:     " << summary.failed << "\n";
        return 0;
    }

    refii::lua::LuaWorkspaceDecompileOptions decompile_options;
    decompile_options.workspace_root = options.workspace;
    decompile_options.output_root = options.output;
    decompile_options.worker_count = options.worker_count;
    decompile_options.progress_callback = MakeProgressCallback();
    refii::lua::LuaWorkspaceDecompileSummary summary;
    std::string error;
    if (!refii::lua::DecompileLuaWorkspace(decompile_options, &summary, &error)) {
        std::cerr << error << "\n";
        return 1;
    }
    std::cout << "Workspace:  " << summary.workspace_root.generic_string() << "\n";
    std::cout << "Output:     " << summary.output_root.generic_string() << "\n";
    PrintDecompileSummary(summary);
    return summary.mismatched == 0 && summary.disassembled == 0 && summary.invalid == 0 ? 0 : 2;
}
