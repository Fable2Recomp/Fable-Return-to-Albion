#include "tools/lua_bytecode_oracles.h"

#include "tools/decompiler/lua51_analysis.h"
#include "tools/lua_decompiler_internal.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <iomanip>
#include <sstream>

#ifdef __cplusplus
extern "C" {
#endif
#include "lauxlib.h"
#include "lua.h"
#ifdef __cplusplus
}
#endif

#if !defined(_WIN32)
#include <fcntl.h>
#include <signal.h>
#include <sys/wait.h>
#include <unistd.h>
#endif

namespace refii::lua::audit {

namespace {

constexpr int kRunTimedOut = -2;
constexpr int kRunSpawnFailed = -3;
constexpr int kRunSignaledBase = -1000;

void AppendFunctionsPreorderIncludingMain(const refii::lua::Lua51Function& function,
                                          std::vector<const refii::lua::Lua51Function*>* out) {
    if (out == nullptr) {
        return;
    }
    out->push_back(&function);
    for (const refii::lua::Lua51Function& child : function.child_functions) {
        AppendFunctionsPreorderIncludingMain(child, out);
    }
}

bool Lua51ConstantEqual(const refii::lua::Lua51Constant& lhs, const refii::lua::Lua51Constant& rhs) {
    if (lhs.type != rhs.type) {
        return false;
    }
    switch (lhs.type) {
        case refii::lua::Lua51ConstantType::kNil:
            return true;
        case refii::lua::Lua51ConstantType::kBoolean:
            return lhs.boolean_value == rhs.boolean_value;
        case refii::lua::Lua51ConstantType::kNumber:
            return lhs.number_value == rhs.number_value;
        case refii::lua::Lua51ConstantType::kString:
            return lhs.string_value == rhs.string_value;
    }
    return false;
}

std::string DescribeLua51ConstantForMismatch(const refii::lua::Lua51Constant& constant) {
    std::ostringstream stream;
    switch (constant.type) {
        case refii::lua::Lua51ConstantType::kNil:
            stream << "nil";
            break;
        case refii::lua::Lua51ConstantType::kBoolean:
            stream << (constant.boolean_value ? "true" : "false");
            break;
        case refii::lua::Lua51ConstantType::kNumber:
            stream << std::setprecision(17) << constant.number_value;
            break;
        case refii::lua::Lua51ConstantType::kString:
            stream << '"' << constant.string_value << '"';
            break;
    }
    return stream.str();
}

bool Lua51LocalEqual(const refii::lua::Lua51Local& lhs, const refii::lua::Lua51Local& rhs) {
    return lhs.name == rhs.name && lhs.start_pc == rhs.start_pc && lhs.end_pc == rhs.end_pc;
}

std::string DescribeInstructionReferenceForMismatch(const refii::lua::Lua51Function& function,
                                                    const refii::lua::Lua51Instruction& instruction) {
    std::ostringstream stream;
    stream << std::string(refii::lua::DescribeLua51Opcode(instruction.opcode));
    stream << "(a=" << instruction.a;
    switch (instruction.opcode) {
        case refii::lua::Lua51Opcode::kLoadK:
        case refii::lua::Lua51Opcode::kGetGlobal:
        case refii::lua::Lua51Opcode::kSetGlobal:
            stream << ",bx=" << instruction.bx;
            if (instruction.bx >= 0 &&
                static_cast<std::size_t>(instruction.bx) < function.constants.size()) {
                stream << ",k=" << DescribeLua51ConstantForMismatch(
                    function.constants[static_cast<std::size_t>(instruction.bx)]);
            }
            break;
        case refii::lua::Lua51Opcode::kClosure:
            stream << ",bx=" << instruction.bx;
            if (instruction.bx >= 0 &&
                static_cast<std::size_t>(instruction.bx) < function.child_functions.size()) {
                stream << ",child_line="
                       << function.child_functions[static_cast<std::size_t>(instruction.bx)].line_defined;
            }
            break;
        case refii::lua::Lua51Opcode::kJmp:
        case refii::lua::Lua51Opcode::kForLoop:
        case refii::lua::Lua51Opcode::kForPrep:
            stream << ",sbx=" << instruction.sbx;
            break;
        default:
            stream << ",b=" << instruction.b << ",c=" << instruction.c;
            break;
    }
    stream << ",raw=0x" << std::hex << std::uppercase << instruction.raw << std::dec << ')';
    return stream.str();
}

int DumpCompiledChunkWriter(lua_State*, const void* data, size_t size, void* user_data) {
    auto* bytes = static_cast<std::vector<char>*>(user_data);
    if (bytes == nullptr || data == nullptr) {
        return 1;
    }
    const char* begin = static_cast<const char*>(data);
    bytes->insert(bytes->end(), begin, begin + size);
    return 0;
}

std::filesystem::path MakeAuditTempOutputPath(const std::filesystem::path& input_path,
                                              std::string_view prefix,
                                              std::string_view extension) {
    static std::uint64_t counter = 0;
#if defined(_WIN32)
    const auto process_id = static_cast<long long>(::_getpid());
#else
    const auto process_id = static_cast<long long>(::getpid());
#endif
    std::filesystem::path output = std::filesystem::temp_directory_path() /
                                   (std::string(prefix) + "-" + std::to_string(process_id) + "-" +
                                    std::to_string(++counter) + "-" +
                                    input_path.filename().generic_string());
    output += std::string(extension);
    return output;
}

#if !defined(_WIN32)
int RunExternalCommandToFile(const std::vector<std::string>& arguments,
                             const std::filesystem::path& output_path,
                             unsigned int timeout_seconds) {
    if (arguments.empty()) {
        return kRunSpawnFailed;
    }

    std::fflush(nullptr);
    const int output_fd = ::open(output_path.c_str(), O_CREAT | O_TRUNC | O_WRONLY, 0666);
    if (output_fd < 0) {
        return kRunSpawnFailed;
    }
    const int devnull = ::open("/dev/null", O_WRONLY);
    if (devnull < 0) {
        ::close(output_fd);
        return kRunSpawnFailed;
    }

    const pid_t pid = ::fork();
    if (pid < 0) {
        ::close(output_fd);
        ::close(devnull);
        return kRunSpawnFailed;
    }

    if (pid == 0) {
        ::dup2(output_fd, STDOUT_FILENO);
        ::dup2(devnull, STDERR_FILENO);
        ::close(output_fd);
        ::close(devnull);

        std::vector<char*> argv;
        argv.reserve(arguments.size() + 1);
        for (const std::string& argument : arguments) {
            argv.push_back(const_cast<char*>(argument.c_str()));
        }
        argv.push_back(nullptr);
        ::execvp(argv[0], argv.data());
        _exit(127);
    }

    ::close(output_fd);
    ::close(devnull);

    int status = 0;
    if (timeout_seconds == 0) {
        if (::waitpid(pid, &status, 0) < 0) {
            return kRunSpawnFailed;
        }
    } else {
        const auto deadline = std::chrono::steady_clock::now() +
                              std::chrono::seconds(timeout_seconds);
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
        if (WEXITSTATUS(status) == 127) {
            return kRunSpawnFailed;
        }
        return WEXITSTATUS(status);
    }
    if (WIFSIGNALED(status)) {
        return kRunSignaledBase - WTERMSIG(status);
    }
    return kRunSpawnFailed;
}
#else
int RunExternalCommandToFile(const std::vector<std::string>&,
                             const std::filesystem::path&,
                             unsigned int) {
    return kRunSpawnFailed;
}
#endif

bool Lua51ConstantsEquivalent(const refii::lua::Lua51Constant& lhs,
                              const refii::lua::Lua51Constant& rhs) {
    if (lhs.type != rhs.type) {
        return false;
    }

    switch (lhs.type) {
        case refii::lua::Lua51ConstantType::kNil:
            return true;
        case refii::lua::Lua51ConstantType::kBoolean:
            return lhs.boolean_value == rhs.boolean_value;
        case refii::lua::Lua51ConstantType::kNumber:
            return lhs.number_value == rhs.number_value;
        case refii::lua::Lua51ConstantType::kString:
            return lhs.string_value == rhs.string_value;
    }

    return false;
}

bool Lua51FunctionBytecodeExact(const refii::lua::Lua51Function& original,
                                const refii::lua::Lua51Function& candidate) {
    if (original.upvalue_count != candidate.upvalue_count ||
        original.parameter_count != candidate.parameter_count ||
        original.vararg_flags != candidate.vararg_flags ||
        original.max_stack_size != candidate.max_stack_size ||
        original.code != candidate.code ||
        original.constants.size() != candidate.constants.size() ||
        original.child_functions.size() != candidate.child_functions.size()) {
        return false;
    }

    for (std::size_t index = 0; index < original.constants.size(); ++index) {
        if (!Lua51ConstantsEquivalent(original.constants[index], candidate.constants[index])) {
            return false;
        }
    }

    for (std::size_t index = 0; index < original.child_functions.size(); ++index) {
        if (!Lua51FunctionBytecodeExact(original.child_functions[index],
                                        candidate.child_functions[index])) {
            return false;
        }
    }

    return true;
}

bool Lua51ChunkBytecodeExact(const refii::lua::Lua51Chunk& original,
                             const refii::lua::Lua51Chunk& candidate) {
    return Lua51FunctionBytecodeExact(original.main, candidate.main);
}

}  // namespace

Lua51FunctionStats SummarizeLua51FunctionStats(const refii::lua::Lua51Function& function) {
    Lua51FunctionStats stats;
    stats.instruction_count = function.code.size();
    stats.local_count = function.locals.size();
    stats.constant_count = function.constants.size();
    stats.string_constant_count = std::count_if(
        function.constants.begin(),
        function.constants.end(),
        [](const refii::lua::Lua51Constant& constant) {
            return constant.type == refii::lua::Lua51ConstantType::kString;
        });
    stats.child_function_count = function.child_functions.size();
    stats.upvalue_count = function.upvalue_count;
    stats.parameter_count = function.parameter_count;
    stats.vararg_flags = function.vararg_flags;
    stats.max_stack_size = function.max_stack_size;
    return stats;
}

bool Lua51FunctionStatsEqual(const Lua51FunctionStats& lhs, const Lua51FunctionStats& rhs) {
    return lhs.instruction_count == rhs.instruction_count &&
           lhs.local_count == rhs.local_count &&
           lhs.constant_count == rhs.constant_count &&
           lhs.string_constant_count == rhs.string_constant_count &&
           lhs.child_function_count == rhs.child_function_count &&
           lhs.upvalue_count == rhs.upvalue_count &&
           lhs.parameter_count == rhs.parameter_count &&
           lhs.vararg_flags == rhs.vararg_flags &&
           lhs.max_stack_size == rhs.max_stack_size;
}

std::string DescribeLua51FunctionStats(const Lua51FunctionStats& stats) {
    std::ostringstream stream;
    stream << "instructions=" << stats.instruction_count;
    stream << ";locals=" << stats.local_count;
    stream << ";constants=" << stats.constant_count;
    stream << ";string_constants=" << stats.string_constant_count;
    stream << ";child_functions=" << stats.child_function_count;
    stream << ";upvalues=" << static_cast<int>(stats.upvalue_count);
    stream << ";params=" << static_cast<int>(stats.parameter_count);
    stream << ";vararg_flags=" << static_cast<int>(stats.vararg_flags);
    stream << ";max_stack=" << static_cast<int>(stats.max_stack_size);
    return stream.str();
}

std::string DescribeLua51ChunkStatsWithBytes(const refii::lua::Lua51ChunkStats& stats,
                                             std::uintmax_t byte_size) {
    std::string description = refii::lua::DescribeLua51ChunkStats(stats);
    if (!description.empty()) {
        description.append(";");
    }
    description.append("chunk_bytes=");
    description.append(std::to_string(byte_size));
    return description;
}

FunctionMismatchInfo FindFirstFunctionMismatch(const refii::lua::Lua51Chunk& original,
                                               const refii::lua::Lua51Chunk& candidate) {
    std::vector<const refii::lua::Lua51Function*> original_functions;
    std::vector<const refii::lua::Lua51Function*> candidate_functions;
    AppendFunctionsPreorderIncludingMain(original.main, &original_functions);
    AppendFunctionsPreorderIncludingMain(candidate.main, &candidate_functions);

    const std::size_t count = std::min(original_functions.size(), candidate_functions.size());
    for (std::size_t index = 0; index < count; ++index) {
        const Lua51FunctionStats original_stats =
            SummarizeLua51FunctionStats(*original_functions[index]);
        const Lua51FunctionStats candidate_stats =
            SummarizeLua51FunctionStats(*candidate_functions[index]);
        if (Lua51FunctionStatsEqual(original_stats, candidate_stats)) {
            const std::vector<refii::lua::Lua51Instruction> original_instructions =
                refii::lua::DecodeLua51Instructions(*original_functions[index]);
            const std::vector<refii::lua::Lua51Instruction> candidate_instructions =
                refii::lua::DecodeLua51Instructions(*candidate_functions[index]);
            const std::size_t instruction_count =
                std::min(original_instructions.size(), candidate_instructions.size());
            for (std::size_t instruction_index = 0; instruction_index < instruction_count;
                 ++instruction_index) {
                if (original_instructions[instruction_index].raw ==
                    candidate_instructions[instruction_index].raw) {
                    continue;
                }
                FunctionMismatchInfo mismatch;
                mismatch.available = true;
                mismatch.function_index = index;
                mismatch.line_defined = original_functions[index]->line_defined;
                std::ostringstream note;
                note << "instruction_diff pc=" << (instruction_index + 1) << " "
                     << DescribeInstructionReferenceForMismatch(*original_functions[index],
                                                               original_instructions[instruction_index])
                     << " -> "
                     << DescribeInstructionReferenceForMismatch(*candidate_functions[index],
                                                               candidate_instructions[instruction_index]);
                mismatch.note = note.str();
                return mismatch;
            }

            const std::size_t constant_count = std::min(original_functions[index]->constants.size(),
                                                        candidate_functions[index]->constants.size());
            for (std::size_t constant_index = 0; constant_index < constant_count; ++constant_index) {
                if (Lua51ConstantEqual(original_functions[index]->constants[constant_index],
                                       candidate_functions[index]->constants[constant_index])) {
                    continue;
                }
                FunctionMismatchInfo mismatch;
                mismatch.available = true;
                mismatch.function_index = index;
                mismatch.line_defined = original_functions[index]->line_defined;
                std::ostringstream note;
                note << "constant_diff k=" << constant_index << " "
                     << DescribeLua51ConstantForMismatch(
                            original_functions[index]->constants[constant_index])
                     << " -> "
                     << DescribeLua51ConstantForMismatch(
                            candidate_functions[index]->constants[constant_index]);
                mismatch.note = note.str();
                return mismatch;
            }

            const std::size_t local_count =
                std::min(original_functions[index]->locals.size(), candidate_functions[index]->locals.size());
            for (std::size_t local_index = 0; local_index < local_count; ++local_index) {
                if (Lua51LocalEqual(original_functions[index]->locals[local_index],
                                    candidate_functions[index]->locals[local_index])) {
                    continue;
                }
                FunctionMismatchInfo mismatch;
                mismatch.available = true;
                mismatch.function_index = index;
                mismatch.line_defined = original_functions[index]->line_defined;
                std::ostringstream note;
                note << "local_diff idx=" << local_index << " "
                     << original_functions[index]->locals[local_index].name << "@"
                     << original_functions[index]->locals[local_index].start_pc << "-"
                     << original_functions[index]->locals[local_index].end_pc << " -> "
                     << candidate_functions[index]->locals[local_index].name << "@"
                     << candidate_functions[index]->locals[local_index].start_pc << "-"
                     << candidate_functions[index]->locals[local_index].end_pc;
                mismatch.note = note.str();
                return mismatch;
            }

            continue;
        }

        FunctionMismatchInfo mismatch;
        mismatch.available = true;
        mismatch.function_index = index;
        mismatch.line_defined = original_functions[index]->line_defined;
        mismatch.instruction_delta =
            static_cast<long long>(candidate_stats.instruction_count) -
            static_cast<long long>(original_stats.instruction_count);
        mismatch.constant_delta =
            static_cast<long long>(candidate_stats.constant_count) -
            static_cast<long long>(original_stats.constant_count);
        mismatch.local_delta = static_cast<long long>(candidate_stats.local_count) -
                               static_cast<long long>(original_stats.local_count);
        mismatch.note = "function_stats_differ";
        return mismatch;
    }

    if (original_functions.size() != candidate_functions.size()) {
        FunctionMismatchInfo mismatch;
        mismatch.available = true;
        mismatch.function_index = count;
        mismatch.line_defined = count < original_functions.size() ? original_functions[count]->line_defined
                                                                  : 0;
        mismatch.note = "function_count_differ";
        return mismatch;
    }

    return {};
}

std::string DescribeMismatchForSummary(const FunctionMismatchInfo& mismatch) {
    if (!mismatch.available) {
        return "n/a";
    }
    std::ostringstream stream;
    if (mismatch.function_index == 0) {
        stream << "main";
    } else {
        stream << "f" << mismatch.function_index;
    }
    if (mismatch.line_defined != 0) {
        stream << " @line " << mismatch.line_defined;
    }
    if (!mismatch.note.empty()) {
        stream << " [" << mismatch.note << "]";
    }
    if (mismatch.note != "function_count_differ") {
        stream << " inst=" << (mismatch.instruction_delta >= 0 ? "+" : "")
               << mismatch.instruction_delta;
        stream << " const=" << (mismatch.constant_delta >= 0 ? "+" : "")
               << mismatch.constant_delta;
        stream << " locals=" << (mismatch.local_delta >= 0 ? "+" : "")
               << mismatch.local_delta;
    }
    return stream.str();
}

bool CompileLuaSourceToChunk(std::string_view source,
                             std::string_view chunk_name,
                             CompiledLuaChunkInfo* output) {
    if (output == nullptr) {
        return false;
    }

    *output = {};
    output->source_available = true;

    lua_State* state = luaL_newstate();
    if (state == nullptr) {
        output->error = "compiler_init_failed";
        return false;
    }

    const std::string name(chunk_name.empty() ? "decompiled_chunk" : std::string(chunk_name));
    const int load_status = luaL_loadbuffer(state, source.data(), source.size(), name.c_str());
    if (load_status != 0) {
        output->syntax_valid = false;
        if (const char* message = lua_tostring(state, -1); message != nullptr) {
            output->error = message;
        } else {
            output->error = "syntax_invalid";
        }
        lua_close(state);
        return false;
    }

    output->syntax_valid = true;
    if (lua_dump(state, DumpCompiledChunkWriter, &output->bytes) != 0) {
        output->error = "lua_dump_failed";
        lua_close(state);
        return false;
    }
    lua_close(state);

    output->byte_size = output->bytes.size();
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(output->bytes, &output->chunk, &parse_error)) {
        output->chunk_parsed = false;
        output->error = parse_error.empty() ? "chunk_parse_failed" : parse_error;
        return false;
    }

