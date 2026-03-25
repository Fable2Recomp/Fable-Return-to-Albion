#include "tools/native_lua_decompiler.h"
#include "tools/native_lua_chunk.h"
#include "tools/native_lua_unluac.h"
#include "tools/decompiler/native_first.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <limits>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#ifdef __cplusplus
extern "C" {
#endif
#include "lauxlib.h"
#include "lua.h"
#ifdef __cplusplus
}
#endif

#if defined(_WIN32)
#include <process.h>
#else
#include <fcntl.h>
#include <signal.h>
#include <sys/wait.h>
#include <unistd.h>
#endif

extern "C" int luadec_main(int argc, char* argv[]);

namespace {

constexpr std::string_view kLuaSignature = "\x1bLua";
constexpr int kRunTimedOut = -2;
constexpr int kRunSpawnFailed = -3;
constexpr int kRunSignaledBase = -1000;
constexpr std::size_t kManifestFlushStride = 32;
constexpr std::uintmax_t kLargeNativeMainChunkSubprocessThresholdBytes = 128 * 1024;

std::string NormalizePathString(std::string value) {
    for (char& ch : value) {
        if (ch == '\\') {
            ch = '/';
        } else {
            ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
        }
    }
    return value;
}

bool StartsWith(std::string_view value, std::string_view prefix) {
    return value.size() >= prefix.size() && value.substr(0, prefix.size()) == prefix;
}

bool EndsWith(std::string_view value, std::string_view suffix) {
    return value.size() >= suffix.size() &&
           value.substr(value.size() - suffix.size(), suffix.size()) == suffix;
}

std::string EnsureTrailingSlash(std::string value) {
    if (!value.empty() && value.back() != '/') {
        value.push_back('/');
    }
    return value;
}

bool IsSourceWorkspaceRoot(const std::filesystem::path& path) {
    return NormalizePathString(path.filename().generic_string()) == ".source";
}

int GetInputPriority(const std::filesystem::path& path) {
    const std::string normalized = NormalizePathString(path.generic_string());
    if (StartsWith(normalized, "gamescripts/scripts/")) {
        return 0;
    }
    if (StartsWith(normalized, "scripts/")) {
        return 1;
    }
    if (StartsWith(normalized, "gamescripts_r/scripts/")) {
        return 2;
    }
    if (StartsWith(normalized, "scripts_r/")) {
        return 3;
    }
    if (StartsWith(normalized, "guiscripts/")) {
        return 4;
    }
    return 5;
}

bool IsLuaBytecode(const std::vector<char>& bytes) {
    return bytes.size() >= kLuaSignature.size() &&
           std::string_view(bytes.data(), kLuaSignature.size()) == kLuaSignature;
}

uint32_t ReadLE32(const std::vector<char>& bytes, std::size_t offset) {
    return static_cast<uint32_t>(static_cast<unsigned char>(bytes[offset])) |
           (static_cast<uint32_t>(static_cast<unsigned char>(bytes[offset + 1])) << 8) |
           (static_cast<uint32_t>(static_cast<unsigned char>(bytes[offset + 2])) << 16) |
           (static_cast<uint32_t>(static_cast<unsigned char>(bytes[offset + 3])) << 24);
}

std::string DecodeSourceName(const std::vector<char>& bytes) {
    if (!IsLuaBytecode(bytes)) {
        return {};
    }

    return refii::lua::GetLua51ChunkSourceName(bytes);
}

std::string DescribeBytecodeChunk(const std::vector<char>& bytes) {
    if (!IsLuaBytecode(bytes)) {
        return {};
    }

    refii::lua::Lua51Chunk chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(bytes, &chunk, &parse_error)) {
        return parse_error.empty() ? "chunk_parse_failed" : "chunk_parse_failed=" + parse_error;
    }

    std::string description =
        refii::lua::DescribeLua51ChunkStats(refii::lua::SummarizeLua51Chunk(chunk));
    const std::string declaration_description =
        refii::lua::DescribeLua51DeclarationSummary(
            refii::lua::SummarizeLua51Declarations(chunk.main));
    if (!declaration_description.empty()) {
        if (!description.empty()) {
            description.append(";");
        }
        description.append(declaration_description);
    }
    const std::string control_flow_description =
        refii::lua::DescribeLua51ControlFlowSummary(
            refii::lua::SummarizeLua51ControlFlow(chunk.main));
    if (!control_flow_description.empty()) {
        if (!description.empty()) {
            description.append(";");
        }
        description.append(control_flow_description);
    }
    const std::string operation_description =
        refii::lua::DescribeLua51OperationSummary(
            refii::lua::SummarizeLua51Operations(chunk.main));
    if (!operation_description.empty()) {
        if (!description.empty()) {
            description.append(";");
        }
        description.append(operation_description);
    }
    const std::string register_description =
        refii::lua::DescribeLua51RegisterSummary(
            refii::lua::SummarizeLua51Registers(chunk.main));
    if (!register_description.empty()) {
        if (!description.empty()) {
            description.append(";");
        }
        description.append(register_description);
    }
    return description;
}

std::string MakeCacheKey(const std::filesystem::path& path) {
    return NormalizePathString(std::filesystem::absolute(path).lexically_normal().generic_string());
}

struct ResolvedInput {
    std::filesystem::path path;
    std::string note;
};

struct CacheEntry {
    bool attempted = false;
    enum class Result {
        kFailed,
        kDecompiled,
        kDisassembled,
    } result = Result::kFailed;
    std::filesystem::path output_path;
    std::string note;
};

ResolvedInput ResolveBestInputPath(const std::filesystem::path& workspace,
                                   const std::filesystem::path& relative_path) {
    ResolvedInput resolved;
    resolved.path = workspace / relative_path;

    const std::string normalized = NormalizePathString(relative_path.generic_string());
    if (StartsWith(normalized, "gamescripts_r/scripts/")) {
        const std::filesystem::path candidate =
            workspace / "gamescripts" / "scripts" /
            relative_path.lexically_relative("gamescripts_r/scripts");
        if (std::filesystem::exists(candidate)) {
            resolved.note = "decompiled_from_gamescripts";
            resolved.path = candidate;
            return resolved;
        }
    }

    if (StartsWith(normalized, "scripts_r/")) {
        const std::filesystem::path candidate =
            workspace / "scripts" / relative_path.lexically_relative("scripts_r");
        if (std::filesystem::exists(candidate)) {
            resolved.note = "decompiled_from_scripts";
            resolved.path = candidate;
            return resolved;
        }
    }

    return resolved;
}

std::filesystem::path MapOutputRelativePath(const std::filesystem::path& relative_path) {
    return relative_path;
}

bool ReadFile(const std::filesystem::path& path, std::vector<char>& bytes) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        return false;
    }

    stream.seekg(0, std::ios::end);
    const std::streampos size = stream.tellg();
    if (size < 0) {
        return false;
    }

    stream.seekg(0, std::ios::beg);
    bytes.resize(static_cast<std::size_t>(size));
    if (!bytes.empty()) {
        stream.read(bytes.data(), static_cast<std::streamsize>(bytes.size()));
    }
    return stream.good() || stream.eof();
}

int RunDecompiler(const refii::lua::LuaWorkspaceDecompileOptions& options,
                  const std::filesystem::path& input_path,
                  const std::filesystem::path& output_path,
                  const std::vector<std::string>& extra_args,
                  bool force_external_tool = false,
                  unsigned int timeout_seconds = std::numeric_limits<unsigned int>::max()) {
    const unsigned int effective_timeout_seconds =
        timeout_seconds == std::numeric_limits<unsigned int>::max() ? options.child_timeout_seconds
                                                                    : timeout_seconds;
#if defined(_WIN32)
    const std::filesystem::path tool_path = options.single_tool_path;
    if (tool_path.empty() || !std::filesystem::exists(tool_path)) {
        return kRunSpawnFailed;
    }

    const std::string tool = tool_path.string();
    const std::string output = output_path.string();
    const std::string input = input_path.string();
    std::vector<std::string> arguments;
    arguments.reserve(extra_args.size() + 4);
    arguments.push_back(tool);
    for (const std::string& extra_arg : extra_args) {
        arguments.push_back(extra_arg);
    }
    arguments.push_back("-o");
    arguments.push_back(output);
    arguments.push_back(input);

    std::vector<const char*> argv;
    argv.reserve(arguments.size() + 1);
    for (const std::string& argument : arguments) {
        argv.push_back(argument.c_str());
    }
    argv.push_back(nullptr);

    const intptr_t result = _spawnv(_P_WAIT, tool.c_str(), argv.data());
    return result == -1 ? kRunSpawnFailed : static_cast<int>(result);
#else
    std::fflush(nullptr);

    const std::filesystem::path tool_path = options.single_tool_path;
    if (force_external_tool && (tool_path.empty() || !std::filesystem::exists(tool_path))) {
        return kRunSpawnFailed;
    }

    const int devnull = ::open("/dev/null", O_WRONLY);
    if (devnull < 0) {
        return kRunSpawnFailed;
    }

    const pid_t pid = ::fork();
    if (pid < 0) {
        ::close(devnull);
        return kRunSpawnFailed;
    }

    if (pid == 0) {
        ::dup2(devnull, STDOUT_FILENO);
        ::dup2(devnull, STDERR_FILENO);
        ::close(devnull);

        std::vector<std::string> arguments;
        arguments.reserve(extra_args.size() + 4);
        if (force_external_tool) {
            arguments.emplace_back(tool_path.string());
        } else {
            arguments.emplace_back("refii-lua-decompiler-single");
        }
        for (const std::string& extra_arg : extra_args) {
            arguments.push_back(extra_arg);
        }
        arguments.emplace_back("-o");
        std::string output = output_path.string();
        std::string input = input_path.string();
        arguments.push_back(output);
        arguments.push_back(input);

        std::vector<char*> argv;
        argv.reserve(arguments.size() + 1);
        for (std::string& argument : arguments) {
            argv.push_back(argument.data());
        }
        argv.push_back(nullptr);

        if (force_external_tool) {
            ::execv(tool_path.c_str(), argv.data());
            _exit(127);
        }

        const int result = luadec_main(static_cast<int>(arguments.size()), argv.data());
        _exit(result);
    }

    ::close(devnull);
    int status = 0;
    if (effective_timeout_seconds == 0) {
        if (::waitpid(pid, &status, 0) < 0) {
            return kRunSpawnFailed;
        }
    } else {
        const auto deadline = std::chrono::steady_clock::now() +
                              std::chrono::seconds(effective_timeout_seconds);
        while (true) {
            const pid_t wait_result = ::waitpid(pid, &status, WNOHANG);
            if (wait_result == pid) {
                break;
            }
            if (wait_result < 0) {
                return kRunSpawnFailed;
            }
            if (std::chrono::steady_clock::now() >= deadline) {
                ::kill(pid, SIGKILL);
                ::waitpid(pid, &status, 0);
                return kRunTimedOut;
            }
            ::usleep(10 * 1000);
        }
    }
    if (WIFEXITED(status)) {
        if (force_external_tool && WEXITSTATUS(status) == 127) {
            return kRunSpawnFailed;
        }
        return WEXITSTATUS(status);
    }
    if (WIFSIGNALED(status)) {
        return kRunSignaledBase - WTERMSIG(status);
    }
    return kRunSpawnFailed;
#endif
}

void AppendManifestRow(std::ofstream& manifest,
                       const char* status,
                       const std::filesystem::path& input_relative,
                       const std::filesystem::path& output_relative,
                       const std::string& source_name,
                       const std::string& note) {
    manifest << status << "\t" << input_relative.generic_string() << "\t"
             << output_relative.generic_string() << "\t" << source_name << "\t" << note << "\n";
}

std::string BuildIgnoredOutputPrefix(const std::filesystem::path& workspace_root,
                                     const std::filesystem::path& output_root) {
    std::error_code error;
    const std::filesystem::path relative_output =
        std::filesystem::relative(output_root, workspace_root, error);
    if (error || relative_output.empty()) {
        return {};
    }

    const std::string normalized = NormalizePathString(relative_output.generic_string());
    if (normalized == "." || normalized == ".." || StartsWith(normalized, "../")) {
        return {};
    }

    return EnsureTrailingSlash(normalized);
}

std::string DescribeRunResult(std::string_view label, int result) {
    if (result == 0) {
        return {};
    }
    if (result == kRunTimedOut) {
        return std::string(label) + "_timeout";
    }
    if (result <= kRunSignaledBase) {
        return std::string(label) + "_signal_" + std::to_string(kRunSignaledBase - result);
    }
    if (result == kRunSpawnFailed) {
        return std::string(label) + "_spawn_failed";
    }
    return std::string(label) + "_exit_" + std::to_string(result);
}

std::string JoinNotes(std::string lhs, std::string rhs) {
    if (lhs.empty()) {
        return rhs;
    }
    if (rhs.empty()) {
        return lhs;
    }
    return lhs + ";" + rhs;
}

bool IsIdentifierChar(char ch) {
    const unsigned char uch = static_cast<unsigned char>(ch);
    return std::isalnum(uch) != 0 || ch == '_';
}

std::string_view StripLineComment(std::string_view line) {
    const std::size_t comment = line.find("--");
    return comment == std::string_view::npos ? line : line.substr(0, comment);
}

bool StripLegacyDecompilerBanner(std::string* source, std::string* note) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    std::vector<std::string_view> lines;
    std::size_t start = 0;
    while (start <= source->size()) {
        const std::size_t end = source->find('\n', start);
        if (end == std::string::npos) {
            lines.emplace_back(source->data() + start, source->size() - start);
            break;
        }
        lines.emplace_back(source->data() + start, end - start);
        start = end + 1;
        if (start == source->size()) {
            lines.emplace_back(source->data() + start, 0);
            break;
        }
    }

    std::size_t banner_lines = 0;
    while (banner_lines < lines.size()) {
        std::string_view trimmed = lines[banner_lines];
        while (!trimmed.empty() &&
               std::isspace(static_cast<unsigned char>(trimmed.front())) != 0) {
            trimmed.remove_prefix(1);
        }
        while (!trimmed.empty() &&
               std::isspace(static_cast<unsigned char>(trimmed.back())) != 0) {
            trimmed.remove_suffix(1);
        }
        if (trimmed.empty()) {
            ++banner_lines;
            continue;
        }
        if (StartsWith(trimmed, "-- Decompiled using luadec ") ||
            StartsWith(trimmed, "-- Command line was: ")) {
            ++banner_lines;
            continue;
        }
        break;
    }

    if (banner_lines == 0) {
        return false;
    }

    std::size_t erase_through = 0;
    for (std::size_t i = 0; i < banner_lines; ++i) {
        erase_through += lines[i].size();
        if (erase_through < source->size() && (*source)[erase_through] == '\n') {
            ++erase_through;
        }
    }

    source->erase(0, erase_through);
    if (note != nullptr) {
        *note = JoinNotes(*note, "normalized_removed_legacy_decompiler_banner");
    }
    return true;
}

bool ContainsWord(std::string_view text, std::string_view word) {
    std::size_t search_from = 0;
    while (true) {
        const std::size_t found = text.find(word, search_from);
        if (found == std::string_view::npos) {
            return false;
        }

        const bool boundary_before =
            found == 0 || !IsIdentifierChar(text[found - 1]);
        const std::size_t end = found + word.size();
        const bool boundary_after =
            end >= text.size() || !IsIdentifierChar(text[end]);
        if (boundary_before && boundary_after) {
            return true;
        }

        search_from = found + 1;
    }
}

bool LooksLikeTopLevelBoundary(std::string_view trimmed) {
    if (trimmed.empty() || StartsWith(trimmed, "--")) {
        return false;
    }

    const unsigned char first = static_cast<unsigned char>(trimmed.front());
    if (std::isalpha(first) == 0 && trimmed.front() != '_') {
        return false;
    }

    if (StartsWith(trimmed, "if ") || StartsWith(trimmed, "elseif ") ||
        StartsWith(trimmed, "while ") || StartsWith(trimmed, "for ") ||
        StartsWith(trimmed, "repeat") || StartsWith(trimmed, "until ") ||
        StartsWith(trimmed, "return") || StartsWith(trimmed, "local ") ||
        trimmed == "end" || trimmed == "else" || trimmed == "do") {
        return false;
    }

    if (trimmed.find("= function") != std::string_view::npos ||
        StartsWith(trimmed, "function ")) {
        return true;
    }

    if (trimmed.find('=') != std::string_view::npos) {
        return true;
    }

    const std::size_t paren = trimmed.find('(');
    if (paren == std::string_view::npos) {
        return false;
    }

    return trimmed.find('.') != std::string_view::npos ||
           trimmed.find(':') != std::string_view::npos;
}

bool LooksLikeDecompilerTemporaryName(std::string_view name) {
    if (name.empty()) {
        return false;
    }

    if (name.front() == 'r' && name.size() > 1) {
        return std::all_of(name.begin() + 1, name.end(), [](char ch) {
            return std::isdigit(static_cast<unsigned char>(ch)) != 0;
        });
    }

    if (StartsWith(name, "l_")) {
        bool saw_digit = false;
        for (std::size_t i = 2; i < name.size(); ++i) {
            const char ch = name[i];
            if (ch == '_') {
                continue;
            }
            if (std::isdigit(static_cast<unsigned char>(ch)) == 0) {
                return false;
            }
            saw_digit = true;
        }
        return saw_digit;
    }

    return false;
}

bool LooksLikeRegisterizedTemporaryName(std::string_view name) {
    if (name.empty()) {
        return false;
    }

    if (name.front() != 'r' || name.size() <= 1) {
        return false;
    }

    return std::all_of(name.begin() + 1, name.end(), [](char ch) {
        return std::isdigit(static_cast<unsigned char>(ch)) != 0;
    });
}

