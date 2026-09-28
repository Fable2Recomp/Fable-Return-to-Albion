// The decompile step of the Lua workspace.
//
// Every script in the luasource/ cache (the three script banks and the loose
// script folders) is decompiled with luadec and the text is verified with the
// in-process oracle against the chunk it came from. Retail paths canonicalise
// onto their debug counterparts (gamescripts_r/scripts/x -> gamescripts/
// scripts/x), so one readable file serves both banks: it is written from the
// debug chunk, whose names survive, and verified against every chunk that maps
// to it. A chunk that cannot be decompiled gets a placeholder at its public
// path, which the runtime and the baseline skip, and its listing under _disasm/.
#include "tools/workspace/lua_workspace_decompile_pipeline.h"

#include "lua_script_paths.h"
#include "luadec.h"
#include "luadec_chunk.h"
#include "luadec_oracle.h"

#include <algorithm>
#include <atomic>
#include <fstream>
#include <iterator>
#include <map>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <system_error>
#include <thread>
#include <vector>

namespace refii::lua {

namespace {

constexpr std::string_view kManifestHeader = "status\tinput\toutput\tsource_name\tnote\n";
constexpr std::string_view kDisassemblyPlaceholderMarker = "-- refii-disassembly-placeholder";
constexpr std::string_view kLuaSignature = "\x1bLua";

std::string NormalizePathString(std::string value) {
    std::replace(value.begin(), value.end(), '\\', '/');
    return value;
}

bool IsSourceWorkspaceRoot(const std::filesystem::path& path) {
    return NormalizePathString(path.filename().generic_string()) == "luasource";
}

bool ReadFile(const std::filesystem::path& path, std::string* out) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) return false;
    out->assign(std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>());
    return true;
}

bool WriteFile(const std::filesystem::path& path, std::string_view text, std::string* error) {
    std::error_code ec;
    std::filesystem::create_directories(path.parent_path(), ec);
    if (ec) {
        if (error != nullptr) *error = path.parent_path().string() + ": " + ec.message();
        return false;
    }
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream) {
        if (error != nullptr) *error = "cannot write " + path.string();
        return false;
    }
    stream.write(text.data(), static_cast<std::streamsize>(text.size()));
    if (!stream) {
        if (error != nullptr) *error = "cannot write " + path.string();
        return false;
    }
    return true;
}

bool IsLuaChunk(std::string_view bytes) {
    return bytes.size() >= kLuaSignature.size() && bytes.substr(0, kLuaSignature.size()) == kLuaSignature;
}

// Inputs that canonicalise onto one output are ranked: the debug bank's chunk
// carries local names and line info, the loose scripts folder is the modder's
// own copy and comes first.
int InputPriority(const std::string& canonical) {
    if (canonical.rfind("scripts/", 0) == 0) return 0;
    if (canonical.rfind("gamescripts/scripts/", 0) == 0) return 1;
    if (canonical.rfind("guiscripts/", 0) == 0) return 2;
    return 3;
}

struct Input {
    std::filesystem::path path;      // absolute
    std::string relative;            // from the input root, generic
    bool canonical = false;          // its relative path is the output path (not a retail alias)
};

struct Job {
    std::string output_relative;
    std::vector<Input> inputs;       // canonical first
};

struct RowResult {
    std::string status;
    std::string input;
    std::string note;
};

struct JobResult {
    std::string output_relative;
    std::string source_name;
    std::vector<RowResult> rows;
    bool exact = false;
    bool nonexact = false;
    bool mismatched = false;
    bool disassembled = false;
    bool invalid = false;
    bool copied = false;
    std::size_t aliased = 0;
    std::size_t aliased_nonexact = 0;
    std::string error;               // fatal IO problem
};