    output->chunk_parsed = true;
    output->full_stats = refii::lua::SummarizeLua51Chunk(output->chunk);
    output->main_stats = SummarizeLua51FunctionStats(output->chunk.main);
    return true;
}

bool BuildLuaBytecodeParityReference(const std::vector<char>& original_bytes,
                                     LuaBytecodeParityReference* output) {
    if (output == nullptr) {
        return false;
    }

    *output = {};
    if (original_bytes.empty()) {
        return false;
    }

    const refii::lua::internal::ParsedChunkCacheEntry& cache_entry =
        refii::lua::internal::GetParsedChunkCacheEntry(original_bytes);
    if (!cache_entry.success) {
        return false;
    }

    output->available = true;
    output->chunk = cache_entry.chunk;
    output->full_stats = cache_entry.chunk_stats;
    output->main_stats = SummarizeLua51FunctionStats(cache_entry.chunk.main);
    output->byte_size = original_bytes.size();
    return true;
}

bool EvaluateCompiledLuaChunkBytecodeParity(const LuaBytecodeParityReference& reference,
                                            const CompiledLuaChunkInfo& compiled_candidate,
                                            LuaBytecodeParityResult* output) {
    if (output == nullptr) {
        return false;
    }

    // `compiled_candidate` may alias `output->compiled_candidate` when callers
    // forward the result object back through this helper. Snapshot it before
    // resetting `*output`, otherwise we can zero out the very candidate we are
    // about to compare.
    const CompiledLuaChunkInfo candidate_snapshot = compiled_candidate;

    *output = {};
    output->reference_available = reference.available;
    output->candidate_compiled = candidate_snapshot.syntax_valid;
    output->candidate_chunk_parsed = candidate_snapshot.chunk_parsed;
    output->compiled_candidate = candidate_snapshot;
    output->error = candidate_snapshot.error;
    if (!reference.available || !candidate_snapshot.chunk_parsed) {
        return false;
    }

    output->main_stats_exact = Lua51FunctionStatsEqual(reference.main_stats,
                                                       candidate_snapshot.main_stats);
    output->full_stats_exact =
        reference.full_stats.function_count == candidate_snapshot.full_stats.function_count &&
        reference.full_stats.instruction_count == candidate_snapshot.full_stats.instruction_count &&
        reference.full_stats.local_count == candidate_snapshot.full_stats.local_count &&
        reference.full_stats.constant_count == candidate_snapshot.full_stats.constant_count &&
        reference.full_stats.string_constant_count ==
            candidate_snapshot.full_stats.string_constant_count;
    output->exact_bytecode = Lua51ChunkBytecodeExact(reference.chunk, candidate_snapshot.chunk);
    output->first_mismatch = FindFirstFunctionMismatch(reference.chunk, candidate_snapshot.chunk);
    if (!output->exact_bytecode && !output->first_mismatch.available) {
        output->first_mismatch.available = true;
        output->first_mismatch.note = "exact_bytecode_differs";
    }
    return true;
}