bool LooksLikeTopLevelLeakageLine(std::string_view trimmed) {
    if (trimmed.empty() || StartsWith(trimmed, "--")) {
        return false;
    }

    if (trimmed == "return" || StartsWith(trimmed, "return ")) {
        return true;
    }

    auto first_token = [&](std::string_view text) {
        const std::size_t end = text.find_first_of(" \t=");
        return end == std::string_view::npos ? text : text.substr(0, end);
    };

    if (StartsWith(trimmed, "local ")) {
        return LooksLikeDecompilerTemporaryName(first_token(trimmed.substr(6)));
    }

    const std::string_view name = first_token(trimmed);
    if (!LooksLikeDecompilerTemporaryName(name)) {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    if (equal != std::string_view::npos) {
        return true;
    }

    return StartsWith(trimmed, name) &&
           trimmed.size() > name.size() &&
           std::isspace(static_cast<unsigned char>(trimmed[name.size()])) != 0;
}

std::string_view ExtractLeadingIdentifier(std::string_view trimmed);
std::string_view ExtractLocalDeclarationName(std::string_view trimmed);

bool LooksLikeOrphanClosureTailLine(std::string_view trimmed) {
    if (trimmed.empty() || StartsWith(trimmed, "--")) {
        return false;
    }

    if (trimmed == "end" || trimmed == "return" || StartsWith(trimmed, "return ")) {
        return true;
    }

    const std::string_view local_name = ExtractLocalDeclarationName(trimmed);
    if (!local_name.empty() && LooksLikeDecompilerTemporaryName(local_name) &&
        trimmed.find('=') != std::string_view::npos) {
        return true;
    }

    const std::string_view leading = ExtractLeadingIdentifier(trimmed);
    return !leading.empty() && LooksLikeDecompilerTemporaryName(leading) &&
           trimmed.find('=') != std::string_view::npos;
}

std::string SummarizeSyntaxError(std::string_view raw_error) {
    std::string message(raw_error);
    const std::size_t detail_pos = message.rfind(": ");
    if (detail_pos != std::string::npos && detail_pos + 2 < message.size()) {
        message = message.substr(detail_pos + 2);
    }

    std::string sanitized;
    sanitized.reserve(message.size());
    bool previous_space = false;
    for (char ch : message) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        if (ch == ';' || ch == '\n' || ch == '\r' || ch == '\t') {
            ch = ' ';
        }
        if (std::isspace(uch) != 0 || ch == ' ') {
            if (!previous_space) {
                sanitized.push_back('_');
                previous_space = true;
            }
        } else {
            sanitized.push_back(ch);
            previous_space = false;
        }
        if (sanitized.size() >= 96) {
            break;
        }
    }

    while (!sanitized.empty() && sanitized.back() == '_') {
        sanitized.pop_back();
    }
    while (!sanitized.empty() && sanitized.front() == '_') {
        sanitized.erase(sanitized.begin());
    }

    return sanitized.empty() ? "unknown" : sanitized;
}

std::size_t ExtractLuaErrorLine(std::string_view raw_error) {
    std::size_t first_colon = raw_error.find(':');
    while (first_colon != std::string_view::npos) {
        const std::size_t number_begin = first_colon + 1;
        std::size_t number_end = number_begin;
        while (number_end < raw_error.size() &&
               std::isdigit(static_cast<unsigned char>(raw_error[number_end])) != 0) {
            ++number_end;
        }
        if (number_end > number_begin && number_end < raw_error.size() && raw_error[number_end] == ':') {
            return static_cast<std::size_t>(
                std::strtoul(std::string(raw_error.substr(number_begin, number_end - number_begin)).c_str(),
                             nullptr,
                             10));
        }
        first_colon = raw_error.find(':', first_colon + 1);
    }
    return 0;
}

int ScoreValidationOutcome(std::string_view raw_error) {
    if (raw_error.empty()) {
        return std::numeric_limits<int>::max();
    }

    int score = 0;
    const int line =
        static_cast<int>(std::min<std::size_t>(ExtractLuaErrorLine(raw_error), 100000));
    score += line * 1000;

    if (raw_error.find("<eof>") == std::string_view::npos) {
        score += 200;
    }
    if (raw_error.find("'end' expected") == std::string_view::npos) {
        score += 100;
    }
    if (raw_error.find("'until' expected") == std::string_view::npos) {
        score += 50;
    }

    const std::string summary = SummarizeSyntaxError(raw_error);
    score -= static_cast<int>(std::min<std::size_t>(summary.size(), 255));
    return score;
}

bool IsValidationOutcomeBetter(std::string_view baseline_error,
                               std::string_view candidate_error) {
    if (candidate_error.empty()) {
        return true;
    }
    if (baseline_error.empty()) {
        return false;
    }
    return ScoreValidationOutcome(candidate_error) > ScoreValidationOutcome(baseline_error);
}

std::string_view TrimWhitespace(std::string_view value) {
    std::size_t begin = 0;
    while (begin < value.size() &&
           std::isspace(static_cast<unsigned char>(value[begin])) != 0) {
        ++begin;
    }

    std::size_t end = value.size();
    while (end > begin &&
           std::isspace(static_cast<unsigned char>(value[end - 1])) != 0) {
        --end;
    }

    return value.substr(begin, end - begin);
}

std::vector<std::string> SplitLines(std::string_view text, bool* had_trailing_newline) {
    std::vector<std::string> lines;
    std::size_t start = 0;
    while (start < text.size()) {
        const std::size_t end = text.find('\n', start);
        if (end == std::string_view::npos) {
            lines.emplace_back(text.substr(start));
            start = text.size();
        } else {
            lines.emplace_back(text.substr(start, end - start));
            start = end + 1;
        }
    }

    if (had_trailing_newline != nullptr) {
        *had_trailing_newline = !text.empty() && text.back() == '\n';
    }

    if (text.empty()) {
        lines.emplace_back();
    }

    return lines;
}

std::string JoinLines(const std::vector<std::string>& lines, bool had_trailing_newline) {
    std::string joined;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (i != 0) {
            joined.push_back('\n');
        }
        joined.append(lines[i]);
    }
    if (had_trailing_newline && !joined.empty()) {
        joined.push_back('\n');
    }
    return joined;
}

std::string ValidateLuaSource(std::string_view source, std::string_view chunk_name);
bool TryStripStandaloneDoReturnEndArtifacts(std::string& decompiled_source,
                                            std::string* note);

struct DecompiledFunctionSpan {
    std::size_t start_line = 0;
    std::size_t end_line = 0;
    std::string prefix;
    std::string suffix;
    bool has_error = false;
    bool looks_suspicious = false;
    bool top_level = false;
};

struct DecompiledMarkerSummary {
    std::size_t decompiler_error_count = 0;
    std::size_t repeated_control_flow_count = 0;
    std::size_t suspicious_while_true_count = 0;
    std::size_t suspicious_until_true_count = 0;
    std::size_t for_generator_count = 0;
};

struct LuaReadabilitySummary {
    std::size_t nonempty_line_count = 0;
    std::size_t temporary_identifier_count = 0;
    std::size_t unique_temporary_identifier_count = 0;
    std::size_t temporary_leading_line_count = 0;
    std::size_t temporary_local_declaration_count = 0;
    std::size_t temporary_assignment_count = 0;
    std::size_t temporary_table_field_assignment_count = 0;
    std::size_t temporary_self_reference_assignment_count = 0;
    std::size_t multi_temporary_token_line_count = 0;
};

struct LuaOutputQualitySummary {
    DecompiledMarkerSummary marker_summary;
    std::size_t undefined_local_warning_count = 0;
    std::size_t missing_end_warning_count = 0;
    std::size_t do_return_end_count = 0;
};

template <typename Callback>
void ForEachLuaToken(std::string_view line, Callback&& callback);

std::string_view ExtractLocalDeclarationName(std::string_view trimmed);

std::size_t CountSubstring(std::string_view haystack, std::string_view needle) {
    if (needle.empty()) {
        return 0;
    }

    std::size_t count = 0;
    std::size_t offset = 0;
    while (true) {
        offset = haystack.find(needle, offset);
        if (offset == std::string_view::npos) {
            break;
        }
        ++count;
        offset += needle.size();
    }
    return count;
}

DecompiledMarkerSummary SummarizeDecompilerMarkers(std::string_view source) {
    DecompiledMarkerSummary summary;
    summary.decompiler_error_count = CountSubstring(source, "DECOMPILER ERROR:");
    summary.repeated_control_flow_count =
        CountSubstring(source, "decompiler aborted repeated control-flow");
    summary.suspicious_while_true_count = CountSubstring(source, "\nwhile true do");
    if (StartsWith(source, "while true do")) {
        ++summary.suspicious_while_true_count;
    }
    summary.suspicious_until_true_count = CountSubstring(source, "\nuntil true");
    if (StartsWith(source, "until true")) {
        ++summary.suspicious_until_true_count;
    }
    summary.for_generator_count = CountSubstring(source, "(for generator)");
    return summary;
}

LuaOutputQualitySummary SummarizeLuaOutputQuality(std::string_view source) {
    LuaOutputQualitySummary summary;
    summary.marker_summary = SummarizeDecompilerMarkers(source);
    summary.undefined_local_warning_count =
        CountSubstring(source, "Warning: undefined locals caused missing assignments!");
    summary.missing_end_warning_count = CountSubstring(source, "Warning: missing end");
    summary.do_return_end_count = CountSubstring(source, "do return end");
    return summary;
}

bool HasBlockingLuaOutputQualityIssues(const LuaOutputQualitySummary& summary) {
    return summary.marker_summary.decompiler_error_count != 0 ||
           summary.marker_summary.repeated_control_flow_count != 0 ||
           summary.undefined_local_warning_count != 0 ||
           summary.missing_end_warning_count != 0 ||
           summary.do_return_end_count != 0;
}

std::string DescribeLuaOutputQuality(const LuaOutputQualitySummary& summary) {
    std::vector<std::string> parts;
    if (summary.marker_summary.decompiler_error_count != 0) {
        parts.push_back("decompiler_errors=" +
                        std::to_string(summary.marker_summary.decompiler_error_count));
    }
    if (summary.marker_summary.repeated_control_flow_count != 0) {
        parts.push_back("repeated_control_flow=" +
                        std::to_string(summary.marker_summary.repeated_control_flow_count));
    }
    if (summary.undefined_local_warning_count != 0) {
        parts.push_back("undefined_local_warnings=" +
                        std::to_string(summary.undefined_local_warning_count));
    }
    if (summary.missing_end_warning_count != 0) {
        parts.push_back("missing_end_warnings=" +
                        std::to_string(summary.missing_end_warning_count));
    }
    if (summary.do_return_end_count != 0) {
        parts.push_back("do_return_end=" +
                        std::to_string(summary.do_return_end_count));
    }
    std::string description;
    for (std::size_t i = 0; i < parts.size(); ++i) {
        if (i != 0) {
            description.push_back(';');
        }
        description += parts[i];
    }
    return description;
}

bool IsDecompilerMarkerSummaryBetter(const DecompiledMarkerSummary& baseline,
                                     const DecompiledMarkerSummary& candidate) {
    const auto baseline_errors =
        baseline.decompiler_error_count + baseline.repeated_control_flow_count;
    const auto candidate_errors =
        candidate.decompiler_error_count + candidate.repeated_control_flow_count;
    if (candidate_errors != baseline_errors) {
        return candidate_errors < baseline_errors;
    }

    const auto baseline_suspicious =
        baseline.suspicious_while_true_count + baseline.suspicious_until_true_count +
        baseline.for_generator_count;
    const auto candidate_suspicious =
        candidate.suspicious_while_true_count + candidate.suspicious_until_true_count +
        candidate.for_generator_count;
    return candidate_suspicious < baseline_suspicious;
}

bool HasValidatedNativeRawNote(std::string_view note) {
    return note.find("native_main_chunk_raw_validated") != std::string_view::npos ||
           note.find("native_first_raw_validated") != std::string_view::npos;
}

bool AllowsEmptyNormalizedLuaOutput(std::string_view note) {
    return note.find("normalized_removed_legacy_decompiler_banner") != std::string_view::npos;
}

bool HasUsableDecompiledLuaOutput(const std::filesystem::path& path, std::string_view note) {
    if (!std::filesystem::exists(path)) {
        return false;
    }

    std::error_code size_error;
    const auto size = std::filesystem::file_size(path, size_error);
    if (!size_error && size > 0) {
        return true;
    }

    return AllowsEmptyNormalizedLuaOutput(note);
}

std::string_view ExtractLeadingIdentifier(std::string_view trimmed) {
    if (trimmed.empty()) {
        return {};
    }

    if (!IsIdentifierChar(trimmed.front()) || std::isdigit(static_cast<unsigned char>(trimmed.front())) != 0) {
        return {};
    }

    std::size_t index = 1;
    while (index < trimmed.size() && IsIdentifierChar(trimmed[index])) {
        ++index;
    }
    return trimmed.substr(0, index);
}

bool LooksLikeTemporarySelfReferenceAssignment(std::string_view trimmed) {
    const std::string_view lhs = ExtractLeadingIdentifier(trimmed);
    if (!LooksLikeRegisterizedTemporaryName(lhs)) {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view rhs = TrimWhitespace(trimmed.substr(equal + 1));
    if (!StartsWith(rhs, lhs)) {
        return false;
    }
    if (rhs.size() == lhs.size()) {
        return false;
    }

    const char next = rhs[lhs.size()];
    return next == '(' || next == '[' || next == '.' || next == ':';
}

bool LooksLikeTemporaryTableFieldAssignment(std::string_view trimmed) {
    const std::string_view lhs = ExtractLeadingIdentifier(trimmed);
    if (!LooksLikeRegisterizedTemporaryName(lhs)) {
        return false;
    }

    if (trimmed.size() <= lhs.size()) {
        return false;
    }

    const char next = trimmed[lhs.size()];
    if (next != '[' && next != '.') {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    return equal != std::string_view::npos && equal > lhs.size();
}

LuaReadabilitySummary SummarizeLuaReadability(std::string_view source) {
    LuaReadabilitySummary summary;
    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(source, &had_trailing_newline);
    std::unordered_set<std::string> unique_temporaries;

    for (const std::string& raw_line : lines) {
        const std::string_view stripped = StripLineComment(raw_line);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (trimmed.empty()) {
            continue;
        }

        ++summary.nonempty_line_count;

        std::size_t temporary_tokens_on_line = 0;
        ForEachLuaToken(stripped, [&](std::string_view token, std::size_t, std::size_t) {
            if (!LooksLikeRegisterizedTemporaryName(token)) {
                return;
            }
            ++summary.temporary_identifier_count;
            ++temporary_tokens_on_line;
            unique_temporaries.emplace(token);
        });

        if (temporary_tokens_on_line >= 2) {
            ++summary.multi_temporary_token_line_count;
        }

        const std::string_view local_name = ExtractLocalDeclarationName(trimmed);
        if (!local_name.empty() && LooksLikeRegisterizedTemporaryName(local_name)) {
            ++summary.temporary_local_declaration_count;
            ++summary.temporary_leading_line_count;
        } else {
            const std::string_view leading = ExtractLeadingIdentifier(trimmed);
            if (!leading.empty() && LooksLikeRegisterizedTemporaryName(leading)) {
                ++summary.temporary_leading_line_count;
                if (trimmed.find('=') != std::string_view::npos) {
                    ++summary.temporary_assignment_count;
                }
                if (LooksLikeTemporaryTableFieldAssignment(trimmed)) {
                    ++summary.temporary_table_field_assignment_count;
                }
            }
        }

        if (LooksLikeTemporarySelfReferenceAssignment(trimmed)) {
            ++summary.temporary_self_reference_assignment_count;
        }
    }

    summary.unique_temporary_identifier_count = unique_temporaries.size();
    return summary;
}

std::size_t ScoreLuaReadabilityRisk(const LuaReadabilitySummary& summary) {
    return summary.temporary_leading_line_count * 5 +
           summary.temporary_local_declaration_count * 4 +
           summary.temporary_assignment_count * 3 +
           summary.temporary_self_reference_assignment_count * 8 +
           summary.multi_temporary_token_line_count * 2 +
           summary.unique_temporary_identifier_count;
}

bool LooksLikeRegisterizedLua(const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 10) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count >= 3 &&
        summary.temporary_leading_line_count * 2 >= summary.nonempty_line_count) {
        return true;
    }

    if (summary.temporary_leading_line_count >= 8 &&
        summary.multi_temporary_token_line_count >= 6 &&
        ScoreLuaReadabilityRisk(summary) >= 48) {
        return true;
    }

    return summary.nonempty_line_count >= 24 &&
           summary.temporary_leading_line_count * 3 >= summary.nonempty_line_count &&
           summary.unique_temporary_identifier_count >= 4 &&
           ScoreLuaReadabilityRisk(summary) >= 64;
}

bool ContainsObviouslyInvalidNilUsage(std::string_view source) {
    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(source, &had_trailing_newline);
    for (const std::string& line : lines) {
        const std::string_view stripped = StripLineComment(line);
        if (stripped.find("nil[") != std::string_view::npos ||
            stripped.find("nil(") != std::string_view::npos ||
            stripped.find("nil.") != std::string_view::npos ||
            stripped.find("nil:") != std::string_view::npos) {
            return true;
        }
    }
    return false;
}

bool IsRunnableLuaDespiteTemporaryNoise(const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 200) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }

    return summary.temporary_leading_line_count * 5 < summary.nonempty_line_count &&
           summary.multi_temporary_token_line_count * 6 < summary.nonempty_line_count &&
           summary.temporary_local_declaration_count * 16 < summary.nonempty_line_count;
}

bool IsStructuredTableInitializerLuaDespiteTemporaryNoise(
    const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 80) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }

    if (summary.temporary_table_field_assignment_count < 24) {
        return false;
    }

    if (summary.temporary_local_declaration_count * 24 >= summary.nonempty_line_count) {
        return false;
    }

    if (summary.multi_temporary_token_line_count * 4 >= summary.nonempty_line_count) {
        return false;
    }

    if (summary.temporary_table_field_assignment_count * 2 >=
        summary.temporary_leading_line_count) {
        return true;
    }

    return summary.nonempty_line_count >= 300 &&
           summary.temporary_assignment_count * 2 >=
               summary.temporary_leading_line_count &&
           summary.unique_temporary_identifier_count <= 12;
}

bool IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
    const LuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 240) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }

    if (summary.unique_temporary_identifier_count > 24) {
        return false;
    }

    if (summary.temporary_leading_line_count * 8 >= summary.nonempty_line_count * 5) {
        return false;
    }

    if (summary.multi_temporary_token_line_count * 6 >= summary.nonempty_line_count) {
        return false;
    }

    const bool locals_heavy =
        summary.temporary_local_declaration_count >= 24 &&
        summary.temporary_local_declaration_count * 10 < summary.nonempty_line_count;
    const bool table_heavy = summary.temporary_table_field_assignment_count >= 20;
    const bool assignment_heavy = summary.temporary_assignment_count >= 48;

    return (locals_heavy || table_heavy) && assignment_heavy;
}