std::vector<Job> CollectJobs(const std::filesystem::path& input_root, std::string* error) {
    std::map<std::string, Job> by_output;
    std::error_code ec;
    for (std::filesystem::recursive_directory_iterator it(input_root, ec), end; it != end; it.increment(ec)) {
        if (ec) {
            if (error != nullptr) *error = ec.message();
            return {};
        }
        if (it->is_directory()) {
            const std::string name = it->path().filename().generic_string();
            if (name == "refii_vfs" || (!name.empty() && name[0] == '.')) it.disable_recursion_pending();
            continue;
        }
        if (!it->is_regular_file() || it->path().extension() != ".lua") continue;
        const std::string relative =
            NormalizePathString(std::filesystem::relative(it->path(), input_root, ec).generic_string());
        if (ec || relative.empty()) continue;
        const std::string canonical = CanonicalizeRetailLuaRelativeString(relative);
        Input input;
        input.path = it->path();
        input.relative = relative;
        input.canonical = canonical == relative;
        Job& job = by_output[canonical];
        job.output_relative = canonical;
        job.inputs.push_back(std::move(input));
    }
    std::vector<Job> jobs;
    jobs.reserve(by_output.size());
    for (auto& [output, job] : by_output) {
        std::stable_sort(job.inputs.begin(), job.inputs.end(), [](const Input& a, const Input& b) {
            if (a.canonical != b.canonical) return a.canonical;
            return InputPriority(a.relative) < InputPriority(b.relative);
        });
        jobs.push_back(std::move(job));
    }
    return jobs;
}

std::string SourceNameOf(const luadec::Chunk& chunk) {
    return chunk.main.source;
}

std::string Placeholder(const Input& input, const std::string& reason, const std::string& listing_relative) {
    std::ostringstream text;
    text << kDisassemblyPlaceholderMarker << "\n"
         << "-- refii could not decompile " << input.relative << ": " << reason << "\n"
         << "-- The listing is under _disasm/" << listing_relative << ". The game keeps loading the original chunk.\n";
    return text.str();
}

JobResult RunJob(const Job& job,
                 const std::filesystem::path& output_root,
                 bool write_disassembly_fallback) {
    JobResult result;
    result.output_relative = job.output_relative;
    const std::filesystem::path output_path = output_root / job.output_relative;
    const Input& primary = job.inputs.front();

    std::string primary_bytes;
    if (!ReadFile(primary.path, &primary_bytes)) {
        result.error = "cannot read " + primary.path.string();
        return result;
    }

    std::string text;
    std::string status;
    std::string note;
    if (!IsLuaChunk(primary_bytes)) {
        // Plain text ships as it is.
        text = primary_bytes;
        status = "copied";
        result.copied = true;
    } else {
        luadec::Chunk chunk;
        std::string load_error;
        if (!luadec::LoadChunk(primary_bytes, &chunk, &load_error)) {
            result.invalid = true;
            result.rows.push_back({"invalid", primary.relative, load_error});
            return result;
        }
        result.source_name = SourceNameOf(chunk);
        luadec::DecompileOptions options;
        std::string decompile_error;
        if (!luadec::Decompile(chunk, options, &text, &decompile_error)) {
            result.disassembled = true;
            std::string listing;
            luadec::Disassemble(chunk, &listing);
            std::string io_error;
            if (!WriteFile(output_root / "_disasm" / job.output_relative, listing, &io_error)) {
                result.error = io_error;
                return result;
            }
            if (write_disassembly_fallback &&
                !WriteFile(output_path, Placeholder(primary, decompile_error, job.output_relative), &io_error)) {
                result.error = io_error;
                return result;
            }
            result.rows.push_back({"disassembled", primary.relative, decompile_error});
            for (std::size_t i = 1; i < job.inputs.size(); ++i) {
                result.rows.push_back({"skipped", job.inputs[i].relative, "primary input could not be decompiled"});
            }
            return result;
        }
        const std::vector<unsigned char> original(primary_bytes.begin(), primary_bytes.end());
        const luadec::OracleResult verify = luadec::VerifySourceAgainstChunk(original, text);
        switch (verify.status) {
            case luadec::OracleStatus::kExact:
                status = "exact";
                result.exact = true;
                break;
            case luadec::OracleStatus::kCodeExact:
                status = "nonexact";
                note = verify.detail;
                result.nonexact = true;
                break;
            case luadec::OracleStatus::kMismatch:
            case luadec::OracleStatus::kCompileError:
            case luadec::OracleStatus::kInvalidOriginal:
                status = "mismatch";
                note = std::string(luadec::DescribeOracleStatus(verify.status)) + ": " + verify.detail;
                result.mismatched = true;
                break;
        }
    }

    std::string io_error;
    if (!WriteFile(output_path, text, &io_error)) {
        result.error = io_error;
        return result;
    }
    result.rows.push_back({status, primary.relative, note});

    // Every other chunk that maps to this output must be reproduced by the
    // same text under its own conventions (a retail chunk compiles stripped).
    for (std::size_t i = 1; i < job.inputs.size(); ++i) {
        const Input& alias = job.inputs[i];
        std::string alias_bytes;
        if (!ReadFile(alias.path, &alias_bytes)) {
            result.rows.push_back({"invalid", alias.relative, "cannot read"});
            result.invalid = true;
            continue;
        }
        if (!IsLuaChunk(alias_bytes)) {
            const bool same = alias_bytes == text;
            result.rows.push_back({same ? "aliased" : "aliased_nonexact", alias.relative,
                                   same ? "" : "plain text differs from the primary input"});
            ++result.aliased;
            if (!same) ++result.aliased_nonexact;
            continue;
        }
        const std::vector<unsigned char> original(alias_bytes.begin(), alias_bytes.end());
        const luadec::OracleResult verify = luadec::VerifySourceAgainstChunk(original, text);
        ++result.aliased;
        if (verify.status == luadec::OracleStatus::kExact) {
            result.rows.push_back({"aliased", alias.relative, ""});
        } else {
            ++result.aliased_nonexact;
            result.rows.push_back({"aliased_nonexact", alias.relative,
                                   std::string(luadec::DescribeOracleStatus(verify.status)) + ": " + verify.detail});
        }
    }
    return result;
}