bool EvaluateLuaSourceBytecodeParity(const LuaBytecodeParityReference& reference,
                                     std::string_view source,
                                     std::string_view chunk_name,
                                     LuaBytecodeParityResult* output) {
    if (output == nullptr) {
        return false;
    }

    *output = {};
    output->reference_available = reference.available;
    if (!reference.available) {
        return false;
    }

    bool cache_compile_ok =
        refii::lua::internal::CompileLuaSourceToChunkCached(source,
                                                            chunk_name,
                                                            &output->compiled_candidate);
    const bool suspicious_cached_candidate =
        output->compiled_candidate.error.empty() &&
        (!output->compiled_candidate.syntax_valid || !output->compiled_candidate.chunk_parsed ||
         output->compiled_candidate.bytes.empty() ||
         output->compiled_candidate.byte_size == 0);
    if (!cache_compile_ok || suspicious_cached_candidate) {
        CompiledLuaChunkInfo direct_candidate;
        const bool direct_compile_ok =
            CompileLuaSourceToChunk(source, chunk_name, &direct_candidate);
        if (direct_compile_ok || !direct_candidate.error.empty()) {
            output->compiled_candidate = std::move(direct_candidate);
            cache_compile_ok = direct_compile_ok;
        }
    }

    if (!cache_compile_ok) {
        output->candidate_compiled = false;
        output->error = output->compiled_candidate.error;
        return false;
    }

    output->candidate_compiled = output->compiled_candidate.syntax_valid;
    output->candidate_chunk_parsed = output->compiled_candidate.chunk_parsed;
    output->error = output->compiled_candidate.error;
    return EvaluateCompiledLuaChunkBytecodeParity(reference, output->compiled_candidate, output);
}