std::string DescribeLuaReadability(const LuaReadabilitySummary& summary) {
    return "lines=" + std::to_string(summary.nonempty_line_count) +
           ",temp_ids=" + std::to_string(summary.temporary_identifier_count) +
           ",temp_names=" + std::to_string(summary.unique_temporary_identifier_count) +
           ",temp_leading=" + std::to_string(summary.temporary_leading_line_count) +
           ",temp_locals=" + std::to_string(summary.temporary_local_declaration_count) +
           ",temp_assignments=" + std::to_string(summary.temporary_assignment_count) +
           ",temp_table_fields=" +
           std::to_string(summary.temporary_table_field_assignment_count) +
           ",temp_self_assignments=" +
           std::to_string(summary.temporary_self_reference_assignment_count) +
           ",temp_dense_lines=" +
           std::to_string(summary.multi_temporary_token_line_count) +
           ",risk=" + std::to_string(ScoreLuaReadabilityRisk(summary));
}

enum class LuaTextBlockKind {
    kFunction,
    kBlock,
    kRepeat,
};

struct LuaTextBlock {
    LuaTextBlockKind kind = LuaTextBlockKind::kBlock;
    std::size_t span_index = 0;
};

template <typename Callback>
void ForEachLuaToken(std::string_view line, Callback&& callback) {
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t i = 0; i < line.size();) {
        if (!in_single_quote && !in_double_quote && i + 1 < line.size() && line[i] == '-' &&
            line[i + 1] == '-') {
            break;
        }

        if (in_single_quote) {
            if (line[i] == '\\' && i + 1 < line.size()) {
                i += 2;
            } else {
                in_single_quote = line[i] != '\'';
                ++i;
            }
            continue;
        }

        if (in_double_quote) {
            if (line[i] == '\\' && i + 1 < line.size()) {
                i += 2;
            } else {
                in_double_quote = line[i] != '"';
                ++i;
            }
            continue;
        }

        if (line[i] == '\'') {
            in_single_quote = true;
            ++i;
            continue;
        }
        if (line[i] == '"') {
            in_double_quote = true;
            ++i;
            continue;
        }

        if (std::isalpha(static_cast<unsigned char>(line[i])) == 0 && line[i] != '_') {
            ++i;
            continue;
        }

        const std::size_t begin = i;
        ++i;
        while (i < line.size() && IsIdentifierChar(line[i])) {
            ++i;
        }
        callback(line.substr(begin, i - begin), begin, i);
    }
}

bool HasInlineFunctionEnd(std::string_view line) {
    bool saw_function = false;
    bool saw_end_after_function = false;
    ForEachLuaToken(line, [&](std::string_view token, std::size_t, std::size_t) {
        if (token == "function") {
            saw_function = true;
            return;
        }
        if (saw_function && token == "end") {
            saw_end_after_function = true;
        }
    });
    return saw_function && saw_end_after_function;
}

std::string_view ExtractLocalDeclarationName(std::string_view trimmed) {
    if (!StartsWith(trimmed, "local ")) {
        return {};
    }

    trimmed = TrimWhitespace(trimmed.substr(6));
    if (trimmed.empty()) {
        return {};
    }

    const std::size_t end = trimmed.find_first_of(" \t=,");
    const std::string_view name =
        end == std::string_view::npos ? trimmed : trimmed.substr(0, end);
    if (name.empty() || !IsIdentifierChar(name.front())) {
        return {};
    }
    return name;
}

bool LooksLikeBrokenMultiResultLocalSequence(const std::vector<std::string>& lines,
                                             std::size_t start_line,
                                             std::size_t end_line) {
    if (start_line >= lines.size() || end_line >= lines.size() || start_line >= end_line) {
        return false;
    }

    auto next_significant = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i <= end_line && i < lines.size(); ++i) {
            const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[i]));
            if (!trimmed.empty()) {
                return i;
            }
        }
        return lines.size();
    };

    for (std::size_t i = start_line; i <= end_line && i < lines.size(); ++i) {
        const std::string_view first_trimmed = TrimWhitespace(StripLineComment(lines[i]));
        const std::string_view first_name = ExtractLocalDeclarationName(first_trimmed);
        if (first_name.empty() || first_trimmed.find('=') != std::string_view::npos) {
            continue;
        }

        const std::size_t second_index = next_significant(i + 1);
        if (second_index > end_line || second_index >= lines.size()) {
            continue;
        }

        const std::string_view second_trimmed =
            TrimWhitespace(StripLineComment(lines[second_index]));
        if (!StartsWith(second_trimmed, "local ") ||
            second_trimmed.find('=') == std::string_view::npos ||
            second_trimmed.find('(') == std::string_view::npos ||
            second_trimmed.find(')') == std::string_view::npos) {
            continue;
        }

        bool references_first_local = false;
        for (std::size_t probe = second_index + 1; probe <= end_line && probe < lines.size();
             ++probe) {
            const std::string_view probe_trimmed =
                TrimWhitespace(StripLineComment(lines[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (ContainsWord(probe_trimmed, first_name)) {
                references_first_local = true;
                break;
            }
            if (StartsWith(probe_trimmed, "local ") || probe_trimmed == "end" ||
                probe_trimmed == "else" || StartsWith(probe_trimmed, "elseif ")) {
                break;
            }
        }

        if (references_first_local) {
            return true;
        }
    }

    return false;
}

bool LooksLikeDegenerateIfElseSequence(const std::vector<std::string>& lines,
                                       std::size_t start_line,
                                       std::size_t end_line) {
    if (start_line >= lines.size() || end_line >= lines.size() || start_line >= end_line) {
        return false;
    }

    auto next_significant = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i <= end_line && i < lines.size(); ++i) {
            const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[i]));
            if (!trimmed.empty()) {
                return i;
            }
        }
        return lines.size();
    };

    for (std::size_t i = start_line; i <= end_line && i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(lines[i]));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            continue;
        }

        const std::size_t next = next_significant(i + 1);
        if (next > end_line || next >= lines.size()) {
            continue;
        }

        const std::string_view next_trimmed =
            TrimWhitespace(StripLineComment(lines[next]));
        if (next_trimmed == "else" || StartsWith(next_trimmed, "elseif ")) {
            return true;
        }
    }

    return false;
}

std::vector<DecompiledFunctionSpan> CollectDecompiledFunctionSpans(
    const std::vector<std::string>& lines) {
    std::vector<DecompiledFunctionSpan> spans;
    std::vector<LuaTextBlock> block_stack;
    const auto span_has_unclosed_blocks = [&](std::size_t start_line,
                                              std::size_t end_line) -> bool {
        int end_balance = 0;
        int until_balance = 0;
        for (std::size_t index = start_line; index <= end_line && index < lines.size(); ++index) {
            ForEachLuaToken(lines[index], [&](std::string_view token, std::size_t, std::size_t) {
                if (token == "repeat") {
                    ++until_balance;
                    return;
                }
                if (token == "until") {
                    until_balance = std::max(0, until_balance - 1);
                    return;
                }
                if (token == "function" || token == "if" || token == "for" ||
                    token == "while" || token == "do") {
                    ++end_balance;
                    return;
                }
                if (token == "end") {
                    end_balance = std::max(0, end_balance - 1);
                }
            });
        }
        return end_balance > 0 || until_balance > 0;
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string& line = lines[line_index];
        ForEachLuaToken(line, [&](std::string_view token, std::size_t begin, std::size_t end) {
            if (token == "function") {
                DecompiledFunctionSpan span;
                span.start_line = line_index;
                span.end_line = line_index;
                span.prefix = line.substr(0, begin);
                const bool no_enclosing_function = std::none_of(
                    block_stack.begin(), block_stack.end(), [](const LuaTextBlock& block) {
                        return block.kind == LuaTextBlockKind::kFunction;
                    });
                const std::size_t first_non_space = line.find_first_not_of(" \t");
                const bool starts_at_root_indent =
                    first_non_space != std::string::npos && first_non_space == 0;
                span.top_level = no_enclosing_function || starts_at_root_indent;
                spans.push_back(std::move(span));
                block_stack.push_back({LuaTextBlockKind::kFunction, spans.size() - 1});
                return;
            }

            if (token == "repeat") {
                block_stack.push_back({LuaTextBlockKind::kRepeat, 0});
                return;
            }

            if (token == "if" || token == "for" || token == "while" || token == "do") {
                block_stack.push_back({LuaTextBlockKind::kBlock, 0});
                return;
            }

            if (token == "end") {
                while (!block_stack.empty() &&
                       block_stack.back().kind == LuaTextBlockKind::kRepeat) {
                    block_stack.pop_back();
                }
                if (block_stack.empty()) {
                    return;
                }
                const LuaTextBlock block = block_stack.back();
                block_stack.pop_back();
                if (block.kind == LuaTextBlockKind::kFunction &&
                    block.span_index < spans.size()) {
                    spans[block.span_index].end_line = line_index;
                    spans[block.span_index].suffix = line.substr(end);
                }
                return;
            }

            if (token == "until") {
                while (!block_stack.empty() &&
                       block_stack.back().kind != LuaTextBlockKind::kRepeat) {
                    block_stack.pop_back();
                }
                if (!block_stack.empty() &&
                    block_stack.back().kind == LuaTextBlockKind::kRepeat) {
                    block_stack.pop_back();
                }
            }
        });

        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        const bool line_looks_broken =
            line.find("DECOMPILER ERROR:") != std::string::npos ||
            line.find("decompiler aborted repeated control-flow") != std::string::npos ||
            line.find("Warning: missing end") != std::string::npos ||
            line.find("(for generator)") != std::string::npos ||
            line.find("nil[") != std::string::npos ||
            line.find("nil(") != std::string::npos ||
            line.find("nil.") != std::string::npos ||
            trimmed == "local";
        const bool line_looks_suspicious =
            line_looks_broken || trimmed == "while true do" || trimmed == "do return end" ||
            trimmed == "until true";
        if (line_looks_suspicious) {
            for (auto it = block_stack.rbegin(); it != block_stack.rend(); ++it) {
                if (it->kind == LuaTextBlockKind::kFunction && it->span_index < spans.size()) {
                    spans[it->span_index].looks_suspicious = true;
                    if (line_looks_broken) {
                        spans[it->span_index].has_error = true;
                    }
                    break;
                }
            }
        }
    }

    std::vector<std::size_t> top_level_starts;
    top_level_starts.reserve(spans.size());
    for (std::size_t index = 0; index < spans.size(); ++index) {
        if (spans[index].top_level) {
            top_level_starts.push_back(index);
        }
    }

    for (std::size_t order = 0; order < top_level_starts.size(); ++order) {
        DecompiledFunctionSpan& span = spans[top_level_starts[order]];
        if (span.end_line == span.start_line &&
            span.start_line < lines.size() &&
            !HasInlineFunctionEnd(lines[span.start_line])) {
            span.looks_suspicious = true;
            span.has_error = true;
        }
        if (!span.looks_suspicious) {
            continue;
        }

        std::size_t next_start_line = lines.size();
        if (order + 1 < top_level_starts.size()) {
            next_start_line = spans[top_level_starts[order + 1]].start_line;
        }
        const std::size_t inspection_end =
            next_start_line > 0 ? std::min(span.end_line, next_start_line - 1) : span.end_line;
        if (inspection_end >= span.start_line &&
            span_has_unclosed_blocks(span.start_line, inspection_end)) {
            span.looks_suspicious = true;
            span.has_error = true;
        }
        if (next_start_line != 0 && next_start_line <= span.end_line + 1) {
            span.looks_suspicious = true;
            span.has_error = true;
            if (next_start_line > 0) {
                std::size_t overlap_end = next_start_line - 1;
                while (overlap_end > span.start_line &&
                       TrimWhitespace(lines[overlap_end]).empty()) {
                    --overlap_end;
                }
                span.end_line = overlap_end;
                span.suffix.clear();
            }
        }
        if (next_start_line == 0) {
            continue;
        }

        std::size_t heuristic_end = next_start_line - 1;
        while (heuristic_end > span.start_line &&
               TrimWhitespace(lines[heuristic_end]).empty()) {
            --heuristic_end;
        }

        for (std::size_t probe = next_start_line; probe > span.end_line + 1; --probe) {
            const std::size_t line_index = probe - 1;
            if (TrimWhitespace(lines[line_index]).empty()) {
                if (line_index > span.start_line) {
                    heuristic_end = line_index - 1;
                }
                break;
            }
        }

        if (heuristic_end > span.end_line) {
            span.end_line = heuristic_end;
            span.suffix.clear();
        }
    }

    for (DecompiledFunctionSpan& span : spans) {
        if (span.start_line > span.end_line || span.end_line >= lines.size()) {
            continue;
        }
        const std::string_view start_trimmed =
            TrimWhitespace(StripLineComment(lines[span.start_line]));
        if (start_trimmed.find("= function") != std::string_view::npos ||
            StartsWith(start_trimmed, "function(") ||
            StartsWith(start_trimmed, "function ")) {
            std::size_t expanded_start = span.start_line;
            int tail_line_count = 0;
            while (expanded_start > 0 && tail_line_count < 4) {
                const std::size_t previous = expanded_start - 1;
                const std::string_view previous_trimmed =
                    TrimWhitespace(StripLineComment(lines[previous]));
                if (previous_trimmed.empty()) {
                    expanded_start = previous;
                    continue;
                }
                if (!LooksLikeOrphanClosureTailLine(previous_trimmed)) {
                    break;
                }
                expanded_start = previous;
                ++tail_line_count;
            }
            if (expanded_start < span.start_line) {
                span.start_line = expanded_start;
                span.looks_suspicious = true;
                span.has_error = true;
            }
        }
        if (LooksLikeBrokenMultiResultLocalSequence(lines, span.start_line, span.end_line)) {
            span.looks_suspicious = true;
            span.has_error = true;
        }
        if (LooksLikeDegenerateIfElseSequence(lines, span.start_line, span.end_line)) {
            span.looks_suspicious = true;
            span.has_error = true;
        }
    }

    return spans;
}

void AppendLuaFunctionChildrenPreorder(const refii::lua::Lua51Function& function,
                                       std::vector<const refii::lua::Lua51Function*>* out) {
    for (const refii::lua::Lua51Function& child : function.child_functions) {
        out->push_back(&child);
        AppendLuaFunctionChildrenPreorder(child, out);
    }
}

bool TryRepairNativeCandidateSource(std::string& candidate_source,
                                    std::string* note,
                                    std::string_view note_prefix,
                                    std::string* validation_error_out);
bool ShouldUseNativeMainChunkSubprocess(const std::vector<char>& input_bytes);
bool TryRenderNativeMainChunkCandidateViaTool(
    const refii::lua::LuaWorkspaceDecompileOptions& options,
    const std::filesystem::path& input_path,
    const std::vector<char>& input_bytes,
    bool simplify_rendered_output,
    std::string* decompiled_source,
    std::string* note,
    std::string_view note_prefix);
bool TryRenderNativeFunctionViaTool(const refii::lua::LuaWorkspaceDecompileOptions& options,
                                    const std::filesystem::path& input_path,
                                    std::size_t function_index,
                                    std::string* rendered_function,
                                    std::string* note);

