#pragma once

#include "tools/bytecode/lua_bytecode_oracles.h"

#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::decompiler {

struct NativeLuaReadabilitySummary {
    std::size_t nonempty_line_count = 0;
    std::size_t temporary_identifier_count = 0;
    std::size_t unique_temporary_identifier_count = 0;
    std::size_t temporary_leading_line_count = 0;
    std::size_t temporary_local_declaration_count = 0;
    std::size_t temporary_assignment_count = 0;
    std::size_t temporary_table_field_assignment_count = 0;
    std::size_t temporary_self_reference_assignment_count = 0;
    std::size_t multi_temporary_token_line_count = 0;
    std::size_t suspicious_self_call_assignment_count = 0;
    std::size_t suspicious_temporary_rhs_assignment_count = 0;
    std::size_t obvious_nil_call_count = 0;
    std::size_t risk_score = 0;
};

struct NativeMainChunkCandidate {
    std::string source;
    std::string note;
    bool used_raw_renderer = false;
    bool syntax_validated = false;
    bool parity_checked = false;
    bool exact_parity = false;
    bool main_stats_exact = false;
    bool full_stats_exact = false;
    NativeLuaReadabilitySummary readability;
    refii::lua::audit::FunctionMismatchInfo first_mismatch;
    refii::lua::audit::CompiledLuaChunkInfo compiled_candidate;
};

bool TryRenderValidatedNativeMainChunk(const std::vector<char>& input_bytes,
                                       std::string_view chunk_name,
                                       NativeMainChunkCandidate* candidate);

bool TryApplyBytecodeBackedMainChunkRestorations(const std::vector<char>& input_bytes,
                                                 std::string_view chunk_name,
                                                 std::string* source,
                                                 std::string* note,
                                                 std::string_view note_prefix);

bool TryRepairValidatedCandidateFromRaw(const std::vector<char>& input_bytes,
                                        std::string_view chunk_name,
                                        std::string_view raw_source,
                                        std::string* clean_source,
                                        std::string* note,
                                        std::string_view note_prefix);

bool CouldValidatedCandidateBenefitFromRawRepair(std::string_view clean_source);

}  // namespace refii::lua::decompiler