bool TryRenderUnluacWholeFile(const refii::lua::LuaBytecodeParityAuditOptions& options,
                              const std::filesystem::path& input_path,
                              std::string* rendered_source,
                              std::string* note) {
    if (rendered_source == nullptr) {
        return false;
    }

    std::vector<std::string> arguments;
    const std::filesystem::path oracle_path = options.oracle_unluac_path;
    if (internal::NormalizePathString(oracle_path.extension().generic_string()) == ".jar") {
        arguments = {"java", "-jar", oracle_path.string(), input_path.string()};
    } else {
        arguments = {oracle_path.string(), input_path.string()};
    }

    const std::filesystem::path temp_output =
        MakeAuditTempOutputPath(input_path, "refii-parity-unluac", ".lua");
    std::error_code cleanup_error;
    const int exit_code =
        RunExternalCommandToFile(arguments, temp_output, options.child_timeout_seconds);
    if (exit_code != 0 || !std::filesystem::exists(temp_output) ||
        std::filesystem::file_size(temp_output) == 0) {
        std::filesystem::remove(temp_output, cleanup_error);
        if (note != nullptr) {
            *note =
                internal::JoinNotes(*note, "unluac_oracle_" + internal::DescribeRunResult("render", exit_code));
        }
        return false;
    }

    std::string text;
    const bool read_ok = internal::ReadTextFile(temp_output, text);
    std::filesystem::remove(temp_output, cleanup_error);
    if (!read_ok) {
        if (note != nullptr) {
            *note = internal::JoinNotes(*note, "unluac_oracle_read_failed");
        }
        return false;
    }

    *rendered_source = std::move(text);
    return true;
}

}  // namespace refii::lua::audit