bool TryReplaceDecompilerErrorFunctions(const std::vector<char>& input_bytes,
                                        std::string& decompiled_source,
                                        std::string* note,
                                        bool allow_full_main_chunk_replacement,
                                        const refii::lua::LuaWorkspaceDecompileOptions* options = nullptr,
                                        const std::filesystem::path* input_path = nullptr) {
    if (!IsLuaBytecode(input_bytes)) {
        return false;
    }

    refii::lua::Lua51Chunk chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(input_bytes, &chunk, &parse_error)) {
        if (note && !parse_error.empty()) {
            *note = JoinNotes(*note, "native_function_parse_failed=" + parse_error);
        }
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    const std::string baseline_validation_error =
        ValidateLuaSource(decompiled_source, "native_function_baseline");
    const std::string baseline_validation_summary =
        SummarizeSyntaxError(baseline_validation_error);
    const DecompiledMarkerSummary baseline_marker_summary =
        SummarizeDecompilerMarkers(decompiled_source);
    const LuaReadabilitySummary baseline_readability_summary =
        SummarizeLuaReadability(decompiled_source);
    const bool baseline_has_repeated_control_flow =
        decompiled_source.find("decompiler aborted repeated control-flow") != std::string::npos;
    const bool use_external_native_render =
        options != nullptr && input_path != nullptr &&
        ShouldUseNativeMainChunkSubprocess(input_bytes);

    const auto try_full_main_chunk_replacement = [&]() -> bool {
        std::string rendered_main;
        std::string render_error;
        const bool rendered_ok =
            use_external_native_render
                ? TryRenderNativeMainChunkCandidateViaTool(*options,
                                                           *input_path,
                                                           input_bytes,
                                                           true,
                                                           &rendered_main,
                                                           note,
                                                           "native_main_chunk")
                : refii::lua::RenderLua51StraightLineFunctionBody(
                      chunk.main, &rendered_main, &render_error);
        if (!rendered_ok) {
            if (note != nullptr && !render_error.empty()) {
                *note = JoinNotes(*note, "native_main_render_failed=" + render_error);
            }
            return false;
        }

        const LuaReadabilitySummary candidate_readability =
            SummarizeLuaReadability(rendered_main);
        if (LooksLikeRegisterizedLua(candidate_readability)) {
            if (IsRunnableLuaDespiteTemporaryNoise(candidate_readability)) {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "native_main_chunk_quality_relaxed=" +
                                          DescribeLuaReadability(candidate_readability));
                }
            } else if (IsStructuredTableInitializerLuaDespiteTemporaryNoise(
                           candidate_readability)) {
                if (note != nullptr) {
                    *note = JoinNotes(
                        *note,
                        "native_main_chunk_quality_relaxed_structured_tables=" +
                            DescribeLuaReadability(candidate_readability));
                }
            } else if (IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
                           candidate_readability)) {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "native_main_chunk_quality_relaxed_large_body=" +
                                          DescribeLuaReadability(candidate_readability));
                }
            } else {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "native_main_chunk_quality_rejected=" +
                                          DescribeLuaReadability(candidate_readability));
                }
                return false;
            }
        }

        std::string candidate_error =
            ValidateLuaSource(rendered_main, "native_main_chunk");
        TryRepairNativeCandidateSource(rendered_main,
                                       note,
                                       "native_main_chunk",
                                       &candidate_error);
        const std::string candidate_summary = SummarizeSyntaxError(candidate_error);
        const bool candidate_acceptable =
            candidate_error.empty() ||
            (!baseline_validation_error.empty() &&
             (candidate_error == baseline_validation_error ||
              candidate_summary == baseline_validation_summary ||
              IsValidationOutcomeBetter(baseline_validation_error, candidate_error)));
        if (!candidate_acceptable) {
            const DecompiledMarkerSummary candidate_markers =
                SummarizeDecompilerMarkers(rendered_main);
            if (!IsDecompilerMarkerSummaryBetter(baseline_marker_summary, candidate_markers)) {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "native_main_chunk_invalid=" +
                                          SummarizeSyntaxError(candidate_error));
                }
                return false;
            }

            decompiled_source = std::move(rendered_main);
            lines = SplitLines(decompiled_source, &had_trailing_newline);
            if (note != nullptr) {
                *note = JoinNotes(*note, "native_main_chunk_progress");
            }
            return true;
        }

        decompiled_source = std::move(rendered_main);
        lines = SplitLines(decompiled_source, &had_trailing_newline);
        if (note != nullptr) {
            *note = JoinNotes(*note, "native_main_chunk_replacement");
        }
        return true;
    };

    const bool consider_full_main_chunk =
        allow_full_main_chunk_replacement &&
        (baseline_marker_summary.decompiler_error_count != 0 ||
         baseline_marker_summary.repeated_control_flow_count != 0);
    if (consider_full_main_chunk && try_full_main_chunk_replacement()) {
        return true;
    }

    std::vector<DecompiledFunctionSpan> spans = CollectDecompiledFunctionSpans(lines);
    const std::size_t baseline_error_line = ExtractLuaErrorLine(baseline_validation_error);
    if (baseline_error_line != 0) {
        for (DecompiledFunctionSpan& span : spans) {
            const std::size_t span_start_line = span.start_line + 1;
            const std::size_t span_end_line = span.end_line + 1;
            if (baseline_error_line >= span_start_line &&
                baseline_error_line <= span_end_line + 2) {
                span.looks_suspicious = true;
                span.has_error = true;
            }
        }
    }

    std::vector<const refii::lua::Lua51Function*> functions;
    AppendLuaFunctionChildrenPreorder(chunk.main, &functions);
    if (spans.empty() || functions.empty()) {
        return false;
    }

    struct FunctionReplacement {
        std::size_t start_line = 0;
        std::size_t end_line = 0;
        std::size_t function_index = 0;
        bool top_level = false;
        bool looks_suspicious = false;
        bool has_error = false;
        std::vector<std::string> lines;
    };

    std::vector<FunctionReplacement> replacements;
    std::vector<FunctionReplacement> rendered_replacements;
    std::vector<FunctionReplacement> rendered_error_replacements;
    std::vector<FunctionReplacement> rendered_top_level_replacements;
    std::vector<FunctionReplacement> rendered_top_level_error_replacements;
    std::vector<FunctionReplacement> rendered_all_top_level_replacements;
    std::size_t suspicious_span_count = 0;
    std::unordered_map<std::size_t, bool> standalone_validation_cache;
    const bool consider_full_top_level_replacements = !baseline_validation_error.empty();
    const std::size_t replacement_count = std::min(spans.size(), functions.size());
    for (std::size_t index = 0; index < replacement_count; ++index) {
        const DecompiledFunctionSpan& span = spans[index];
        const bool consider_span =
            span.has_error || span.looks_suspicious ||
            (consider_full_top_level_replacements && span.top_level);
        if (!consider_span) {
            continue;
        }
        if (span.has_error || span.looks_suspicious) {
            ++suspicious_span_count;
        }
        if (span.start_line > span.end_line || span.end_line >= lines.size()) {
            if (note) {
                *note = JoinNotes(*note,
                                  "native_function_span_invalid_idx=" +
                                      std::to_string(index + 1) + "@line=" +
                                      std::to_string(functions[index]->line_defined));
            }
            continue;
        }

        std::string rendered_function;
        std::string render_error;
        const bool rendered_ok =
            use_external_native_render
                ? TryRenderNativeFunctionViaTool(
                      *options, *input_path, index + 1, &rendered_function, note)
                : refii::lua::RenderLua51StraightLineFunction(*functions[index],
                                                              &rendered_function,
                                                              &render_error);
        if (!rendered_ok) {
            if (note && !render_error.empty()) {
                *note = JoinNotes(*note, "native_function_render_failed=" + render_error);
                *note = JoinNotes(*note,
                                  "native_function_render_failed_idx=" + std::to_string(index + 1) +
                                      "@line=" +
                                      std::to_string(functions[index]->line_defined));
            }
            continue;
        }

        const LuaReadabilitySummary rendered_readability =
            SummarizeLuaReadability(rendered_function);
        if (ContainsObviouslyInvalidNilUsage(rendered_function)) {
            if (note) {
                *note = JoinNotes(*note,
                                  "native_function_invalid_nil_idx=" +
                                      std::to_string(index + 1));
            }
            continue;
        }
        if (LooksLikeRegisterizedLua(rendered_readability)) {
            if (note) {
                *note = JoinNotes(*note,
                                  "native_function_quality_rejected_idx=" +
                                      std::to_string(index + 1));
                *note = JoinNotes(*note,
                                  "native_function_quality_rejected=" +
                                      DescribeLuaReadability(rendered_readability));
            }
            continue;
        }

        bool rendered_trailing_newline = false;
        std::vector<std::string> replacement_lines =
            SplitLines(rendered_function, &rendered_trailing_newline);
        if (replacement_lines.empty()) {
            continue;
        }
        replacement_lines.front() = span.prefix + replacement_lines.front();
        replacement_lines.back() += span.suffix;

        rendered_replacements.push_back(
            {span.start_line,
             span.end_line,
             index + 1,
             span.top_level,
             span.looks_suspicious,
             span.has_error,
             replacement_lines});
        if (span.has_error) {
            rendered_error_replacements.push_back(
                {span.start_line,
                 span.end_line,
                 index + 1,
                 span.top_level,
                 span.looks_suspicious,
                 span.has_error,
                 replacement_lines});
        }
        if (span.top_level) {
            rendered_all_top_level_replacements.push_back(
                {span.start_line,
                 span.end_line,
                 index + 1,
                 span.top_level,
                 span.looks_suspicious,
                 span.has_error,
                 replacement_lines});
            rendered_top_level_replacements.push_back(
                {span.start_line,
                 span.end_line,
                 index + 1,
                 span.top_level,
                 span.looks_suspicious,
                 span.has_error,
                 replacement_lines});
            if (span.has_error) {
                rendered_top_level_error_replacements.push_back(
                    {span.start_line,
                     span.end_line,
                     index + 1,
                     span.top_level,
                     span.looks_suspicious,
                     span.has_error,
                     replacement_lines});
            }
        }
    }

    if (rendered_replacements.empty()) {
        return false;
    }

    auto sort_replacements = [](std::vector<FunctionReplacement>* replacement_list) {
        std::sort(replacement_list->begin(), replacement_list->end(),
                  [](const FunctionReplacement& lhs, const FunctionReplacement& rhs) {
                      return lhs.start_line > rhs.start_line;
                  });
    };

    auto prune_nested_replacements_prefer_outer =
        [](std::vector<FunctionReplacement>* replacement_list) {
        if (replacement_list == nullptr || replacement_list->size() < 2) {
            return;
        }

        std::sort(replacement_list->begin(), replacement_list->end(),
                  [](const FunctionReplacement& lhs, const FunctionReplacement& rhs) {
                      if (lhs.start_line != rhs.start_line) {
                          return lhs.start_line < rhs.start_line;
                      }
                      if (lhs.end_line != rhs.end_line) {
                          return lhs.end_line > rhs.end_line;
                      }
                      return lhs.function_index < rhs.function_index;
                  });

        std::vector<FunctionReplacement> kept;
        kept.reserve(replacement_list->size());
        for (const FunctionReplacement& replacement : *replacement_list) {
            bool skip_replacement = false;
            for (auto existing = kept.begin(); existing != kept.end();) {
                const bool existing_contains_replacement =
                    existing->start_line <= replacement.start_line &&
                    existing->end_line >= replacement.end_line;
                if (existing_contains_replacement) {
                    skip_replacement = true;
                    break;
                }

                const bool replacement_contains_existing =
                    replacement.start_line <= existing->start_line &&
                    replacement.end_line >= existing->end_line;
                if (replacement_contains_existing) {
                    existing = kept.erase(existing);
                    continue;
                }

                ++existing;
            }

            if (!skip_replacement) {
                kept.push_back(replacement);
            }
        }

        *replacement_list = std::move(kept);
    };

    auto apply_replacements = [&](const std::vector<FunctionReplacement>& replacement_list,
                                  std::vector<std::string>* target_lines,
                                  bool record_notes) {
        std::size_t applied_floor = target_lines->size();
        std::size_t applied_count = 0;
        for (const FunctionReplacement& replacement : replacement_list) {
            if (replacement.start_line > replacement.end_line ||
                replacement.end_line >= target_lines->size() ||
                replacement.end_line >= applied_floor) {
                if (record_notes && note) {
                    *note = JoinNotes(*note,
                                      "native_function_replacement_skipped_range=" +
                                          std::to_string(replacement.start_line) + "-" +
                                          std::to_string(replacement.end_line));
                }
                continue;
            }
            target_lines->erase(
                target_lines->begin() + static_cast<std::ptrdiff_t>(replacement.start_line),
                target_lines->begin() + static_cast<std::ptrdiff_t>(replacement.end_line) + 1);
            target_lines->insert(
                target_lines->begin() + static_cast<std::ptrdiff_t>(replacement.start_line),
                replacement.lines.begin(),
                replacement.lines.end());
            applied_floor = replacement.start_line;
            ++applied_count;
            if (record_notes && note) {
                *note = JoinNotes(*note,
                                  "native_function_replacement_applied_idx=" +
                                      std::to_string(replacement.function_index));
            }
        }
        return applied_count;
    };

    auto try_combined_replacements =
        [&](std::vector<FunctionReplacement> replacement_list,
            std::string_view combined_note_label) -> bool {
        if (replacement_list.empty()) {
            return false;
        }

        prune_nested_replacements_prefer_outer(&replacement_list);
        sort_replacements(&replacement_list);
        std::vector<std::string> combined_lines = lines;
        const std::size_t combined_applied =
            apply_replacements(replacement_list, &combined_lines, false);
        if (combined_applied == 0) {
            return false;
        }

        std::string combined_source = JoinLines(combined_lines, had_trailing_newline);
        const LuaReadabilitySummary combined_readability =
            SummarizeLuaReadability(combined_source);
        bool combined_has_registerized_replacement = false;
        for (const FunctionReplacement& replacement : replacement_list) {
            const std::string replacement_source = JoinLines(replacement.lines, true);
            const LuaReadabilitySummary replacement_readability =
                SummarizeLuaReadability(replacement_source);
            if (LooksLikeRegisterizedLua(replacement_readability)) {
                combined_has_registerized_replacement = true;
                break;
            }
        }
        if (LooksLikeRegisterizedLua(combined_readability) &&
            combined_has_registerized_replacement) {
            if (note) {
                *note = JoinNotes(*note,
                                  std::string(combined_note_label) + "_quality_rejected=" +
                                      DescribeLuaReadability(combined_readability));
            }
            return false;
        }

        std::string combined_error =
            ValidateLuaSource(combined_source, "native_function_replacement");
        TryRepairNativeCandidateSource(combined_source,
                                       note,
                                       combined_note_label,
                                       &combined_error);
        const std::string combined_summary = SummarizeSyntaxError(combined_error);
        const bool combined_acceptable =
            combined_error.empty() ||
            (!baseline_validation_error.empty() &&
             (combined_error == baseline_validation_error ||
              combined_summary == baseline_validation_summary ||
              IsValidationOutcomeBetter(baseline_validation_error, combined_error)));
        if (!combined_acceptable) {
            const DecompiledMarkerSummary combined_marker_summary =
                SummarizeDecompilerMarkers(combined_source);
            if (IsDecompilerMarkerSummaryBetter(baseline_marker_summary,
                                                combined_marker_summary)) {
                decompiled_source = std::move(combined_source);
                lines = SplitLines(decompiled_source, &had_trailing_newline);
                if (note) {
                    *note = JoinNotes(*note,
                                      std::string(combined_note_label) +
                                          "_progress=" + std::to_string(combined_applied));
                    *note = JoinNotes(
                        *note,
                        std::string(combined_note_label) + "_progress_markers=" +
                            std::to_string(combined_marker_summary.decompiler_error_count) + "," +
                            std::to_string(combined_marker_summary.repeated_control_flow_count) +
                            "," +
                            std::to_string(combined_marker_summary.suspicious_while_true_count +
                                           combined_marker_summary.suspicious_until_true_count +
                                           combined_marker_summary.for_generator_count));
                }
                return true;
            }
            if (note) {
                *note = JoinNotes(*note,
                                  std::string(combined_note_label) + "_invalid=" +
                                      SummarizeSyntaxError(combined_error));
            }
            return false;
        }

        decompiled_source = std::move(combined_source);
        lines = SplitLines(decompiled_source, &had_trailing_newline);
        if (note) {
            *note = JoinNotes(*note,
                              std::string(combined_note_label) + "=" +
                                  std::to_string(combined_applied));
            for (const FunctionReplacement& replacement : replacement_list) {
                *note = JoinNotes(*note,
                                  "native_function_replacement_applied_idx=" +
                                      std::to_string(replacement.function_index));
            }
            *note = JoinNotes(*note,
                              "native_function_replacements=" +
                                  std::to_string(combined_applied));
        }
        return true;
    };

    const bool prefer_combined_replacement =
        baseline_has_repeated_control_flow || suspicious_span_count >= 24;
    const bool restrict_to_explicit_errors = !rendered_error_replacements.empty();

    if (!rendered_top_level_error_replacements.empty() &&
        try_combined_replacements(rendered_top_level_error_replacements,
                                  "native_function_top_level_error_replacements")) {
        return true;
    }

    if (!rendered_error_replacements.empty() &&
        try_combined_replacements(rendered_error_replacements,
                                  "native_function_error_replacements")) {
        return true;
    }

    if (!restrict_to_explicit_errors && !rendered_top_level_replacements.empty() &&
        try_combined_replacements(rendered_top_level_replacements,
                                  "native_function_top_level_combined_replacements")) {
        return true;
    }

    if (consider_full_top_level_replacements &&
        !rendered_all_top_level_replacements.empty() &&
        try_combined_replacements(rendered_all_top_level_replacements,
                                  "native_function_top_level_full_replacements")) {
        return true;
    }

    if (!restrict_to_explicit_errors &&
        (prefer_combined_replacement || rendered_replacements.size() > 1) &&
        try_combined_replacements(rendered_replacements,
                                  "native_function_combined_replacements")) {
        return true;
    }

    auto collect_validation_candidates = [&](const std::vector<FunctionReplacement>& candidates) {
        const auto is_already_selected = [&](const FunctionReplacement& replacement) {
            return std::any_of(replacements.begin(),
                               replacements.end(),
                               [&](const FunctionReplacement& existing) {
                                   return existing.start_line == replacement.start_line &&
                                          existing.end_line == replacement.end_line &&
                                          existing.function_index == replacement.function_index;
                               });
        };

        const auto replacement_validates_standalone =
            [&](const FunctionReplacement& replacement) {
                const auto cached = standalone_validation_cache.find(replacement.function_index);
                if (cached != standalone_validation_cache.end()) {
                    return cached->second;
                }

                std::string replacement_source = JoinLines(replacement.lines, true);
                const bool valid =
                    ValidateLuaSource(replacement_source, "native_function_replacement") == "";
                standalone_validation_cache.emplace(replacement.function_index, valid);
                return valid;
            };

        for (const FunctionReplacement& replacement : candidates) {
            if (replacement.top_level && replacement.looks_suspicious &&
                replacement_validates_standalone(replacement)) {
                if (!is_already_selected(replacement)) {
                    replacements.push_back(replacement);
                }
                if (note) {
                    *note = JoinNotes(*note,
                                      "native_function_candidate_standalone_selected_idx=" +
                                          std::to_string(replacement.function_index));
                }
                continue;
            }

            std::vector<std::string> candidate_lines = lines;
            candidate_lines.erase(candidate_lines.begin() +
                                      static_cast<std::ptrdiff_t>(replacement.start_line),
                                  candidate_lines.begin() +
                                          static_cast<std::ptrdiff_t>(replacement.end_line) +
                                      1);
            candidate_lines.insert(candidate_lines.begin() +
                                       static_cast<std::ptrdiff_t>(replacement.start_line),
                                   replacement.lines.begin(),
                                   replacement.lines.end());

            const std::string candidate_source = JoinLines(candidate_lines, had_trailing_newline);
            const LuaReadabilitySummary candidate_readability =
                SummarizeLuaReadability(candidate_source);
            const std::string replacement_source = JoinLines(replacement.lines, true);
            const LuaReadabilitySummary replacement_readability =
                SummarizeLuaReadability(replacement_source);
            if (ContainsObviouslyInvalidNilUsage(replacement_source) ||
                ContainsObviouslyInvalidNilUsage(candidate_source)) {
                if (note) {
                    *note = JoinNotes(*note,
                                      "native_function_candidate_invalid_nil_idx=" +
                                          std::to_string(replacement.function_index));
                }
                continue;
            }
            if (LooksLikeRegisterizedLua(candidate_readability) &&
                LooksLikeRegisterizedLua(replacement_readability)) {
                if (note) {
                    *note = JoinNotes(*note,
                                      "native_function_candidate_quality_rejected_idx=" +
                                          std::to_string(replacement.function_index));
                    *note = JoinNotes(*note,
                                      "native_function_candidate_quality_rejected=" +
                                          DescribeLuaReadability(candidate_readability));
                }
                continue;
            }

            const std::string candidate_error =
                ValidateLuaSource(candidate_source, "native_function_replacement");
            const std::string candidate_summary = SummarizeSyntaxError(candidate_error);
            const bool candidate_acceptable =
                candidate_error.empty() ||
                (!baseline_validation_error.empty() &&
                 (candidate_error == baseline_validation_error ||
                  candidate_summary == baseline_validation_summary ||
                  IsValidationOutcomeBetter(baseline_validation_error, candidate_error)));
            if (!candidate_acceptable) {
                const DecompiledMarkerSummary candidate_marker_summary =
                    SummarizeDecompilerMarkers(candidate_source);
                if (IsDecompilerMarkerSummaryBetter(baseline_marker_summary,
                                                    candidate_marker_summary)) {
                    if (!is_already_selected(replacement)) {
                        replacements.push_back(replacement);
                    }
                    if (note) {
                        *note = JoinNotes(*note,
                                          "native_function_candidate_progress_idx=" +
                                              std::to_string(replacement.function_index));
                    }
                    continue;
                }
                if (note) {
                    *note = JoinNotes(*note,
                                      "native_function_candidate_invalid=" +
                                          SummarizeSyntaxError(candidate_error));
                    *note = JoinNotes(*note,
                                      "native_function_candidate_invalid_idx=" +
                                          std::to_string(replacement.function_index));
                }
                continue;
            }

            if (!is_already_selected(replacement)) {
                replacements.push_back(replacement);
            }
        }
    };

    if (!rendered_top_level_error_replacements.empty()) {
        sort_replacements(&rendered_top_level_error_replacements);
        collect_validation_candidates(rendered_top_level_error_replacements);
    }
    if (replacements.empty() && !rendered_error_replacements.empty()) {
        sort_replacements(&rendered_error_replacements);
        collect_validation_candidates(rendered_error_replacements);
    }
    if (!restrict_to_explicit_errors && replacements.empty() && !rendered_top_level_replacements.empty()) {
        sort_replacements(&rendered_top_level_replacements);
        collect_validation_candidates(rendered_top_level_replacements);
    }
    if (consider_full_top_level_replacements &&
        !rendered_all_top_level_replacements.empty()) {
        sort_replacements(&rendered_all_top_level_replacements);
        collect_validation_candidates(rendered_all_top_level_replacements);
    }
    if (!restrict_to_explicit_errors && replacements.empty() &&
        rendered_top_level_replacements.size() != rendered_replacements.size()) {
        sort_replacements(&rendered_replacements);
        collect_validation_candidates(rendered_replacements);
    }

    if (replacements.empty()) {
        return false;
    }

    prune_nested_replacements_prefer_outer(&replacements);
    sort_replacements(&replacements);

    const std::size_t applied_count = apply_replacements(replacements, &lines, true);

    decompiled_source = JoinLines(lines, had_trailing_newline);
    const LuaReadabilitySummary replacement_readability =
        SummarizeLuaReadability(decompiled_source);
    if (LooksLikeRegisterizedLua(replacement_readability) &&
        !LooksLikeRegisterizedLua(baseline_readability_summary) &&
        !IsStructuredTableInitializerLuaDespiteTemporaryNoise(replacement_readability)) {
        if (note) {
            *note = JoinNotes(*note,
                              "native_function_replacements_quality_rejected=" +
                                  DescribeLuaReadability(replacement_readability));
        }
        return false;
    }
    if (note) {
        *note = JoinNotes(*note,
                          "native_function_replacements=" +
                              std::to_string(applied_count));
    }
    return applied_count != 0;
}