std::size_t ResolveWorkerCount(std::size_t requested, std::size_t jobs) {
    std::size_t workers = requested;
    if (workers == 0) {
        const unsigned int hardware = std::thread::hardware_concurrency();
        workers = hardware > 2 ? hardware - 2 : 1;
    }
    if (jobs != 0) workers = std::min(workers, jobs);
    return std::max<std::size_t>(workers, 1);
}

}  // namespace

std::filesystem::path GetDefaultLuaDecompiledOutputRoot(const std::filesystem::path& workspace_root) {
    return IsSourceWorkspaceRoot(workspace_root) ? workspace_root.parent_path() : workspace_root;
}

std::filesystem::path GetDefaultLuaDecompileInputRoot(const std::filesystem::path& workspace_root) {
    if (IsSourceWorkspaceRoot(workspace_root)) return workspace_root;
    const std::filesystem::path source_root = workspace_root / "luasource";
    std::error_code ec;
    if (std::filesystem::is_directory(source_root, ec)) return source_root;
    return workspace_root;
}

namespace workspace {

bool RunLuaWorkspaceDecompilePipeline(const LuaWorkspaceDecompileOptions& options,
                                      LuaWorkspaceDecompileSummary* summary,
                                      std::string* error) {
    LuaWorkspaceDecompileSummary local_summary;
    LuaWorkspaceDecompileSummary* const out = summary != nullptr ? summary : &local_summary;
    *out = {};

    const std::filesystem::path workspace_root = std::filesystem::absolute(options.workspace_root);
    const std::filesystem::path input_root = GetDefaultLuaDecompileInputRoot(workspace_root);
    const std::filesystem::path output_root = options.output_root.empty()
                                                  ? GetDefaultLuaDecompiledOutputRoot(workspace_root)
                                                  : std::filesystem::absolute(options.output_root);
    out->workspace_root = workspace_root;
    out->output_root = output_root;
    out->manifest_path = output_root / "_manifest.tsv";

    std::error_code ec;
    if (!std::filesystem::is_directory(input_root, ec)) {
        if (error != nullptr) *error = "no Lua sources under " + input_root.string();
        return false;
    }
    std::filesystem::create_directories(output_root, ec);
    if (ec) {
        if (error != nullptr) *error = output_root.string() + ": " + ec.message();
        return false;
    }

    std::string collect_error;
    std::vector<Job> jobs = CollectJobs(input_root, &collect_error);
    if (!collect_error.empty()) {
        if (error != nullptr) *error = collect_error;
        return false;
    }
    std::size_t total_inputs = 0;
    for (const Job& job : jobs) total_inputs += job.inputs.size();
    out->total_files = total_inputs;

    const auto emit = [&](const std::string& phase, std::size_t completed, const std::filesystem::path& current,
                          std::size_t slot, std::size_t workers, std::size_t active) {
        if (!options.progress_callback) return;
        LuaWorkspaceDecompileProgress progress;
        progress.phase = phase;
        progress.completed_files = completed;
        progress.total_files = jobs.size();
        progress.current_input = current;
        progress.worker_slot = slot;
        progress.worker_count = workers;
        progress.active_workers = active;
        options.progress_callback(progress);
    };
    emit("starting", 0, {}, 0, 0, 0);

    const std::size_t workers = ResolveWorkerCount(options.worker_count, jobs.size());
    std::vector<JobResult> results(jobs.size());
    std::atomic<std::size_t> next{0};
    std::atomic<std::size_t> completed{0};
    std::atomic<std::size_t> active{0};
    std::mutex progress_mutex;
    const std::size_t stride = std::max<std::size_t>(options.progress_update_stride, 1);

    const auto worker = [&](std::size_t slot) {
        for (;;) {
            const std::size_t index = next.fetch_add(1);
            if (index >= jobs.size()) return;
            ++active;
            results[index] = RunJob(jobs[index], output_root, options.write_disassembly_fallback);
            --active;
            const std::size_t done = ++completed;
            if (done % stride == 0 || done == jobs.size()) {
                std::lock_guard<std::mutex> lock(progress_mutex);
                emit("decompiling", done, jobs[index].inputs.front().relative, slot, workers, active.load());
            }
        }
    };
    std::vector<std::thread> threads;
    for (std::size_t slot = 0; slot < workers; ++slot) threads.emplace_back(worker, slot);
    for (std::thread& thread : threads) thread.join();

    std::ostringstream manifest;
    manifest << kManifestHeader;
    for (const JobResult& result : results) {
        if (!result.error.empty()) {
            if (error != nullptr) *error = result.error;
            return false;
        }
        out->decompiled += result.exact ? 1 : 0;
        out->nonexact += result.nonexact ? 1 : 0;
        out->mismatched += result.mismatched ? 1 : 0;
        out->disassembled += result.disassembled ? 1 : 0;
        out->invalid += result.invalid ? 1 : 0;
        out->copied += result.copied ? 1 : 0;
        out->aliased += result.aliased;
        out->aliased_nonexact += result.aliased_nonexact;
        for (const RowResult& row : result.rows) {
            if (row.status == "skipped") ++out->skipped;
            manifest << row.status << '\t' << row.input << '\t' << result.output_relative << '\t'
                     << result.source_name << '\t' << row.note << '\n';
        }
    }
    std::string io_error;
    if (!WriteFile(out->manifest_path, manifest.str(), &io_error)) {
        if (error != nullptr) *error = io_error;
        return false;
    }
    emit("done", jobs.size(), {}, 0, workers, 0);
    return true;
}

}  // namespace workspace

bool DecompileLuaWorkspace(const LuaWorkspaceDecompileOptions& options,
                          LuaWorkspaceDecompileSummary* summary,
                          std::string* error) {
    return workspace::RunLuaWorkspaceDecompilePipeline(options, summary, error);
}

}  // namespace refii::lua