bool TryReplaceWithValidatedNativeMainChunk(const std::vector<char>& input_bytes,
                                            std::string& decompiled_source,
                                            std::string* note,
                                            const refii::lua::LuaWorkspaceDecompileOptions* options = nullptr,
                                            const std::filesystem::path* input_path = nullptr);

bool ShouldUseNativeMainChunkSubprocess(const std::vector<char>& input_bytes) {
    return input_bytes.size() >= kLargeNativeMainChunkSubprocessThresholdBytes;
}

std::filesystem::path MakeNativeMainChunkTempOutputPath(const std::filesystem::path& input_path) {
    static std::uint64_t counter = 0;
#if defined(_WIN32)
    const auto process_id = static_cast<long long>(::_getpid());
#else
    const auto process_id = static_cast<long long>(::getpid());
#endif
    std::filesystem::path output = std::filesystem::temp_directory_path() /
                                   ("refii-native-main-" + std::to_string(process_id) + "-" +
                                    std::to_string(++counter) + "-" +
                                    input_path.filename().generic_string());
    output += ".lua";
    return output;
}

bool TryRenderNativeMainChunkCandidateViaTool(
    const refii::lua::LuaWorkspaceDecompileOptions& options,
    const std::filesystem::path& input_path,
    const std::vector<char>& input_bytes,
    bool simplify_rendered_output,
    std::string* decompiled_source,
    std::string* note,
    std::string_view note_prefix) {
    if (decompiled_source == nullptr) {
        return false;
    }
    if (options.single_tool_path.empty() || !std::filesystem::exists(options.single_tool_path)) {
        return false;
    }

    const std::filesystem::path temp_output = MakeNativeMainChunkTempOutputPath(input_path);
    std::vector<std::string> tool_args = {"--native-render", "--body"};
    if (!simplify_rendered_output) {
        tool_args.push_back("--raw");
    }
    std::error_code cleanup_error;
    const int exit_code = RunDecompiler(options,
                                        input_path,
                                        temp_output,
                                        tool_args,
                                        true,
                                        options.native_render_timeout_seconds);
    if (exit_code != 0 || !std::filesystem::exists(temp_output) ||
        std::filesystem::file_size(temp_output) == 0) {
        std::filesystem::remove(temp_output, cleanup_error);
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "_subprocess_" +
                                  DescribeRunResult("render", exit_code));
        }
        return false;
    }

    std::vector<char> rendered_bytes;
    const bool read_ok = ReadFile(temp_output, rendered_bytes);
    std::filesystem::remove(temp_output, cleanup_error);
    if (!read_ok) {
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "_subprocess_read_failed");
        }
        return false;
    }
    std::string rendered_main(rendered_bytes.begin(), rendered_bytes.end());

    if (TrimWhitespace(rendered_main).empty() && !input_bytes.empty()) {
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_subprocess_render_failed=empty_body");
        }
        return false;
    }

    const LuaReadabilitySummary candidate_readability =
        SummarizeLuaReadability(rendered_main);
    if (LooksLikeRegisterizedLua(candidate_readability)) {
        if (IsRunnableLuaDespiteTemporaryNoise(candidate_readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_quality_relaxed=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (IsStructuredTableInitializerLuaDespiteTemporaryNoise(
                       candidate_readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_relaxed_structured_tables=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
                       candidate_readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_relaxed_large_body=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_quality_rejected=" +
                                      DescribeLuaReadability(candidate_readability));
            }
            return false;
        }
    }

    *decompiled_source = std::move(rendered_main);
    if (note != nullptr) {
        *note = JoinNotes(*note, std::string(note_prefix) + "_subprocess_used");
    }
    return true;
}

bool TryRenderNativeFunctionViaTool(const refii::lua::LuaWorkspaceDecompileOptions& options,
                                    const std::filesystem::path& input_path,
                                    std::size_t function_index,
                                    std::string* rendered_function,
                                    std::string* note) {
    if (rendered_function == nullptr) {
        return false;
    }
    if (options.single_tool_path.empty() || !std::filesystem::exists(options.single_tool_path)) {
        return false;
    }

    const std::filesystem::path temp_output = MakeNativeMainChunkTempOutputPath(input_path);
    std::error_code cleanup_error;
    const int exit_code =
        RunDecompiler(options,
                      input_path,
                      temp_output,
                      {"--native-render", "-f", std::to_string(function_index)},
                      true,
                      options.native_render_timeout_seconds);
    if (exit_code != 0 || !std::filesystem::exists(temp_output) ||
        std::filesystem::file_size(temp_output) == 0) {
        std::filesystem::remove(temp_output, cleanup_error);
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              "native_function_subprocess_" +
                                  DescribeRunResult("render", exit_code) + "_idx=" +
                                  std::to_string(function_index));
        }
        return false;
    }

    std::vector<char> rendered_bytes;
    const bool read_ok = ReadFile(temp_output, rendered_bytes);
    std::filesystem::remove(temp_output, cleanup_error);
    if (!read_ok) {
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              "native_function_subprocess_read_failed_idx=" +
                                  std::to_string(function_index));
        }
        return false;
    }

    *rendered_function = std::string(rendered_bytes.begin(), rendered_bytes.end());
    return true;
}

bool TryRenderNativeMainChunkCandidate(const std::vector<char>& input_bytes,
                                       std::string* decompiled_source,
                                       std::string* note,
                                       bool simplify_rendered_output = true,
                                       std::string_view note_prefix = "native_main_chunk") {
    if (decompiled_source == nullptr) {
        return false;
    }

    if (!IsLuaBytecode(input_bytes)) {
        return false;
    }

    refii::lua::Lua51Chunk chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(input_bytes, &chunk, &parse_error)) {
        if (note != nullptr && !parse_error.empty()) {
            *note = JoinNotes(*note, "native_main_chunk_parse_failed=" + parse_error);
        }
        return false;
    }

    std::string rendered_main;
    std::string render_error;
    const bool rendered_ok =
        simplify_rendered_output
            ? refii::lua::RenderLua51StraightLineFunctionBody(
                  chunk.main, &rendered_main, &render_error)
            : refii::lua::RenderLua51StraightLineFunctionBodyRaw(
                  chunk.main, &rendered_main, &render_error);
    if (!rendered_ok) {
        if (note != nullptr && !render_error.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "_render_failed=" + render_error);
        }
        return false;
    }

    if (TrimWhitespace(rendered_main).empty() && !chunk.main.code.empty()) {
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "_render_failed=empty_body");
        }
        return false;
    }

    const LuaReadabilitySummary candidate_readability =
        SummarizeLuaReadability(rendered_main);
    if (LooksLikeRegisterizedLua(candidate_readability)) {
        if (!simplify_rendered_output) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_quality_bypassed_raw=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (IsRunnableLuaDespiteTemporaryNoise(candidate_readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_quality_relaxed=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (IsStructuredTableInitializerLuaDespiteTemporaryNoise(
                       candidate_readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_relaxed_structured_tables=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
                       candidate_readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_relaxed_large_body=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_quality_rejected=" +
                                      DescribeLuaReadability(candidate_readability));
            }
            return false;
        }
    }

    *decompiled_source = std::move(rendered_main);
    return true;
}

bool TryReplaceWithValidatedNativeMainChunk(const std::vector<char>& input_bytes,
                                            std::string& decompiled_source,
                                            std::string* note,
                                            const refii::lua::LuaWorkspaceDecompileOptions* options,
                                            const std::filesystem::path* input_path) {
    auto try_variant = [&](bool simplify_rendered_output,
                           std::string_view note_prefix) {
        std::string candidate_source;
        bool rendered = false;
        if (options != nullptr && input_path != nullptr &&
            ShouldUseNativeMainChunkSubprocess(input_bytes)) {
            rendered = TryRenderNativeMainChunkCandidateViaTool(*options,
                                                                *input_path,
                                                                input_bytes,
                                                                simplify_rendered_output,
                                                                &candidate_source,
                                                                note,
                                                                note_prefix);
        }

        if (!rendered &&
            !TryRenderNativeMainChunkCandidate(input_bytes,
                                               &candidate_source,
                                               note,
                                               simplify_rendered_output,
                                               note_prefix)) {
            return false;
        }

        std::string candidate_error =
            ValidateLuaSource(candidate_source, std::string(note_prefix));
        TryRepairNativeCandidateSource(candidate_source,
                                       note,
                                       note_prefix,
                                       &candidate_error);
        if (!candidate_error.empty()) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_invalid=" +
                                      SummarizeSyntaxError(candidate_error));
            }
            return false;
        }

        if (note != nullptr) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_validated");
        }
        decompiled_source = std::move(candidate_source);
        return true;
    };

    if (ShouldUseNativeMainChunkSubprocess(input_bytes)) {
        if (try_variant(false, "native_main_chunk_raw")) {
            return true;
        }
        return try_variant(true, "native_main_chunk");
    }

    if (try_variant(true, "native_main_chunk")) {
        return true;
    }
    return try_variant(false, "native_main_chunk_raw");
}

struct LuaBlockBalance {
    int end_deficit = 0;
    int until_deficit = 0;
};

LuaBlockBalance EstimateLuaBlockBalance(std::string_view source) {
    LuaBlockBalance balance;
    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(source, &had_trailing_newline);

    for (const std::string& raw_line : lines) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(raw_line));
        if (trimmed.empty()) {
            continue;
        }

        if (trimmed == "repeat") {
            ++balance.until_deficit;
        } else if (trimmed == "until" || StartsWith(trimmed, "until ")) {
            balance.until_deficit = std::max(0, balance.until_deficit - 1);
        }

        bool opens_end_block = false;
        if ((StartsWith(trimmed, "if ") || StartsWith(trimmed, "while ") || StartsWith(trimmed, "for ")) &&
            (trimmed.find(" then") != std::string_view::npos ||
             EndsWith(trimmed, " then") ||
             trimmed.find(" do") != std::string_view::npos ||
             EndsWith(trimmed, " do"))) {
            opens_end_block = true;
        } else if (trimmed == "do") {
            opens_end_block = true;
        }

        if (ContainsWord(trimmed, "function")) {
            opens_end_block = true;
        }

        if (opens_end_block) {
            ++balance.end_deficit;
        }

        if (trimmed == "end") {
            balance.end_deficit = std::max(0, balance.end_deficit - 1);
        }
    }

    return balance;
}

bool ReadTextFile(const std::filesystem::path& path, std::string& text) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        return false;
    }

    text.assign(std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>());
    return stream.good() || stream.eof();
}

bool WriteTextFile(const std::filesystem::path& path, std::string_view text) {
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream) {
        return false;
    }

    stream.write(text.data(), static_cast<std::streamsize>(text.size()));
    return static_cast<bool>(stream);
}

bool WriteNormalizedLuaTextFile(const std::filesystem::path& path,
                                std::string text,
                                std::string* note) {
    StripLegacyDecompilerBanner(&text, note);
    return WriteTextFile(path, text);
}

bool CopyNormalizedLuaTextFile(const std::filesystem::path& from,
                               const std::filesystem::path& to,
                               std::string* note) {
    std::string text;
    if (!ReadTextFile(from, text)) {
        return false;
    }
    return WriteNormalizedLuaTextFile(to, std::move(text), note);
}

bool FinalizeValidatedLuaOutput(std::string_view source,
                                const std::filesystem::path& output_path,
                                const std::filesystem::path& failed_output_path,
                                std::string* note) {
    std::string final_source(source);
    if (final_source.find("do return end") != std::string::npos) {
        std::string repaired_source = final_source;
        if (TryStripStandaloneDoReturnEndArtifacts(repaired_source, note) &&
            ValidateLuaSource(repaired_source, output_path.generic_string()).empty()) {
            final_source = std::move(repaired_source);
        }
    }

    const LuaReadabilitySummary readability = SummarizeLuaReadability(final_source);
    const LuaOutputQualitySummary quality = SummarizeLuaOutputQuality(final_source);
    const bool allow_validated_native_raw =
        note != nullptr && HasValidatedNativeRawNote(*note);
    if (LooksLikeRegisterizedLua(readability)) {
        if (allow_validated_native_raw) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_bypassed_validated_native_raw=" +
                                      DescribeLuaReadability(readability));
            }
        } else if (IsRunnableLuaDespiteTemporaryNoise(readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_relaxed_temporary_noise=" +
                                      DescribeLuaReadability(readability));
            }
        } else if (IsStructuredTableInitializerLuaDespiteTemporaryNoise(readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_relaxed_structured_tables=" +
                                      DescribeLuaReadability(readability));
            }
        } else if (IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(readability)) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "quality_relaxed_large_body=" +
                                      DescribeLuaReadability(readability));
            }
        } else {
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              "quality_rejected_registerized_output=" +
                                  DescribeLuaReadability(readability));
        }
        if (!failed_output_path.empty()) {
            std::filesystem::create_directories(failed_output_path.parent_path());
            if (WriteNormalizedLuaTextFile(failed_output_path, final_source, note)) {
                if (note != nullptr) {
                    *note = JoinNotes(*note, "failed_output_saved");
                }
            } else if (note != nullptr) {
                *note = JoinNotes(*note, "failed_output_save_failed");
            }
        }
        return false;
        }
    }

    if (HasBlockingLuaOutputQualityIssues(quality)) {
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              "quality_rejected_decompiler_artifacts=" +
                                  DescribeLuaOutputQuality(quality));
        }
        if (!failed_output_path.empty()) {
            std::filesystem::create_directories(failed_output_path.parent_path());
            if (WriteNormalizedLuaTextFile(failed_output_path, final_source, note)) {
                if (note != nullptr) {
                    *note = JoinNotes(*note, "failed_output_saved");
                }
            } else if (note != nullptr) {
                *note = JoinNotes(*note, "failed_output_save_failed");
            }
        }
        return false;
    }

    if (!WriteNormalizedLuaTextFile(output_path, final_source, note)) {
        if (note != nullptr) {
            *note = JoinNotes(*note, "write_decompiled_output_failed");
        }
        return false;
    }
    return true;
}

bool TryFinalizeNativeCandidateOutput(const std::filesystem::path& relative_input,
                                      std::string candidate_source,
                                      const std::filesystem::path& output_path,
                                      const std::filesystem::path& failed_output_path,
                                      std::string* note,
                                      std::string_view note_prefix) {
    StripLegacyDecompilerBanner(&candidate_source, note);

    const LuaReadabilitySummary candidate_readability =
        SummarizeLuaReadability(candidate_source);
    const bool allow_validated_native_raw =
        note != nullptr && HasValidatedNativeRawNote(*note);
    if (LooksLikeRegisterizedLua(candidate_readability)) {
        if (allow_validated_native_raw) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_bypassed_validated_raw=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (IsRunnableLuaDespiteTemporaryNoise(candidate_readability)) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_quality_relaxed=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (IsStructuredTableInitializerLuaDespiteTemporaryNoise(
                       candidate_readability)) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_relaxed_structured_tables=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else if (IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
                       candidate_readability)) {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_quality_relaxed_large_body=" +
                                      DescribeLuaReadability(candidate_readability));
            }
        } else {
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_quality_rejected=" +
                                      DescribeLuaReadability(candidate_readability));
            }
            return false;
        }
    }

    const std::string normalized_relative =
        NormalizePathString(relative_input.generic_string());
    std::string validation_error =
        ValidateLuaSource(candidate_source, normalized_relative);
    TryRepairNativeCandidateSource(candidate_source,
                                   note,
                                   note_prefix,
                                   &validation_error);
    if (!validation_error.empty()) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "_invalid=" +
                                  SummarizeSyntaxError(validation_error));
        }
        return false;
    }

    if (note != nullptr && !note_prefix.empty()) {
        *note = JoinNotes(*note, std::string(note_prefix) + "_validated");
    }
    return FinalizeValidatedLuaOutput(candidate_source,
                                      output_path,
                                      failed_output_path,
                                      note);
}

std::string BuildDisassemblyPlaceholder(const std::filesystem::path& input_relative,
                                        const std::filesystem::path& disassembly_relative,
                                        std::string_view source_name) {
    std::string placeholder;
    placeholder.reserve(512);
    placeholder += "-- refii-disassembly-placeholder\n";
    placeholder += "-- This script could not be decompiled into valid Lua yet.\n";
    placeholder += "-- Path: " + input_relative.generic_string() + "\n";
    if (!source_name.empty()) {
        placeholder += "-- Source: " + std::string(source_name) + "\n";
    }
    placeholder += "-- Readable disassembly: " + disassembly_relative.generic_string() + "\n";
    placeholder += "-- Replace this file with valid Lua to activate a loose-script override.\n";
    return placeholder;
}

std::string ValidateLuaSource(std::string_view source, std::string_view chunk_name) {
    lua_State* state = luaL_newstate();
    if (state == nullptr) {
        return "validator_init_failed";
    }

    const std::string name(chunk_name.empty() ? "decompiled_chunk" : chunk_name);
    const int status = luaL_loadbuffer(state, source.data(), source.size(), name.c_str());
    std::string error;
    if (status != 0) {
        if (const char* message = lua_tostring(state, -1); message != nullptr) {
            error = message;
        } else {
            error = "syntax_invalid";
        }
    }

    lua_close(state);
    return error;
}

bool TryAppendMissingEnds(std::string& decompiled_source, std::string_view validation_error,
                          std::string* note) {
    if (validation_error.find("<eof>") == std::string_view::npos &&
        validation_error.find("'end' expected") == std::string_view::npos) {
        return false;
    }

    std::string candidate = decompiled_source;
    std::string best_candidate;
    std::string best_error;
    for (int i = 0; i < 32; ++i) {
        candidate.append("\nend");
        const std::string candidate_error = ValidateLuaSource(candidate, "decompiled_chunk");
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note) {
                *note = JoinNotes(*note, "syntax_repaired_by_appending_end");
            }
            return true;
        }
        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
            best_candidate = candidate;
            best_error = candidate_error;
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note) {
            *note = JoinNotes(*note, "syntax_repaired_by_appending_end_progress");
        }
        return true;
    }

    return false;
}

bool TryAppendMissingUntil(std::string& decompiled_source, std::string_view validation_error,
                           std::string* note) {
    if (validation_error.find("'until' expected") == std::string_view::npos ||
        validation_error.find("to close 'repeat'") == std::string_view::npos) {
        return false;
    }

    std::string candidate = decompiled_source;
    std::string best_candidate;
    std::string best_error;
    for (int i = 0; i < 16; ++i) {
        candidate.append("\nuntil true");
        const std::string candidate_error = ValidateLuaSource(candidate, "decompiled_chunk");
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note) {
                *note = JoinNotes(*note, "syntax_repaired_by_appending_until");
            }
            return true;
        }
        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
            best_candidate = candidate;
            best_error = candidate_error;
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note) {
            *note = JoinNotes(*note, "syntax_repaired_by_appending_until_progress");
        }
        return true;
    }

    return false;
}

bool TryBalanceOpenBlocks(std::string& decompiled_source,
                          std::string_view validation_error,
                          std::string* note) {
    if (validation_error.find("<eof>") == std::string_view::npos &&
        validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("'until' expected") == std::string_view::npos) {
        return false;
    }

    const LuaBlockBalance balance = EstimateLuaBlockBalance(decompiled_source);
    if (balance.end_deficit <= 0 && balance.until_deficit <= 0) {
        return false;
    }

    auto append_suffix = [&](bool until_first) -> std::string {
        std::string suffix;
        for (int i = 0; i < (until_first ? balance.until_deficit : balance.end_deficit); ++i) {
            suffix.append(until_first ? "\nuntil true" : "\nend");
        }
        for (int i = 0; i < (until_first ? balance.end_deficit : balance.until_deficit); ++i) {
            suffix.append(until_first ? "\nend" : "\nuntil true");
        }
        return suffix;
    };

    const std::array<std::string, 2> suffixes = {
        append_suffix(true),
        append_suffix(false),
    };

    std::string best_candidate;
    std::string best_error;
    for (const std::string& suffix : suffixes) {
        if (suffix.empty()) {
            continue;
        }

        std::string candidate = decompiled_source;
        candidate.append(suffix);
        const std::string candidate_error = ValidateLuaSource(candidate, "decompiled_chunk");
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "syntax_repaired_balanced_blocks:end=" +
                                      std::to_string(std::max(0, balance.end_deficit)) +
                                      ",until=" +
                                      std::to_string(std::max(0, balance.until_deficit)));
            }
            return true;
        }

        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              "syntax_repaired_balanced_blocks_progress:end=" +
                                  std::to_string(std::max(0, balance.end_deficit)) +
                                  ",until=" +
                                  std::to_string(std::max(0, balance.until_deficit)));
        }
        return true;
    }

    return false;
}

bool TryMaterializeMissingEndWarnings(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::string& line : lines) {
        if (TrimWhitespace(line) != "-- Warning: missing end command somewhere! Added here") {
            continue;
        }

        const std::size_t indent_end = line.find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : line.substr(0, indent_end);
        line = indent + "end";
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_materialized_missing_end_warning");
    }
    return true;
}

bool TryMaterializeCommentedControlLines(std::string& decompiled_source, std::string* note) {
    constexpr std::string_view kInvalidPrefix = "-- refii-invalid:";

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::string& line : lines) {
        const std::string_view trimmed = TrimWhitespace(line);
        if (!StartsWith(trimmed, kInvalidPrefix)) {
            continue;
        }

        const std::string_view payload =
            TrimWhitespace(trimmed.substr(kInvalidPrefix.size()));
        if (!(payload == "else" || payload == "end" || payload == "until" ||
              StartsWith(payload, "elseif ") || StartsWith(payload, "until "))) {
            continue;
        }

        const std::size_t indent_end = line.find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : line.substr(0, indent_end);
        line = indent + std::string(payload);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_materialized_control_lines");
    }
    return true;
}

bool TryRewriteDanglingRepeatLoops(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::vector<std::size_t> repeat_lines;
    std::size_t until_count = 0;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (trimmed == "repeat") {
            repeat_lines.push_back(i);
        } else if (trimmed == "until" || StartsWith(trimmed, "until ")) {
            ++until_count;
        }
    }

    if (repeat_lines.empty() || repeat_lines.size() <= until_count) {
        return false;
    }

    const std::size_t unmatched_repeats = repeat_lines.size() - until_count;
    for (std::size_t i = repeat_lines.size() - unmatched_repeats; i < repeat_lines.size(); ++i) {
        std::string& line = lines[repeat_lines[i]];
        const std::size_t prefix = line.find_first_not_of(" \t");
        if (prefix == std::string::npos) {
            line = "while true do";
        } else {
            line.replace(prefix, std::string::npos, "while true do");
        }
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_repeat_to_while");
    }
    return true;
}

bool TryRewriteWhileTrueToRepeat(std::string& decompiled_source,
                                 std::string_view validation_error,
                                 std::string* note) {
    if (validation_error.find("near 'until'") == std::string_view::npos &&
        validation_error.find("'until' expected") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "while true do") {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        const std::size_t indent_end = candidate_lines[i].find_first_not_of(" \t");
        if (indent_end == std::string::npos) {
            candidate_lines[i] = "repeat";
        } else {
            candidate_lines[i].replace(indent_end, std::string::npos, "repeat");
        }

        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaSource(candidate, "decompiled_chunk");
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_while_true_to_repeat");
            }
            return true;
        }

        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note, "syntax_repaired_while_true_to_repeat_progress");
        }
        return true;
    }

    return false;
}

bool TryStripDanglingDoAfterUntil(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!(trimmed == "until" || StartsWith(trimmed, "until "))) {
            continue;
        }

        std::size_t remove_begin = i + 1;
        while (remove_begin < lines.size() && TrimWhitespace(lines[remove_begin]).empty()) {
            ++remove_begin;
        }

        std::size_t remove_end = remove_begin;
        while (remove_end < lines.size() && TrimWhitespace(lines[remove_end]) == "do") {
            ++remove_end;
        }

        if (remove_end == remove_begin) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(remove_begin),
                    lines.begin() + static_cast<std::ptrdiff_t>(remove_end));
        changed = true;
        i = remove_begin > 0 ? remove_begin - 1 : 0;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_dangling_do_after_until");
    }
    return true;
}

bool TryStripDanglingDoBeforeElse(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "do") {
            continue;
        }

        std::size_t next = i + 1;
        while (next < lines.size() && TrimWhitespace(lines[next]).empty()) {
            ++next;
        }
        if (next >= lines.size()) {
            continue;
        }

        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (next_trimmed == "else" || StartsWith(next_trimmed, "elseif ")) {
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
            changed = true;
            --i;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_dangling_do");
    }
    return true;
}

bool TryStripDanglingEndBeforeElse(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    auto next_significant = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "end") {
            continue;
        }

        const std::size_t next = next_significant(i + 1);
        if (next >= lines.size()) {
            continue;
        }

        bool remove_end = false;
        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (next_trimmed == "return" || StartsWith(next_trimmed, "return ") ||
            next_trimmed == "do return end") {
            const std::size_t next_after_return = next_significant(next + 1);
            if (next_after_return < lines.size()) {
                const std::string_view tail_trimmed = TrimWhitespace(lines[next_after_return]);
                remove_end = tail_trimmed == "else" || StartsWith(tail_trimmed, "elseif ");
            }
        }

        if (!remove_end) {
            std::size_t prev = lines.size();
            for (std::size_t j = i; j-- > 0;) {
                if (!TrimWhitespace(lines[j]).empty()) {
                    prev = j;
                    break;
                }
                if (j == 0) {
                    break;
                }
            }
            if (prev < lines.size()) {
                const std::string_view prev_trimmed = TrimWhitespace(lines[prev]);
                if (StartsWith(prev_trimmed, "-- DECOMPILER ERROR:") ||
                    prev_trimmed == "-- Warning: missing end command somewhere! Added here") {
                    std::size_t probe = next;
                    for (int depth = 0; depth < 2 && probe < lines.size(); ++depth) {
                        const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
                        if (probe_trimmed == "else" || StartsWith(probe_trimmed, "elseif ")) {
                            remove_end = true;
                            break;
                        }
                        probe = next_significant(probe + 1);
                    }
                }
            }
        }

        if (!remove_end) {
            continue;
        }
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
        changed = true;
        --i;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_dangling_end");
    }
    return true;
}

bool TryRemoveExcessEndLines(std::string& decompiled_source,
                             std::string_view validation_error,
                             std::string* note) {
    if (validation_error.find("<eof>") == std::string_view::npos ||
        validation_error.find("near 'end'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    std::string best_candidate;
    std::string best_error;
    for (std::size_t run_end = lines.size(); run_end-- > 0;) {
        if (TrimWhitespace(lines[run_end]) != "end") {
            continue;
        }

        std::size_t run_begin = run_end;
        while (run_begin > 0 && TrimWhitespace(lines[run_begin - 1]) == "end") {
            --run_begin;
        }

        const std::size_t run_length = run_end - run_begin + 1;
        const std::size_t max_remove = std::min<std::size_t>(run_length, 6);
        for (std::size_t remove_count = 1; remove_count <= max_remove; ++remove_count) {
            std::vector<std::string> candidate_lines = lines;
            const std::size_t erase_begin = run_end - remove_count + 1;
            candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(erase_begin),
                                  candidate_lines.begin() + static_cast<std::ptrdiff_t>(run_end + 1));
            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaSource(candidate, "decompiled_chunk");
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note, "syntax_repaired_removed_excess_end");
                }
                return true;
            }

            if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
                (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
                best_candidate = std::move(candidate);
                best_error = candidate_error;
            }
        }

        run_end = run_begin;
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note, "syntax_repaired_removed_excess_end_progress");
        }
        return true;
    }

    return false;
}

bool TryStripEmptyElseBlocks(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    auto next_significant = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "else") {
            continue;
        }

        const std::size_t next = next_significant(i + 1);
        if (next >= lines.size()) {
            continue;
        }

        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (next_trimmed != "end") {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
        changed = true;
        --i;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_empty_else");
    }
    return true;
}

bool TryStripTopLevelLeakageAfterFunction(std::string& decompiled_source,
                                          std::string_view validation_error,
                                          std::string* note) {
    if (validation_error.find("near 'return'") == std::string_view::npos &&
        validation_error.find("<eof>") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    auto next_significant = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    auto previous_significant = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i-- > 0;) {
            if (!TrimWhitespace(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    auto is_root_indented = [&](const std::string& line) {
        const std::size_t first = line.find_first_not_of(" \t");
        return first != std::string::npos && first == 0;
    };

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!is_root_indented(lines[i]) || !LooksLikeTopLevelLeakageLine(trimmed)) {
            continue;
        }

        const std::size_t previous = previous_significant(i);
        if (previous == lines.size() || TrimWhitespace(lines[previous]) != "end") {
            continue;
        }

        std::size_t end = i;
        std::size_t probe = i;
        while (probe < lines.size()) {
            const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
            if (probe_trimmed.empty()) {
                end = probe;
                ++probe;
                continue;
            }
            if (!is_root_indented(lines[probe]) ||
                !LooksLikeTopLevelLeakageLine(probe_trimmed)) {
                break;
            }
            end = probe;
            ++probe;
        }

        const std::size_t next = next_significant(end + 1);
        if (next >= lines.size() ||
            !is_root_indented(lines[next]) ||
            !LooksLikeTopLevelBoundary(TrimWhitespace(lines[next]))) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i),
                    lines.begin() + static_cast<std::ptrdiff_t>(end + 1));
        changed = true;
        --i;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_top_level_leakage");
    }
    return true;
}

bool TryInsertEndBeforeReportedLine(std::string& decompiled_source,
                                    std::string_view validation_error,
                                    std::string* note) {
    if (validation_error.find("<eof>") == std::string_view::npos &&
        validation_error.find("'end' expected") == std::string_view::npos) {
        return false;
    }
    if (validation_error.find("near '<eof>'") != std::string_view::npos) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    if (reported_line == 0) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    std::size_t insert_at = reported_line > lines.size() ? lines.size() : reported_line - 1;
    std::size_t probe = insert_at == lines.size() ? lines.size() - 1 : insert_at;
    while (probe > 0) {
        const std::string_view trimmed = TrimWhitespace(lines[probe]);
        if (!trimmed.empty() && !StartsWith(trimmed, "--")) {
            insert_at = probe;
            break;
        }
        --probe;
    }

    const std::string_view reference =
        insert_at < lines.size() ? TrimWhitespace(lines[insert_at]) : std::string_view();
    if (reference == "end" || reference == "else" || StartsWith(reference, "elseif ")) {
        return false;
    }

    std::string indent;
    if (insert_at < lines.size()) {
        const std::size_t indent_end = lines[insert_at].find_first_not_of(" \t");
        if (indent_end != std::string::npos) {
            indent = lines[insert_at].substr(0, indent_end);
        }
    }

    lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(insert_at), indent + "end");
    std::string candidate = JoinLines(lines, had_trailing_newline);
    const std::string candidate_error = ValidateLuaSource(candidate, "decompiled_chunk");
    if (candidate_error.empty() || IsValidationOutcomeBetter(validation_error, candidate_error)) {
        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note, candidate_error.empty()
                                         ? "syntax_repaired_inserted_end_before_reported_line"
                                         : "syntax_repaired_inserted_end_before_reported_line_progress");
        }
        return true;
    }
    return false;
}

bool TryInsertEndsBeforeTopLevelBoundaries(std::string& decompiled_source,
                                           std::string_view validation_error,
                                           std::string* note) {
    if (validation_error.find("<eof>") == std::string_view::npos &&
        validation_error.find("'end' expected") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    std::vector<std::size_t> candidates;
    candidates.reserve(lines.size());
    for (std::size_t i = 1; i < lines.size(); ++i) {
        if (lines[i].find_first_not_of(" \t") != 0) {
            continue;
        }
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (LooksLikeTopLevelBoundary(trimmed)) {
            candidates.push_back(i);
        }
    }

    std::string best_candidate;
    std::string best_error;
    for (auto it = candidates.rbegin(); it != candidates.rend(); ++it) {
        const std::size_t index = *it;
        for (int end_count = 1; end_count <= 4; ++end_count) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(index),
                                   static_cast<std::size_t>(end_count),
                                   std::string("end"));
            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaSource(candidate, "decompiled_chunk");
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "syntax_repaired_inserted_end_before_top_level_boundary");
                }
                return true;
            }
            if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
                (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
                best_candidate = std::move(candidate);
                best_error = candidate_error;
            }
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              "syntax_repaired_inserted_end_before_top_level_boundary_progress");
        }
        return true;
    }

    return false;
}

bool TryInsertEndBeforeElse(std::string& decompiled_source,
                            std::string_view validation_error,
                            std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        (validation_error.find("near 'else'") == std::string_view::npos &&
         validation_error.find("near 'elseif'") == std::string_view::npos)) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (trimmed != "else" && !StartsWith(trimmed, "elseif ")) {
            continue;
        }

        std::size_t previous = i;
        while (previous > 0) {
            --previous;
            const std::string_view previous_trimmed = TrimWhitespace(lines[previous]);
            if (previous_trimmed.empty()) {
                continue;
            }
            if (previous_trimmed == "end" || previous_trimmed == "else" ||
                StartsWith(previous_trimmed, "elseif ") ||
                StartsWith(previous_trimmed, "--")) {
                previous = lines.size();
            }
            break;
        }
        if (previous == lines.size()) {
            continue;
        }

        const std::size_t indent_end = lines[i].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : lines[i].substr(0, indent_end);
        for (int end_count = 1; end_count <= 4; ++end_count) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i),
                                   static_cast<std::size_t>(end_count),
                                   indent + "end");
            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaSource(candidate, "decompiled_chunk");
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_else");
                }
                return true;
            }

            if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
                (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
                best_candidate = std::move(candidate);
                best_error = candidate_error;
            }
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_else_progress");
        }
        return true;
    }

    return false;
}

bool TryInsertEndBeforeReturn(std::string& decompiled_source,
                              std::string_view validation_error,
                              std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        validation_error.find("near 'return'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!(trimmed == "return" || StartsWith(trimmed, "return "))) {
            continue;
        }

        std::size_t previous = i;
        while (previous > 0) {
            --previous;
            const std::string_view previous_trimmed = TrimWhitespace(lines[previous]);
            if (previous_trimmed.empty()) {
                continue;
            }
            if (previous_trimmed == "end" || previous_trimmed == "else" ||
                StartsWith(previous_trimmed, "elseif ") ||
                StartsWith(previous_trimmed, "--")) {
                previous = lines.size();
            }
            break;
        }
        if (previous == lines.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        const std::size_t indent_end = lines[i].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : lines[i].substr(0, indent_end);
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i),
                               indent + "end");
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaSource(candidate, "decompiled_chunk");
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_return");
            }
            return true;
        }

        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_return_progress");
        }
        return true;
    }

    return false;
}

bool TryInsertEndBeforeUntil(std::string& decompiled_source,
                             std::string_view validation_error,
                             std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        validation_error.find("near 'until'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!(trimmed == "until" || StartsWith(trimmed, "until "))) {
            continue;
        }

        std::size_t previous = i;
        while (previous > 0) {
            --previous;
            const std::string_view previous_trimmed = TrimWhitespace(lines[previous]);
            if (previous_trimmed.empty()) {
                continue;
            }
            if (previous_trimmed == "end" || previous_trimmed == "until" ||
                StartsWith(previous_trimmed, "until ") ||
                StartsWith(previous_trimmed, "--")) {
                previous = lines.size();
            }
            break;
        }
        if (previous == lines.size()) {
            continue;
        }

        const std::size_t indent_end = lines[i].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : lines[i].substr(0, indent_end);
        for (int end_count = 1; end_count <= 4; ++end_count) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i),
                                   static_cast<std::size_t>(end_count),
                                   indent + "end");
            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaSource(candidate, "decompiled_chunk");
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_until");
                }
                return true;
            }

            if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
                (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
                best_candidate = std::move(candidate);
                best_error = candidate_error;
            }
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note, "syntax_repaired_inserted_end_before_until_progress");
        }
        return true;
    }

    return false;
}

bool TryInsertUntilBeforeTrailingEnd(std::string& decompiled_source,
                                     std::string_view validation_error,
                                     std::string* note) {
    if (validation_error.find("'until' expected") == std::string_view::npos ||
        validation_error.find("to close 'repeat'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = lines.size(); i-- > 0;) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (trimmed != "end" && trimmed != "else" && !StartsWith(trimmed, "elseif ")) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        const std::size_t indent_end = lines[i].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : lines[i].substr(0, indent_end);
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i),
                               indent + "until true");
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaSource(candidate, "decompiled_chunk");
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_inserted_until_before_end");
            }
            return true;
        }

        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note, "syntax_repaired_inserted_until_before_end_progress");
        }
        return true;
    }

    return false;
}

bool TryRewriteBareMethodReferenceAssignments(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::string& line : lines) {
        const std::size_t comment = line.find("--");
        const std::size_t limit = comment == std::string::npos ? line.size() : comment;
        const std::size_t equal = line.find('=');
        if (equal == std::string::npos || equal >= limit) {
            continue;
        }

        const std::size_t colon = line.find(':', equal + 1);
        if (colon == std::string::npos || colon >= limit) {
            continue;
        }

        const std::size_t paren = line.find('(', colon + 1);
        if (paren != std::string::npos && paren < limit) {
            continue;
        }

        line[colon] = '.';
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_bare_method_reference");
    }
    return true;
}

bool TryRewriteInvalidMethodFieldAssignments(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::string& line : lines) {
        const std::size_t comment = line.find("--");
        const std::size_t limit = comment == std::string::npos ? line.size() : comment;
        const std::size_t equal = line.find('=');
        if (equal == std::string::npos || equal >= limit) {
            continue;
        }

        const std::size_t colon = line.find(':');
        const std::size_t dot = line.find('.', colon == std::string::npos ? 0 : colon + 1);
        if (colon == std::string::npos || colon >= equal || dot == std::string::npos ||
            dot >= equal) {
            continue;
        }

        line[colon] = '.';
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_method_field_assignment");
    }
    return true;
}

bool TryRewriteForGeneratorElseArtifacts(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!StartsWith(trimmed, "for ") || trimmed.find("(for generator)") == std::string_view::npos ||
            !EndsWith(trimmed, " do")) {
            continue;
        }

        std::size_t next = i + 1;
        while (next < lines.size() && TrimWhitespace(lines[next]).empty()) {
            ++next;
        }
        if (next >= lines.size() || TrimWhitespace(lines[next]) != "end") {
            continue;
        }

        const std::size_t indent_end = lines[i].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : lines[i].substr(0, indent_end);
        lines[i] = indent + "else";
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(next));
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_for_generator_else");
    }
    return true;
}

bool TryRewriteDoReturnEndBranches(std::string& decompiled_source,
                                   std::string_view validation_error,
                                   std::string* note) {
    if (validation_error.find("near 'if'") == std::string_view::npos &&
        validation_error.find("near 'elseif'") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    auto indentation_of = [&](std::size_t index) -> std::string {
        const std::size_t indent_end = lines[index].find_first_not_of(" \t");
        return indent_end == std::string::npos ? std::string() : lines[index].substr(0, indent_end);
    };

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "do return end") {
            continue;
        }

        std::size_t next = i + 1;
        while (next < lines.size() && TrimWhitespace(lines[next]).empty()) {
            ++next;
        }
        if (next >= lines.size()) {
            continue;
        }

        const std::string indent = indentation_of(i);
        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (next_trimmed == "end" || next_trimmed == "else" ||
            StartsWith(next_trimmed, "elseif ")) {
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
            changed = true;
            --i;
            continue;
        }

        if (StartsWith(next_trimmed, "if ") && EndsWith(next_trimmed, " then")) {
            lines[next] = indentation_of(next) + "elseif " + std::string(next_trimmed.substr(3));
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
            changed = true;
            --i;
            continue;
        }

        lines[i] = indent + "else";
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_do_return_branch");
    }
    return true;
}

bool TryStripStandaloneDoReturnEndArtifacts(std::string& decompiled_source,
                                            std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "do return end") {
            continue;
        }
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
        changed = true;
        if (i != 0) {
            --i;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_removed_do_return_end_artifact");
    }
    return true;
}

bool TryCommentForGeneratorArtifacts(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::string& line : lines) {
        const std::string_view trimmed = TrimWhitespace(line);
        if (trimmed.find("(for generator)") == std::string_view::npos) {
            continue;
        }
        if (StartsWith(trimmed, "--")) {
            continue;
        }

        const std::size_t indent_end = line.find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : line.substr(0, indent_end);
        line = indent + "-- refii-invalid: " + std::string(trimmed);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_for_generator_artifact");
    }
    return true;
}

bool LooksUnsafeToCommentForSyntaxRepair(std::string_view line) {
    const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
    if (trimmed.empty() || StartsWith(trimmed, "--")) {
        return true;
    }

    int paren_balance = 0;
    int brace_balance = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t i = 0; i < trimmed.size(); ++i) {
        const char ch = trimmed[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < trimmed.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < trimmed.size()) {
                ++i;
            } else if (ch == '"') {
                in_double_quote = false;
            }
            continue;
        }
        if (ch == '\'') {
            in_single_quote = true;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            continue;
        }
        if (ch == '(') {
            ++paren_balance;
        } else if (ch == ')') {
            --paren_balance;
        } else if (ch == '{') {
            ++brace_balance;
        } else if (ch == '}') {
            --brace_balance;
        }
    }

    if (paren_balance > 0 || brace_balance > 0) {
        return true;
    }

    if (trimmed.find("function(") != std::string_view::npos ||
        trimmed.find("= function(") != std::string_view::npos) {
        return true;
    }

    if (EndsWith(trimmed, ",") || EndsWith(trimmed, "{") || EndsWith(trimmed, "(")) {
        return true;
    }

    if (trimmed == "else" || trimmed == "elseif" || trimmed == "end" || trimmed == "repeat") {
        return true;
    }

    return false;
}

bool TryCommentReportedSyntaxLine(std::string& decompiled_source,
                                  std::string_view validation_error,
                                  std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    std::size_t line_index = 0;
    if (reported_line == 0) {
        line_index = lines.size() - 1;
    } else if (reported_line > lines.size()) {
        line_index = lines.size() - 1;
    } else {
        line_index = reported_line - 1;
    }

    std::size_t candidate = lines.size();
    for (std::size_t i = line_index + 1; i-- > 0;) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!trimmed.empty() && !StartsWith(trimmed, "--") &&
            !LooksUnsafeToCommentForSyntaxRepair(lines[i])) {
            candidate = i;
            break;
        }
        if (i == 0) {
            break;
        }
    }

    if (candidate == lines.size()) {
        for (std::size_t i = line_index + 1; i < lines.size(); ++i) {
            const std::string_view trimmed = TrimWhitespace(lines[i]);
            if (!trimmed.empty() && !StartsWith(trimmed, "--") &&
                !LooksUnsafeToCommentForSyntaxRepair(lines[i])) {
                candidate = i;
                break;
            }
        }
    }

    if (candidate == lines.size()) {
        return false;
    }

    std::string& line = lines[candidate];
    const std::string_view trimmed = TrimWhitespace(line);
    const std::size_t indent_end = line.find_first_not_of(" \t");
    const std::string indent =
        indent_end == std::string::npos ? std::string() : line.substr(0, indent_end);
    line = indent + "-- refii-invalid: " + std::string(trimmed);

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_by_commenting_invalid_lines");
    }
    return true;
}

bool TryRepairNativeCandidateSource(std::string& candidate_source,
                                    std::string* note,
                                    std::string_view note_prefix,
                                    std::string* validation_error_out) {
    std::string validation_error =
        validation_error_out != nullptr ? *validation_error_out
                                        : ValidateLuaSource(candidate_source,
                                                            "native_candidate");
    if (validation_error.empty()) {
        if (validation_error_out != nullptr) {
            *validation_error_out = validation_error;
        }
        return false;
    }

    const std::string original_source = candidate_source;
    bool tried_insert_before_reported_line = false;
    std::unordered_set<std::size_t> seen_states;
    for (int attempt = 0; attempt < 64 && !validation_error.empty(); ++attempt) {
        const std::size_t state_hash =
            std::hash<std::string>{}(validation_error) ^
            (std::hash<std::string>{}(candidate_source) + 0x9e3779b97f4a7c15ULL +
             (std::hash<std::string>{}(validation_error) << 6) +
             (std::hash<std::string>{}(validation_error) >> 2));
        if (!seen_states.insert(state_hash).second) {
            break;
        }

        bool changed = false;
        changed |= TryMaterializeMissingEndWarnings(candidate_source, note);
        changed |= TryMaterializeCommentedControlLines(candidate_source, note);
        changed |= TryStripDanglingEndBeforeElse(candidate_source, note);
        changed |= TryRemoveExcessEndLines(candidate_source, validation_error, note);
        changed |= TryStripEmptyElseBlocks(candidate_source, note);
        changed |= TryStripTopLevelLeakageAfterFunction(candidate_source, validation_error, note);
        changed |= TryStripDanglingDoBeforeElse(candidate_source, note);
        changed |= TryRewriteBareMethodReferenceAssignments(candidate_source, note);
        changed |= TryRewriteInvalidMethodFieldAssignments(candidate_source, note);
        changed |= TryRewriteForGeneratorElseArtifacts(candidate_source, note);
        changed |= TryRewriteDanglingRepeatLoops(candidate_source, note);
        changed |= TryRewriteWhileTrueToRepeat(candidate_source, validation_error, note);
        changed |= TryStripDanglingDoAfterUntil(candidate_source, note);
        changed |= TryRewriteDoReturnEndBranches(candidate_source, validation_error, note);
        changed |= TryInsertEndBeforeReturn(candidate_source, validation_error, note);
        changed |= TryInsertEndBeforeUntil(candidate_source, validation_error, note);
        changed |= TryInsertUntilBeforeTrailingEnd(candidate_source, validation_error, note);

        if (!changed && !tried_insert_before_reported_line &&
            TryInsertEndBeforeReportedLine(candidate_source, validation_error, note)) {
            tried_insert_before_reported_line = true;
            changed = true;
        }
        if (!changed &&
            TryInsertEndsBeforeTopLevelBoundaries(candidate_source, validation_error, note)) {
            changed = true;
        }
        if (!changed && TryInsertEndBeforeElse(candidate_source, validation_error, note)) {
            changed = true;
        }
        if (!changed && TryAppendMissingUntil(candidate_source, validation_error, note)) {
            changed = true;
        }
        if (!changed && TryBalanceOpenBlocks(candidate_source, validation_error, note)) {
            changed = true;
        }
        if (!changed && TryAppendMissingEnds(candidate_source, validation_error, note)) {
            changed = true;
        }

        if (!changed) {
            break;
        }

        validation_error = ValidateLuaSource(candidate_source, "native_candidate");
        if (validation_error.empty()) {
            break;
        }
    }

    if (validation_error_out != nullptr) {
        *validation_error_out = validation_error;
    }

    if (candidate_source == original_source) {
        return false;
    }

    if (note != nullptr && !note_prefix.empty()) {
        *note = JoinNotes(*note, std::string(note_prefix) + "_candidate_repaired");
        if (validation_error.empty()) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_candidate_validated");
        } else {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "_candidate_progress=" +
                                  SummarizeSyntaxError(validation_error));
        }
    }
    return true;
}

bool ValidateAndRepairDecompiledOutput(const std::filesystem::path& relative_input,
                                       std::string_view source_name,
                                       const std::vector<char>& input_bytes,
                                       const refii::lua::LuaWorkspaceDecompileOptions* options,
                                       const std::filesystem::path* input_path,
                                       const std::filesystem::path& output_path,
                                       const std::filesystem::path& failed_output_path,
                                       std::string* note) {
    std::string decompiled_source;
    if (!ReadTextFile(output_path, decompiled_source)) {
        if (note) {
            *note = JoinNotes(*note, "read_decompiled_output_failed");
        }
        return false;
    }
    StripLegacyDecompilerBanner(&decompiled_source, note);

    const std::string normalized_relative = NormalizePathString(relative_input.generic_string());
    const LuaOutputQualitySummary baseline_quality =
        SummarizeLuaOutputQuality(decompiled_source);
    std::string validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
    const bool baseline_has_blocking_quality =
        HasBlockingLuaOutputQualityIssues(baseline_quality) ||
        ContainsObviouslyInvalidNilUsage(decompiled_source);
    if (validation_error.empty() &&
        !HasBlockingLuaOutputQualityIssues(baseline_quality) &&
        !ContainsObviouslyInvalidNilUsage(decompiled_source)) {
        return FinalizeValidatedLuaOutput(decompiled_source,
                                          output_path,
                                          failed_output_path,
                                          note);
    }

    if (baseline_has_blocking_quality &&
        TryReplaceWithValidatedNativeMainChunk(input_bytes,
                                              decompiled_source,
                                              note,
                                              options,
                                              input_path)) {
        validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
        if (validation_error.empty()) {
            return FinalizeValidatedLuaOutput(decompiled_source,
                                             output_path,
                                             failed_output_path,
                                             note);
        }
    }

    bool retried_native_replacements = false;
    if (baseline_has_blocking_quality) {
        retried_native_replacements =
            TryReplaceDecompilerErrorFunctions(input_bytes,
                                               decompiled_source,
                                               note,
                                               false,
                                               options,
                                               input_path);
    }

    validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
    const LuaOutputQualitySummary current_quality =
        SummarizeLuaOutputQuality(decompiled_source);
    if (validation_error.empty()) {
        if ((HasBlockingLuaOutputQualityIssues(current_quality) ||
             ContainsObviouslyInvalidNilUsage(decompiled_source)) &&
            TryReplaceWithValidatedNativeMainChunk(input_bytes,
                                                  decompiled_source,
                                                  note,
                                                  options,
                                                  input_path)) {
            validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
            if (validation_error.empty()) {
                return FinalizeValidatedLuaOutput(decompiled_source,
                                                 output_path,
                                                 failed_output_path,
                                                 note);
            }
        }
        return FinalizeValidatedLuaOutput(decompiled_source,
                                          output_path,
                                          failed_output_path,
                                          note);
    }

    if (TryReplaceWithValidatedNativeMainChunk(input_bytes,
                                              decompiled_source,
                                              note,
                                              options,
                                              input_path)) {
        validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
        if (validation_error.empty()) {
            return FinalizeValidatedLuaOutput(decompiled_source,
                                             output_path,
                                             failed_output_path,
                                             note);
        }
    }

    bool tried_insert_before_reported_line = false;
    std::unordered_set<std::size_t> seen_states;
    for (int attempt = 0; attempt < 128 && !validation_error.empty(); ++attempt) {
        const std::size_t state_hash =
            std::hash<std::string>{}(validation_error) ^
            (std::hash<std::string>{}(decompiled_source) + 0x9e3779b97f4a7c15ULL +
             (std::hash<std::string>{}(validation_error) << 6) +
             (std::hash<std::string>{}(validation_error) >> 2));
        if (!seen_states.insert(state_hash).second) {
            if (note) {
                *note = JoinNotes(*note, "syntax_repair_cycle_detected");
            }
            break;
        }

        bool changed = false;

        if (TryMaterializeMissingEndWarnings(decompiled_source, note)) {
            changed = true;
        }
        if (TryMaterializeCommentedControlLines(decompiled_source, note)) {
            changed = true;
        }
        if (TryStripDanglingEndBeforeElse(decompiled_source, note)) {
            changed = true;
        }
        if (TryRemoveExcessEndLines(decompiled_source, validation_error, note)) {
            changed = true;
        }
        if (TryStripEmptyElseBlocks(decompiled_source, note)) {
            changed = true;
        }
        if (TryStripTopLevelLeakageAfterFunction(decompiled_source, validation_error, note)) {
            changed = true;
        }
        if (TryStripDanglingDoBeforeElse(decompiled_source, note)) {
            changed = true;
        }
        if (TryRewriteBareMethodReferenceAssignments(decompiled_source, note)) {
            changed = true;
        }
        if (TryRewriteInvalidMethodFieldAssignments(decompiled_source, note)) {
            changed = true;
        }
        if (TryRewriteForGeneratorElseArtifacts(decompiled_source, note)) {
            changed = true;
        }
        if (TryCommentForGeneratorArtifacts(decompiled_source, note)) {
            changed = true;
        }
        if (TryRewriteDanglingRepeatLoops(decompiled_source, note)) {
            changed = true;
        }
        if (TryRewriteWhileTrueToRepeat(decompiled_source, validation_error, note)) {
            changed = true;
        }
        if (TryStripDanglingDoAfterUntil(decompiled_source, note)) {
            changed = true;
        }
        if (TryRewriteDoReturnEndBranches(decompiled_source, validation_error, note)) {
            changed = true;
        }
        if (TryInsertEndBeforeReturn(decompiled_source, validation_error, note)) {
            changed = true;
        }
        if (TryInsertEndBeforeUntil(decompiled_source, validation_error, note)) {
            changed = true;
        }
        if (TryInsertUntilBeforeTrailingEnd(decompiled_source, validation_error, note)) {
            changed = true;
        }

        if (changed) {
            validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
            if (!validation_error.empty()) {
                const std::string before_native_retry = decompiled_source;
                if (TryReplaceDecompilerErrorFunctions(input_bytes,
                                                      decompiled_source,
                                                      note,
                                                      false,
                                                      options,
                                                      input_path) &&
                    decompiled_source != before_native_retry) {
                    retried_native_replacements = true;
                    validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
                }
            }
            if (validation_error.empty()) {
                return FinalizeValidatedLuaOutput(decompiled_source, output_path,
                                                 failed_output_path, note);
            }
            continue;
        }

        if (!tried_insert_before_reported_line &&
            TryInsertEndBeforeReportedLine(decompiled_source, validation_error, note)) {
            tried_insert_before_reported_line = true;
            validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
            if (validation_error.empty()) {
                return FinalizeValidatedLuaOutput(decompiled_source, output_path,
                                                 failed_output_path, note);
            }
            continue;
        }

        if (TryInsertEndsBeforeTopLevelBoundaries(decompiled_source, validation_error, note)) {
            validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
            if (validation_error.empty()) {
                return FinalizeValidatedLuaOutput(decompiled_source, output_path,
                                                 failed_output_path, note);
            }
            continue;
        }

        if (TryInsertEndBeforeElse(decompiled_source, validation_error, note)) {
            validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
            if (validation_error.empty()) {
                return FinalizeValidatedLuaOutput(decompiled_source, output_path,
                                                 failed_output_path, note);
            }
            continue;
        }

        if (TryAppendMissingUntil(decompiled_source, validation_error, note)) {
            validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
            if (validation_error.empty()) {
                return FinalizeValidatedLuaOutput(decompiled_source, output_path,
                                                 failed_output_path, note);
            }
            continue;
        }

        if (TryBalanceOpenBlocks(decompiled_source, validation_error, note)) {
            validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
            if (validation_error.empty()) {
                return FinalizeValidatedLuaOutput(decompiled_source, output_path,
                                                 failed_output_path, note);
            }
            continue;
        }

        if (TryAppendMissingEnds(decompiled_source, validation_error, note)) {
            validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
            if (validation_error.empty()) {
                return FinalizeValidatedLuaOutput(decompiled_source, output_path,
                                                 failed_output_path, note);
            }
            continue;
        }

        if (!TryCommentReportedSyntaxLine(decompiled_source, validation_error, note)) {
            break;
        }

        validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
        if (!validation_error.empty()) {
            const std::string before_native_retry = decompiled_source;
            if (TryReplaceDecompilerErrorFunctions(input_bytes,
                                                  decompiled_source,
                                                  note,
                                                  false,
                                                  options,
                                                  input_path) &&
                decompiled_source != before_native_retry) {
                retried_native_replacements = true;
                validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
            }
        }
        if (validation_error.empty()) {
            return FinalizeValidatedLuaOutput(decompiled_source, output_path,
                                             failed_output_path, note);
        }
    }

    const std::string before_native_retry = decompiled_source;
    if (TryReplaceDecompilerErrorFunctions(input_bytes,
                                          decompiled_source,
                                          note,
                                          true,
                                          options,
                                          input_path) &&
        decompiled_source != before_native_retry) {
        retried_native_replacements = true;
        validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
        if (validation_error.empty()) {
            return FinalizeValidatedLuaOutput(decompiled_source, output_path,
                                             failed_output_path, note);
        }
        decompiled_source = before_native_retry;
        validation_error = ValidateLuaSource(decompiled_source, normalized_relative);
        if (note) {
            *note = JoinNotes(*note, "native_full_retry_reverted_after_invalid_result");
        }
    }

    if (note) {
        *note = JoinNotes(*note, "syntax_invalid");
        *note = JoinNotes(*note, "syntax_error=" + SummarizeSyntaxError(validation_error));
    }
    if (!failed_output_path.empty()) {
        std::filesystem::create_directories(failed_output_path.parent_path());
        if (WriteNormalizedLuaTextFile(failed_output_path, std::move(decompiled_source), note)) {
            if (note) {
                *note = JoinNotes(
                    *note, "failed_output_path=" +
                               std::filesystem::path("_failed")
                                   .append(relative_input.generic_string())
                                   .generic_string());
            }
        } else if (note) {
            *note = JoinNotes(*note, "failed_output_write_failed");
        }
    }
    return false;
}

}  // namespace

namespace refii::lua {

std::filesystem::path GetDefaultLuaDecompiledOutputRoot(const std::filesystem::path& workspace_root) {
    return IsSourceWorkspaceRoot(workspace_root) ? workspace_root.parent_path() : workspace_root;
}

std::filesystem::path GetDefaultLuaDecompileInputRoot(const std::filesystem::path& workspace_root) {
    if (IsSourceWorkspaceRoot(workspace_root)) {
        return workspace_root;
    }

    const std::filesystem::path source_root = workspace_root / ".source";
    std::error_code error;
    if (std::filesystem::exists(source_root, error) && std::filesystem::is_directory(source_root, error)) {
        return source_root;
    }

    return workspace_root;
}

bool DecompileLuaWorkspace(const LuaWorkspaceDecompileOptions& raw_options,
                          LuaWorkspaceDecompileSummary* summary,
                          std::string* error) {
    try {
        LuaWorkspaceDecompileOptions options = raw_options;
        options.workspace_root =
            std::filesystem::absolute(GetDefaultLuaDecompileInputRoot(raw_options.workspace_root));
        if (options.output_root.empty()) {
            options.output_root = GetDefaultLuaDecompiledOutputRoot(options.workspace_root);
        } else {
            options.output_root = std::filesystem::absolute(options.output_root);
        }
        if (options.disassembly_output_root.empty()) {
            options.disassembly_output_root = options.output_root / "_disasm";
        } else {
            options.disassembly_output_root =
                std::filesystem::absolute(options.disassembly_output_root);
        }
        if (!options.single_tool_path.empty()) {
            options.single_tool_path = std::filesystem::absolute(options.single_tool_path);
        }
        if (options.progress_update_stride == 0) {
            options.progress_update_stride = 1;
        }

        if (!std::filesystem::is_directory(options.workspace_root)) {
            if (error) {
                *error = "workspace not found: " + options.workspace_root.string();
            }
            return false;
        }

#if defined(_WIN32)
        if (options.single_tool_path.empty() || !std::filesystem::exists(options.single_tool_path)) {
            if (error) {
                *error = "single-file decompiler not found: " + options.single_tool_path.string();
            }
            return false;
        }
#endif

        const std::string ignored_output_prefix =
            BuildIgnoredOutputPrefix(options.workspace_root, options.output_root);

        std::vector<std::filesystem::path> relative_inputs;
        for (std::filesystem::recursive_directory_iterator it(options.workspace_root), end; it != end;
             ++it) {
            if (!it->is_regular_file() || it->path().extension() != ".lua") {
                continue;
            }

            const std::filesystem::path relative =
                std::filesystem::relative(it->path(), options.workspace_root);
            const std::string normalized = NormalizePathString(relative.generic_string());
            if (StartsWith(normalized, "decompiled/")) {
                continue;
            }
            if (StartsWith(normalized, "_disasm/")) {
                continue;
            }
            if (StartsWith(normalized, ".cache/")) {
                continue;
            }
            if (!ignored_output_prefix.empty() && StartsWith(normalized, ignored_output_prefix)) {
                continue;
            }
            relative_inputs.push_back(relative);
        }

        std::sort(relative_inputs.begin(), relative_inputs.end(),
                  [](const std::filesystem::path& lhs, const std::filesystem::path& rhs) {
                      const int lhs_priority = GetInputPriority(lhs);
                      const int rhs_priority = GetInputPriority(rhs);
                      if (lhs_priority != rhs_priority) {
                          return lhs_priority < rhs_priority;
                      }
                      return NormalizePathString(lhs.generic_string()) <
                             NormalizePathString(rhs.generic_string());
                  });

        std::filesystem::create_directories(options.output_root);
        std::ofstream manifest(options.output_root / "_manifest.tsv",
                               std::ios::binary | std::ios::trunc);
        manifest << "status\tinput\toutput\tsource_name\tnote\n";
        manifest.flush();

        const std::filesystem::path cache_root = options.output_root / ".cache";
        const std::filesystem::path failed_output_root = options.output_root / "_failed";
        std::unordered_map<std::string, CacheEntry> cache_entries;

        LuaWorkspaceDecompileSummary local_summary;
        local_summary.workspace_root = options.workspace_root;
        local_summary.output_root = options.output_root;
        local_summary.manifest_path = options.output_root / "_manifest.tsv";
        local_summary.total_files = relative_inputs.size();

        auto emit_progress = [&](std::size_t completed,
                                 const std::filesystem::path& current_input,
                                 const char* phase,
                                 bool force) {
            if (!options.progress_callback) {
                return;
            }
            if (!force && options.progress_update_stride > 1 &&
                (completed % options.progress_update_stride) != 0) {
                return;
            }

            LuaWorkspaceDecompileProgress progress;
            progress.completed_files = completed;
            progress.total_files = local_summary.total_files;
            progress.current_input = current_input;
            progress.phase = phase;
            options.progress_callback(progress);
        };

        emit_progress(0, {}, "starting", true);

        std::size_t processed_files = 0;
        for (const std::filesystem::path& relative : relative_inputs) {
            emit_progress(processed_files, relative, "processing", false);

            const ResolvedInput resolved = ResolveBestInputPath(options.workspace_root, relative);
            const std::filesystem::path& input_path = resolved.path;
            const std::filesystem::path output_relative = MapOutputRelativePath(relative);
            const std::filesystem::path output_path = options.output_root / output_relative;
            const std::filesystem::path disassembly_output_path =
                options.disassembly_output_root / output_relative;
            std::filesystem::create_directories(output_path.parent_path());

            std::vector<char> bytes;
            if (!ReadFile(input_path, bytes)) {
                ++local_summary.skipped;
                std::error_code remove_error;
                std::filesystem::remove(disassembly_output_path, remove_error);
                AppendManifestRow(manifest, "skipped", relative, {}, {}, "read_failed");
                ++processed_files;
                if ((processed_files % kManifestFlushStride) == 0) {
                    manifest.flush();
                }
                emit_progress(processed_files, relative, "processed", false);
                continue;
            }

            const std::string source_name = DecodeSourceName(bytes);
            const std::string chunk_note = DescribeBytecodeChunk(bytes);
            if (!IsLuaBytecode(bytes)) {
                std::filesystem::copy_file(input_path, output_path,
                                           std::filesystem::copy_options::overwrite_existing);
                std::error_code remove_error;
                std::filesystem::remove(disassembly_output_path, remove_error);
                ++local_summary.copied;
                AppendManifestRow(manifest, "copied", relative, output_relative, source_name,
                                  resolved.note);
                ++processed_files;
                if ((processed_files % kManifestFlushStride) == 0) {
                    manifest.flush();
                }
                emit_progress(processed_files, relative, "processed", false);
                continue;
            }

            const std::string cache_key = MakeCacheKey(input_path);
            CacheEntry& entry = cache_entries[cache_key];
            const std::string input_note = JoinNotes(resolved.note, chunk_note);
            if (!entry.attempted) {
                entry.attempted = true;
                std::error_code relative_error;
                const std::filesystem::path source_relative =
                    std::filesystem::relative(input_path, options.workspace_root, relative_error);
                const std::filesystem::path cache_output =
                    cache_root / (relative_error ? output_relative : MapOutputRelativePath(source_relative));
                const std::filesystem::path disassembly_cache_output =
                    cache_root / "_disasm" /
                    (relative_error ? output_relative : MapOutputRelativePath(source_relative));
                const std::filesystem::path failed_output_path =
                    failed_output_root /
                    (relative_error ? output_relative : MapOutputRelativePath(source_relative));

                std::filesystem::create_directories(cache_output.parent_path());

                entry.note = input_note;
                std::error_code remove_error;
                std::filesystem::remove(cache_output, remove_error);
                decompiler::NativeMainChunkCandidate native_candidate;
                if (decompiler::TryRenderValidatedNativeMainChunk(
                        bytes,
                        NormalizePathString(relative.generic_string()),
                        &native_candidate)) {
                    entry.note = JoinNotes(entry.note, native_candidate.note);
                    if (TryFinalizeNativeCandidateOutput(relative,
                                                         std::move(native_candidate.source),
                                                         cache_output,
                                                         failed_output_path,
                                                         &entry.note,
                                                         "native_first_main_chunk")) {
                        entry.result = CacheEntry::Result::kDecompiled;
                        entry.output_path = cache_output;
                    } else {
                        entry.note = JoinNotes(entry.note, "native_first_candidate_rejected");
                        std::filesystem::remove(cache_output, remove_error);
                    }
                } else {
                    entry.note = JoinNotes(entry.note, native_candidate.note);
                }

                if (entry.result != CacheEntry::Result::kDecompiled) {
                    const int exit_code = RunDecompiler(options, input_path, cache_output, {});
                    if (exit_code == 0 && std::filesystem::exists(cache_output) &&
                        std::filesystem::file_size(cache_output) > 0) {
                        if (ValidateAndRepairDecompiledOutput(relative, source_name, bytes,
                                                              &options,
                                                              &input_path,
                                                              cache_output,
                                                              failed_output_path,
                                                              &entry.note)) {
                            entry.result = CacheEntry::Result::kDecompiled;
                            entry.output_path = cache_output;
                        } else {
                            std::error_code remove_error;
                            std::filesystem::remove(cache_output, remove_error);
                        }
                    } else {
                        std::filesystem::remove(cache_output, remove_error);
                        entry.note = JoinNotes(entry.note,
                                               DescribeRunResult("decompiler", exit_code));
                    }
                }

                if (entry.result != CacheEntry::Result::kDecompiled) {
                    if (options.write_disassembly_fallback) {
                        emit_progress(processed_files, relative, "disassembling_fallback", true);
                        std::filesystem::create_directories(disassembly_cache_output.parent_path());
                        const int disassembly_exit_code = RunDecompiler(
                            options, input_path, disassembly_cache_output, {"-dis"});
                        if (disassembly_exit_code == 0 &&
                            std::filesystem::exists(disassembly_cache_output) &&
                            std::filesystem::file_size(disassembly_cache_output) > 0) {
                            entry.result = CacheEntry::Result::kDisassembled;
                            entry.output_path = disassembly_cache_output;
                            entry.note = JoinNotes(entry.note, "disassembly_fallback");
                        } else {
                            std::error_code remove_error;
                            std::filesystem::remove(disassembly_cache_output, remove_error);
                            entry.note = JoinNotes(
                                entry.note,
                                DescribeRunResult("disassembler", disassembly_exit_code));
                        }
                    }
                }
            }

            if (entry.result == CacheEntry::Result::kDecompiled &&
                HasUsableDecompiledLuaOutput(entry.output_path, entry.note)) {
                if (!CopyNormalizedLuaTextFile(entry.output_path, output_path, &entry.note)) {
                    ++local_summary.skipped;
                    std::error_code remove_error;
                    std::filesystem::remove(output_path, remove_error);
                    std::filesystem::remove(disassembly_output_path, remove_error);
                    AppendManifestRow(
                        manifest, "skipped", relative, {}, source_name,
                        JoinNotes(entry.note, "copy_decompiled_output_failed"));
                    ++processed_files;
                    if ((processed_files % kManifestFlushStride) == 0) {
                        manifest.flush();
                    }
                    emit_progress(processed_files, relative, "processed", false);
                    continue;
                }
                std::error_code remove_error;
                std::filesystem::remove(disassembly_output_path, remove_error);
                const bool was_aliased =
                    (MakeCacheKey(input_path) != MakeCacheKey(options.workspace_root / relative));
                if (was_aliased) {
                    ++local_summary.aliased;
                    AppendManifestRow(manifest, "aliased", relative, output_relative, source_name,
                                      entry.note);
                } else {
                    ++local_summary.decompiled;
                    AppendManifestRow(manifest, "decompiled", relative, output_relative, source_name,
                                      entry.note);
                }
            } else if (entry.result == CacheEntry::Result::kDisassembled &&
                       std::filesystem::exists(entry.output_path) &&
                       std::filesystem::file_size(entry.output_path) > 0) {
                const std::filesystem::path disassembly_output_relative =
                    std::filesystem::path("_disasm") / output_relative;
                std::filesystem::create_directories(disassembly_output_path.parent_path());
                std::filesystem::copy_file(entry.output_path, disassembly_output_path,
                                           std::filesystem::copy_options::overwrite_existing);
                std::string note = entry.note;
                note = JoinNotes(note, "disassembly_path=" + disassembly_output_relative.generic_string());
                if (WriteTextFile(output_path,
                                  BuildDisassemblyPlaceholder(output_relative,
                                                             disassembly_output_relative,
                                                             source_name))) {
                    note = JoinNotes(note, "placeholder_written");
                } else {
                    std::error_code remove_error;
                    std::filesystem::remove(output_path, remove_error);
                    note = JoinNotes(note, "placeholder_write_failed");
                }
                ++local_summary.disassembled;
                AppendManifestRow(manifest, "disassembled", relative, output_relative,
                                  source_name, note);
            } else {
                ++local_summary.skipped;
                std::error_code remove_error;
                std::filesystem::remove(output_path, remove_error);
                std::filesystem::remove(disassembly_output_path, remove_error);
                AppendManifestRow(
                    manifest, "skipped", relative, {}, source_name,
                    entry.note.empty() ? std::string("decompiler_failed") : entry.note);
            }

            ++processed_files;
            if ((processed_files % kManifestFlushStride) == 0) {
                manifest.flush();
            }
            emit_progress(processed_files, relative, "processed", false);
        }

        manifest.flush();
        std::error_code cleanup_error;
        std::filesystem::remove_all(cache_root, cleanup_error);
        emit_progress(processed_files, {}, "done", true);

        if (summary != nullptr) {
            *summary = std::move(local_summary);
        }
        return true;
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }
}

}  // namespace refii::lua
