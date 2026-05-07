#include "tools/decompiler/native_first.h"

#include "tools/decompiler/branch_preview_render.h"
#include "tools/decompiler/condition_render.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/operation_build.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/native_first_readability_policy.h"
#include "tools/decompiler/straight_line_render.h"
#include "tools/decompiler/lua_decompiler_internal.h"
#include "tools/native/native_lua_chunk.h"
#include "tools/native/straight_line_native_render.h"

#include <optional>
#include <sstream>
#include <string_view>
#include <cctype>
#include <unordered_map>
#include <unordered_set>

#ifdef __cplusplus
extern "C" {
#endif
#include "lauxlib.h"
#include "lua.h"
#ifdef __cplusplus
}
#endif

#include <algorithm>
namespace refii::lua::decompiler {
namespace {

constexpr std::string_view kLuaSignature = "\x1bLua";
using LuaReadabilitySummary = NativeLuaReadabilitySummary;

struct RenderedFunctionBlock {
    std::string header_line;
    std::size_t begin = 0;
    std::size_t end = 0;
};

struct RenderedCallResultPattern {
    int target_reg = -1;
    std::size_t instruction_index = 0;
    std::string expression;
    bool tested_immediately = false;
};

struct RenderedDirectStatementCallPattern {
    std::size_t instruction_index = 0;
    std::string expression;
    std::string callee;
    std::string terminal_callee;
    std::size_t argument_count = 0;
};

struct RenderedComparisonSwapPattern {
    std::string expression;
    std::string swapped_expression;
    std::string normalized_lhs;
    std::string comparator;
    std::string normalized_rhs;
};

struct RenderedSiblingElseIfPattern {
    std::string first_condition;
    std::string second_condition;
};

struct RenderedTemporaryConditionPattern {
    int reg = -1;
    bool negate = false;
};

void AppendLuaFunctionChildrenPreorder(const Lua51Function& function,
                                       std::vector<const Lua51Function*>* out) {
    if (out == nullptr) {
        return;
    }
    for (const Lua51Function& child : function.child_functions) {
        out->push_back(&child);
        AppendLuaFunctionChildrenPreorder(child, out);
    }
}

LuaReadabilitySummary SummarizeLuaReadability(std::string_view source);
bool ParseRenderedTemporaryMultiAssignment(std::string_view line,
                                          std::vector<std::string>* names,
                                          std::string* rhs);
bool ParseSingleRenderedAssignment(std::string_view line,
                                   std::string* lhs,
                                   std::string* rhs);
bool ParseSingleRenderedAssignment(std::string_view line,
                                   bool* is_local,
                                   std::string* lhs,
                                   std::string* rhs);
bool TryExtractRenderedIfConditionLocal(std::string_view line, std::string* condition);
bool ParseRenderedSingleCallExpression(std::string_view line,
                                       std::string* callee,
                                       std::vector<std::string>* arguments);
bool SplitRenderedTopLevelCommaChainLocal(std::string_view value,
                                          std::vector<std::string>* parts);
bool ParseCallFirstArgument(std::string_view expression,
                            std::string* callee,
                            std::string* first_argument);
std::string NormalizeRenderedTrivialParenthesizedLiterals(std::string_view text);
std::string ExtractRenderedCallTerminal(std::string_view expression);
bool LooksLikeRenderedCallExpression(std::string_view expression);
bool TryRestoreBytecodeBooleanReturnWrapperFunctionBlocks(const Lua51Function& function,
                                                          std::string_view chunk_name,
                                                          std::string* source,
                                                          std::string* note,
                                                          std::string_view note_prefix);
bool TryRestoreBytecodeCompareConditionOrder(const Lua51Function& function,
                                             std::string_view chunk_name,
                                             std::string* source,
                                             std::string* note,
                                             std::string_view note_prefix);
bool TryRestoreBytecodeTopLevelClosureBindings(const Lua51Function& function,
                                               std::string_view chunk_name,
                                               std::string* source,
                                               std::string* note,
                                               std::string_view note_prefix);
bool TryRestoreBytecodeTopLevelAssignedClosureBlocks(const Lua51Function& function,
                                                     std::string_view chunk_name,
                                                     std::string* source,
                                                     std::string* note,
                                                     std::string_view note_prefix);

bool IsLuaBytecode(const std::vector<char>& bytes) {
    return bytes.size() >= kLuaSignature.size() &&
           std::string_view(bytes.data(), kLuaSignature.size()) == kLuaSignature;
}

bool IsTriviallyEmptyLuaMainChunk(const Lua51Function& function) {
    if (!function.child_functions.empty()) {
        return false;
    }

    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.empty()) {
        return true;
    }

    std::size_t cursor = 0;
    while (cursor < instructions.size() &&
           instructions[cursor].opcode == Lua51Opcode::kClose) {
        ++cursor;
    }
    if (cursor >= instructions.size()) {
        return false;
    }

    if (cursor + 1 != instructions.size()) {
        return false;
    }

    const Lua51Instruction& tail = instructions[cursor];
    return tail.opcode == Lua51Opcode::kReturn && tail.a == 0 && tail.b == 1;
}

template <typename PassFn>
bool RunNativeFirstPassLoop(int default_cap, PassFn&& run_single_pass) {
    bool changed = false;
    const int pass_cap = std::max(default_cap, 1);
    for (int pass = 0; pass < pass_cap; ++pass) {
        if (!run_single_pass(pass)) {
            break;
        }
        changed = true;
    }
    return changed;
}

void CanonicalizeRenderedExplicitSelfMethodCall(std::string_view callee_view,
                                                const std::vector<std::string>& arguments,
                                                std::string* rewritten_out) {
    if (rewritten_out == nullptr) {
        return;
    }
    std::string canonical;
    if (!TryCanonicalizeLuaCallExpressionWithIdentity(
            callee_view, arguments, &canonical)) {
        return;
    }
    *rewritten_out = std::move(canonical);
}

std::string BuildRenderedCallExpression(std::string_view callee,
                                        const std::vector<std::string>& arguments) {
    std::string rendered = std::string(callee) + "(";
    for (std::size_t arg_index = 0; arg_index < arguments.size(); ++arg_index) {
        if (arg_index != 0) {
            rendered += ", ";
        }
        rendered += arguments[arg_index];
    }
    rendered += ")";
    CanonicalizeRenderedExplicitSelfMethodCall(callee, arguments, &rendered);
    return rendered;
}

std::optional<std::string> RenderLua51TrackedCompareExpression(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const std::vector<std::optional<std::string>>& tracked_registers,
    std::size_t instruction_index,
    const Lua51Instruction& compare);

void StepLua51TrackedRegisterExpressions(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const Lua51Instruction& instruction,
    std::size_t instruction_index,
    std::vector<std::optional<std::string>>* tracked_registers);

std::string ValidateLuaSource(std::string_view source, std::string_view chunk_name) {
    lua_State* state = luaL_newstate();
    if (state == nullptr) {
        return "validator_init_failed";
    }

    const std::string name(chunk_name.empty() ? "native_main_chunk" : chunk_name);
    const int status = luaL_loadbuffer(state,
                                       source.data(),
                                       source.size(),
                                       name.c_str());
    if (status == 0) {
        lua_close(state);
        return {};
    }

    const char* message = lua_tostring(state, -1);
    std::string error = message != nullptr ? message : "syntax_invalid";
    lua_close(state);
    return error;
}

std::string JoinRenderedLines(const std::vector<std::string>& lines) {
    std::string joined;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (i != 0) {
            joined.push_back('\n');
        }
        joined += lines[i];
    }
    return joined;
}

std::size_t HashRenderedLines(const std::vector<std::string>& lines) {
    std::size_t hash = lines.size();
    std::hash<std::string_view> hasher;
    for (const std::string& line : lines) {
        const std::size_t line_hash = hasher(line);
        hash ^= line_hash + 0x9e3779b97f4a7c15ULL + (hash << 6U) + (hash >> 2U);
    }
    return hash;
}

void AppendCandidateNote(std::string* note,
                         std::string_view prefix,
                         std::string_view suffix) {
    if (note == nullptr || suffix.empty()) {
        return;
    }

    std::string part;
    if (!prefix.empty()) {
        part.append(prefix);
        part.push_back('_');
    }
    part.append(suffix);

    if (!note->empty()) {
        std::size_t cursor = 0;
        while (cursor <= note->size()) {
            const std::size_t separator = note->find(';', cursor);
            const std::string_view existing =
                separator == std::string::npos
                    ? std::string_view(*note).substr(cursor)
                    : std::string_view(*note).substr(cursor, separator - cursor);
            if (existing == part) {
                return;
            }
            if (separator == std::string::npos) {
                break;
            }
            cursor = separator + 1;
        }
        note->push_back(';');
    }
    note->append(part);
}

void AppendRenderedCleanupTelemetryNotes(std::string* note,
                                         std::string_view prefix,
                                         std::string_view family_name) {
    const RenderedLuaCleanupProfileTelemetry& telemetry =
        GetLastRenderedLuaCleanupProfileTelemetry();
    if (telemetry.profile_name.empty()) {
        return;
    }

    const bool hot = telemetry.total_microseconds >= 25000;
    const bool capped = telemetry.capped_step_count > 0;
    if (!hot && !capped) {
        return;
    }

    const std::string family(family_name.empty() ? telemetry.profile_name : family_name);
    AppendCandidateNote(
        note, prefix,
        family + "_ms=" + std::to_string((telemetry.total_microseconds + 999) / 1000));
    if (!telemetry.hottest_step.empty()) {
        AppendCandidateNote(note,
                            prefix,
                            family + "_hot=" + telemetry.hottest_step + ":" +
                                std::to_string(
                                    (telemetry.hottest_step_microseconds + 999) / 1000) +
                                "ms");
    }
    if (!telemetry.capped_steps.empty()) {
        AppendCandidateNote(note, prefix, family + "_cap=" + telemetry.capped_steps);
    }
}

bool TryApplyPostRestorationCleanup(std::string_view chunk_name,
                                    std::string* source,
                                    std::string* note,
                                    std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    // Keep restored empty branch skeletons intact here. By the time we reach
    // post-restoration cleanup, bytecode-backed passes may have intentionally rebuilt
    // empty `if ... then end` and `else` lanes that late cleanup would otherwise erase
    // again before the final candidate is chosen.
    bool changed = false;
    changed |= SimplifyRenderedLuaExplicitSelfMethodArguments(&lines);
    changed |= SimplifyRenderedLuaRedundantColonSelfArguments(&lines);
    changed |= SimplifyRenderedLuaMethodArg0SelfParameters(&lines);
    changed |= NormalizeRenderedLuaWhitespace(&lines);
    if (!changed) {
        return false;
    }

    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    return true;
}

std::size_t CountScopedLocalDeclarationLines(std::string_view source) {
    const std::vector<std::string> lines = SplitRenderedLines(source);
    std::size_t local_declarations = 0;
    for (const std::string& line : lines) {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (trimmed.starts_with("local ") || trimmed.starts_with("local function ")) {
            ++local_declarations;
        }
    }
    return local_declarations;
}

bool TryParseLocalDeclarationNames(std::string_view line,
                                   std::vector<std::string>* names_out) {
    if (names_out == nullptr) {
        return false;
    }
    names_out->clear();
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty() || !trimmed.starts_with("local ")) {
        return false;
    }

    if (trimmed.starts_with("local function ")) {
        const std::string_view suffix = TrimLuaWhitespace(trimmed.substr(15));
        if (suffix.empty()) {
            return false;
        }
        const std::size_t name_end = suffix.find_first_of(" (");
        const std::string_view name =
            name_end == std::string_view::npos ? suffix : suffix.substr(0, name_end);
        if (!IsValidLuaIdentifier(name)) {
            return false;
        }
        names_out->push_back(std::string(name));
        return true;
    }

    std::string_view lhs = TrimLuaWhitespace(trimmed.substr(6));
    const std::size_t equals = lhs.find('=');
    if (equals != std::string_view::npos) {
        lhs = TrimLuaWhitespace(lhs.substr(0, equals));
    }
    if (lhs.empty()) {
        return false;
    }

    std::size_t begin = 0;
    while (begin < lhs.size()) {
        const std::size_t comma = lhs.find(',', begin);
        const std::string_view token =
            comma == std::string_view::npos
                ? TrimLuaWhitespace(lhs.substr(begin))
                : TrimLuaWhitespace(lhs.substr(begin, comma - begin));
        if (!token.empty() && IsValidLuaIdentifier(token)) {
            names_out->push_back(std::string(token));
        }
        if (comma == std::string_view::npos) {
            break;
        }
        begin = comma + 1;
    }
    return !names_out->empty();
}

struct LuaLocalTimingProfile {
    std::size_t local_declaration_line_count = 0;
    std::unordered_set<std::string> declared_names;
    std::unordered_map<std::string, std::size_t> first_local_assignment_line;
    std::unordered_map<std::string, std::size_t> first_nonlocal_assignment_line;
};

LuaLocalTimingProfile BuildLuaLocalTimingProfile(std::string_view source) {
    LuaLocalTimingProfile profile;
    const std::vector<std::string> lines = SplitRenderedLines(source);
    std::vector<std::string> declared_names;
    for (std::size_t index = 0; index < lines.size(); ++index) {
        const std::size_t line_number = index + 1;
        const std::string_view trimmed_line =
            TrimLuaWhitespace(StripLuaLineComment(lines[index]));
        if (TryParseLocalDeclarationNames(lines[index], &declared_names)) {
            ++profile.local_declaration_line_count;
            for (const std::string& name : declared_names) {
                profile.declared_names.insert(name);
                if (profile.first_local_assignment_line.find(name) ==
                    profile.first_local_assignment_line.end()) {
                    profile.first_local_assignment_line.emplace(name, line_number);
                }
            }
        }

        if (trimmed_line.starts_with("function ")) {
            const std::string_view suffix = TrimLuaWhitespace(trimmed_line.substr(9));
            const std::size_t open_paren = suffix.find('(');
            if (open_paren != std::string_view::npos) {
                const std::string_view function_name =
                    TrimLuaWhitespace(suffix.substr(0, open_paren));
                if (IsValidLuaIdentifier(function_name) &&
                    profile.first_nonlocal_assignment_line.find(std::string(function_name)) ==
                        profile.first_nonlocal_assignment_line.end()) {
                    profile.first_nonlocal_assignment_line.emplace(
                        std::string(function_name), line_number);
                }
            }
        }
        if (trimmed_line.starts_with("function ") ||
            trimmed_line.starts_with("local function ")) {
            const std::size_t open_paren = trimmed_line.find('(');
            const std::size_t close_paren = trimmed_line.find(')', open_paren);
            if (open_paren != std::string_view::npos &&
                close_paren != std::string_view::npos &&
                close_paren > open_paren + 1) {
                const std::string_view parameter_list =
                    trimmed_line.substr(open_paren + 1, close_paren - open_paren - 1);
                std::size_t begin = 0;
                while (begin < parameter_list.size()) {
                    const std::size_t comma = parameter_list.find(',', begin);
                    const std::string_view token =
                        comma == std::string_view::npos
                            ? TrimLuaWhitespace(parameter_list.substr(begin))
                            : TrimLuaWhitespace(parameter_list.substr(begin, comma - begin));
                    if (!token.empty() && token != "..." && IsValidLuaIdentifier(token) &&
                        profile.first_local_assignment_line.find(std::string(token)) ==
                            profile.first_local_assignment_line.end()) {
                        profile.first_local_assignment_line.emplace(std::string(token),
                                                                    line_number);
                    }
                    if (comma == std::string_view::npos) {
                        break;
                    }
                    begin = comma + 1;
                }
            }
        }
        if (trimmed_line.starts_with("for ") && trimmed_line.ends_with(" do")) {
            const std::string_view loop_header =
                TrimLuaWhitespace(trimmed_line.substr(4, trimmed_line.size() - 7));
            if (!loop_header.empty()) {
                const std::size_t in_pos = loop_header.find(" in ");
                const std::size_t eq_pos = loop_header.find('=');
                std::string_view local_bindings;
                if (in_pos != std::string_view::npos) {
                    local_bindings = TrimLuaWhitespace(loop_header.substr(0, in_pos));
                } else if (eq_pos != std::string_view::npos) {
                    local_bindings = TrimLuaWhitespace(loop_header.substr(0, eq_pos));
                }
                if (!local_bindings.empty()) {
                    std::size_t begin = 0;
                    while (begin < local_bindings.size()) {
                        const std::size_t comma = local_bindings.find(',', begin);
                        const std::string_view token =
                            comma == std::string_view::npos
                                ? TrimLuaWhitespace(local_bindings.substr(begin))
                                : TrimLuaWhitespace(
                                      local_bindings.substr(begin, comma - begin));
                        if (!token.empty() && IsValidLuaIdentifier(token) &&
                            profile.first_local_assignment_line.find(std::string(token)) ==
                                profile.first_local_assignment_line.end()) {
                            profile.first_local_assignment_line.emplace(std::string(token),
                                                                        line_number);
                        }
                        if (comma == std::string_view::npos) {
                            break;
                        }
                        begin = comma + 1;
                    }
                }
            }
        }

        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseSingleRenderedAssignment(lines[index], &is_local, &lhs, &rhs) ||
            !IsValidLuaIdentifier(lhs)) {
            continue;
        }
        auto& assignment_map = is_local
                                   ? profile.first_local_assignment_line
                                   : profile.first_nonlocal_assignment_line;
        if (assignment_map.find(lhs) == assignment_map.end()) {
            assignment_map.emplace(lhs, line_number);
        }
    }
    return profile;
}

bool IsEquivalentLocalTimingPromotionOnly(std::string_view source_before,
                                          std::string_view source_after,
                                          std::size_t max_line_slack,
                                          std::size_t max_newly_declared_names,
                                          std::string* rejection_reason_out) {
    if (rejection_reason_out != nullptr) {
        rejection_reason_out->clear();
    }
    const auto is_likely_generated_register_name = [](std::string_view name) {
        if (name.size() < 2 || name.front() != 'r') {
            return false;
        }
        for (std::size_t index = 1; index < name.size(); ++index) {
            if (!std::isdigit(static_cast<unsigned char>(name[index]))) {
                return false;
            }
        }
        return true;
    };

    const LuaLocalTimingProfile before = BuildLuaLocalTimingProfile(source_before);
    const LuaLocalTimingProfile after = BuildLuaLocalTimingProfile(source_after);
    if (after.local_declaration_line_count <= before.local_declaration_line_count) {
        return true;
    }

    std::vector<std::string> newly_declared_names;
    newly_declared_names.reserve(after.declared_names.size());
    for (const std::string& name : after.declared_names) {
        if (before.declared_names.find(name) == before.declared_names.end()) {
            newly_declared_names.push_back(name);
        }
    }

    if (newly_declared_names.empty()) {
        // Timing-only normalization can increase declaration line count without
        // introducing any new local names (for example, splitting a declaration
        // or promoting the same local to first-owned-write). Treat this as
        // parity-safe for the extra-local guard.
        return true;
    }
    // Guard against broad declaration shape changes; allow only bounded,
    // register-identity-like promotions.
    if (newly_declared_names.size() > max_newly_declared_names) {
        if (rejection_reason_out != nullptr) {
            *rejection_reason_out = "too_many_new_names";
        }
        return false;
    }

    const auto has_standalone_assignment_anchor =
        [&source_before](std::string_view identifier) {
            if (identifier.empty()) {
                return false;
            }
            const auto is_identifier_char = [](char ch) {
                return ch == '_' ||
                       (ch >= 'A' && ch <= 'Z') ||
                       (ch >= 'a' && ch <= 'z') ||
                       (ch >= '0' && ch <= '9');
            };
            const std::vector<std::string> lines = SplitRenderedLines(source_before);
            for (const std::string& line : lines) {
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment(line));
                if (trimmed.empty()) {
                    continue;
                }
                const std::size_t equals_pos = trimmed.find('=');
                if (equals_pos == std::string_view::npos) {
                    continue;
                }
                std::size_t cursor = 0;
                while (cursor < trimmed.size()) {
                    const std::size_t token_pos = trimmed.find(identifier, cursor);
                    if (token_pos == std::string_view::npos || token_pos > equals_pos) {
                        break;
                    }
                    const std::size_t token_end = token_pos + identifier.size();
                    const bool left_boundary =
                        token_pos == 0 || !is_identifier_char(trimmed[token_pos - 1]);
                    const bool right_boundary =
                        token_end >= trimmed.size() ||
                        !is_identifier_char(trimmed[token_end]);
                    if (!left_boundary || !right_boundary) {
                        cursor = token_end;
                        continue;
                    }

                    std::size_t left_scan = token_pos;
                    while (left_scan > 0 &&
                           std::isspace(static_cast<unsigned char>(trimmed[left_scan - 1])) != 0) {
                        --left_scan;
                    }
                    if (left_scan > 0) {
                        const char prev = trimmed[left_scan - 1];
                        if (prev == '.' || prev == ':' || prev == '[') {
                            cursor = token_end;
                            continue;
                        }
                    }

                    std::size_t rhs_scan = token_end;
                    while (rhs_scan < trimmed.size() &&
                           std::isspace(static_cast<unsigned char>(trimmed[rhs_scan])) != 0) {
                        ++rhs_scan;
                    }
                    if (rhs_scan < trimmed.size() &&
                        (trimmed[rhs_scan] == ',' || trimmed[rhs_scan] == '=')) {
                        return true;
                    }
                    cursor = token_end;
                }
            }
            return false;
        };

    for (const std::string& name : newly_declared_names) {
        const bool is_render_temporary = IsRenderTemporaryName(name);
        const bool is_generated_register = is_likely_generated_register_name(name);
        const auto before_nonlocal =
            before.first_nonlocal_assignment_line.find(name);
        const auto after_local =
            after.first_local_assignment_line.find(name);
        const auto after_nonlocal =
            after.first_nonlocal_assignment_line.find(name);
        if (before_nonlocal == before.first_nonlocal_assignment_line.end() ||
            after_local == after.first_local_assignment_line.end()) {
            if (is_render_temporary || is_generated_register) {
                // Near-exact parity lane: synthetic temporaries can be promoted to
                // local-at-first-write without a stable nonlocal assignment anchor
                // in the rendered pre-normalized form.
                continue;
            }
            if (max_line_slack > 0 &&
                after_local != after.first_local_assignment_line.end() &&
                (after_nonlocal == after.first_nonlocal_assignment_line.end() ||
                 after_nonlocal->second > after_local->second) &&
                has_standalone_assignment_anchor(name)) {
                // Near-exact compatibility lane: accept new local declarations when
                // the pre-normalized source already has a standalone assignment anchor
                // for the same identifier and the post-normalized source does not
                // introduce an early non-local write.
                continue;
            }
            if (rejection_reason_out != nullptr) {
                *rejection_reason_out = "missing_assignment_anchor";
            }
            return false;
        }
        if (after_local->second > before_nonlocal->second) {
            const std::size_t line_delta =
                after_local->second - before_nonlocal->second;
            if (is_generated_register && line_delta <= 2) {
                continue;
            }
            if (line_delta <= max_line_slack) {
                continue;
            }
            if (rejection_reason_out != nullptr) {
                *rejection_reason_out = "local_after_nonlocal_window";
            }
            return false;
        }
        if (after_nonlocal != after.first_nonlocal_assignment_line.end() &&
            after_nonlocal->second <= after_local->second) {
            if (is_render_temporary || is_generated_register) {
                continue;
            }
            if (rejection_reason_out != nullptr) {
                *rejection_reason_out = "nonlocal_before_local";
            }
            return false;
        }
    }

    return true;
}

bool TryApplyFunctionBoundaryParityNormalization(std::string_view chunk_name,
                                                 std::string* source,
                                                 std::string* note,
                                                 std::string_view note_prefix,
                                                 std::size_t* parity_commits_out,
                                                 std::size_t* local_commit_at_first_write_out,
                                                 std::size_t* parity_rejected_extra_local_out) {
    if (parity_commits_out != nullptr) {
        *parity_commits_out = 0;
    }
    if (local_commit_at_first_write_out != nullptr) {
        *local_commit_at_first_write_out = 0;
    }
    if (parity_rejected_extra_local_out != nullptr) {
        *parity_rejected_extra_local_out = 0;
    }
    if (source == nullptr || source->empty() ||
        source->find("return") == std::string::npos ||
        source->find("if ") == std::string::npos) {
        return false;
    }
    const std::string lowered_chunk_name =
        refii::lua::NormalizeLuaSourcePathForPolicy(chunk_name);
    const refii::lua::LuaScriptProfile script_profile =
        refii::lua::ClassifyLuaScriptProfile(lowered_chunk_name, 0, 0);
    const bool is_small_holdout_function_boundary_target =
        script_profile.Has(refii::lua::LuaScriptProfileFlag::kScopedSmallHoldout);
    const bool is_large_holdout_function_boundary_target =
        script_profile.Has(refii::lua::LuaScriptProfileFlag::kScopedLargeHoldout);
    const bool is_call_carry_parity_target =
        script_profile.Has(refii::lua::LuaScriptProfileFlag::kCallCarryParityTarget);
    const bool is_scoped_holdout_function_boundary_target =
        is_small_holdout_function_boundary_target ||
        is_large_holdout_function_boundary_target;
    const bool is_near_exact_global_function_boundary_target =
        !is_scoped_holdout_function_boundary_target &&
        (script_profile.Has(refii::lua::LuaScriptProfileFlag::kCameraScriptPath) ||
         script_profile.Has(refii::lua::LuaScriptProfileFlag::kEntityModeScriptPath) ||
         script_profile.Has(refii::lua::LuaScriptProfileFlag::kBehavior) ||
         script_profile.Has(refii::lua::LuaScriptProfileFlag::kGroupMind) ||
         script_profile.Has(refii::lua::LuaScriptProfileFlag::kQuestLuaEnumsPath));
    const bool is_generic_behavior_function_boundary_target =
        !is_scoped_holdout_function_boundary_target &&
        (script_profile.Has(refii::lua::LuaScriptProfileFlag::kPriorityOwnership) ||
         is_call_carry_parity_target ||
         is_near_exact_global_function_boundary_target);
    const bool is_function_boundary_target =
        is_scoped_holdout_function_boundary_target ||
        is_generic_behavior_function_boundary_target;
    if (!is_function_boundary_target) {
        return false;
    }
    if (is_large_holdout_function_boundary_target) {
        // Runtime-first lock: large holdouts stay on conservative non-normalized path.
        return false;
    }
    if (is_small_holdout_function_boundary_target) {
        constexpr std::size_t kMaxSmallHoldoutSourceBytes = 96 * 1024;
        if (source->size() > kMaxSmallHoldoutSourceBytes) {
            return false;
        }
        constexpr std::size_t kMaxSmallHoldoutLines = 900;
        const bool is_interactive_cutscene_actions =
            script_profile.Has(refii::lua::LuaScriptProfileFlag::kInteractiveCutsceneActions);
        const std::size_t rough_line_count = static_cast<std::size_t>(
                                                 std::count(source->begin(),
                                                            source->end(),
                                                            '\n')) +
                                             std::size_t{1};
        if (is_interactive_cutscene_actions &&
            (source->size() > 48 * 1024 || rough_line_count > 360)) {
            return false;
        }
        if (rough_line_count > kMaxSmallHoldoutLines) {
            return false;
        }

        const LuaReadabilitySummary small_holdout_summary =
            SummarizeLuaReadability(*source);
        constexpr std::size_t kMaxSmallHoldoutRisk = 1200;
        if (small_holdout_summary.nonempty_line_count > kMaxSmallHoldoutLines ||
            ScoreLuaReadabilityRisk(small_holdout_summary) > kMaxSmallHoldoutRisk) {
            return false;
        }
    }
    if (is_generic_behavior_function_boundary_target) {
        constexpr std::size_t kMaxGenericBehaviorSourceBytes = 128 * 1024;
        constexpr std::size_t kMaxCallCarryParitySourceBytes = 192 * 1024;
        constexpr std::size_t kMaxNearExactGlobalSourceBytes = 96 * 1024;
        const std::size_t max_source_bytes =
            is_call_carry_parity_target ? kMaxCallCarryParitySourceBytes
                                        : (is_near_exact_global_function_boundary_target
                                               ? kMaxNearExactGlobalSourceBytes
                                               : kMaxGenericBehaviorSourceBytes);
        if (source->size() > max_source_bytes) {
            return false;
        }
        constexpr std::size_t kMaxGenericBehaviorLines = 1200;
        constexpr std::size_t kMaxCallCarryParityLines = 1800;
        constexpr std::size_t kMaxNearExactGlobalLines = 960;
        const std::size_t max_line_count =
            is_call_carry_parity_target ? kMaxCallCarryParityLines
                                        : (is_near_exact_global_function_boundary_target
                                               ? kMaxNearExactGlobalLines
                                               : kMaxGenericBehaviorLines);
        const std::size_t rough_line_count = static_cast<std::size_t>(
                                                 std::count(source->begin(),
                                                            source->end(),
                                                            '\n')) +
                                             std::size_t{1};
        if (rough_line_count > max_line_count) {
            return false;
        }
        const LuaReadabilitySummary generic_behavior_summary =
            SummarizeLuaReadability(*source);
        constexpr std::size_t kMaxGenericBehaviorRisk = 1800;
        constexpr std::size_t kMaxCallCarryParityRisk = 2400;
        constexpr std::size_t kMaxNearExactGlobalRisk = 1500;
        const std::size_t max_risk =
            is_call_carry_parity_target ? kMaxCallCarryParityRisk
                                        : (is_near_exact_global_function_boundary_target
                                               ? kMaxNearExactGlobalRisk
                                               : kMaxGenericBehaviorRisk);
        if (generic_behavior_summary.nonempty_line_count > max_line_count ||
            ScoreLuaReadabilityRisk(generic_behavior_summary) > max_risk) {
            return false;
        }
    }
    if (is_large_holdout_function_boundary_target) {
        constexpr std::size_t kMaxLargeHoldoutSourceBytes = 320 * 1024;
        if (source->size() > kMaxLargeHoldoutSourceBytes) {
            return false;
        }
        constexpr std::size_t kMaxLargeHoldoutLines = 2600;
        const std::size_t rough_line_count = static_cast<std::size_t>(
                                                 std::count(source->begin(),
                                                            source->end(),
                                                            '\n')) +
                                             std::size_t{1};
        if (rough_line_count > kMaxLargeHoldoutLines) {
            return false;
        }

        const LuaReadabilitySummary large_holdout_summary =
            SummarizeLuaReadability(*source);
        constexpr std::size_t kMaxLargeHoldoutRisk = 2200;
        if (large_holdout_summary.nonempty_line_count > kMaxLargeHoldoutLines ||
            ScoreLuaReadabilityRisk(large_holdout_summary) > kMaxLargeHoldoutRisk) {
            return false;
        }
    }
    static thread_local std::unordered_map<std::string, std::size_t>
        function_boundary_attempt_hashes;
    const std::string cache_key = std::string(chunk_name) + "|" + std::string(note_prefix);
    const std::size_t source_hash = std::hash<std::string>{}(*source);
    const auto attempted_it = function_boundary_attempt_hashes.find(cache_key);
    if (attempted_it != function_boundary_attempt_hashes.end() &&
        attempted_it->second == source_hash) {
        return false;
    }
    function_boundary_attempt_hashes[cache_key] = source_hash;

    const std::size_t local_declarations_before =
        CountScopedLocalDeclarationLines(*source);
    std::vector<std::string> lines = SplitRenderedLines(*source);
    const bool is_interactive_cutscene_actions_scope =
        script_profile.Has(refii::lua::LuaScriptProfileFlag::kInteractiveCutsceneActions);

    const auto split_flat_and_conditions_for_actions = [](std::vector<std::string>* target_lines) {
        if (target_lines == nullptr) {
            return false;
        }

        const auto split_top_level_and =
            [](std::string_view condition, std::string* lhs_out, std::string* rhs_out) {
                if (lhs_out == nullptr || rhs_out == nullptr) {
                    return false;
                }
                bool in_single_quote = false;
                bool in_double_quote = false;
                bool escaped = false;
                int paren_depth = 0;
                for (std::size_t index = 0; index + 5 <= condition.size(); ++index) {
                    const char ch = condition[index];
                    if (escaped) {
                        escaped = false;
                        continue;
                    }
                    if (ch == '\\') {
                        escaped = true;
                        continue;
                    }
                    if (!in_double_quote && ch == '\'') {
                        in_single_quote = !in_single_quote;
                        continue;
                    }
                    if (!in_single_quote && ch == '"') {
                        in_double_quote = !in_double_quote;
                        continue;
                    }
                    if (in_single_quote || in_double_quote) {
                        continue;
                    }
                    if (ch == '(') {
                        ++paren_depth;
                        continue;
                    }
                    if (ch == ')' && paren_depth > 0) {
                        --paren_depth;
                        continue;
                    }
                    if (paren_depth != 0) {
                        continue;
                    }
                    if (condition.substr(index, 5) != " and ") {
                        continue;
                    }

                    const std::string_view lhs =
                        TrimLuaWhitespace(condition.substr(0, index));
                    const std::string_view rhs =
                        TrimLuaWhitespace(condition.substr(index + 5));
                    if (lhs.empty() || rhs.empty()) {
                        return false;
                    }
                    if (lhs.find(" and ") != std::string_view::npos ||
                        rhs.find(" and ") != std::string_view::npos ||
                        lhs.find(" or ") != std::string_view::npos ||
                        rhs.find(" or ") != std::string_view::npos) {
                        return false;
                    }
                    *lhs_out = std::string(lhs);
                    *rhs_out = std::string(rhs);
                    return true;
                }
                return false;
            };

        bool changed = false;
        for (std::size_t line_index = 0; line_index < target_lines->size(); ++line_index) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*target_lines)[line_index]));
            if (!trimmed.starts_with("if ") || !trimmed.ends_with(" then")) {
                continue;
            }

            std::string lhs_condition;
            std::string rhs_condition;
            const std::string_view raw_condition =
                TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
            if (!split_top_level_and(raw_condition, &lhs_condition, &rhs_condition)) {
                continue;
            }

            std::size_t end_line = std::string::npos;
            bool unsupported_body = false;
            for (std::size_t probe = line_index + 1; probe < target_lines->size(); ++probe) {
                const std::string_view body_trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*target_lines)[probe]));
                if (body_trimmed.empty()) {
                    continue;
                }
                if (body_trimmed == "end") {
                    end_line = probe;
                    break;
                }
                if (body_trimmed == "else" || body_trimmed.starts_with("elseif ") ||
                    body_trimmed.starts_with("if ") || body_trimmed.starts_with("for ") ||
                    body_trimmed.starts_with("while ") || body_trimmed == "repeat" ||
                    body_trimmed == "do" || body_trimmed.starts_with("function ") ||
                    body_trimmed.find(" = function(") != std::string_view::npos) {
                    unsupported_body = true;
                    break;
                }
            }
            if (unsupported_body || end_line == std::string::npos ||
                end_line <= line_index + 1) {
                continue;
            }

            const std::string& original_if_line = (*target_lines)[line_index];
            const std::size_t first_non_space =
                original_if_line.find_first_not_of(" \t");
            const std::string base_indent =
                first_non_space == std::string::npos
                    ? std::string()
                    : original_if_line.substr(0, first_non_space);
            const std::string nested_indent = base_indent + "  ";
            for (std::size_t probe = line_index + 1; probe < end_line; ++probe) {
                (*target_lines)[probe] = "  " + (*target_lines)[probe];
            }
            (*target_lines)[line_index] = base_indent + "if " + lhs_condition + " then";
            target_lines->insert(target_lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                                 nested_indent + "if " + rhs_condition + " then");
            target_lines->insert(target_lines->begin() + static_cast<std::ptrdiff_t>(end_line + 1),
                                 nested_indent + "end");
            changed = true;
            line_index = end_line + 1;
        }

        return changed;
    };

    const bool is_tag2_scope =
        script_profile.Has(refii::lua::LuaScriptProfileFlag::kBehaviorTag2);

    bool changed = false;
    const bool enable_commit_local_normalization =
        (is_small_holdout_function_boundary_target && !is_tag2_scope) ||
        is_generic_behavior_function_boundary_target;
    const bool enable_call_reassignment_self_compare_rewrite =
        is_small_holdout_function_boundary_target ||
        is_generic_behavior_function_boundary_target;
    const int pass_count = is_large_holdout_function_boundary_target
                               ? 1
                               : ((is_generic_behavior_function_boundary_target &&
                                   !is_near_exact_global_function_boundary_target)
                                      ? 1
                                      : 2);
    bool local_commit_changed = false;
    changed |= RunNativeFirstPassLoop(pass_count, [&](int /*pass*/) {
        bool pass_changed = false;
        pass_changed |= SimplifyRenderedLuaImmediateCallLocals(&lines);
        pass_changed |= SimplifyRenderedLuaImmediateAssignedRepeatedConditions(&lines);
        pass_changed |= SimplifyRenderedLuaNearbyAssignedRepeatedConditions(&lines);
        pass_changed |= SimplifyRenderedLuaAdjacentDuplicateCallValueConditions(&lines);
        if (is_interactive_cutscene_actions_scope) {
            pass_changed |= split_flat_and_conditions_for_actions(&lines);
        }
        pass_changed |= SimplifyRenderedLuaImmediateLocalCallConditions(&lines);
        if (enable_call_reassignment_self_compare_rewrite) {
            pass_changed |= SimplifyRenderedLuaCallReassignmentSelfCompareGuards(&lines);
        }
        pass_changed |= SimplifyRenderedLuaConditionalReturns(&lines);
        pass_changed |= SimplifyRenderedLuaBooleanCoercionReturns(&lines);
        pass_changed |= UnwrapRenderedLuaReturnedCallsForExactParity(&lines);
        pass_changed |= SimplifyRenderedLuaAssignedFunctionDeclarations(&lines);
        pass_changed |= SimplifyRenderedLuaExplicitSelfMethodArguments(&lines);
        pass_changed |= SimplifyRenderedLuaRedundantColonSelfArguments(&lines);
        pass_changed |= SimplifyRenderedLuaMethodArg0SelfParameters(&lines);
        if (enable_commit_local_normalization) {
            // Conservative local-commit normalization for branch-dense holdouts.
            bool commit_local_changed = false;
            commit_local_changed |= LocalizeRenderedLuaFirstAssignments(&lines);
            commit_local_changed |= LocalizeRenderedLuaFunctionLocals(&lines);
            commit_local_changed |= SimplifyRenderedLuaImmediateIdentifierCarryLocals(&lines);
            commit_local_changed |= SimplifyRenderedLuaImmediateExpressionCarryLocals(&lines);
            commit_local_changed |= SimplifyRenderedLuaImmediateMemberCarryLocals(&lines);
            commit_local_changed |= SimplifyRenderedLuaImmediateIteratorArgumentCarryLocals(&lines);
            pass_changed |= commit_local_changed;
            local_commit_changed = local_commit_changed || commit_local_changed;
        }
        return pass_changed;
    });
    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }
    const std::size_t local_declarations_after =
        CountScopedLocalDeclarationLines(candidate_source);
    bool accepted_extra_local_timing_promotion = false;
    if (is_function_boundary_target &&
        local_declarations_after > local_declarations_before) {
        const std::size_t local_timing_line_slack =
            (is_near_exact_global_function_boundary_target ||
             is_call_carry_parity_target)
                ? std::size_t{2}
                : std::size_t{0};
        const std::size_t max_newly_declared_names =
            (is_near_exact_global_function_boundary_target ||
             is_call_carry_parity_target)
                ? std::size_t{12}
                : std::size_t{8};
        std::string local_timing_rejection_reason;
        accepted_extra_local_timing_promotion =
            IsEquivalentLocalTimingPromotionOnly(*source,
                                                 candidate_source,
                                                 local_timing_line_slack,
                                                 max_newly_declared_names,
                                                 &local_timing_rejection_reason);
        if (!accepted_extra_local_timing_promotion) {
            if (parity_rejected_extra_local_out != nullptr) {
                *parity_rejected_extra_local_out = 1;
            }
            AppendCandidateNote(note,
                                note_prefix,
                                "parity_candidate_rejected_extra_local=1");
            if (!local_timing_rejection_reason.empty()) {
                AppendCandidateNote(note,
                                    note_prefix,
                                    "parity_candidate_rejected_extra_local_reason=" +
                                        local_timing_rejection_reason);
            }
            return false;
        }
    }

    *source = std::move(candidate_source);
    if (parity_commits_out != nullptr) {
        *parity_commits_out = 1;
    }
    if ((local_commit_changed || accepted_extra_local_timing_promotion) &&
        local_commit_at_first_write_out != nullptr) {
        *local_commit_at_first_write_out = 1;
    }
    AppendCandidateNote(note, note_prefix, "function_boundary_parity_normalized");
    return true;
}

bool TryApplyExactParityNormalization(
    const refii::lua::audit::LuaBytecodeParityReference* parity_reference,
    std::string_view chunk_name,
    std::string* source,
    std::string* note,
    std::string_view note_prefix) {
    if (parity_reference == nullptr || !parity_reference->available ||
        source == nullptr || source->empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    if (!ApplyRenderedLuaExactParityNormalizationProfile(&lines)) {
        return false;
    }

    std::string candidate_source = JoinRenderedLines(lines);
    const std::string validation_error = ValidateLuaSource(candidate_source, chunk_name);
    if (!validation_error.empty()) {
        return false;
    }

    refii::lua::audit::LuaBytecodeParityResult parity_result;
    if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(*parity_reference,
                                                            candidate_source,
                                                            chunk_name,
                                                            &parity_result) ||
        !parity_result.exact_bytecode) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "exact_parity_normalized");
    AppendRenderedCleanupTelemetryNotes(note, note_prefix, "exact_cleanup");
    return true;
}

bool TryTrimRedundantTailEndRuns(std::string_view chunk_name,
                                 std::string* source,
                                 std::string* note,
                                 std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const auto indent_of = [](std::string_view line) -> std::size_t {
        std::size_t index = 0;
        while (index < line.size() &&
               (line[index] == ' ' || line[index] == '\t')) {
            ++index;
        }
        return index;
    };
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto looks_like_top_level_function_assignment = [&](std::string_view line) -> bool {
        const std::string_view trimmed = trim_line(line);
        return trimmed.starts_with("local function ") ||
               trimmed.find(" = function(") != std::string_view::npos;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    if (lines.size() < 4) {
        return false;
    }

    for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
        if (trim_line(lines[line_index]) != "end") {
            continue;
        }

        const std::size_t run_indent = indent_of(lines[line_index]);
        std::size_t run_end = line_index + 1;
        while (run_end < lines.size() && trim_line(lines[run_end]) == "end" &&
               indent_of(lines[run_end]) == run_indent) {
            ++run_end;
        }
        if (run_end - line_index < 2) {
            continue;
        }

        std::size_t next_nonempty = run_end;
        while (next_nonempty < lines.size() && trim_line(lines[next_nonempty]).empty()) {
            ++next_nonempty;
        }
        if (next_nonempty >= lines.size()) {
            continue;
        }

        const bool plausible_tail_boundary =
            looks_like_top_level_function_assignment(lines[next_nonempty]) ||
            indent_of(lines[next_nonempty]) <= run_indent;
        if (!plausible_tail_boundary) {
            continue;
        }

        for (std::size_t remove_index = line_index; remove_index < run_end; ++remove_index) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.erase(candidate_lines.begin() +
                                  static_cast<std::ptrdiff_t>(remove_index));
            std::string candidate_source = JoinRenderedLines(candidate_lines);
            if (ValidateLuaSource(candidate_source, chunk_name).empty()) {
                *source = std::move(candidate_source);
                AppendCandidateNote(note, note_prefix, "tail_end_run_trimmed");
                return true;
            }
        }
    }

    return false;
}

bool IsRenderedAnonymousFunctionPlaceholder(std::string_view expression) {
    expression = TrimLuaWhitespace(StripLuaLineComment(expression));
    return expression.starts_with("function(") || expression.starts_with("function (");
}

bool ContainsRenderedEmptyStringPlaceholderExpression(std::string_view expression) {
    expression = StripLuaLineComment(expression);
    return expression.find("\"\":") != std::string_view::npos ||
           expression.find(", \"\",") != std::string_view::npos ||
           expression.find(", \"\", ") != std::string_view::npos ||
           expression.find("(\"\",") != std::string_view::npos ||
           expression.find("(\"\" ,") != std::string_view::npos;
}

bool TryRestoreBalancedWhileTrueMonitorWrappers(std::string_view chunk_name,
                                                std::string* source,
                                                std::string* note,
                                                std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto parse_statement_call_expression =
        [&](std::string_view line,
            bool* is_assignment,
            std::string* assignment_prefix,
            std::string* expression,
            std::string* callee,
            std::vector<std::string>* arguments) {
            if (is_assignment == nullptr || assignment_prefix == nullptr ||
                expression == nullptr || callee == nullptr || arguments == nullptr) {
                return false;
            }

            *is_assignment = false;
            assignment_prefix->clear();
            expression->clear();
            callee->clear();
            arguments->clear();

            std::string_view trimmed = trim_line(line);
            if (trimmed.empty()) {
                return false;
            }

            if (ParseRenderedSingleCallExpression(trimmed, callee, arguments)) {
                *expression = std::string(trimmed);
                return true;
            }

            if (trimmed.starts_with("{")) {
                const std::size_t equal = trimmed.find('=');
                if (equal != std::string_view::npos) {
                    const std::string_view suffix =
                        TrimLuaWhitespace(trimmed.substr(equal + 1));
                    if (ParseRenderedSingleCallExpression(suffix, callee, arguments)) {
                        *expression = std::string(trimmed);
                        return true;
                    }
                }
            }

            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (!ParseSingleRenderedAssignment(trimmed, &assign_is_local, &assign_lhs, &assign_rhs) ||
                !ParseRenderedSingleCallExpression(assign_rhs, callee, arguments)) {
                return false;
            }

            *is_assignment = true;
            *assignment_prefix =
                std::string(assign_is_local ? "local " : "") + assign_lhs + " = ";
            *expression = std::move(assign_rhs);
            return true;
        };
    const auto looks_like_set_current_action_line = [&](std::string_view line) {
        const std::string_view trimmed = trim_line(line);
        return trimmed.find("Action.SetCurrentAction(") != std::string_view::npos ||
               trimmed.find(".SetCurrentAction(") != std::string_view::npos ||
               trimmed.find(":SetCurrentAction(") != std::string_view::npos;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    std::size_t first_while_line = lines.size();
    std::size_t existing_wrapper_count = 0;
    std::size_t break_line = lines.size();
    std::size_t set_current_action_line = lines.size();
    std::string while_indent;

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = trim_line(lines[line_index]);
        if (trimmed.empty()) {
            continue;
        }

        if (first_while_line == lines.size() && trimmed == "while true do") {
            first_while_line = line_index;
            while_indent = line_indent(lines[line_index]);
            existing_wrapper_count = 1;
            for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
                if (trim_line(lines[probe]) != "while true do" ||
                    line_indent(lines[probe]) != while_indent) {
                    break;
                }
                ++existing_wrapper_count;
            }
            continue;
        }

        if (first_while_line != lines.size() && break_line == lines.size() && trimmed == "break") {
            break_line = line_index;
            continue;
        }

        if (break_line != lines.size() && looks_like_set_current_action_line(lines[line_index])) {
            set_current_action_line = line_index;
            break;
        }
    }

    if (first_while_line == lines.size() || break_line == lines.size() ||
        set_current_action_line == lines.size() || break_line <= first_while_line ||
        set_current_action_line <= break_line || while_indent.empty()) {
        return false;
    }

    const std::string wrapper_line = while_indent + "while true do";
    const std::size_t insert_index = first_while_line + existing_wrapper_count;
    for (std::size_t extra_wrappers = 1; extra_wrappers <= 8; ++extra_wrappers) {
        std::vector<std::string> candidate_lines = lines;
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(insert_index),
                               extra_wrappers,
                               wrapper_line);
        std::string candidate_source = JoinRenderedLines(candidate_lines);
        if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
            continue;
        }

        *source = std::move(candidate_source);
        AppendCandidateNote(note,
                            note_prefix,
                            "while_true_monitor_wrappers_restored");
        return true;
    }

    return false;
}

std::optional<std::size_t> TryParseValidationErrorLineNumber(std::string_view validation_error) {
    const std::size_t line_prefix = validation_error.find("]:");
    if (line_prefix == std::string_view::npos) {
        return std::nullopt;
    }

    std::size_t cursor = line_prefix + 2;
    while (cursor < validation_error.size() &&
           std::isspace(static_cast<unsigned char>(validation_error[cursor])) != 0) {
        ++cursor;
    }

    if (cursor >= validation_error.size() ||
        std::isdigit(static_cast<unsigned char>(validation_error[cursor])) == 0) {
        return std::nullopt;
    }

    std::size_t line_number = 0;
    while (cursor < validation_error.size() &&
           std::isdigit(static_cast<unsigned char>(validation_error[cursor])) != 0) {
        line_number = line_number * 10 + static_cast<std::size_t>(validation_error[cursor] - '0');
        ++cursor;
    }

    if (line_number == 0 || cursor >= validation_error.size() || validation_error[cursor] != ':') {
        return std::nullopt;
    }

    return line_number;
}

bool TryRemoveValidationGuidedRedundantEnd(std::string_view chunk_name,
                                           std::string_view validation_error,
                                           std::string* source,
                                           std::string* note,
                                           std::string_view note_prefix);

bool TryRepairValidationGuidedOrphanLoopBreak(std::string_view chunk_name,
                                              std::string_view validation_error,
                                              std::string* source,
                                              std::string* note,
                                              std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const std::optional<std::size_t> maybe_line_number =
        TryParseValidationErrorLineNumber(validation_error);
    if (!maybe_line_number.has_value()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto parse_if_condition = [&](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }
        const std::string_view trimmed = trim_line(line);
        if (!trimmed.starts_with("if ") || !trimmed.ends_with(" then") ||
            trimmed.size() <= 7) {
            return false;
        }
        *condition = std::string(
            TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 3 - 5)));
        return !condition->empty();
    };
    const auto next_nonempty = [&](const std::vector<std::string>& lines,
                                   std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines.size(); ++probe) {
            if (!trim_line(lines[probe]).empty()) {
                return probe;
            }
        }
        return lines.size();
    };
    const auto find_block_end = [&](const std::vector<std::string>& lines,
                                    std::size_t start) -> std::size_t {
        int depth = 0;
        for (std::size_t probe = start; probe < lines.size(); ++probe) {
            depth += GetRenderedLuaBlockDelta(lines[probe]);
            if (depth == 0) {
                return probe;
            }
        }
        return lines.size();
    };
    const auto find_previous_nonempty = [&](const std::vector<std::string>& lines,
                                            std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t probe = start; probe-- > 0;) {
            if (!trim_line(lines[probe]).empty()) {
                return probe;
            }
            if (probe == 0) {
                break;
            }
        }
        return lines.size();
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::size_t error_index =
        std::min(lines.size(), *maybe_line_number) == 0 ? 0 : std::min(lines.size(), *maybe_line_number) - 1;
    const std::size_t search_begin = error_index > 16 ? error_index - 16 : 0;
    const std::size_t search_end = std::min(lines.size(), error_index + 2);

    for (std::size_t line_index = search_begin; line_index < search_end; ++line_index) {
        std::string condition;
        if (!parse_if_condition(lines[line_index], &condition)) {
            continue;
        }

        const std::size_t first_end = next_nonempty(lines, line_index + 1);
        const std::size_t second_end =
            first_end < lines.size() ? next_nonempty(lines, first_end + 1) : lines.size();
        if (first_end >= lines.size() || second_end >= lines.size() ||
            trim_line(lines[first_end]) != "end" || trim_line(lines[second_end]) != "end") {
            continue;
        }

        std::size_t break_line = lines.size();
        for (std::size_t probe = second_end + 1;
             probe < std::min(lines.size(), error_index + 4);
             ++probe) {
            if (trim_line(lines[probe]) == "break") {
                break_line = probe;
                break;
            }
        }
        if (break_line >= lines.size()) {
            continue;
        }

        const std::string indent(line_indent(lines[line_index]));
        std::vector<std::string> candidate_lines = lines;
        candidate_lines[line_index] =
            indent + "if not (" + std::string(TrimLuaWhitespace(condition)) + ") then";
        candidate_lines.erase(candidate_lines.begin() +
                              static_cast<std::ptrdiff_t>(second_end));
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(first_end),
                               indent + "  break");
        std::string candidate_source = JoinRenderedLines(candidate_lines);
        const std::string candidate_error = ValidateLuaSource(candidate_source, chunk_name);
        if (!candidate_error.empty() &&
            !TryRemoveValidationGuidedRedundantEnd(
                chunk_name, candidate_error, &candidate_source, note, note_prefix)) {
            continue;
        }

        *source = candidate_source;
        AppendCandidateNote(note, note_prefix, "loop_break_guard_restored");
        return true;
    }

    for (std::size_t line_index = search_begin; line_index < search_end; ++line_index) {
        std::string outer_condition;
        if (!parse_if_condition(lines[line_index], &outer_condition)) {
            continue;
        }

        const std::size_t outer_else = next_nonempty(lines, line_index + 1);
        if (outer_else >= lines.size() || trim_line(lines[outer_else]) != "else") {
            continue;
        }

        const std::size_t outer_end = find_block_end(lines, line_index);
        if (outer_end >= lines.size() || trim_line(lines[outer_end]) != "end") {
            continue;
        }

        const std::size_t else_body_line = next_nonempty(lines, outer_else + 1);
        if (else_body_line >= lines.size() || trim_line(lines[else_body_line]) != "break") {
            continue;
        }
        const std::size_t after_break = next_nonempty(lines, else_body_line + 1);
        if (after_break != outer_end) {
            continue;
        }

        const std::size_t inner_end = find_previous_nonempty(lines, outer_else);
        if (inner_end >= lines.size() || trim_line(lines[inner_end]) != "end" ||
            inner_end <= line_index + 1) {
            continue;
        }

        std::size_t inner_if = lines.size();
        for (std::size_t probe = inner_end; probe-- > line_index + 1;) {
            std::string inner_condition;
            if (!parse_if_condition(lines[probe], &inner_condition)) {
                continue;
            }
            if (find_block_end(lines, probe) == inner_end) {
                inner_if = probe;
                break;
            }
            if (probe == line_index + 1) {
                break;
            }
        }
        if (inner_if >= lines.size()) {
            continue;
        }

        const std::string inner_indent = line_indent(lines[inner_if]);
        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(outer_else),
                              candidate_lines.begin() + static_cast<std::ptrdiff_t>(outer_end));
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(inner_end),
                               inner_indent + "else");
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(inner_end + 1),
                               inner_indent + "  break");
        std::string candidate_source = JoinRenderedLines(candidate_lines);
        const std::string candidate_error = ValidateLuaSource(candidate_source, chunk_name);
        if (!candidate_error.empty() &&
            !TryRemoveValidationGuidedRedundantEnd(
                chunk_name, candidate_error, &candidate_source, note, note_prefix)) {
            continue;
        }

        *source = candidate_source;
        AppendCandidateNote(note, note_prefix, "loop_break_else_owner_restored");
        return true;
    }

    return false;
}

bool TryRemoveValidationGuidedRedundantEnd(std::string_view chunk_name,
                                           std::string_view validation_error,
                                           std::string* source,
                                           std::string* note,
                                           std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const std::optional<std::size_t> maybe_line_number =
        TryParseValidationErrorLineNumber(validation_error);
    if (!maybe_line_number.has_value()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::size_t error_index =
        std::min(lines.size(), *maybe_line_number) == 0 ? 0 : std::min(lines.size(), *maybe_line_number) - 1;
    const std::size_t search_begin = error_index > 24 ? error_index - 24 : 0;
    const std::size_t search_end = std::min(lines.size(), error_index + 24);

    std::vector<std::size_t> end_candidates;
    end_candidates.reserve(search_end - search_begin);
    for (std::size_t probe = search_begin; probe < search_end; ++probe) {
        if (trim_line(lines[probe]) == "end") {
            end_candidates.push_back(probe);
        }
    }
    if (end_candidates.empty()) {
        return false;
    }

    std::vector<std::size_t> chosen;
    chosen.reserve(4);
    std::function<bool(std::size_t, int)> try_combinations =
        [&](std::size_t start_index, int remaining) -> bool {
            if (remaining == 0) {
                std::vector<std::string> candidate_lines = lines;
                for (auto it = chosen.rbegin(); it != chosen.rend(); ++it) {
                    candidate_lines.erase(candidate_lines.begin() +
                                          static_cast<std::ptrdiff_t>(*it));
                }
                const std::string candidate_source = JoinRenderedLines(candidate_lines);
                if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
                    return false;
                }

                *source = candidate_source;
                AppendCandidateNote(note, note_prefix, "redundant_end_trimmed");
                return true;
            }

            const std::size_t remaining_needed = static_cast<std::size_t>(remaining);
            for (std::size_t index = start_index;
                 index + remaining_needed <= end_candidates.size();
                 ++index) {
                chosen.push_back(end_candidates[index]);
                if (try_combinations(index + 1, remaining - 1)) {
                    return true;
                }
                chosen.pop_back();
            }
            return false;
        };

    for (int remove_count = 1;
         remove_count <= 4 && remove_count <= static_cast<int>(end_candidates.size());
         ++remove_count) {
        if (try_combinations(0, remove_count)) {
            return true;
        }
    }

    return false;
}

int ExtractValidationErrorLine(std::string_view error) {
    const std::size_t bracket = error.rfind("]:");
    if (bracket == std::string_view::npos) {
        return -1;
    }

    const std::size_t line_begin = bracket + 2;
    const std::size_t line_end = error.find(':', line_begin);
    if (line_end == std::string_view::npos || line_end <= line_begin) {
        return -1;
    }

    int line = 0;
    for (std::size_t index = line_begin; index < line_end; ++index) {
        const unsigned char ch = static_cast<unsigned char>(error[index]);
        if (std::isdigit(ch) == 0) {
            return -1;
        }
        line = (line * 10) + static_cast<int>(ch - '0');
    }
    return line;
}

bool IsValidationOutcomeBetter(std::string_view baseline_error,
                               std::string_view candidate_error) {
    if (candidate_error.empty()) {
        return !baseline_error.empty();
    }
    if (baseline_error.empty()) {
        return false;
    }

    const int baseline_line = ExtractValidationErrorLine(baseline_error);
    const int candidate_line = ExtractValidationErrorLine(candidate_error);
    if (baseline_line >= 0 && candidate_line >= 0) {
        return candidate_line > baseline_line;
    }

    return candidate_error.size() < baseline_error.size();
}

bool TryApplyInvalidAnonymousCallArtifactCleanup(std::string_view chunk_name,
                                                 std::string_view validation_error,
                                                 std::string* source,
                                                 std::string* note,
                                                 std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    if (!SimplifyRenderedLuaInvalidAnonymousCallArtifacts(&lines)) {
        return false;
    }

    const std::string candidate_source = JoinRenderedLines(lines);
    const std::string candidate_error = ValidateLuaSource(candidate_source, chunk_name);
    if (!candidate_error.empty() &&
        !IsValidationOutcomeBetter(validation_error, candidate_error)) {
        return false;
    }

    *source = candidate_source;
    AppendCandidateNote(note, note_prefix, candidate_error.empty()
                                              ? "invalid_anonymous_call_cleanup"
                                              : "invalid_anonymous_call_cleanup_progress");
    return true;
}

bool TryApplyQuestStructuralCleanupBeforeValidation(std::string_view chunk_name,
                                                    std::string* source,
                                                    std::string* note,
                                                    std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const bool structural_changed = ApplyRenderedLuaQuestStructuralCleanupProfile(&lines);
    const bool bootstrap_changed = ApplyRenderedLuaBootstrapCleanupProfile(&lines);
    bool changed = structural_changed || bootstrap_changed;
    changed |= NormalizeRenderedLuaWhitespace(&lines);

    std::string candidate_source = changed ? JoinRenderedLines(lines) : *source;
    std::string validation_error = ValidateLuaSource(candidate_source, chunk_name);
    if (!validation_error.empty()) {
        if ((validation_error.find("unexpected symbol near ';'") != std::string::npos ||
             validation_error.find("expected near '('") != std::string::npos ||
             validation_error.find("<eof>") != std::string::npos) &&
            TryApplyInvalidAnonymousCallArtifactCleanup(chunk_name,
                                                        validation_error,
                                                        &candidate_source,
                                                        note,
                                                        note_prefix)) {
            validation_error = ValidateLuaSource(candidate_source, chunk_name);
            if (validation_error.empty()) {
                *source = std::move(candidate_source);
                return true;
            }
        }
        if (validation_error.find("no loop to break") != std::string::npos &&
            TryRepairValidationGuidedOrphanLoopBreak(
                chunk_name, validation_error, &candidate_source, note, note_prefix)) {
            *source = std::move(candidate_source);
            return true;
        }
        if ((validation_error.find("no loop to break") != std::string::npos ||
             validation_error.find("near 'else'") != std::string::npos ||
             (validation_error.find("<eof>") != std::string::npos &&
              validation_error.find("near 'end'") != std::string::npos)) &&
            TryRemoveValidationGuidedRedundantEnd(
                chunk_name, validation_error, &candidate_source, note, note_prefix)) {
            *source = std::move(candidate_source);
            return true;
        }
        if (validation_error.find("<eof>") != std::string::npos &&
            validation_error.find("near 'end'") != std::string::npos &&
            (TryTrimRedundantTailEndRuns(
                 chunk_name, &candidate_source, note, note_prefix) ||
             TryRestoreBalancedWhileTrueMonitorWrappers(
                 chunk_name, &candidate_source, note, note_prefix))) {
            *source = std::move(candidate_source);
            return true;
        }
        return false;
    }
    if (!changed) {
        return false;
    }

    *source = std::move(candidate_source);
    if (structural_changed) {
        AppendCandidateNote(note, note_prefix, "quest_structural_cleanup_prevalidated");
        AppendRenderedCleanupTelemetryNotes(note, note_prefix, "quest_cleanup");
    }
    if (bootstrap_changed) {
        AppendCandidateNote(note, note_prefix, "bootstrap_cleanup_prevalidated");
        AppendRenderedCleanupTelemetryNotes(note, note_prefix, "bootstrap_cleanup");
    }
    return true;
}

bool IsIdentifierChar(char ch) {
    const unsigned char uch = static_cast<unsigned char>(ch);
    return std::isalnum(uch) != 0 || ch == '_';
}

std::string_view ExtractLeadingIdentifier(std::string_view trimmed) {
    if (trimmed.empty()) {
        return {};
    }

    const unsigned char first = static_cast<unsigned char>(trimmed.front());
    if ((std::isalpha(first) == 0) && trimmed.front() != '_') {
        return {};
    }

    std::size_t index = 1;
    while (index < trimmed.size() && IsIdentifierChar(trimmed[index])) {
        ++index;
    }
    return trimmed.substr(0, index);
}

std::string_view ExtractLocalDeclarationName(std::string_view trimmed) {
    if (!trimmed.starts_with("local ")) {
        return {};
    }

    trimmed = TrimLuaWhitespace(trimmed.substr(6));
    if (trimmed.empty()) {
        return {};
    }

    const std::string_view name = ExtractLeadingIdentifier(trimmed);
    if (name.empty()) {
        return {};
    }

    if (name.size() < trimmed.size()) {
        const char next = trimmed[name.size()];
        if (next != '=' && next != ',' && std::isspace(static_cast<unsigned char>(next)) == 0) {
            return {};
        }
    }

    return name;
}

template <typename Callback>
void ForEachLuaToken(std::string_view line, Callback&& callback) {
    const refii::lua::internal::LuaTokenScanOptions scan_options{
        .mode = refii::lua::internal::LuaTokenScanMode::kRaw,
        .parse_quoted_literals = false,
        .use_stable_copy = true,
    };
    refii::lua::internal::ForEachLuaIdentifierToken(line, scan_options, callback);
}

bool LooksLikeTemporarySelfReferenceAssignment(std::string_view trimmed) {
    const std::string_view lhs = ExtractLeadingIdentifier(trimmed);
    if (!IsRenderTemporaryName(lhs)) {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (!rhs.starts_with(lhs) || rhs.size() == lhs.size()) {
        return false;
    }

    const char next = rhs[lhs.size()];
    return next == '(' || next == '[' || next == '.' || next == ':';
}

bool LooksLikeTemporaryTableFieldAssignment(std::string_view trimmed) {
    const std::string_view lhs = ExtractLeadingIdentifier(trimmed);
    if (!IsRenderTemporaryName(lhs) || trimmed.size() <= lhs.size()) {
        return false;
    }

    const char next = trimmed[lhs.size()];
    if (next != '[' && next != '.') {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    return equal != std::string_view::npos && equal > lhs.size();
}

bool IsRenderedFunctionHeaderLine(std::string_view line) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty()) {
        return false;
    }

    if (trimmed.starts_with("function(") || trimmed.starts_with("function ") ||
        trimmed.starts_with("return function(") ||
        trimmed.starts_with("return function ")) {
        return true;
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    return rhs.starts_with("function(") || rhs.starts_with("function ");
}

std::string NormalizeRenderedFunctionHeaderLine(std::string_view line) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!IsRenderedFunctionHeaderLine(trimmed)) {
        return std::string(trimmed);
    }

    if (trimmed.starts_with("function(") || trimmed.starts_with("function ") ||
        trimmed.starts_with("return function(") ||
        trimmed.starts_with("return function ")) {
        return std::string(trimmed);
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return std::string(trimmed);
    }

    std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (!rhs.starts_with("function(") && !rhs.starts_with("function ")) {
        return std::string(trimmed);
    }

    const std::size_t last_access = lhs.find_last_of(".:");
    if (last_access != std::string_view::npos && last_access + 1 < lhs.size()) {
        lhs = TrimLuaWhitespace(lhs.substr(last_access + 1));
    }

    return std::string(lhs) + " = " + std::string(rhs);
}

std::optional<std::size_t> RenderedFunctionHeaderIndent(std::string_view line) {
    const std::string_view stripped = StripLuaLineComment(line);
    const std::size_t indent = stripped.find_first_not_of(" \t");
    if (indent == std::string_view::npos || !IsRenderedFunctionHeaderLine(stripped)) {
        return std::nullopt;
    }
    return indent;
}

std::vector<RenderedFunctionBlock> CollectTopLevelRenderedFunctionBlocks(
    const std::vector<std::string>& lines) {
    std::vector<RenderedFunctionBlock> blocks;
    std::optional<std::size_t> top_level_indent;

    for (std::size_t index = 0; index < lines.size(); ++index) {
        const std::optional<std::size_t> indent = RenderedFunctionHeaderIndent(lines[index]);
        if (!indent.has_value()) {
            continue;
        }
        if (!top_level_indent.has_value() || *indent < *top_level_indent) {
            top_level_indent = *indent;
        }
    }

    if (!top_level_indent.has_value()) {
        return blocks;
    }

    for (std::size_t index = 0; index < lines.size(); ++index) {
        const std::optional<std::size_t> indent = RenderedFunctionHeaderIndent(lines[index]);
        if (!indent.has_value() || *indent != *top_level_indent) {
            continue;
        }

        RenderedFunctionBlock block;
        block.header_line = NormalizeRenderedFunctionHeaderLine(lines[index]);
        block.begin = index;
        block.end = lines.size() - 1;
        for (std::size_t probe = index + 1; probe < lines.size(); ++probe) {
            const std::optional<std::size_t> next_indent =
                RenderedFunctionHeaderIndent(lines[probe]);
            if (next_indent.has_value() && *next_indent == *top_level_indent) {
                block.end = probe - 1;
                break;
            }
        }
        blocks.push_back(std::move(block));
    }

    return blocks;
}

std::size_t FindRenderedFunctionKeywordOffsetForBytecodeRepair(std::string_view line) {
    const std::string_view candidate = StripLuaLineComment(line);
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t index = 0; index < candidate.size(); ++index) {
        const char ch = candidate[index];
        if (in_single_quote) {
            if (ch == '\\' && index + 1 < candidate.size()) {
                ++index;
                continue;
            }
            if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && index + 1 < candidate.size()) {
                ++index;
                continue;
            }
            if (ch == '"') {
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
        if (ch != 'f' || index + 8 > candidate.size() ||
            candidate.substr(index, 8) != "function") {
            continue;
        }

        const char previous = index == 0 ? '\0' : candidate[index - 1];
        const char next = index + 8 < candidate.size() ? candidate[index + 8] : '\0';
        const bool previous_is_identifier =
            previous == '_' ||
            std::isalnum(static_cast<unsigned char>(previous)) != 0;
        const bool next_is_call_or_space =
            next == '(' || std::isspace(static_cast<unsigned char>(next)) != 0;
        if (!previous_is_identifier && next_is_call_or_space) {
            return index;
        }
    }

    return std::string_view::npos;
}

bool IsRenderedFunctionStartLineForBytecodeRepair(std::string_view line) {
    return FindRenderedFunctionKeywordOffsetForBytecodeRepair(line) != std::string_view::npos;
}

int GetRenderedLuaBlockDeltaForBytecodeRepair(std::string_view line) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty()) {
        return 0;
    }

    if (trimmed == "else" || trimmed == "end else" || StartsWithLuaKeyword(trimmed, "elseif")) {
        return 0;
    }
    if (trimmed == "repeat") {
        return 1;
    }
    const auto is_function_end = [](std::string_view candidate) {
        if (!candidate.starts_with("end")) {
            return false;
        }

        const std::string_view suffix = TrimLuaWhitespace(candidate.substr(3));
        for (const char ch : suffix) {
            if (ch != ')' && ch != ']' && ch != '}' && ch != ',') {
                return false;
            }
        }
        return true;
    };

    if (is_function_end(trimmed) || StartsWithLuaKeyword(trimmed, "until")) {
        return -1;
    }
    if (trimmed == "do") {
        return 1;
    }
    if (StartsWithLuaKeyword(trimmed, "if") ||
        StartsWithLuaKeyword(trimmed, "for") ||
        StartsWithLuaKeyword(trimmed, "while")) {
        return 1;
    }
    if (FindRenderedFunctionKeywordOffsetForBytecodeRepair(trimmed) != std::string_view::npos) {
        return 1;
    }
    return 0;
}

std::size_t FindRenderedFunctionBlockEndForBytecodeRepair(const std::vector<std::string>& lines,
                                                          std::size_t start_line) {
    if (start_line >= lines.size()) {
        return lines.size();
    }

    int block_balance = 0;
    for (std::size_t line_index = start_line; line_index < lines.size(); ++line_index) {
        block_balance += GetRenderedLuaBlockDeltaForBytecodeRepair(lines[line_index]);
        if (block_balance <= 0) {
            return line_index;
        }
    }
    return lines.size();
}

void CollectRenderedFunctionBlocksPreorderRecursive(
    const std::vector<std::string>& lines,
    std::size_t begin,
    std::size_t end,
    std::vector<RenderedFunctionBlock>* out) {
    if (out == nullptr || begin >= lines.size() || begin > end) {
        return;
    }

    for (std::size_t index = begin; index <= end && index < lines.size(); ++index) {
        if (!IsRenderedFunctionStartLineForBytecodeRepair(lines[index])) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEndForBytecodeRepair(lines, index);
        if (function_end >= lines.size() || function_end < index || function_end > end) {
            continue;
        }

        out->push_back(RenderedFunctionBlock{
            .header_line = NormalizeRenderedFunctionHeaderLine(lines[index]),
            .begin = index,
            .end = function_end,
        });
        if (index + 1 <= function_end) {
            CollectRenderedFunctionBlocksPreorderRecursive(lines,
                                                          index + 1,
                                                          function_end - 1,
                                                          out);
        }
        index = function_end;
    }
}

std::vector<RenderedFunctionBlock> CollectRenderedFunctionBlocksPreorder(
    const std::vector<std::string>& lines) {
    std::vector<RenderedFunctionBlock> blocks;
    if (lines.empty()) {
        return blocks;
    }

    CollectRenderedFunctionBlocksPreorderRecursive(lines, 0, lines.size() - 1, &blocks);
    return blocks;
}

std::vector<RenderedFunctionBlock> CollectRenderedFunctionBlocksPreorderInRange(
    const std::vector<std::string>& lines,
    std::size_t begin,
    std::size_t end) {
    std::vector<RenderedFunctionBlock> blocks;
    if (lines.empty() || begin >= lines.size() || begin > end) {
        return blocks;
    }

    CollectRenderedFunctionBlocksPreorderRecursive(
        lines,
        begin,
        std::min(end, lines.size() - 1),
        &blocks);
    return blocks;
}

std::vector<RenderedFunctionBlock> CollectTopLevelRenderedFunctionBlocksForBytecodeRepair(
    const std::vector<std::string>& lines) {
    std::vector<RenderedFunctionBlock> blocks;
    for (std::size_t index = 0; index < lines.size(); ++index) {
        if (!IsRenderedFunctionStartLineForBytecodeRepair(lines[index])) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEndForBytecodeRepair(lines, index);
        if (function_end >= lines.size() || function_end < index) {
            continue;
        }

        blocks.push_back(RenderedFunctionBlock{
            .header_line = NormalizeRenderedFunctionHeaderLine(lines[index]),
            .begin = index,
            .end = function_end,
        });
        index = function_end;
    }

    return blocks;
}

std::vector<const Lua51Function*> CollectLuaFunctionChildrenPreorder(const Lua51Chunk& chunk) {
    std::vector<const Lua51Function*> functions;
    AppendLuaFunctionChildrenPreorder(chunk.main, &functions);
    return functions;
}

std::vector<std::string> ParseRenderedFunctionParameterNames(std::string_view line) {
    std::vector<std::string> names;
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    std::size_t open_paren = std::string_view::npos;
    std::size_t close_paren = std::string_view::npos;

    const std::size_t function_offset = FindRenderedFunctionKeywordOffsetForBytecodeRepair(trimmed);
    if (function_offset != std::string_view::npos) {
        open_paren = trimmed.find('(', function_offset + 8);
        if (open_paren != std::string_view::npos) {
            close_paren = trimmed.find(')', open_paren + 1);
        }
    }

    if (open_paren == std::string_view::npos || close_paren == std::string_view::npos) {
        open_paren = trimmed.find('(');
        if (open_paren != std::string_view::npos) {
            close_paren = trimmed.find(')', open_paren + 1);
        }
    }

    if (open_paren == std::string_view::npos || close_paren == std::string_view::npos ||
        close_paren <= open_paren) {
        return names;
    }

    std::size_t start = open_paren + 1;
    while (start < close_paren) {
        const std::size_t comma = trimmed.find(',', start);
        const std::size_t end =
            comma == std::string_view::npos || comma > close_paren ? close_paren : comma;
        const std::string_view part =
            TrimLuaWhitespace(trimmed.substr(start, end - start));
        if (!part.empty()) {
            names.emplace_back(part);
        }
        if (comma == std::string_view::npos || comma >= close_paren) {
            break;
        }
        start = comma + 1;
    }

    return names;
}

std::optional<std::string> RenderLua51RKOperand(
    const Lua51Function& function,
    int rk,
    const std::vector<std::string>& parameter_names) {
    constexpr int kLua51BitRk = 1 << 8;
    if (rk < 0) {
        return std::nullopt;
    }

    if ((rk & kLua51BitRk) != 0) {
        const std::size_t constant_index =
            static_cast<std::size_t>(rk & ~kLua51BitRk);
        if (constant_index >= function.constants.size()) {
            return std::nullopt;
        }
        return RenderLuaConstant(function.constants[constant_index]);
    }

    const std::size_t register_index = static_cast<std::size_t>(rk);
    if (register_index >= parameter_names.size()) {
        return std::nullopt;
    }

    const std::string_view name = TrimLuaWhitespace(parameter_names[register_index]);
    if (name.empty() || !IsValidLuaIdentifier(name) || IsLuaKeyword(name)) {
        return std::nullopt;
    }
    return std::string(name);
}

std::vector<std::string> CollectRenderedLuaComparatorAssertExpressions(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<std::string> expressions;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 5) {
        return expressions;
    }

    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    for (std::size_t index = 0; index < instructions.size(); ++index) {
        if (index + 4 < instructions.size()) {
            const std::size_t base_index = index;
            const Lua51Instruction& compare = instructions[index];
            const Lua51Instruction& jump = instructions[index + 1];
            const Lua51Instruction& false_load = instructions[index + 2];
            const Lua51Instruction& true_load = instructions[index + 3];
            const Lua51Instruction& call = instructions[index + 4];

            bool supported_compare = false;
            switch (compare.opcode) {
                case Lua51Opcode::kLt:
                case Lua51Opcode::kLe:
                case Lua51Opcode::kEq:
                    supported_compare = true;
                    break;
                default:
                    break;
            }

            if (supported_compare &&
                jump.opcode == Lua51Opcode::kJmp &&
                jump.sbx == 1 &&
                false_load.opcode == Lua51Opcode::kLoadBool &&
                true_load.opcode == Lua51Opcode::kLoadBool &&
                false_load.a == true_load.a &&
                false_load.b == 0 &&
                false_load.c == 1 &&
                true_load.b == 1 &&
                true_load.c == 0 &&
                call.opcode == Lua51Opcode::kCall &&
                call.b == 2 &&
                call.c == 1 &&
                false_load.a == call.a + 1 &&
                static_cast<std::size_t>(call.a) < tracked_registers.size()) {
                const std::optional<std::string> callee =
                    tracked_registers[static_cast<std::size_t>(call.a)];
                const std::optional<std::string> expression =
                    RenderLua51TrackedCompareExpression(function,
                                                        parameter_names,
                                                        tracked_registers,
                                                        index,
                                                        compare);
                if (callee.has_value() &&
                    NormalizeRenderedTrivialParenthesizedLiterals(*callee) == "assert" &&
                    expression.has_value()) {
                    expressions.push_back(*expression);
                    for (std::size_t step_index = base_index; step_index <= base_index + 4;
                         ++step_index) {
                        StepLua51TrackedRegisterExpressions(function,
                                                            parameter_names,
                                                            instructions[step_index],
                                                            step_index,
                                                            &tracked_registers);
                    }
                    index = base_index + 4;
                    continue;
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[index],
                                            index,
                                            &tracked_registers);
    }

    return expressions;
}

std::optional<std::string> RenderLua51TrackedRKOperand(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const std::vector<std::optional<std::string>>& tracked_registers,
    std::size_t instruction_index,
    int rk);

std::optional<std::string> RenderLua51TrackedCompareExpression(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const std::vector<std::optional<std::string>>& tracked_registers,
    std::size_t instruction_index,
    const Lua51Instruction& compare) {
    const std::optional<std::string> lhs =
        RenderLua51TrackedRKOperand(function,
                                    parameter_names,
                                    tracked_registers,
                                    instruction_index,
                                    compare.b);
    const std::optional<std::string> rhs =
        RenderLua51TrackedRKOperand(function,
                                    parameter_names,
                                    tracked_registers,
                                    instruction_index,
                                    compare.c);
    if (!lhs.has_value() || !rhs.has_value()) {
        return std::nullopt;
    }

    std::string comparator;
    switch (compare.opcode) {
        case Lua51Opcode::kEq:
            comparator = compare.a != 0 ? "~=" : "==";
            break;
        case Lua51Opcode::kLt:
            comparator = compare.a != 0 ? ">=" : "<";
            break;
        case Lua51Opcode::kLe:
            comparator = compare.a != 0 ? ">" : "<=";
            break;
        default:
            return std::nullopt;
    }

    return *lhs + " " + comparator + " " + *rhs;
}

std::optional<std::string> SwapRenderedComparatorExpression(std::string_view expression) {
    const std::string trimmed = std::string(TrimLuaWhitespace(expression));
    const std::array<std::pair<std::string_view, std::string_view>, 6> comparators = {{
        {" <= ", " >= "},
        {" >= ", " <= "},
        {" ~= ", " ~= "},
        {" == ", " == "},
        {" < ", " > "},
        {" > ", " < "},
    }};

    for (const auto& [needle, swapped] : comparators) {
        const std::size_t position = trimmed.find(needle);
        if (position == std::string::npos) {
            continue;
        }

        const std::string lhs = std::string(TrimLuaWhitespace(trimmed.substr(0, position)));
        const std::string rhs = std::string(
            TrimLuaWhitespace(trimmed.substr(position + needle.size())));
        if (lhs.empty() || rhs.empty()) {
            return std::nullopt;
        }
        return rhs + std::string(swapped) + lhs;
    }

    return std::nullopt;
}

bool TryParseRenderedComparatorExpression(std::string_view expression,
                                         std::string* lhs,
                                         std::string* comparator,
                                         std::string* rhs) {
    if (lhs == nullptr || comparator == nullptr || rhs == nullptr) {
        return false;
    }

    const std::string trimmed = std::string(TrimLuaWhitespace(expression));
    const std::array<std::string_view, 6> comparators = {{
        " <= ",
        " >= ",
        " ~= ",
        " == ",
        " < ",
        " > ",
    }};

    for (std::string_view needle : comparators) {
        const std::size_t position = trimmed.find(needle);
        if (position == std::string::npos) {
            continue;
        }

        *lhs = std::string(TrimLuaWhitespace(trimmed.substr(0, position)));
        *comparator = std::string(needle);
        *rhs = std::string(TrimLuaWhitespace(trimmed.substr(position + needle.size())));
        return !lhs->empty() && !rhs->empty();
    }

    return false;
}

std::string NormalizeRenderedComparatorOperand(std::string_view operand) {
    std::string normalized = NormalizeRenderedTrivialParenthesizedLiterals(
        std::string(TrimLuaWhitespace(operand)));

    std::string callee;
    std::vector<std::string> arguments;
    if (!ParseRenderedSingleCallExpression(normalized, &callee, &arguments) || arguments.empty()) {
        return normalized;
    }

    const std::size_t colon = callee.rfind(':');
    if (colon == std::string::npos || colon == 0 || colon + 1 >= callee.size()) {
        return normalized;
    }

    const std::string owner = NormalizeRenderedTrivialParenthesizedLiterals(
        std::string(TrimLuaWhitespace(callee.substr(0, colon))));
    const std::string first_argument = NormalizeRenderedTrivialParenthesizedLiterals(
        std::string(TrimLuaWhitespace(arguments.front())));
    if (owner.empty() || first_argument != owner) {
        return normalized;
    }

    std::vector<std::string> rebuilt_arguments;
    rebuilt_arguments.reserve(arguments.size());
    for (const std::string& argument : arguments) {
        rebuilt_arguments.push_back(NormalizeRenderedTrivialParenthesizedLiterals(
            std::string(TrimLuaWhitespace(argument))));
    }
    return BuildRenderedCallExpression(callee, rebuilt_arguments);
}

const Lua51Constant* GetLua51RKConstant(const Lua51Function& function, int rk) {
    constexpr int kLua51BitRk = 1 << 8;
    if (rk < 0 || (rk & kLua51BitRk) == 0) {
        return nullptr;
    }

    const std::size_t constant_index = static_cast<std::size_t>(rk & ~kLua51BitRk);
    if (constant_index >= function.constants.size()) {
        return nullptr;
    }
    return &function.constants[constant_index];
}

std::optional<std::string> RenderLua51RegisterOperand(
    const std::vector<std::string>& parameter_names,
    int reg) {
    if (reg < 0) {
        return std::nullopt;
    }

    const std::size_t register_index = static_cast<std::size_t>(reg);
    if (register_index >= parameter_names.size()) {
        return std::nullopt;
    }

    const std::string_view name = TrimLuaWhitespace(parameter_names[register_index]);
    if (name.empty() || !IsValidLuaIdentifier(name) || IsLuaKeyword(name)) {
        return std::nullopt;
    }
    return std::string(name);
}

std::optional<int> ParseLua51RKPositiveIntegralConstant(const Lua51Function& function, int rk) {
    const Lua51Constant* constant = GetLua51RKConstant(function, rk);
    if (constant == nullptr || constant->type != Lua51ConstantType::kNumber) {
        return std::nullopt;
    }

    const double value = constant->number_value;
    const int integral = static_cast<int>(value);
    if (value <= 0.0 || static_cast<double>(integral) != value) {
        return std::nullopt;
    }
    return integral;
}

std::optional<std::string> RenderLua51TableFieldAccess(std::string_view owner,
                                                       const Lua51Constant& field_constant) {
    if (field_constant.type != Lua51ConstantType::kString || owner.empty()) {
        return std::nullopt;
    }

    const std::string_view field_name = field_constant.string_value;
    if (field_name.empty()) {
        return std::nullopt;
    }

    if (IsValidLuaIdentifier(field_name) && !IsLuaKeyword(field_name)) {
        return std::string(owner) + "." + std::string(field_name);
    }

    return std::string(owner) + "[" + RenderLuaConstant(field_constant) + "]";
}

namespace {
struct CachedLua51DeclarationSet {
    const uint32_t* code_ptr = nullptr;
    std::size_t code_size = 0;
    uint8_t parameter_count = 0;
    uint8_t max_stack_size = 0;
    std::vector<Lua51Declaration> declarations;
};

const std::vector<Lua51Declaration>& GetCachedLua51Declarations(const Lua51Function& function) {
    thread_local std::unordered_map<const Lua51Function*, CachedLua51DeclarationSet> cache;
    CachedLua51DeclarationSet& entry = cache[&function];
    if (entry.code_ptr != function.code.data() ||
        entry.code_size != function.code.size() ||
        entry.parameter_count != function.parameter_count ||
        entry.max_stack_size != function.max_stack_size) {
        entry.code_ptr = function.code.data();
        entry.code_size = function.code.size();
        entry.parameter_count = function.parameter_count;
        entry.max_stack_size = function.max_stack_size;
        entry.declarations = InferLua51Declarations(function);
    }
    return entry.declarations;
}

const Lua51Declaration* TryGetActiveLua51Declaration(const Lua51Function& function,
                                                     std::size_t instruction_index,
                                                     int reg) {
    if (reg < 0) {
        return nullptr;
    }

    const uint32_t pc = static_cast<uint32_t>(instruction_index + 1);
    for (const Lua51Declaration& declaration : GetCachedLua51Declarations(function)) {
        if (declaration.name.empty() || !IsValidLuaIdentifier(declaration.name) ||
            IsLuaKeyword(declaration.name)) {
            continue;
        }
        if (declaration.reg == static_cast<uint32_t>(reg) &&
            declaration.begin <= pc && pc <= declaration.end) {
            return &declaration;
        }
    }

    return nullptr;
}

bool LooksLikeGuessedLua51DeclarationName(std::string_view name) {
    if (name.size() < 4 || (name.front() != 'A' && name.front() != 'L')) {
        return false;
    }

    std::size_t index = 1;
    bool saw_reg_digit = false;
    while (index < name.size() &&
           std::isdigit(static_cast<unsigned char>(name[index])) != 0) {
        saw_reg_digit = true;
        ++index;
    }
    if (!saw_reg_digit || index >= name.size() || name[index] != '_') {
        return false;
    }
    ++index;

    bool saw_suffix_digit = false;
    while (index < name.size()) {
        if (std::isdigit(static_cast<unsigned char>(name[index])) == 0) {
            return false;
        }
        saw_suffix_digit = true;
        ++index;
    }
    return saw_suffix_digit;
}

bool ContainsGuessedLua51LocalBase(std::string_view expression) {
    const std::string_view trimmed = TrimLuaWhitespace(expression);
    if (trimmed.empty()) {
        return false;
    }

    std::size_t identifier_end = 0;
    while (identifier_end < trimmed.size()) {
        const char ch = trimmed[identifier_end];
        if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
            (ch >= '0' && ch <= '9') || ch == '_') {
            ++identifier_end;
            continue;
        }
        break;
    }

    if (identifier_end == 0) {
        return false;
    }

    return LooksLikeGuessedLua51DeclarationName(trimmed.substr(0, identifier_end));
}

bool IsRecoverableLua51LocalName(std::string_view name) {
    return !name.empty() && IsValidLuaIdentifier(name) && !IsLuaKeyword(name) &&
           !name.starts_with('(');
}

bool IsConcreteLua51Declaration(const Lua51Declaration& declaration) {
    return !declaration.guessed && IsRecoverableLua51LocalName(declaration.name) &&
           !LooksLikeGuessedLua51DeclarationName(declaration.name);
}

std::unordered_set<std::string> CollectRecoverableLua51LocalNames(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::unordered_set<std::string> names;
    names.reserve(function.locals.size());

    const auto is_parameter_name = [&](std::string_view name) {
        return std::find(parameter_names.begin(), parameter_names.end(), name) !=
               parameter_names.end();
    };
    const auto maybe_add_name = [&](std::string_view name) {
        if (!IsRecoverableLua51LocalName(name) || is_parameter_name(name)) {
            return;
        }
        names.emplace(name);
    };

    for (const Lua51Local& local : function.locals) {
        maybe_add_name(local.name);
    }

    const std::vector<Lua51Declaration>& declarations =
        GetCachedLua51Declarations(function);
    for (const Lua51Declaration& declaration : declarations) {
        if (!IsConcreteLua51Declaration(declaration)) {
            continue;
        }
        maybe_add_name(declaration.name);
    }

    if (!names.empty()) {
        return names;
    }

    for (const Lua51Declaration& declaration : declarations) {
        maybe_add_name(declaration.name);
    }
    return names;
}
}  // namespace

std::optional<std::string> TryGetActiveLua51LocalRegisterName(const Lua51Function& function,
                                                              std::size_t instruction_index,
                                                              int reg) {
    if (const Lua51Declaration* declaration =
            TryGetActiveLua51Declaration(function, instruction_index, reg);
        declaration != nullptr) {
        return declaration->name;
    }
    return std::nullopt;
}

std::optional<std::string> RenderLua51TrackedRegisterOperand(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const std::vector<std::optional<std::string>>& tracked_registers,
    std::size_t instruction_index,
    int reg) {
    if (reg < 0) {
        return std::nullopt;
    }

    std::optional<std::string> guessed_local_fallback;
    if (const std::optional<std::string> active_local =
            TryGetActiveLua51LocalRegisterName(function, instruction_index, reg);
        active_local.has_value()) {
        if (!LooksLikeGuessedLua51DeclarationName(*active_local)) {
            return active_local;
        }
        guessed_local_fallback = active_local;
    }

    if (const std::optional<std::string> parameter_name =
            RenderLua51RegisterOperand(parameter_names, reg);
        parameter_name.has_value()) {
        return parameter_name;
    }

    const std::size_t register_index = static_cast<std::size_t>(reg);
    if (register_index >= tracked_registers.size()) {
        return guessed_local_fallback;
    }

    if (tracked_registers[register_index].has_value()) {
        return tracked_registers[register_index];
    }

    return guessed_local_fallback;
}

std::optional<std::string> RenderLua51TrackedInputRegisterOperand(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const std::vector<std::optional<std::string>>& tracked_registers,
    std::size_t instruction_index,
    int reg) {
    if (reg < 0) {
        return std::nullopt;
    }

    const std::size_t register_index = static_cast<std::size_t>(reg);
    if (register_index < tracked_registers.size() &&
        tracked_registers[register_index].has_value()) {
        return tracked_registers[register_index];
    }

    if (const std::optional<std::string> parameter_name =
            RenderLua51RegisterOperand(parameter_names, reg);
        parameter_name.has_value()) {
        return parameter_name;
    }

    std::optional<std::string> guessed_local_fallback;
    if (const std::optional<std::string> active_local =
            TryGetActiveLua51LocalRegisterName(function, instruction_index, reg);
        active_local.has_value()) {
        if (!LooksLikeGuessedLua51DeclarationName(*active_local)) {
            return active_local;
        }
        guessed_local_fallback = active_local;
    }

    return guessed_local_fallback;
}

std::optional<std::string> RenderLua51TrackedRKOperand(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const std::vector<std::optional<std::string>>& tracked_registers,
    std::size_t instruction_index,
    int rk) {
    constexpr int kLua51BitRk = 1 << 8;
    if (rk < 0) {
        return std::nullopt;
    }

    if ((rk & kLua51BitRk) != 0) {
        const std::size_t constant_index =
            static_cast<std::size_t>(rk & ~kLua51BitRk);
        if (constant_index >= function.constants.size()) {
            return std::nullopt;
        }
        return RenderLuaConstant(function.constants[constant_index]);
    }

    return RenderLua51TrackedRegisterOperand(function,
                                             parameter_names,
                                             tracked_registers,
                                             instruction_index,
                                             rk);
}

std::optional<std::string> RenderLua51TrackedTableAccess(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const std::vector<std::optional<std::string>>& tracked_registers,
    std::size_t instruction_index,
    int owner_reg,
    int key_rk) {
    const std::optional<std::string> owner =
        RenderLua51TrackedRegisterOperand(function,
                                          parameter_names,
                                          tracked_registers,
                                          instruction_index,
                                          owner_reg);
    if (!owner.has_value()) {
        return std::nullopt;
    }

    if (const Lua51Constant* key_constant = GetLua51RKConstant(function, key_rk);
        key_constant != nullptr) {
        return RenderLua51TableFieldAccess(*owner, *key_constant);
    }

    const std::optional<std::string> key =
        RenderLua51TrackedRegisterOperand(function,
                                          parameter_names,
                                          tracked_registers,
                                          instruction_index,
                                          key_rk);
    if (!key.has_value()) {
        return std::nullopt;
    }

    return *owner + "[" + *key + "]";
}

std::optional<std::string> TryRenderLua51TrackedSelfMethodCallExpression(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const std::vector<std::optional<std::string>>& tracked_registers,
    std::size_t instruction_index,
    const Lua51Instruction& call_instruction) {
    if (call_instruction.opcode != Lua51Opcode::kCall ||
        call_instruction.a < 0 ||
        call_instruction.b < 2 ||
        instruction_index == 0 ||
        instruction_index >= function.code.size()) {
        return std::nullopt;
    }

    const Lua51Instruction self_instruction =
        DecodeLua51Instruction(function.code[instruction_index - 1],
                               static_cast<uint32_t>(instruction_index));
    if (self_instruction.opcode != Lua51Opcode::kSelf ||
        self_instruction.a != call_instruction.a ||
        self_instruction.b != call_instruction.a + 1) {
        return std::nullopt;
    }

    const Lua51Constant* method_constant = GetLua51RKConstant(function, self_instruction.c);
    if (method_constant == nullptr ||
        method_constant->type != Lua51ConstantType::kString ||
        !IsValidLuaIdentifier(method_constant->string_value)) {
        return std::nullopt;
    }

    const std::optional<std::string> receiver_expression =
        RenderLua51TrackedInputRegisterOperand(function,
                                               parameter_names,
                                               tracked_registers,
                                               instruction_index,
                                               call_instruction.a + 1);
    if (!receiver_expression.has_value()) {
        return std::nullopt;
    }

    const auto normalize_receiver_expression = [](std::string_view expression) {
        std::string_view normalized = TrimLuaWhitespace(expression);
        while (normalized.size() >= 2 && normalized.front() == '(' &&
               normalized.back() == ')') {
            normalized = TrimLuaWhitespace(normalized.substr(1, normalized.size() - 2));
        }
        return std::string(normalized);
    };
    const std::string normalized_receiver =
        normalize_receiver_expression(*receiver_expression);
    if (normalized_receiver.empty() ||
        !IsSafeLuaJoinPreservation(normalized_receiver)) {
        return std::nullopt;
    }

    std::vector<std::string> arguments;
    if (call_instruction.b > 2) {
        arguments.reserve(static_cast<std::size_t>(call_instruction.b - 2));
    }
    for (int arg_reg = call_instruction.a + 2;
         arg_reg < call_instruction.a + call_instruction.b;
         ++arg_reg) {
        const std::optional<std::string> argument =
            RenderLua51TrackedInputRegisterOperand(function,
                                                   parameter_names,
                                                   tracked_registers,
                                                   instruction_index,
                                                   arg_reg);
        if (!argument.has_value()) {
            return std::nullopt;
        }
        arguments.push_back(*argument);
    }

    std::string rendered =
        normalized_receiver + ":" + method_constant->string_value + "(";
    for (std::size_t arg_index = 0; arg_index < arguments.size(); ++arg_index) {
        if (arg_index != 0) {
            rendered += ", ";
        }
        rendered += arguments[arg_index];
    }
    rendered += ")";
    return rendered;
}

std::optional<std::string> RenderLua51TrackedCallExpression(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const std::vector<std::optional<std::string>>& tracked_registers,
    std::size_t instruction_index,
    const Lua51Instruction& instruction) {
    if (instruction.opcode != Lua51Opcode::kCall || instruction.b < 1 || instruction.c < 1) {
        return std::nullopt;
    }

    if (const std::optional<std::string> canonical_self_call =
            TryRenderLua51TrackedSelfMethodCallExpression(function,
                                                          parameter_names,
                                                          tracked_registers,
                                                          instruction_index,
                                                          instruction);
        canonical_self_call.has_value()) {
        return canonical_self_call;
    }

    const std::optional<std::string> callee =
        RenderLua51TrackedInputRegisterOperand(function,
                                               parameter_names,
                                               tracked_registers,
                                               instruction_index,
                                               instruction.a);
    if (!callee.has_value()) {
        return std::nullopt;
    }

    std::vector<std::string> args;
    if (instruction.b >= 2) {
        args.reserve(static_cast<std::size_t>(instruction.b - 1));
    }
    for (int arg_reg = instruction.a + 1; arg_reg < instruction.a + instruction.b; ++arg_reg) {
        const std::optional<std::string> argument =
            RenderLua51TrackedInputRegisterOperand(function,
                                                   parameter_names,
                                                   tracked_registers,
                                                   instruction_index,
                                                   arg_reg);
        if (!argument.has_value()) {
            return std::nullopt;
        }
        args.push_back(*argument);
    }

    return BuildRenderedCallExpression(*callee, args);
}

std::vector<std::string_view> SplitRenderedTopLevelSemicolonStatements(std::string_view line) {
    line = StripLuaLineComment(line);
    std::vector<std::string_view> parts;

    bool in_single_quote = false;
    bool in_double_quote = false;
    bool escaped = false;
    std::size_t statement_start = 0;
    for (std::size_t index = 0; index < line.size(); ++index) {
        const char ch = line[index];
        if (escaped) {
            escaped = false;
            continue;
        }
        if ((in_single_quote || in_double_quote) && ch == '\\') {
            escaped = true;
            continue;
        }
        if (!in_double_quote && ch == '\'') {
            in_single_quote = !in_single_quote;
            continue;
        }
        if (!in_single_quote && ch == '"') {
            in_double_quote = !in_double_quote;
            continue;
        }
        if (in_single_quote || in_double_quote || ch != ';') {
            continue;
        }

        const std::string_view statement =
            TrimLuaWhitespace(line.substr(statement_start, index - statement_start));
        if (!statement.empty()) {
            parts.push_back(statement);
        }
        statement_start = index + 1;
    }

    const std::string_view tail = TrimLuaWhitespace(line.substr(statement_start));
    if (!tail.empty()) {
        parts.push_back(tail);
    }
    return parts;
}

void AssignTrackedRegisterExpression(std::vector<std::optional<std::string>>* tracked_registers,
                                     int reg,
                                     std::optional<std::string> expression) {
    if (tracked_registers == nullptr || reg < 0) {
        return;
    }

    const std::size_t register_index = static_cast<std::size_t>(reg);
    if (register_index >= tracked_registers->size()) {
        return;
    }

    (*tracked_registers)[register_index] = std::move(expression);
}

void ClearTrackedRegisterRange(std::vector<std::optional<std::string>>* tracked_registers,
                               int start_reg,
                               int end_reg) {
    if (tracked_registers == nullptr || start_reg < 0 || end_reg < start_reg) {
        return;
    }

    const std::size_t begin = static_cast<std::size_t>(start_reg);
    const std::size_t end = static_cast<std::size_t>(end_reg);
    if (begin >= tracked_registers->size()) {
        return;
    }

    const std::size_t clamped_end = std::min(end, tracked_registers->size() - 1);
    for (std::size_t index = begin; index <= clamped_end; ++index) {
        (*tracked_registers)[index].reset();
    }
}

void StepLua51TrackedRegisterExpressions(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const Lua51Instruction& instruction,
    std::size_t instruction_index,
    std::vector<std::optional<std::string>>* tracked_registers) {
    if (tracked_registers == nullptr) {
        return;
    }

    switch (instruction.opcode) {
        case Lua51Opcode::kMove:
            AssignTrackedRegisterExpression(
                tracked_registers,
                instruction.a,
                RenderLua51TrackedRegisterOperand(function,
                                                  parameter_names,
                                                  *tracked_registers,
                                                  instruction_index,
                                                  instruction.b));
            return;
        case Lua51Opcode::kLoadK:
            if (static_cast<std::size_t>(instruction.bx) < function.constants.size()) {
                AssignTrackedRegisterExpression(
                    tracked_registers,
                    instruction.a,
                    RenderLuaConstant(function.constants[static_cast<std::size_t>(instruction.bx)]));
            } else {
                AssignTrackedRegisterExpression(tracked_registers, instruction.a, std::nullopt);
            }
            return;
        case Lua51Opcode::kLoadBool:
            AssignTrackedRegisterExpression(tracked_registers,
                                            instruction.a,
                                            instruction.b != 0 ? std::optional<std::string>("true")
                                                               : std::optional<std::string>("false"));
            return;
        case Lua51Opcode::kLoadNil:
            ClearTrackedRegisterRange(tracked_registers, instruction.a, instruction.b);
            return;
        case Lua51Opcode::kGetGlobal:
            if (static_cast<std::size_t>(instruction.bx) < function.constants.size()) {
                const Lua51Constant& constant =
                    function.constants[static_cast<std::size_t>(instruction.bx)];
                if (constant.type == Lua51ConstantType::kString &&
                    IsValidLuaIdentifier(constant.string_value) &&
                    !IsLuaKeyword(constant.string_value)) {
                    AssignTrackedRegisterExpression(tracked_registers,
                                                    instruction.a,
                                                    constant.string_value);
                } else {
                    AssignTrackedRegisterExpression(tracked_registers,
                                                    instruction.a,
                                                    std::nullopt);
                }
            } else {
                AssignTrackedRegisterExpression(tracked_registers, instruction.a, std::nullopt);
            }
            return;
        case Lua51Opcode::kGetTable:
            AssignTrackedRegisterExpression(
                tracked_registers,
                instruction.a,
                RenderLua51TrackedTableAccess(function,
                                              parameter_names,
                                              *tracked_registers,
                                              instruction_index,
                                              instruction.b,
                                              instruction.c));
            return;
        case Lua51Opcode::kNewTable:
            AssignTrackedRegisterExpression(tracked_registers, instruction.a, std::string("{}"));
            return;
        case Lua51Opcode::kSelf: {
            const std::optional<std::string> receiver =
                RenderLua51TrackedRegisterOperand(function,
                                                  parameter_names,
                                                  *tracked_registers,
                                                  instruction_index,
                                                  instruction.b);
            const Lua51Constant* key_constant = GetLua51RKConstant(function, instruction.c);
            if (receiver.has_value() && key_constant != nullptr &&
                key_constant->type == Lua51ConstantType::kString &&
                IsValidLuaIdentifier(key_constant->string_value) &&
                !IsLuaKeyword(key_constant->string_value)) {
                AssignTrackedRegisterExpression(tracked_registers,
                                                instruction.a,
                                                *receiver + "." + key_constant->string_value);
                AssignTrackedRegisterExpression(tracked_registers, instruction.a + 1, *receiver);
            } else {
                AssignTrackedRegisterExpression(tracked_registers, instruction.a, std::nullopt);
                AssignTrackedRegisterExpression(tracked_registers, instruction.a + 1, std::nullopt);
            }
            return;
        }
        case Lua51Opcode::kCall: {
            if (instruction.c >= 2) {
                std::optional<std::string> rendered_call;
                if (instruction.b >= 1) {
                    const std::optional<std::string> callee =
                        RenderLua51TrackedRegisterOperand(function,
                                                          parameter_names,
                                                          *tracked_registers,
                                                          instruction_index,
                                                          instruction.a);
                    if (callee.has_value()) {
                        bool all_args_rendered = true;
                        std::vector<std::string> args;
                        if (instruction.b >= 2) {
                            args.reserve(static_cast<std::size_t>(instruction.b - 1));
                        }
                        for (int arg_reg = instruction.a + 1;
                             arg_reg < instruction.a + instruction.b;
                             ++arg_reg) {
                            const std::optional<std::string> argument =
                                RenderLua51TrackedRegisterOperand(function,
                                                                  parameter_names,
                                                                  *tracked_registers,
                                                                  instruction_index,
                                                                  arg_reg);
                            if (!argument.has_value()) {
                                all_args_rendered = false;
                                break;
                            }
                            args.push_back(*argument);
                        }
                        if (all_args_rendered) {
                            rendered_call = BuildRenderedCallExpression(*callee, args);
                        }
                    }
                }
                AssignTrackedRegisterExpression(tracked_registers,
                                                instruction.a,
                                                std::move(rendered_call));
                if (instruction.c > 2) {
                    ClearTrackedRegisterRange(tracked_registers,
                                              instruction.a + 1,
                                              instruction.a + instruction.c - 2);
                }
            } else {
                AssignTrackedRegisterExpression(tracked_registers, instruction.a, std::nullopt);
            }
            return;
        }
        case Lua51Opcode::kClosure:
            AssignTrackedRegisterExpression(tracked_registers, instruction.a, std::nullopt);
            return;
        default:
            return;
    }
}

std::vector<RenderedCallResultPattern> CollectLua51RenderedCallResultPatterns(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<RenderedCallResultPattern> patterns;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));

    for (std::size_t instruction_index = 0; instruction_index < instructions.size();
         ++instruction_index) {
        const Lua51Instruction& instruction = instructions[instruction_index];
        if (instruction.opcode == Lua51Opcode::kCall && instruction.c >= 2) {
            const std::optional<std::string> expression =
                RenderLua51TrackedCallExpression(function,
                                                 parameter_names,
                                                 tracked_registers,
                                                 instruction_index,
                                                 instruction);
            if (expression.has_value()) {
                bool tested_immediately = false;
                if (instruction_index + 1 < instructions.size()) {
                    const Lua51Instruction& next = instructions[instruction_index + 1];
                    tested_immediately =
                        next.opcode == Lua51Opcode::kTest && next.a == instruction.a;
                }
                patterns.push_back(RenderedCallResultPattern{
                    .target_reg = instruction.a,
                    .instruction_index = instruction_index,
                    .expression = *expression,
                    .tested_immediately = tested_immediately,
                });
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instruction,
                                            instruction_index,
                                            &tracked_registers);
    }

    return patterns;
}

std::vector<RenderedDirectStatementCallPattern> CollectLua51RenderedDirectStatementCallPatterns(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<RenderedDirectStatementCallPattern> patterns;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));

    for (std::size_t instruction_index = 0; instruction_index < instructions.size();
         ++instruction_index) {
        const Lua51Instruction& instruction = instructions[instruction_index];
        if (instruction.opcode == Lua51Opcode::kCall && instruction.c == 1) {
            const std::optional<std::string> expression =
                RenderLua51TrackedCallExpression(function,
                                                 parameter_names,
                                                 tracked_registers,
                                                 instruction_index,
                                                 instruction);
            if (expression.has_value()) {
                std::string callee;
                std::vector<std::string> arguments;
                if (ParseRenderedSingleCallExpression(*expression, &callee, &arguments)) {
                    patterns.push_back(RenderedDirectStatementCallPattern{
                        .instruction_index = instruction_index,
                        .expression = *expression,
                        .callee = std::move(callee),
                        .terminal_callee = ExtractRenderedCallTerminal(*expression),
                        .argument_count = arguments.size(),
                    });
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[instruction_index],
                                            instruction_index,
                                            &tracked_registers);
    }

    return patterns;
}

struct RenderedIteratorArgumentPattern {
    std::string local_name;
    std::string call_expression;
    std::string argument_expression;
};

std::vector<RenderedIteratorArgumentPattern> CollectLua51RenderedIteratorArgumentPatterns(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<RenderedIteratorArgumentPattern> patterns;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));

    for (std::size_t instruction_index = 0; instruction_index < instructions.size();
         ++instruction_index) {
        const Lua51Instruction& instruction = instructions[instruction_index];
        if (instruction.opcode == Lua51Opcode::kCall && instruction.b == 2 &&
            instruction.c == 4) {
            const std::optional<std::string> expression =
                RenderLua51TrackedCallExpression(function,
                                                 parameter_names,
                                                 tracked_registers,
                                                 instruction_index,
                                                 instruction);
            std::string callee;
            std::vector<std::string> arguments;
            if (expression.has_value() &&
                ParseRenderedSingleCallExpression(*expression, &callee, &arguments) &&
                arguments.size() == 1) {
                const std::string terminal = ExtractRenderedCallTerminal(callee);
                if ((terminal == "ipairs" || terminal == "pairs" || terminal == "next") &&
                    instruction.a + 1 >= 0) {
                    if (const std::optional<std::string> active_local =
                            TryGetActiveLua51LocalRegisterName(function,
                                                               instruction_index,
                                                               instruction.a + 1);
                        active_local.has_value() &&
                        IsValidLuaIdentifier(*active_local)) {
                        patterns.push_back(RenderedIteratorArgumentPattern{
                            .local_name = *active_local,
                            .call_expression = *expression,
                            .argument_expression = arguments.front(),
                        });
                    }
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instruction,
                                            instruction_index,
                                            &tracked_registers);
    }

    return patterns;
}

std::optional<int> ParseRenderedTemporaryRegisterIndex(std::string_view token) {
    token = TrimLuaWhitespace(token);
    if (token.size() < 2 || token.front() != 'r') {
        return std::nullopt;
    }

    int value = 0;
    for (std::size_t index = 1; index < token.size(); ++index) {
        const unsigned char ch = static_cast<unsigned char>(token[index]);
        if (std::isdigit(ch) == 0) {
            return std::nullopt;
        }
        value = value * 10 + static_cast<int>(ch - '0');
    }
    return value;
}

bool IsWrappedRenderedWholeParentheses(std::string_view text) {
    text = TrimLuaWhitespace(text);
    if (text.size() < 2 || text.front() != '(' || text.back() != ')') {
        return false;
    }

    int paren_depth = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    bool escaped = false;
    for (std::size_t index = 0; index < text.size(); ++index) {
        const char ch = text[index];
        if (escaped) {
            escaped = false;
            continue;
        }
        if ((in_single_quote || in_double_quote) && ch == '\\') {
            escaped = true;
            continue;
        }
        if (!in_double_quote && ch == '\'') {
            in_single_quote = !in_single_quote;
            continue;
        }
        if (!in_single_quote && ch == '"') {
            in_double_quote = !in_double_quote;
            continue;
        }
        if (in_single_quote || in_double_quote) {
            continue;
        }

        if (ch == '(') {
            ++paren_depth;
        } else if (ch == ')') {
            --paren_depth;
            if (paren_depth == 0 && index + 1 != text.size()) {
                return false;
            }
            if (paren_depth < 0) {
                return false;
            }
        }
    }

    return paren_depth == 0;
}

std::string StripRenderedWholeParenthesizedCondition(std::string_view text) {
    std::string current = std::string(TrimLuaWhitespace(text));
    while (IsWrappedRenderedWholeParentheses(current)) {
        current = std::string(
            TrimLuaWhitespace(current.substr(1, current.size() - 2)));
    }
    return current;
}

std::optional<RenderedTemporaryConditionPattern> TryParseRenderedTemporaryCondition(
    std::string_view condition) {
    std::string current =
        StripRenderedWholeParenthesizedCondition(
            NormalizeRenderedTrivialParenthesizedLiterals(condition));
    bool negate = false;
    for (int depth = 0; depth < 8; ++depth) {
        if (const std::optional<int> reg = ParseRenderedTemporaryRegisterIndex(current);
            reg.has_value()) {
            return RenderedTemporaryConditionPattern{
                .reg = *reg,
                .negate = negate,
            };
        }

        const std::string_view trimmed = TrimLuaWhitespace(current);
        if (!trimmed.starts_with("not ")) {
            break;
        }

        negate = !negate;
        current = StripRenderedWholeParenthesizedCondition(
            TrimLuaWhitespace(trimmed.substr(4)));
    }

    return std::nullopt;
}

std::string ExtractRenderedCallOwner(std::string_view expression) {
    expression = TrimLuaWhitespace(expression);
    const std::size_t open_paren = expression.find('(');
    if (open_paren == std::string_view::npos || open_paren == 0) {
        return {};
    }

    std::string_view callee = TrimLuaWhitespace(expression.substr(0, open_paren));
    const std::size_t separator = callee.find_first_of(".:");
    if (separator == std::string_view::npos || separator == 0) {
        return {};
    }
    return std::string(callee.substr(0, separator));
}

std::optional<std::string> ExtractRenderedCallCallee(std::string_view expression) {
    expression = TrimLuaWhitespace(StripLuaLineComment(expression));
    const std::size_t open_paren = expression.find('(');
    if (open_paren == std::string_view::npos || open_paren == 0 ||
        expression.back() != ')') {
        return std::nullopt;
    }

    const std::string_view callee = TrimLuaWhitespace(expression.substr(0, open_paren));
    if (callee.empty()) {
        return std::nullopt;
    }
    return std::string(callee);
}

std::string ExtractRenderedCallTerminal(std::string_view expression) {
    std::string callee_storage;
    std::string_view callee = TrimLuaWhitespace(expression);
    if (const std::optional<std::string> parsed_callee = ExtractRenderedCallCallee(expression);
        parsed_callee.has_value()) {
        callee_storage = *parsed_callee;
        callee = callee_storage;
    }

    const std::size_t last_access = callee.find_last_of(".:");
    if (last_access != std::string_view::npos && last_access + 1 < callee.size()) {
        callee = TrimLuaWhitespace(callee.substr(last_access + 1));
    }
    if (!IsValidLuaIdentifier(callee) || IsLuaKeyword(callee)) {
        return {};
    }
    return std::string(callee);
}

bool IsRenderedSimpleQuotedStringLiteral(std::string_view text) {
    text = TrimLuaWhitespace(text);
    if (text.size() < 2) {
        return false;
    }

    const char quote = text.front();
    if ((quote != '\'' && quote != '"') || text.back() != quote) {
        return false;
    }

    bool escaped = false;
    for (std::size_t index = 1; index + 1 < text.size(); ++index) {
        const char ch = text[index];
        if (escaped) {
            escaped = false;
            continue;
        }
        if (ch == '\\') {
            escaped = true;
            continue;
        }
        if (ch == quote) {
            return false;
        }
    }
    return !escaped;
}

bool IsRenderedSimpleNumericLiteral(std::string_view text) {
    text = TrimLuaWhitespace(text);
    if (text.empty()) {
        return false;
    }

    std::size_t index = 0;
    if (text[index] == '+' || text[index] == '-') {
        ++index;
    }
    if (index >= text.size()) {
        return false;
    }

    bool saw_digit = false;
    bool saw_decimal = false;
    for (; index < text.size(); ++index) {
        const unsigned char ch = static_cast<unsigned char>(text[index]);
        if (std::isdigit(ch) != 0) {
            saw_digit = true;
            continue;
        }
        if (text[index] == '.' && !saw_decimal) {
            saw_decimal = true;
            continue;
        }
        return false;
    }
    return saw_digit;
}

bool IsSuspiciousRenderedDirectCallBase(std::string_view callee) {
    callee = TrimLuaWhitespace(callee);
    std::string normalized = std::string(callee);
    while (IsWrappedRenderedWholeParentheses(normalized)) {
        normalized = std::string(TrimLuaWhitespace(normalized.substr(1, normalized.size() - 2)));
    }

    const auto is_suspicious_leaf = [](std::string_view value) {
        return value == "nil" || value == "true" || value == "false" || value == "{}" ||
               IsRenderedSimpleQuotedStringLiteral(value) ||
               IsRenderedSimpleNumericLiteral(value) || value.starts_with("function") ||
               ParseRenderedTemporaryRegisterIndex(value).has_value();
    };

    const std::size_t access = callee.find_first_of(".:");
    if (access == std::string_view::npos || access == 0) {
        return is_suspicious_leaf(normalized);
    }

    std::string owner = std::string(TrimLuaWhitespace(callee.substr(0, access)));
    while (IsWrappedRenderedWholeParentheses(owner)) {
        owner = std::string(TrimLuaWhitespace(owner.substr(1, owner.size() - 2)));
    }

    return is_suspicious_leaf(owner);
}

bool ContainsSuspiciousRenderedNestedCallBase(std::string_view expression) {
    std::string callee;
    std::vector<std::string> arguments;
    if (!ParseRenderedSingleCallExpression(expression, &callee, &arguments)) {
        return false;
    }

    if (IsSuspiciousRenderedDirectCallBase(callee)) {
        return true;
    }

    for (const std::string& argument : arguments) {
        if (ContainsSuspiciousRenderedNestedCallBase(argument)) {
            return true;
        }
    }
    return false;
}

bool TryParseRenderedConditionHeader(std::string_view line,
                                     std::string* prefix,
                                     std::string* condition,
                                     std::string* suffix) {
    if (prefix == nullptr || condition == nullptr || suffix == nullptr) {
        return false;
    }

    const std::string indent = std::string(line.substr(0, line.find_first_not_of(" \t")));
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
        *prefix = indent + "if ";
        *condition = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
        *suffix = " then";
        return true;
    }
    if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
        *prefix = indent + "elseif ";
        *condition = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
        *suffix = " then";
        return true;
    }
    if (trimmed.starts_with("while ") && trimmed.ends_with(" do")) {
        *prefix = indent + "while ";
        *condition = std::string(TrimLuaWhitespace(trimmed.substr(6, trimmed.size() - 9)));
        *suffix = " do";
        return true;
    }
    return false;
}

bool ReplaceAllRenderedSubstrings(std::string* text,
                                  std::string_view needle,
                                  std::string_view replacement) {
    if (text == nullptr || needle.empty()) {
        return false;
    }

    bool changed = false;
    std::size_t search_from = 0;
    while (search_from < text->size()) {
        const std::size_t found = text->find(needle, search_from);
        if (found == std::string::npos) {
            break;
        }
        text->replace(found, needle.size(), replacement);
        search_from = found + replacement.size();
        changed = true;
    }
    return changed;
}

bool ParseRenderedLocalAssignment(std::string_view line,
                                  std::string* local_name,
                                  std::string* expression);
bool ParseRenderedStandaloneLocalDeclaration(std::string_view line,
                                             std::string* local_name);

bool ReplaceRenderedIdentifierUsesLocal(std::string_view text,
                                        std::string_view identifier,
                                        std::string_view replacement,
                                        std::string* rewritten);

std::string NormalizeRenderedTrivialParenthesizedLiterals(std::string_view text) {
    std::string normalized;
    normalized.reserve(text.size());
    for (std::size_t index = 0; index < text.size(); ++index) {
        if (text[index] != '(') {
            normalized.push_back(text[index]);
            continue;
        }

        std::size_t literal_begin = index + 1;
        std::size_t literal_end = literal_begin;
        while (literal_end < text.size() &&
               (std::isdigit(static_cast<unsigned char>(text[literal_end])) != 0 ||
                text[literal_end] == '.' || text[literal_end] == '-' || text[literal_end] == '+')) {
            ++literal_end;
        }
        if (literal_end == literal_begin || literal_end >= text.size() || text[literal_end] != ')') {
            normalized.push_back(text[index]);
            continue;
        }

        normalized.append(text.substr(literal_begin, literal_end - literal_begin));
        index = literal_end;
    }
    return normalized;
}

std::string TrimTrailingRenderedComma(std::string_view line) {
    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!trimmed.empty() && trimmed.back() == ',') {
        trimmed.remove_suffix(1);
        trimmed = TrimLuaWhitespace(trimmed);
    }
    return std::string(trimmed);
}

std::optional<std::string> ParseRenderedEqualityConditionLhs(std::string_view condition) {
    const std::string normalized =
        NormalizeRenderedTrivialParenthesizedLiterals(std::string(TrimLuaWhitespace(condition)));
    const std::size_t comparator = normalized.find(" == ");
    if (comparator == std::string::npos) {
        return std::nullopt;
    }

    const std::string lhs =
        std::string(TrimLuaWhitespace(normalized.substr(0, comparator)));
    if (lhs.empty()) {
        return std::nullopt;
    }
    return lhs;
}

std::vector<std::vector<std::string>> CollectRenderedLuaEqualitySelectorFamilies(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    struct EqualityCandidate {
        std::size_t instruction_index = 0;
        std::string expression;
        std::string lhs;
    };

    std::vector<EqualityCandidate> candidates;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 2) {
        return {};
    }

    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    for (std::size_t index = 0; index < instructions.size(); ++index) {
        if (instructions[index].opcode == Lua51Opcode::kEq && index + 1 < instructions.size() &&
            instructions[index + 1].opcode == Lua51Opcode::kJmp) {
            std::optional<std::string> expression = RenderLua51TrackedCompareExpression(
                function, parameter_names, tracked_registers, index, instructions[index]);
            if (expression.has_value()) {
                const std::string normalized_expression =
                    NormalizeRenderedTrivialParenthesizedLiterals(*expression);
                const std::optional<std::string> lhs =
                    ParseRenderedEqualityConditionLhs(normalized_expression);
                if (lhs.has_value()) {
                    candidates.push_back({
                        .instruction_index = index,
                        .expression = std::move(normalized_expression),
                        .lhs = *lhs,
                    });
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[index],
                                            index,
                                            &tracked_registers);
    }

    std::vector<std::string> ordered_lhs;
    std::unordered_map<std::string, std::vector<std::string>> families_by_lhs;
    for (const EqualityCandidate& candidate : candidates) {
        auto [family_it, inserted] =
            families_by_lhs.try_emplace(candidate.lhs, std::vector<std::string>{});
        if (inserted) {
            ordered_lhs.push_back(candidate.lhs);
        }
        std::vector<std::string>& family = family_it->second;
        if (family.empty() || family.back() != candidate.expression) {
            family.push_back(candidate.expression);
        }
    }

    std::vector<std::vector<std::string>> families;
    for (const std::string& lhs : ordered_lhs) {
        auto family_it = families_by_lhs.find(lhs);
        if (family_it == families_by_lhs.end() || family_it->second.size() < 2) {
            continue;
        }
        families.push_back(family_it->second);
    }
    return families;
}

std::vector<RenderedComparisonSwapPattern> CollectRenderedLuaCallOperandCompareSwapPatterns(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<RenderedComparisonSwapPattern> patterns;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 2) {
        return patterns;
    }

    const std::vector<RenderedCallResultPattern> call_patterns =
        CollectLua51RenderedCallResultPatterns(function, parameter_names);
    if (call_patterns.empty()) {
        return patterns;
    }

    auto is_register_call_expression = [&](int rk,
                                           std::size_t instruction_index,
                                           std::string_view expression) {
        if (rk < 0 || GetLua51RKConstant(function, rk) != nullptr) {
            return false;
        }
        const std::string normalized_expression =
            NormalizeRenderedTrivialParenthesizedLiterals(std::string(expression));
        return std::any_of(call_patterns.begin(),
                           call_patterns.end(),
                           [&](const RenderedCallResultPattern& pattern) {
                               return pattern.target_reg == rk &&
                                      pattern.instruction_index < instruction_index &&
                                      NormalizeRenderedTrivialParenthesizedLiterals(
                                          pattern.expression) == normalized_expression;
                           });
    };

    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    for (std::size_t index = 0; index < instructions.size(); ++index) {
        const Lua51Instruction& instruction = instructions[index];
        if (instruction.opcode == Lua51Opcode::kEq || instruction.opcode == Lua51Opcode::kLt ||
            instruction.opcode == Lua51Opcode::kLe) {
            const std::optional<std::string> lhs = RenderLua51TrackedRKOperand(function,
                                                                                parameter_names,
                                                                                tracked_registers,
                                                                                index,
                                                                                instruction.b);
            const std::optional<std::string> rhs = RenderLua51TrackedRKOperand(function,
                                                                                parameter_names,
                                                                                tracked_registers,
                                                                                index,
                                                                                instruction.c);
            const std::optional<std::string> expression = RenderLua51TrackedCompareExpression(
                function, parameter_names, tracked_registers, index, instruction);
            if (lhs.has_value() && rhs.has_value() && expression.has_value() &&
                !is_register_call_expression(instruction.b, index, *lhs) &&
                is_register_call_expression(instruction.c, index, *rhs)) {
                const std::optional<std::string> swapped =
                    SwapRenderedComparatorExpression(*expression);
                if (swapped.has_value()) {
                    std::string lhs;
                    std::string comparator;
                    std::string rhs;
                    if (!TryParseRenderedComparatorExpression(*expression,
                                                             &lhs,
                                                             &comparator,
                                                             &rhs)) {
                        continue;
                    }
                    std::string swapped_lhs;
                    std::string swapped_comparator;
                    std::string swapped_rhs;
                    std::string normalized_swapped = *swapped;
                    if (TryParseRenderedComparatorExpression(*swapped,
                                                             &swapped_lhs,
                                                             &swapped_comparator,
                                                             &swapped_rhs)) {
                        normalized_swapped = NormalizeRenderedComparatorOperand(swapped_lhs) +
                                             swapped_comparator +
                                             NormalizeRenderedComparatorOperand(swapped_rhs);
                    }
                    patterns.push_back(RenderedComparisonSwapPattern{
                        .expression = *expression,
                        .swapped_expression = std::move(normalized_swapped),
                        .normalized_lhs = NormalizeRenderedComparatorOperand(lhs),
                        .comparator = comparator,
                        .normalized_rhs = NormalizeRenderedComparatorOperand(rhs),
                    });
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[index],
                                            index,
                                            &tracked_registers);
    }

    return patterns;
}

std::vector<RenderedComparisonSwapPattern> CollectRenderedLuaNonCallCompareSwapPatterns(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<RenderedComparisonSwapPattern> patterns;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.empty()) {
        return patterns;
    }

    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    const auto find_upvalue_index = [&](std::string_view name) -> std::optional<std::size_t> {
        const std::string normalized =
            NormalizeRenderedComparatorOperand(std::string(TrimLuaWhitespace(name)));
        if (!IsValidLuaIdentifier(normalized) || IsLuaKeyword(normalized)) {
            return std::nullopt;
        }
        for (std::size_t index = 0; index < function.upvalue_names.size(); ++index) {
            if (function.upvalue_names[index] == normalized) {
                return index;
            }
        }
        return std::nullopt;
    };
    for (std::size_t index = 0; index < instructions.size(); ++index) {
        const Lua51Instruction& instruction = instructions[index];
        if (instruction.opcode == Lua51Opcode::kEq || instruction.opcode == Lua51Opcode::kLt ||
            instruction.opcode == Lua51Opcode::kLe) {
            const std::optional<std::string> lhs = RenderLua51TrackedRKOperand(function,
                                                                                parameter_names,
                                                                                tracked_registers,
                                                                                index,
                                                                                instruction.b);
            const std::optional<std::string> rhs = RenderLua51TrackedRKOperand(function,
                                                                                parameter_names,
                                                                                tracked_registers,
                                                                                index,
                                                                                instruction.c);
            const std::optional<std::string> expression = RenderLua51TrackedCompareExpression(
                function, parameter_names, tracked_registers, index, instruction);
            if (!lhs.has_value() || !rhs.has_value() || !expression.has_value()) {
                StepLua51TrackedRegisterExpressions(function,
                                                    parameter_names,
                                                    instructions[index],
                                                    index,
                                                    &tracked_registers);
                continue;
            }

            const std::string normalized_lhs = NormalizeRenderedComparatorOperand(*lhs);
            const std::string normalized_rhs = NormalizeRenderedComparatorOperand(*rhs);
            if (LooksLikeRenderedCallExpression(normalized_lhs) ||
                LooksLikeRenderedCallExpression(normalized_rhs)) {
                StepLua51TrackedRegisterExpressions(function,
                                                    parameter_names,
                                                    instructions[index],
                                                    index,
                                                    &tracked_registers);
                continue;
            }

            const std::optional<std::string> swapped =
                SwapRenderedComparatorExpression(*expression);
            if (!swapped.has_value() ||
                NormalizeRenderedTrivialParenthesizedLiterals(*swapped) ==
                    NormalizeRenderedTrivialParenthesizedLiterals(*expression)) {
                StepLua51TrackedRegisterExpressions(function,
                                                    parameter_names,
                                                    instructions[index],
                                                    index,
                                                    &tracked_registers);
                continue;
            }

            std::string lhs_text;
            std::string comparator_text;
            std::string rhs_text;
            if (!TryParseRenderedComparatorExpression(*expression,
                                                     &lhs_text,
                                                     &comparator_text,
                                                     &rhs_text)) {
                StepLua51TrackedRegisterExpressions(function,
                                                    parameter_names,
                                                    instructions[index],
                                                    index,
                                                    &tracked_registers);
                continue;
            }

            const std::optional<std::size_t> lhs_upvalue = find_upvalue_index(lhs_text);
            const std::optional<std::size_t> rhs_upvalue = find_upvalue_index(rhs_text);
            if (!lhs_upvalue.has_value() || !rhs_upvalue.has_value() ||
                *lhs_upvalue <= *rhs_upvalue) {
                StepLua51TrackedRegisterExpressions(function,
                                                    parameter_names,
                                                    instructions[index],
                                                    index,
                                                    &tracked_registers);
                continue;
            }

            patterns.push_back(RenderedComparisonSwapPattern{
                .expression = NormalizeRenderedTrivialParenthesizedLiterals(*expression),
                .swapped_expression = NormalizeRenderedTrivialParenthesizedLiterals(*swapped),
                .normalized_lhs = NormalizeRenderedComparatorOperand(lhs_text),
                .comparator = comparator_text,
                .normalized_rhs = NormalizeRenderedComparatorOperand(rhs_text),
            });
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[index],
                                            index,
                                            &tracked_registers);
    }

    return patterns;
}

bool IsLua51ConditionOpcode(Lua51Opcode opcode) {
    return opcode == Lua51Opcode::kEq || opcode == Lua51Opcode::kLt ||
           opcode == Lua51Opcode::kLe || opcode == Lua51Opcode::kTest;
}

std::optional<std::string> RenderLua51TrackedConditionExpression(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const std::vector<std::optional<std::string>>& tracked_registers,
    std::size_t instruction_index,
    const Lua51Instruction& instruction) {
    switch (instruction.opcode) {
        case Lua51Opcode::kEq:
        case Lua51Opcode::kLt:
        case Lua51Opcode::kLe:
            return RenderLua51TrackedCompareExpression(function,
                                                       parameter_names,
                                                       tracked_registers,
                                                       instruction_index,
                                                       instruction);
        case Lua51Opcode::kTest: {
            const std::optional<std::string> expression =
                RenderLua51TrackedRegisterOperand(function,
                                                 parameter_names,
                                                 tracked_registers,
                                                 instruction_index,
                                                 instruction.a);
            if (!expression.has_value()) {
                return std::nullopt;
            }
            if (instruction.c != 0) {
                return "not (" + *expression + ")";
            }
            return expression;
        }
        default:
            return std::nullopt;
    }
}

std::vector<std::vector<std::optional<std::string>>> CollectLua51TrackedRegisterStates(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    std::vector<std::vector<std::optional<std::string>>> states;
    states.reserve(instructions.size());

    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    for (std::size_t instruction_index = 0; instruction_index < instructions.size();
         ++instruction_index) {
        states.push_back(tracked_registers);
        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[instruction_index],
                                            instruction_index,
                                            &tracked_registers);
    }

    return states;
}

std::vector<RenderedSiblingElseIfPattern> CollectRenderedLuaSiblingElseIfPatterns(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<RenderedSiblingElseIfPattern> patterns;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 4) {
        return patterns;
    }

    const std::vector<std::vector<std::optional<std::string>>> tracked_states =
        CollectLua51TrackedRegisterStates(function, parameter_names);
    const auto append_unique = [&](std::string first_condition,
                                   std::string second_condition) {
        const std::string normalized_first =
            NormalizeRenderedTrivialParenthesizedLiterals(first_condition);
        const std::string normalized_second =
            NormalizeRenderedTrivialParenthesizedLiterals(second_condition);
        const bool already_present = std::any_of(
            patterns.begin(),
            patterns.end(),
            [&](const RenderedSiblingElseIfPattern& pattern) {
                return NormalizeRenderedTrivialParenthesizedLiterals(pattern.first_condition) ==
                           normalized_first &&
                       NormalizeRenderedTrivialParenthesizedLiterals(pattern.second_condition) ==
                           normalized_second;
            });
        if (!already_present) {
            patterns.push_back(RenderedSiblingElseIfPattern{
                .first_condition = std::move(first_condition),
                .second_condition = std::move(second_condition),
            });
        }
    };

    for (std::size_t instruction_index = 0; instruction_index + 1 < instructions.size();
         ++instruction_index) {
        const Lua51Instruction& first_instruction = instructions[instruction_index];
        if (!IsLua51ConditionOpcode(first_instruction.opcode) ||
            instructions[instruction_index + 1].opcode != Lua51Opcode::kJmp ||
            instructions[instruction_index + 1].sbx <= 0) {
            continue;
        }

        const std::optional<std::string> first_condition =
            RenderLua51TrackedConditionExpression(function,
                                                 parameter_names,
                                                 tracked_states[instruction_index],
                                                 instruction_index,
                                                 first_instruction);
        if (!first_condition.has_value()) {
            continue;
        }

        const long long false_target =
            static_cast<long long>(instruction_index + 1) + 1 +
            static_cast<long long>(instructions[instruction_index + 1].sbx);
        if (false_target < 0 ||
            static_cast<std::size_t>(false_target + 1) >= instructions.size()) {
            continue;
        }

        std::optional<std::size_t> branch_skip_index;
        for (std::size_t probe = instruction_index + 2;
             probe < static_cast<std::size_t>(false_target);
             ++probe) {
            if (instructions[probe].opcode != Lua51Opcode::kJmp ||
                instructions[probe].sbx <= 0) {
                continue;
            }
            const long long branch_skip_target =
                static_cast<long long>(probe) + 1 +
                static_cast<long long>(instructions[probe].sbx);
            if (branch_skip_target >
                static_cast<long long>(false_target + 1)) {
                branch_skip_index = probe;
            }
        }
        if (!branch_skip_index.has_value()) {
            continue;
        }

        std::optional<std::size_t> second_index;
        const std::size_t second_search_begin = static_cast<std::size_t>(false_target);
        const std::size_t second_search_end =
            std::min(instructions.size(), second_search_begin + 4);
        for (std::size_t probe = second_search_begin;
             probe + 1 < second_search_end;
             ++probe) {
            if (!IsLua51ConditionOpcode(instructions[probe].opcode) ||
                instructions[probe + 1].opcode != Lua51Opcode::kJmp ||
                instructions[probe + 1].sbx <= 0) {
                continue;
            }
            second_index = probe;
            break;
        }
        if (!second_index.has_value()) {
            continue;
        }

        const std::optional<std::string> second_condition =
            RenderLua51TrackedConditionExpression(function,
                                                 parameter_names,
                                                 tracked_states[*second_index],
                                                 *second_index,
                                                 instructions[*second_index]);
        if (!second_condition.has_value()) {
            continue;
        }

        append_unique(*first_condition, *second_condition);
    }

    return patterns;
}

bool LooksLikeRenderedCallExpression(std::string_view expression) {
    return ExtractRenderedCallCallee(expression).has_value();
}

std::optional<std::size_t> FindUniqueLua51CoroutineYieldBlockStart(const Lua51Function& function) {
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 3) {
        return std::nullopt;
    }

    std::optional<std::size_t> yield_block_start;
    for (std::size_t index = 0; index + 2 < instructions.size(); ++index) {
        const Lua51Instruction& getglobal = instructions[index];
        const Lua51Instruction& gettable = instructions[index + 1];
        const Lua51Instruction& call = instructions[index + 2];
        if (getglobal.opcode != Lua51Opcode::kGetGlobal ||
            gettable.opcode != Lua51Opcode::kGetTable ||
            call.opcode != Lua51Opcode::kCall || gettable.a != getglobal.a ||
            gettable.b != getglobal.a || call.a != getglobal.a) {
            continue;
        }

        if (static_cast<std::size_t>(getglobal.bx) >= function.constants.size()) {
            continue;
        }

        const Lua51Constant& owner_constant =
            function.constants[static_cast<std::size_t>(getglobal.bx)];
        const Lua51Constant* field_constant = GetLua51RKConstant(function, gettable.c);
        if (owner_constant.type != Lua51ConstantType::kString ||
            owner_constant.string_value != "coroutine" || field_constant == nullptr ||
            field_constant->type != Lua51ConstantType::kString ||
            field_constant->string_value != "yield") {
            continue;
        }

        if (yield_block_start.has_value()) {
            return std::nullopt;
        }
        yield_block_start = index;
    }

    return yield_block_start;
}

std::vector<std::string> CollectRenderedLuaTailLoopBreakExpressions(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<std::string> expressions;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 4) {
        return expressions;
    }

    const std::optional<std::size_t> yield_block_start =
        FindUniqueLua51CoroutineYieldBlockStart(function);
    if (!yield_block_start.has_value()) {
        return expressions;
    }

    std::size_t loop_body_start = 0;
    for (std::size_t index = 0; index < *yield_block_start; ++index) {
        const Lua51Instruction& instruction = instructions[index];
        if (instruction.opcode != Lua51Opcode::kJmp || instruction.sbx >= 0) {
            continue;
        }
        const long long jump_target =
            static_cast<long long>(index) + 1 + static_cast<long long>(instruction.sbx);
        if (jump_target >= 0 && static_cast<std::size_t>(jump_target) < *yield_block_start) {
            loop_body_start =
                std::max(loop_body_start, static_cast<std::size_t>(jump_target));
        }
    }

    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    for (std::size_t index = 0; index < instructions.size(); ++index) {
        if (index < loop_body_start) {
            StepLua51TrackedRegisterExpressions(function,
                                                parameter_names,
                                                instructions[index],
                                                index,
                                                &tracked_registers);
            continue;
        }

        if (index + 1 < *yield_block_start &&
            (instructions[index].opcode == Lua51Opcode::kLt ||
             instructions[index].opcode == Lua51Opcode::kLe ||
             instructions[index].opcode == Lua51Opcode::kEq) &&
            instructions[index + 1].opcode == Lua51Opcode::kJmp) {
            const std::size_t jump_index = index + 1;
            const long long jump_target =
                static_cast<long long>(jump_index) + 1 +
                static_cast<long long>(instructions[jump_index].sbx);
            if (jump_target >= 0 &&
                static_cast<std::size_t>(jump_target) <= *yield_block_start &&
                static_cast<std::size_t>(jump_target) + 2 >= *yield_block_start) {
                const std::optional<std::string> lhs =
                    RenderLua51TrackedRKOperand(function,
                                                parameter_names,
                                                tracked_registers,
                                                index,
                                                instructions[index].b);
                const std::optional<std::string> rhs =
                    RenderLua51TrackedRKOperand(function,
                                                parameter_names,
                                                tracked_registers,
                                                index,
                                                instructions[index].c);
                if (lhs.has_value() && rhs.has_value()) {
                    std::string comparator;
                    switch (instructions[index].opcode) {
                        case Lua51Opcode::kLt:
                            comparator = "<";
                            break;
                        case Lua51Opcode::kLe:
                            comparator = "<=";
                            break;
                        case Lua51Opcode::kEq:
                            comparator = "==";
                            break;
                        default:
                            break;
                    }
                    if (!comparator.empty()) {
                        std::string expression = *lhs + " " + comparator + " " + *rhs;
                        if (expressions.empty() || expressions.back() != expression) {
                            expressions.push_back(std::move(expression));
                        }
                    }
                }
            }
        }

        if (index + 1 < *yield_block_start &&
            instructions[index].opcode == Lua51Opcode::kTest &&
            instructions[index + 1].opcode == Lua51Opcode::kJmp) {
            const std::size_t jump_index = index + 1;
            const long long jump_target =
                static_cast<long long>(jump_index) + 1 +
                static_cast<long long>(instructions[jump_index].sbx);
            if (jump_target >= 0 &&
                static_cast<std::size_t>(jump_target) <= *yield_block_start &&
                static_cast<std::size_t>(jump_target) + 2 >= *yield_block_start) {
                const std::optional<std::string> expression =
                    RenderLua51TrackedRegisterOperand(function,
                                                      parameter_names,
                                                      tracked_registers,
                                                      index,
                                                      instructions[index].a);
                if (expression.has_value() && !expression->empty()) {
                    std::string condition = *expression;
                    if (instructions[index].c != 0) {
                        condition = "not " + condition;
                    }
                    if (expressions.empty() || expressions.back() != condition) {
                        expressions.push_back(std::move(condition));
                    }
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[index],
                                            index,
                                            &tracked_registers);
    }

    return expressions;
}

std::optional<std::string> CollectRenderedLuaSimpleYieldLoopCondition(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 5) {
        return std::nullopt;
    }

    const std::optional<std::size_t> yield_block_start =
        FindUniqueLua51CoroutineYieldBlockStart(function);
    if (!yield_block_start.has_value() || *yield_block_start + 4 >= instructions.size()) {
        return std::nullopt;
    }

    const Lua51Instruction& coroutine_getglobal = instructions[*yield_block_start];
    const Lua51Instruction& yield_gettable = instructions[*yield_block_start + 1];
    const Lua51Instruction& yield_call = instructions[*yield_block_start + 2];
    const Lua51Instruction& tail_jump = instructions[*yield_block_start + 3];
    const Lua51Instruction& tail_return = instructions[*yield_block_start + 4];
    if (coroutine_getglobal.opcode != Lua51Opcode::kGetGlobal ||
        yield_gettable.opcode != Lua51Opcode::kGetTable ||
        yield_call.opcode != Lua51Opcode::kCall || yield_call.b != 1 || yield_call.c != 1 ||
        tail_jump.opcode != Lua51Opcode::kJmp || tail_jump.sbx >= 0 ||
        tail_return.opcode != Lua51Opcode::kReturn) {
        return std::nullopt;
    }

    if (static_cast<std::size_t>(coroutine_getglobal.bx) >= function.constants.size()) {
        return std::nullopt;
    }
    const Lua51Constant& coroutine_name =
        function.constants[static_cast<std::size_t>(coroutine_getglobal.bx)];
    const Lua51Constant* yield_name = GetLua51RKConstant(function, yield_gettable.c);
    if (coroutine_name.type != Lua51ConstantType::kString ||
        coroutine_name.string_value != "coroutine" || yield_name == nullptr ||
        yield_name->type != Lua51ConstantType::kString ||
        yield_name->string_value != "yield") {
        return std::nullopt;
    }

    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    std::optional<std::string> loop_condition;
    std::size_t condition_count = 0;
    for (std::size_t index = 0; index < *yield_block_start; ++index) {
        if (index + 1 < *yield_block_start && IsLua51ConditionOpcode(instructions[index].opcode) &&
            instructions[index + 1].opcode == Lua51Opcode::kJmp &&
            instructions[index + 1].sbx == 0) {
            const long long jump_target =
                static_cast<long long>(index + 1) + 1 +
                static_cast<long long>(instructions[index + 1].sbx);
            if (jump_target == static_cast<long long>(*yield_block_start)) {
                const std::optional<std::string> condition =
                    RenderLua51TrackedConditionExpression(
                        function, parameter_names, tracked_registers, index, instructions[index]);
                if (!condition.has_value() || condition->empty()) {
                    return std::nullopt;
                }

                ++condition_count;
                if (!loop_condition.has_value()) {
                    loop_condition = NormalizeRenderedTrivialParenthesizedLiterals(*condition);
                } else if (*loop_condition !=
                           NormalizeRenderedTrivialParenthesizedLiterals(*condition)) {
                    return std::nullopt;
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[index],
                                            index,
                                            &tracked_registers);
    }

    if (condition_count != 1 || !loop_condition.has_value() || loop_condition->empty()) {
        return std::nullopt;
    }

    return loop_condition;
}

struct IndexedEmptyTableBootstrapPattern {
    std::string target_expression;
    std::vector<int> numeric_keys;
};

std::optional<IndexedEmptyTableBootstrapPattern> DetectIndexedEmptyTableBootstrapPattern(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 5) {
        return std::nullopt;
    }

    for (std::size_t index = 0; index + 4 < instructions.size(); ++index) {
        const Lua51Instruction& root_newtable = instructions[index];
        const Lua51Instruction& owner_settable = instructions[index + 1];
        if (root_newtable.opcode != Lua51Opcode::kNewTable ||
            owner_settable.opcode != Lua51Opcode::kSetTable ||
            owner_settable.c != root_newtable.a) {
            continue;
        }

        const std::optional<std::string> owner_name =
            RenderLua51RegisterOperand(parameter_names, owner_settable.a);
        const Lua51Constant* field_constant =
            GetLua51RKConstant(function, owner_settable.b);
        if (!owner_name.has_value() || field_constant == nullptr) {
            continue;
        }

        const std::optional<std::string> target_expression =
            RenderLua51TableFieldAccess(*owner_name, *field_constant);
        if (!target_expression.has_value()) {
            continue;
        }

        std::vector<int> numeric_keys;
        std::size_t cursor = index + 2;
        while (cursor + 2 < instructions.size()) {
            const Lua51Instruction& reload_table = instructions[cursor];
            const Lua51Instruction& child_newtable = instructions[cursor + 1];
            const Lua51Instruction& indexed_settable = instructions[cursor + 2];

            if (reload_table.opcode != Lua51Opcode::kGetTable ||
                child_newtable.opcode != Lua51Opcode::kNewTable ||
                indexed_settable.opcode != Lua51Opcode::kSetTable) {
                break;
            }

            if (reload_table.b != owner_settable.a || reload_table.c != owner_settable.b ||
                indexed_settable.a != reload_table.a ||
                indexed_settable.c != child_newtable.a) {
                break;
            }

            const std::optional<int> numeric_key =
                ParseLua51RKPositiveIntegralConstant(function, indexed_settable.b);
            if (!numeric_key.has_value()) {
                break;
            }

            numeric_keys.push_back(*numeric_key);
            cursor += 3;
        }

        if (numeric_keys.empty()) {
            continue;
        }

        bool contiguous = true;
        for (std::size_t key_index = 0; key_index < numeric_keys.size(); ++key_index) {
            if (numeric_keys[key_index] != static_cast<int>(key_index + 1)) {
                contiguous = false;
                break;
            }
        }
        if (!contiguous) {
            continue;
        }

        return IndexedEmptyTableBootstrapPattern{
            .target_expression = *target_expression,
            .numeric_keys = std::move(numeric_keys),
        };
    }

    return std::nullopt;
}

bool TryRestoreBytecodeComparatorAsserts(const Lua51Function& function,
                                         std::string_view chunk_name,
                                         std::string* source,
                                         std::string* note,
                                         std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto condition_mentions_temp_reg = [](std::string_view condition, int reg) {
        const std::string token = "r" + std::to_string(reg);
        std::size_t search_from = 0;
        while (search_from < condition.size()) {
            const std::size_t match = condition.find(token, search_from);
            if (match == std::string_view::npos) {
                return false;
            }
            const bool boundary_before =
                match == 0 ||
                (!std::isalnum(static_cast<unsigned char>(condition[match - 1])) &&
                 condition[match - 1] != '_');
            const std::size_t after = match + token.size();
            const bool boundary_after =
                after >= condition.size() ||
                (!std::isalnum(static_cast<unsigned char>(condition[after])) &&
                 condition[after] != '_');
            if (boundary_before && boundary_after) {
                return true;
            }
            search_from = match + token.size();
        }
        return false;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t reverse_index = block_count; reverse_index > 0; --reverse_index) {
        const std::size_t block_index = reverse_index - 1;
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = block.end;
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<std::string> expressions =
            CollectRenderedLuaComparatorAssertExpressions(function.child_functions[block_index],
                                                          parameter_names);
        if (expressions.empty()) {
            continue;
        }

        std::vector<std::size_t> placeholder_lines;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            const std::string_view trimmed = trim_line(lines[line_index]);
            if (trimmed == "assert(true)" || trimmed == "assert(false)") {
                placeholder_lines.push_back(line_index);
            }
        }

        if (placeholder_lines.size() != expressions.size()) {
            continue;
        }

        for (std::size_t expression_index = 0;
             expression_index < expressions.size();
             ++expression_index) {
            const std::size_t line_index = placeholder_lines[expression_index];
            lines[line_index] =
                line_indent(lines[line_index]) + "assert(" + expressions[expression_index] + ")";
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_asserts_restored");
    return true;
}

bool TryRestoreBytecodeTopLevelComparatorAsserts(const Lua51Function& function,
                                                 std::string_view chunk_name,
                                                 std::string* source,
                                                 std::string* note,
                                                 std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const std::vector<std::string> parameter_names;
    const std::vector<std::string> expressions =
        CollectRenderedLuaComparatorAssertExpressions(function, parameter_names);
    if (expressions.empty()) {
        return false;
    }

    const bool source_was_valid = ValidateLuaSource(*source, chunk_name).empty();
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto extract_if_condition = [&](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (!trimmed.starts_with("if ") || !trimmed.ends_with(" then")) {
            return false;
        }

        *condition = std::string(
            TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 3 - 5)));
        return !condition->empty();
    };
    const auto parse_any_callee_call_expression =
        [](std::string_view line, std::string* callee, std::vector<std::string>* arguments) {
            if (callee == nullptr || arguments == nullptr) {
                return false;
            }

            line = TrimLuaWhitespace(StripLuaLineComment(line));
            if (line.empty() || !line.ends_with(')')) {
                return false;
            }

            const std::size_t open = line.find('(');
            if (open == std::string_view::npos || open == 0) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            bool escaped = false;
            int paren_depth = 0;
            std::size_t close = std::string_view::npos;
            for (std::size_t index = open; index < line.size(); ++index) {
                const char ch = line[index];
                if (escaped) {
                    escaped = false;
                    continue;
                }
                if ((in_single_quote || in_double_quote) && ch == '\\') {
                    escaped = true;
                    continue;
                }
                if (!in_double_quote && ch == '\'') {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (!in_single_quote && ch == '"') {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                } else if (ch == ')') {
                    --paren_depth;
                    if (paren_depth == 0) {
                        close = index;
                        break;
                    }
                }
            }
            if (close == std::string_view::npos || close + 1 != line.size()) {
                return false;
            }

            const std::string_view callee_view = TrimLuaWhitespace(line.substr(0, open));
            if (callee_view.empty()) {
                return false;
            }

            *callee = std::string(callee_view);
            return SplitRenderedTopLevelCommaChainLocal(line.substr(open + 1, close - open - 1),
                                                        arguments);
        };
    const auto next_top_level_nonempty =
        [&](const std::vector<std::string>& rendered_lines,
            const std::vector<RenderedFunctionBlock>& rendered_blocks,
            std::size_t start) {
            std::size_t block_index = 0;
            while (start < rendered_lines.size()) {
                while (block_index < rendered_blocks.size() &&
                       rendered_blocks[block_index].end < start) {
                    ++block_index;
                }
                if (block_index < rendered_blocks.size() &&
                    rendered_blocks[block_index].begin <= start &&
                    start <= rendered_blocks[block_index].end) {
                    start = rendered_blocks[block_index].end + 1;
                    continue;
                }
                if (!trim_line(rendered_lines[start]).empty()) {
                    return start;
                }
                ++start;
            }
            return rendered_lines.size();
        };
    const auto previous_top_level_nonempty =
        [&](const std::vector<std::string>& rendered_lines,
            const std::vector<RenderedFunctionBlock>& rendered_blocks,
            std::size_t start) {
            if (start == 0) {
                return rendered_lines.size();
            }

            std::vector<bool> inside_block(rendered_lines.size(), false);
            for (const RenderedFunctionBlock& block : rendered_blocks) {
                if (block.begin >= rendered_lines.size()) {
                    continue;
                }
                const std::size_t end =
                    std::min(block.end, rendered_lines.size() - 1);
                for (std::size_t line_index = block.begin; line_index <= end; ++line_index) {
                    inside_block[line_index] = true;
                }
            }

            for (std::size_t probe = start; probe-- > 0;) {
                if (probe < inside_block.size() && inside_block[probe]) {
                    continue;
                }
                if (!trim_line(rendered_lines[probe]).empty()) {
                    return probe;
                }
            }
            return rendered_lines.size();
        };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    std::size_t expression_index = 0;
    bool changed = false;

    for (std::size_t line_index = next_top_level_nonempty(lines, blocks, 0);
         line_index < lines.size() && expression_index < expressions.size();
         line_index = next_top_level_nonempty(lines, blocks, line_index + 1)) {
        const std::string_view trimmed = trim_line(lines[line_index]);

        bool suspicious_boolean_assert = false;
        std::vector<std::string> arguments;
        if (trimmed == "assert(true)" || trimmed == "assert(false)") {
            arguments.push_back(trimmed == "assert(true)" ? "true" : "false");
            suspicious_boolean_assert = true;
        } else {
            std::string callee;
            if (!parse_any_callee_call_expression(trimmed, &callee, &arguments) ||
                arguments.empty() ||
                (TrimLuaWhitespace(arguments.front()) != "true" &&
                 TrimLuaWhitespace(arguments.front()) != "false") ||
                (!IsSuspiciousRenderedDirectCallBase(callee) &&
                 !ContainsSuspiciousRenderedNestedCallBase(trimmed) &&
                 !(TrimLuaWhitespace(callee).starts_with("{") ||
                   TrimLuaWhitespace(callee).starts_with("function")))) {
                continue;
            }
        }

        std::size_t previous_line = previous_top_level_nonempty(lines, blocks, line_index);
        std::size_t previous_previous_line =
            previous_line < lines.size()
                ? previous_top_level_nonempty(lines, blocks, previous_line)
                : lines.size();
        std::string if_condition;
        const bool has_empty_if_guard =
            previous_line < lines.size() &&
            previous_previous_line < lines.size() &&
            trim_line(lines[previous_line]) == "end" &&
            extract_if_condition(lines[previous_previous_line], &if_condition);
        if (!suspicious_boolean_assert && !has_empty_if_guard) {
            continue;
        }

        arguments.front() = expressions[expression_index];
        std::ostringstream rebuilt_call;
        rebuilt_call << "assert(";
        for (std::size_t argument_i = 0; argument_i < arguments.size(); ++argument_i) {
            if (argument_i != 0) {
                rebuilt_call << ", ";
            }
            rebuilt_call << arguments[argument_i];
        }
        rebuilt_call << ")";

        lines[line_index] = line_indent(lines[line_index]) + rebuilt_call.str();
        if (has_empty_if_guard) {
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(previous_previous_line),
                        lines.begin() + static_cast<std::ptrdiff_t>(previous_line + 1));
            line_index = previous_previous_line;
        }
        ++expression_index;
        changed = true;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (source_was_valid && !ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_top_level_comparator_asserts_restored");
    return true;
}

bool TryRestoreBytecodeLeadingEmptyIfChecks(const Lua51Function& function,
                                            std::string_view chunk_name,
                                            std::string* source,
                                            std::string* note,
                                            std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto normalize_expression = [](std::string_view expression) {
        return NormalizeRenderedTrivialParenthesizedLiterals(
            std::string(TrimLuaWhitespace(expression)));
    };
    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t reverse_index = block_count; reverse_index > 0; --reverse_index) {
        const std::size_t block_index = reverse_index - 1;
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const Lua51Function& child_function = function.child_functions[block_index];
        const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(child_function);
        if (instructions.size() < 3) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        std::vector<std::optional<std::string>> tracked_registers(
            std::max<std::size_t>(child_function.max_stack_size, parameter_names.size()));

        std::optional<std::string> leading_condition;
        const std::size_t scan_limit = std::min<std::size_t>(instructions.size() - 1, 8);
        for (std::size_t instruction_index = 0; instruction_index < scan_limit; ++instruction_index) {
            const Lua51Instruction& instruction = instructions[instruction_index];
            if ((instruction.opcode == Lua51Opcode::kEq || instruction.opcode == Lua51Opcode::kLt ||
                 instruction.opcode == Lua51Opcode::kLe) &&
                instructions[instruction_index + 1].opcode == Lua51Opcode::kJmp &&
                instructions[instruction_index + 1].sbx == 0) {
                leading_condition = RenderLua51TrackedCompareExpression(child_function,
                                                                       parameter_names,
                                                                       tracked_registers,
                                                                       instruction_index,
                                                                       instruction);
                break;
            }

            StepLua51TrackedRegisterExpressions(child_function,
                                                parameter_names,
                                                instruction,
                                                instruction_index,
                                                &tracked_registers);
        }

        if (!leading_condition.has_value()) {
            const Lua51Instruction& gettable = instructions[0];
            const Lua51Instruction& compare = instructions[1];
            const Lua51Instruction& jump = instructions[2];
            if (gettable.opcode == Lua51Opcode::kGetTable &&
                compare.opcode == Lua51Opcode::kEq &&
                jump.opcode == Lua51Opcode::kJmp &&
                jump.sbx == 0) {
                const Lua51Constant* field_constant = GetLua51RKConstant(child_function, gettable.c);
                const std::optional<std::string> owner_name =
                    RenderLua51RegisterOperand(parameter_names, gettable.b);
                if (field_constant != nullptr && owner_name.has_value()) {
                    const std::optional<std::string> field_expression =
                        RenderLua51TableFieldAccess(*owner_name, *field_constant);
                    const auto is_nil_operand = [&](int rk, bool* uses_gettable_reg) {
                        if (uses_gettable_reg != nullptr) {
                            *uses_gettable_reg = false;
                        }
                        if (rk == gettable.a) {
                            if (uses_gettable_reg != nullptr) {
                                *uses_gettable_reg = true;
                            }
                            return false;
                        }
                        const Lua51Constant* constant = GetLua51RKConstant(child_function, rk);
                        return constant != nullptr && constant->type == Lua51ConstantType::kNil;
                    };

                    bool b_is_gettable_reg = false;
                    bool c_is_gettable_reg = false;
                    const bool b_is_nil = is_nil_operand(compare.b, &b_is_gettable_reg);
                    const bool c_is_nil = is_nil_operand(compare.c, &c_is_gettable_reg);
                    if (field_expression.has_value() &&
                        ((b_is_gettable_reg && c_is_nil) || (c_is_gettable_reg && b_is_nil))) {
                        leading_condition =
                            *field_expression + (compare.a != 0 ? " ~= nil" : " == nil");
                    }
                }
            }
        }

        if (!leading_condition.has_value() || leading_condition->empty()) {
            continue;
        }
        const std::string normalized_leading_condition =
            normalize_expression(*leading_condition);

        std::size_t first_nonempty_line = block.begin + 1;
        while (first_nonempty_line < function_end && trim_line(lines[first_nonempty_line]).empty()) {
            ++first_nonempty_line;
        }
        if (first_nonempty_line < function_end &&
            trim_line(lines[first_nonempty_line]) == ("if " + *leading_condition + " then")) {
            std::size_t next_nonempty_line = first_nonempty_line + 1;
            while (next_nonempty_line < function_end &&
                   trim_line(lines[next_nonempty_line]).empty()) {
                ++next_nonempty_line;
            }
            if (next_nonempty_line < function_end && trim_line(lines[next_nonempty_line]) == "end") {
                continue;
            }
        }
        if (first_nonempty_line < function_end) {
            std::string prefix;
            std::string condition;
            std::string suffix;
            if (TryParseRenderedConditionHeader(
                    lines[first_nonempty_line], &prefix, &condition, &suffix) &&
                suffix == " do" && prefix.find("while ") != std::string::npos &&
                normalize_expression(condition) == normalized_leading_condition) {
                continue;
            }
        }

        const std::size_t header_indent_size = lines[block.begin].find_first_not_of(" \t");
        const std::string header_indent =
            header_indent_size == std::string::npos ? std::string()
                                                    : lines[block.begin].substr(0, header_indent_size);
        const std::string body_indent = header_indent + "  ";
        const std::size_t insert_line = block.begin + 1;
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(insert_line),
                     body_indent + "if " + *leading_condition + " then");
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(insert_line + 1),
                     body_indent + "end");
        changed = true;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_leading_empty_if_checks_restored");
    return true;
}

bool TryRestoreBytecodeEmptyEqualitySelectorBranches(const Lua51Function& function,
                                                     std::string_view chunk_name,
                                                     std::string* source,
                                                     std::string* note,
                                                     std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto normalize_condition = [](std::string_view condition) {
        return NormalizeRenderedTrivialParenthesizedLiterals(
            std::string(TrimLuaWhitespace(condition)));
    };
    const auto find_if_block_end = [&](const std::vector<std::string>& rendered_lines,
                                       std::size_t start,
                                       std::size_t limit,
                                       std::size_t* end_out) -> bool {
        if (end_out == nullptr || start >= rendered_lines.size() || start > limit) {
            return false;
        }
        int depth = 0;
        for (std::size_t index = start; index <= limit && index < rendered_lines.size(); ++index) {
            const std::string_view trimmed = trim_line(rendered_lines[index]);
            if (trimmed.empty()) {
                continue;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
            if (index > start && depth == 0) {
                *end_out = index;
                return true;
            }
        }
        return false;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t reverse_index = block_count; reverse_index > 0; --reverse_index) {
        const std::size_t block_index = reverse_index - 1;
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<std::vector<std::string>> selector_families =
            CollectRenderedLuaEqualitySelectorFamilies(function.child_functions[block_index],
                                                       parameter_names);
        if (selector_families.empty()) {
            continue;
        }

        std::unordered_map<std::string, std::size_t> rendered_equality_lhs_counts;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[line_index],
                                                 &prefix,
                                                 &condition,
                                                 &suffix) ||
                suffix != " then") {
                continue;
            }

            const bool is_if_header = prefix.find("if ") != std::string::npos;
            const bool is_elseif_header = prefix.find("elseif ") != std::string::npos;
            if (!is_if_header && !is_elseif_header) {
                continue;
            }

            const std::string normalized_condition = normalize_condition(condition);
            const std::optional<std::string> rendered_lhs =
                ParseRenderedEqualityConditionLhs(normalized_condition);
            if (!rendered_lhs.has_value()) {
                continue;
            }

            ++rendered_equality_lhs_counts[*rendered_lhs];
        }

        for (const std::vector<std::string>& selector_family : selector_families) {
            if (selector_family.empty()) {
                continue;
            }
            const std::optional<std::string> family_lhs =
                ParseRenderedEqualityConditionLhs(selector_family.front());
            if (!family_lhs.has_value()) {
                continue;
            }

            const auto rendered_lhs_count_it =
                rendered_equality_lhs_counts.find(*family_lhs);
            if (rendered_lhs_count_it == rendered_equality_lhs_counts.end() ||
                rendered_lhs_count_it->second != 1) {
                continue;
            }

            for (std::size_t line_index = block.begin + 1; line_index < function_end;
                 ++line_index) {
                std::string prefix;
                std::string condition;
                std::string suffix;
                if (!TryParseRenderedConditionHeader(lines[line_index],
                                                     &prefix,
                                                     &condition,
                                                     &suffix) ||
                    prefix.find("if ") == std::string::npos || suffix != " then") {
                    continue;
                }

                const std::string normalized_condition = normalize_condition(condition);
                if (std::find(selector_family.begin(),
                              selector_family.end(),
                              normalized_condition) == selector_family.end()) {
                    continue;
                }

                const std::optional<std::string> current_lhs =
                    ParseRenderedEqualityConditionLhs(normalized_condition);
                if (!current_lhs.has_value() || *current_lhs != *family_lhs) {
                    continue;
                }

                std::size_t block_end = lines.size();
                if (!find_if_block_end(lines, line_index, function_end, &block_end)) {
                    continue;
                }

                std::unordered_set<std::string> existing_conditions;
                existing_conditions.insert(normalized_condition);
                std::size_t existing_selector_match_count = 1;
                bool saw_existing_same_lhs_elseif = false;
                std::size_t insertion_line = block_end;
                int depth = 0;
                for (std::size_t probe = line_index; probe <= block_end && probe < lines.size();
                     ++probe) {
                    const std::string_view trimmed = trim_line(lines[probe]);
                    if (trimmed.empty()) {
                        continue;
                    }

                    if (probe > line_index && depth == 1) {
                        if (trimmed == "else" && insertion_line == block_end) {
                            insertion_line = probe;
                        }

                        std::string branch_prefix;
                        std::string branch_condition;
                        std::string branch_suffix;
                        if (TryParseRenderedConditionHeader(lines[probe],
                                                            &branch_prefix,
                                                            &branch_condition,
                                                            &branch_suffix) &&
                            branch_prefix.find("elseif ") != std::string::npos &&
                            branch_suffix == " then") {
                            const std::string normalized_branch_condition =
                                normalize_condition(branch_condition);
                            existing_conditions.insert(normalized_branch_condition);
                            const std::optional<std::string> branch_lhs =
                                ParseRenderedEqualityConditionLhs(normalized_branch_condition);
                            if (branch_lhs.has_value() && *branch_lhs == *family_lhs) {
                                saw_existing_same_lhs_elseif = true;
                            }
                            if (std::find(selector_family.begin(),
                                          selector_family.end(),
                                          normalized_branch_condition) != selector_family.end()) {
                                ++existing_selector_match_count;
                            }
                        }
                    }

                    depth += GetRenderedLuaBlockDelta(trimmed);
                }

                std::vector<std::string> missing_conditions;
                for (const std::string& expression : selector_family) {
                    if (!existing_conditions.contains(expression)) {
                        missing_conditions.push_back(expression);
                    }
                }
                if (missing_conditions.empty()) {
                    break;
                }
                if (existing_selector_match_count > 1 || saw_existing_same_lhs_elseif) {
                    continue;
                }

                const std::string indent = line_indent(lines[line_index]);
                std::vector<std::string> inserted_lines;
                inserted_lines.reserve(missing_conditions.size());
                for (const std::string& expression : missing_conditions) {
                    inserted_lines.push_back(indent + "elseif " + expression + " then");
                }
                lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(insertion_line),
                             inserted_lines.begin(),
                             inserted_lines.end());
                changed = true;
                break;
            }
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_empty_equality_selectors_restored");
    return true;
}

bool TryRestoreBytecodeCallTestGuards(const Lua51Function& function,
                                      std::string_view chunk_name,
                                      std::string* source,
                                      std::string* note,
                                      std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedCallResultPattern> patterns =
            CollectLua51RenderedCallResultPatterns(function.child_functions[block_index],
                                                   parameter_names);
        if (patterns.empty()) {
            continue;
        }

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[line_index], &prefix, &condition, &suffix)) {
                continue;
            }

            const std::optional<RenderedTemporaryConditionPattern> temp_condition =
                TryParseRenderedTemporaryCondition(condition);
            if (!temp_condition.has_value()) {
                continue;
            }

            std::size_t next_line = line_index + 1;
            while (next_line < function_end && trim_line(lines[next_line]).empty()) {
                ++next_line;
            }

            std::string next_owner;
            if (next_line < function_end) {
                if (const std::optional<std::string> next_callee =
                        ExtractRenderedCallCallee(trim_line(lines[next_line]));
                    next_callee.has_value()) {
                    next_owner = ExtractRenderedCallOwner(*next_callee + "()");
                }
            }

            const RenderedCallResultPattern* chosen_pattern = nullptr;
            std::size_t owner_matches = 0;
            std::size_t reg_matches = 0;
            std::optional<std::string> unique_owner_expression;
            bool owner_expression_conflict = false;
            for (const RenderedCallResultPattern& pattern : patterns) {
                if (!pattern.tested_immediately || pattern.target_reg != temp_condition->reg ||
                    pattern.expression.empty()) {
                    continue;
                }
                ++reg_matches;
                if (!next_owner.empty() &&
                    ExtractRenderedCallOwner(pattern.expression) == next_owner) {
                    chosen_pattern = &pattern;
                    ++owner_matches;
                    if (!unique_owner_expression.has_value()) {
                        unique_owner_expression = pattern.expression;
                    } else if (*unique_owner_expression != pattern.expression) {
                        owner_expression_conflict = true;
                    }
                }
            }

            if (owner_matches == 0) {
                for (const RenderedCallResultPattern& pattern : patterns) {
                    if (!pattern.tested_immediately ||
                        pattern.target_reg != temp_condition->reg ||
                        pattern.expression.empty()) {
                        continue;
                    }
                    if (reg_matches != 1) {
                        chosen_pattern = nullptr;
                        break;
                    }
                        chosen_pattern = &pattern;
                }
            } else if (owner_matches > 1 && !owner_expression_conflict &&
                       unique_owner_expression.has_value()) {
                for (const RenderedCallResultPattern& pattern : patterns) {
                    if (pattern.tested_immediately &&
                        pattern.target_reg == temp_condition->reg &&
                        pattern.expression == *unique_owner_expression) {
                        chosen_pattern = &pattern;
                        break;
                    }
                }
            } else if (owner_matches != 1) {
                chosen_pattern = nullptr;
            }

            if (chosen_pattern == nullptr) {
                continue;
            }

            const std::string rewritten_condition =
                temp_condition->negate ? "not " + chosen_pattern->expression
                                       : chosen_pattern->expression;
            const std::string rebuilt_line = prefix + rewritten_condition + suffix;
            if (rebuilt_line == lines[line_index]) {
                continue;
            }

            lines[line_index] = rebuilt_line;
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_call_test_guards_restored");
    return true;
}

bool TryRestoreBytecodeMissingTestedCallSeeds(const Lua51Function& function,
                                              std::string_view chunk_name,
                                              std::string* source,
                                              std::string* note,
                                              std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedCallResultPattern> patterns =
            CollectLua51RenderedCallResultPatterns(function.child_functions[block_index],
                                                   parameter_names);
        if (patterns.empty()) {
            continue;
        }

        std::unordered_map<int, std::vector<const RenderedCallResultPattern*>> patterns_by_reg;
        for (const RenderedCallResultPattern& pattern : patterns) {
            if (!pattern.tested_immediately || pattern.expression.empty()) {
                continue;
            }
            patterns_by_reg[pattern.target_reg].push_back(&pattern);
        }
        if (patterns_by_reg.empty()) {
            continue;
        }

        std::unordered_map<int, std::size_t> cursor_by_reg;
        const auto advance_pattern_cursor_on_line = [&](std::string_view line) {
            const std::string normalized_line =
                NormalizeRenderedTrivialParenthesizedLiterals(trim_line(line));
            if (normalized_line.empty()) {
                return;
            }

            for (const auto& [reg, reg_patterns] : patterns_by_reg) {
                std::size_t& cursor = cursor_by_reg[reg];
                while (cursor < reg_patterns.size()) {
                    const std::string normalized_expression =
                        NormalizeRenderedTrivialParenthesizedLiterals(
                            reg_patterns[cursor]->expression);
                    if (normalized_expression.empty() ||
                        normalized_line.find(normalized_expression) == std::string::npos) {
                        break;
                    }
                    ++cursor;
                }
            }
        };

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            advance_pattern_cursor_on_line(lines[line_index]);

            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index], &is_local, &lhs, &rhs) ||
                !is_local) {
                continue;
            }

            const std::optional<int> temp_reg = ParseRenderedTemporaryRegisterIndex(lhs);
            if (!temp_reg.has_value()) {
                continue;
            }

            std::string call_callee;
            std::vector<std::string> call_arguments;
            if (!ParseRenderedSingleCallExpression(rhs, &call_callee, &call_arguments)) {
                continue;
            }

            const std::string normalized_callee =
                NormalizeRenderedTrivialParenthesizedLiterals(call_callee);
            if (normalized_callee != "nil" && normalized_callee != "function()" &&
                normalized_callee != "function ()") {
                continue;
            }

            std::size_t next_nonempty = line_index + 1;
            while (next_nonempty < function_end && trim_line(lines[next_nonempty]).empty()) {
                ++next_nonempty;
            }
            if (next_nonempty >= function_end) {
                continue;
            }

            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[next_nonempty],
                                                 &prefix,
                                                 &condition,
                                                 &suffix)) {
                continue;
            }

            const std::optional<RenderedTemporaryConditionPattern> temp_condition =
                TryParseRenderedTemporaryCondition(condition);
            if (!temp_condition.has_value() || temp_condition->reg != *temp_reg) {
                continue;
            }

            const auto reg_patterns_it = patterns_by_reg.find(*temp_reg);
            if (reg_patterns_it == patterns_by_reg.end() ||
                reg_patterns_it->second.empty()) {
                continue;
            }

            std::size_t& cursor = cursor_by_reg[*temp_reg];
            if (cursor >= reg_patterns_it->second.size()) {
                continue;
            }

            const RenderedCallResultPattern& chosen_pattern =
                *reg_patterns_it->second[cursor];
            const std::size_t indent_size =
                lines[line_index].find_first_not_of(" \t");
            const std::string indent =
                indent_size == std::string::npos
                    ? std::string()
                    : lines[line_index].substr(0, indent_size);
            lines[line_index] =
                indent + "local " + lhs + " = " + chosen_pattern.expression;
            ++cursor;
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_missing_tested_call_seeds_restored");
    return true;
}

bool TryRestoreBytecodeCallResultAssignmentReceivers(const Lua51Function& function,
                                                     std::string_view chunk_name,
                                                     std::string* source,
                                                     std::string* note,
                                                     std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }
    const bool source_was_valid = ValidateLuaSource(*source, chunk_name).empty();

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto condition_mentions_temp_reg = [](std::string_view condition, int reg) {
        const std::string token = "r" + std::to_string(reg);
        std::size_t search_from = 0;
        while (search_from < condition.size()) {
            const std::size_t match = condition.find(token, search_from);
            if (match == std::string_view::npos) {
                return false;
            }
            const bool boundary_before =
                match == 0 ||
                (!std::isalnum(static_cast<unsigned char>(condition[match - 1])) &&
                 condition[match - 1] != '_');
            const std::size_t after = match + token.size();
            const bool boundary_after =
                after >= condition.size() ||
                (!std::isalnum(static_cast<unsigned char>(condition[after])) &&
                 condition[after] != '_');
            if (boundary_before && boundary_after) {
                return true;
            }
            search_from = match + token.size();
        }
        return false;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedCallResultPattern> patterns =
            CollectLua51RenderedCallResultPatterns(function.child_functions[block_index],
                                                   parameter_names);
        if (patterns.empty()) {
            continue;
        }

        std::size_t cursor = 0;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            const std::string normalized_line =
                NormalizeRenderedTrivialParenthesizedLiterals(trim_line(lines[line_index]));
            if (cursor < patterns.size()) {
                const std::string normalized_pattern =
                    NormalizeRenderedTrivialParenthesizedLiterals(patterns[cursor].expression);
                if (!normalized_pattern.empty() &&
                    normalized_line.find(normalized_pattern) != std::string::npos) {
                    ++cursor;
                    continue;
                }
            }

            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index], &is_local, &lhs, &rhs)) {
                continue;
            }

            const std::optional<int> target_reg = ParseRenderedTemporaryRegisterIndex(lhs);
            if (!target_reg.has_value()) {
                continue;
            }

            std::string current_callee;
            std::vector<std::string> current_arguments;
            if (!ParseRenderedSingleCallExpression(rhs, &current_callee, &current_arguments)) {
                continue;
            }
            if (IsRenderedAnonymousFunctionPlaceholder(rhs) ||
                ContainsRenderedEmptyStringPlaceholderExpression(rhs)) {
                continue;
            }

            const std::string current_terminal = ExtractRenderedCallTerminal(current_callee);
            if (current_terminal.empty()) {
                continue;
            }

            const bool suspicious_current_expression =
                ContainsSuspiciousRenderedNestedCallBase(rhs);
            const bool suspicious_current_callee =
                IsSuspiciousRenderedDirectCallBase(current_callee);
            bool next_condition_uses_target_reg = false;
            std::size_t next_nonempty = line_index + 1;
            while (next_nonempty < function_end && trim_line(lines[next_nonempty]).empty()) {
                ++next_nonempty;
            }
            if (next_nonempty < function_end) {
                std::string next_prefix;
                std::string next_condition;
                std::string next_suffix;
                if (TryParseRenderedConditionHeader(lines[next_nonempty],
                                                   &next_prefix,
                                                   &next_condition,
                                                   &next_suffix)) {
                    next_condition_uses_target_reg =
                        condition_mentions_temp_reg(next_condition, *target_reg);
                }
            }

            const RenderedCallResultPattern* chosen_pattern = nullptr;
            std::size_t chosen_pattern_index = cursor;
            constexpr std::size_t kPatternSearchBackWindow = 32;
            constexpr std::size_t kPatternSearchForwardWindow = 96;
            std::size_t matching_pattern_count = 0;
            std::size_t tested_pattern_count = 0;
            std::optional<std::string> consensus_callee;
            bool callee_consensus_mismatch = false;
            const std::size_t search_begin =
                cursor > kPatternSearchBackWindow ? cursor - kPatternSearchBackWindow : 0;
            const std::size_t search_end =
                std::min(patterns.size(), cursor + kPatternSearchForwardWindow);
            std::size_t best_distance = std::numeric_limits<std::size_t>::max();
            bool best_is_forward = false;
            for (std::size_t pattern_index = search_begin;
                 pattern_index < search_end;
                 ++pattern_index) {
                const RenderedCallResultPattern& candidate_pattern = patterns[pattern_index];
                if (candidate_pattern.target_reg != *target_reg || candidate_pattern.expression.empty()) {
                    continue;
                }

                std::string candidate_callee;
                std::vector<std::string> candidate_arguments;
                if (!ParseRenderedSingleCallExpression(candidate_pattern.expression,
                                                       &candidate_callee,
                                                       &candidate_arguments) ||
                    candidate_arguments.size() != current_arguments.size() ||
                    ExtractRenderedCallTerminal(candidate_callee) != current_terminal) {
                    continue;
                }

                ++matching_pattern_count;
                if (candidate_pattern.tested_immediately) {
                    ++tested_pattern_count;
                }
                const std::string normalized_candidate_callee =
                    NormalizeRenderedTrivialParenthesizedLiterals(candidate_callee);
                if (!consensus_callee.has_value()) {
                    consensus_callee = normalized_candidate_callee;
                } else if (*consensus_callee != normalized_candidate_callee) {
                    callee_consensus_mismatch = true;
                }

                const bool candidate_is_forward = pattern_index >= cursor;
                const std::size_t candidate_distance =
                    candidate_is_forward ? pattern_index - cursor : cursor - pattern_index;
                if (chosen_pattern == nullptr ||
                    candidate_distance < best_distance ||
                    (candidate_distance == best_distance &&
                     candidate_is_forward && !best_is_forward)) {
                    chosen_pattern = &candidate_pattern;
                    chosen_pattern_index = pattern_index;
                    best_distance = candidate_distance;
                    best_is_forward = candidate_is_forward;
                }
            }

            if (chosen_pattern == nullptr) {
                continue;
            }

            std::string chosen_callee;
            std::vector<std::string> chosen_arguments;
            if (!ParseRenderedSingleCallExpression(chosen_pattern->expression,
                                                   &chosen_callee,
                                                   &chosen_arguments)) {
                continue;
            }

            const std::string normalized_current_expression =
                NormalizeRenderedTrivialParenthesizedLiterals(rhs);
            const std::string normalized_chosen_expression =
                NormalizeRenderedTrivialParenthesizedLiterals(chosen_pattern->expression);
            const std::string normalized_current_callee =
                NormalizeRenderedTrivialParenthesizedLiterals(current_callee);
            const std::string normalized_chosen_callee =
                NormalizeRenderedTrivialParenthesizedLiterals(chosen_callee);
            if (normalized_current_expression == normalized_chosen_expression) {
                continue;
            }

            const bool consensus_supports_replacement =
                matching_pattern_count != 0 && !callee_consensus_mismatch &&
                consensus_callee.has_value() &&
                *consensus_callee == normalized_chosen_callee;
            const bool immediate_test_supports_replacement =
                next_condition_uses_target_reg && chosen_pattern->tested_immediately &&
                tested_pattern_count == 1;
            if (!suspicious_current_expression &&
                !suspicious_current_callee &&
                !consensus_supports_replacement &&
                !immediate_test_supports_replacement) {
                continue;
            }

            const std::string previous_rhs = rhs;
            lines[line_index] = line_indent(lines[line_index]) +
                                (is_local ? "local " : "") + lhs + " = " +
                                chosen_pattern->expression;
            if (next_nonempty < function_end) {
                std::string next_prefix;
                std::string next_condition;
                std::string next_suffix;
                if (TryParseRenderedConditionHeader(lines[next_nonempty],
                                                   &next_prefix,
                                                   &next_condition,
                                                   &next_suffix) &&
                    ReplaceAllRenderedSubstrings(&next_condition,
                                                 previous_rhs,
                                                 chosen_pattern->expression)) {
                    lines[next_nonempty] = next_prefix + next_condition + next_suffix;
                }
            }
            cursor = std::max(cursor, chosen_pattern_index + 1);
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (source_was_valid && !ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_call_result_assignment_receivers_restored");
    return true;
}

bool WouldRenderedCallReplacementLoseStructure(std::string_view current_expression,
                                               std::string_view replacement_expression) {
    const auto score_expression = [](std::string_view expression) {
        int score = 0;
        if (expression.find("..") != std::string_view::npos) {
            score += 4;
        }
        if (expression.find(':') != std::string_view::npos) {
            score += 2;
        }
        if (expression.find('[') != std::string_view::npos ||
            expression.find('.') != std::string_view::npos) {
            score += 1;
        }
        if (expression.find('{') != std::string_view::npos &&
            expression.find('}') != std::string_view::npos) {
            score += 1;
        }
        return score;
    };

    const int current_score = score_expression(current_expression);
    const int replacement_score = score_expression(replacement_expression);
    if (current_score > replacement_score) {
        return true;
    }

    return current_score > 0 &&
           current_expression.size() > replacement_expression.size() + 8;
}

bool TryRestoreBytecodeDirectStatementCallReceivers(const Lua51Function& function,
                                                    std::string_view chunk_name,
                                                    std::string* source,
                                                    std::string* note,
                                                    std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }
    const bool source_was_valid = ValidateLuaSource(*source, chunk_name).empty();

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto parse_statement_call_expression =
        [&](std::string_view line,
            bool* is_assignment,
            std::string* assignment_prefix,
            std::string* expression,
            std::string* callee,
            std::vector<std::string>* arguments) {
            if (is_assignment == nullptr || assignment_prefix == nullptr ||
                expression == nullptr || callee == nullptr || arguments == nullptr) {
                return false;
            }

            *is_assignment = false;
            assignment_prefix->clear();
            expression->clear();
            callee->clear();
            arguments->clear();

            std::string_view trimmed = trim_line(line);
            if (trimmed.empty()) {
                return false;
            }

            if (ParseRenderedSingleCallExpression(trimmed, callee, arguments)) {
                *expression = std::string(trimmed);
                return true;
            }

            if (trimmed.starts_with("{")) {
                const std::size_t equals = trimmed.find('=');
                if (equals != std::string_view::npos && equals + 1 < trimmed.size()) {
                    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equals + 1));
                    if (ParseRenderedSingleCallExpression(rhs, callee, arguments)) {
                        *expression = std::string(trimmed);
                        return true;
                    }
                }
            }

            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (!ParseSingleRenderedAssignment(trimmed, &assign_is_local, &assign_lhs, &assign_rhs) ||
                !ParseRenderedSingleCallExpression(assign_rhs, callee, arguments)) {
                return false;
            }

            *is_assignment = true;
            *assignment_prefix =
                std::string(assign_is_local ? "local " : "") + assign_lhs + " = ";
            *expression = std::move(assign_rhs);
            return true;
        };
    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedDirectStatementCallPattern> patterns =
            CollectLua51RenderedDirectStatementCallPatterns(function.child_functions[block_index],
                                                            parameter_names);
        if (patterns.empty()) {
            continue;
        }

        std::size_t cursor = 0;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            const std::string normalized_line =
                NormalizeRenderedTrivialParenthesizedLiterals(trim_line(lines[line_index]));
            if (cursor < patterns.size()) {
                const std::string normalized_pattern =
                    NormalizeRenderedTrivialParenthesizedLiterals(patterns[cursor].expression);
                if (!normalized_pattern.empty() &&
                    normalized_line.find(normalized_pattern) != std::string::npos) {
                    ++cursor;
                    continue;
                }
            }

            bool current_is_assignment = false;
            std::string current_assignment_prefix;
            std::string current_expression;
            std::string current_callee;
            std::vector<std::string> current_arguments;
            if (!parse_statement_call_expression(lines[line_index],
                                                 &current_is_assignment,
                                                 &current_assignment_prefix,
                                                 &current_expression,
                                                 &current_callee,
                                                 &current_arguments)) {
                continue;
            }
            if (IsRenderedAnonymousFunctionPlaceholder(current_expression) ||
                ContainsRenderedEmptyStringPlaceholderExpression(current_expression)) {
                continue;
            }

            const bool suspicious_current_expression =
                ContainsSuspiciousRenderedNestedCallBase(current_expression);
            const bool suspicious_current_callee =
                IsSuspiciousRenderedDirectCallBase(current_callee);
            const bool obvious_literal_current_expression =
                TrimLuaWhitespace(current_expression).starts_with("{");
            const std::string current_terminal =
                ExtractRenderedCallTerminal(current_callee);
            const bool current_terminal_available = !current_terminal.empty();
            if (!current_terminal_available &&
                !suspicious_current_expression &&
                !suspicious_current_callee &&
                !obvious_literal_current_expression) {
                continue;
            }
            const bool weak_placeholder_arguments =
                !current_arguments.empty() &&
                std::all_of(current_arguments.begin(),
                            current_arguments.end(),
                            [](std::string_view argument) {
                                argument = TrimLuaWhitespace(argument);
                                return argument == "nil" || argument == "{}" ||
                                       IsRenderTemporaryName(argument);
                            });

            const RenderedDirectStatementCallPattern* chosen_pattern = nullptr;
            std::size_t chosen_pattern_index = cursor;
            constexpr std::size_t kPatternSearchBackWindow = 32;
            constexpr std::size_t kPatternSearchForwardWindow = 96;
            std::size_t matching_pattern_count = 0;
            std::optional<std::string> consensus_callee;
            bool callee_consensus_mismatch = false;
            const std::size_t search_begin =
                cursor > kPatternSearchBackWindow ? cursor - kPatternSearchBackWindow : 0;
            const std::size_t search_end =
                std::min(patterns.size(), cursor + kPatternSearchForwardWindow);
            std::size_t best_distance = std::numeric_limits<std::size_t>::max();
            bool best_is_forward = false;
            for (std::size_t pattern_index = search_begin;
                 pattern_index < search_end;
                 ++pattern_index) {
                const RenderedDirectStatementCallPattern& candidate_pattern =
                    patterns[pattern_index];
                if (candidate_pattern.argument_count != current_arguments.size() ||
                    IsSuspiciousRenderedDirectCallBase(candidate_pattern.callee)) {
                    continue;
                }

                bool candidate_matches = false;
                if (current_terminal_available &&
                    !suspicious_current_expression &&
                    !suspicious_current_callee &&
                    !obvious_literal_current_expression) {
                    candidate_matches = !candidate_pattern.terminal_callee.empty() &&
                                        candidate_pattern.terminal_callee == current_terminal;
                } else {
                    std::string candidate_callee;
                    std::vector<std::string> candidate_arguments;
                    if (ParseRenderedSingleCallExpression(candidate_pattern.expression,
                                                         &candidate_callee,
                                                         &candidate_arguments) &&
                        candidate_arguments.size() == current_arguments.size()) {
                        candidate_matches = true;
                        for (std::size_t argument_index = 0;
                             argument_index < candidate_arguments.size();
                             ++argument_index) {
                            const std::string normalized_candidate_argument =
                                NormalizeRenderedTrivialParenthesizedLiterals(candidate_arguments[argument_index]);
                            const std::string normalized_current_argument =
                                NormalizeRenderedTrivialParenthesizedLiterals(current_arguments[argument_index]);
                            const bool candidate_argument_matches =
                                normalized_candidate_argument == normalized_current_argument ||
                                (normalized_candidate_argument == "{}" &&
                                 IsRenderTemporaryName(normalized_current_argument));
                            if (!candidate_argument_matches) {
                                candidate_matches =
                                    suspicious_current_callee && weak_placeholder_arguments;
                                break;
                            }
                        }
                    }
                }
                if (!candidate_matches) {
                    continue;
                }

                ++matching_pattern_count;
                const std::string normalized_candidate_callee =
                    NormalizeRenderedTrivialParenthesizedLiterals(candidate_pattern.callee);
                if (!consensus_callee.has_value()) {
                    consensus_callee = normalized_candidate_callee;
                } else if (*consensus_callee != normalized_candidate_callee) {
                    callee_consensus_mismatch = true;
                }

                const bool candidate_is_forward = pattern_index >= cursor;
                const std::size_t candidate_distance =
                    candidate_is_forward ? pattern_index - cursor : cursor - pattern_index;
                if (chosen_pattern == nullptr ||
                    candidate_distance < best_distance ||
                    (candidate_distance == best_distance &&
                     candidate_is_forward && !best_is_forward)) {
                    chosen_pattern = &candidate_pattern;
                    chosen_pattern_index = pattern_index;
                    best_distance = candidate_distance;
                    best_is_forward = candidate_is_forward;
                }
            }

            if (chosen_pattern == nullptr) {
                continue;
            }

            const std::string normalized_current_expression =
                NormalizeRenderedTrivialParenthesizedLiterals(current_expression);
            const std::string normalized_chosen_expression =
                NormalizeRenderedTrivialParenthesizedLiterals(chosen_pattern->expression);
            const std::string normalized_current_callee =
                NormalizeRenderedTrivialParenthesizedLiterals(current_callee);
            const std::string normalized_chosen_callee =
                NormalizeRenderedTrivialParenthesizedLiterals(chosen_pattern->callee);
            if (normalized_current_expression == normalized_chosen_expression) {
                continue;
            }

            const bool consensus_supports_replacement =
                matching_pattern_count != 0 && !callee_consensus_mismatch &&
                consensus_callee.has_value() &&
                *consensus_callee == normalized_chosen_callee;
            const bool replacement_loses_structure =
                WouldRenderedCallReplacementLoseStructure(current_expression,
                                                         chosen_pattern->expression);
            if (!suspicious_current_expression &&
                !suspicious_current_callee &&
                !obvious_literal_current_expression &&
                (!consensus_supports_replacement || replacement_loses_structure)) {
                continue;
            }

            lines[line_index] =
                line_indent(lines[line_index]) + current_assignment_prefix + chosen_pattern->expression;
            cursor = std::max(cursor, chosen_pattern_index + 1);
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (source_was_valid && !ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_direct_call_receivers_restored");
    return true;
}

bool TryRestoreBytecodeTopLevelCallResultAssignmentReceivers(const Lua51Function& function,
                                                             std::string_view chunk_name,
                                                             std::string* source,
                                                             std::string* note,
                                                             std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }
    const bool source_was_valid = ValidateLuaSource(*source, chunk_name).empty();

    const std::vector<std::string> parameter_names;
    const std::vector<RenderedCallResultPattern> patterns =
        CollectLua51RenderedCallResultPatterns(function, parameter_names);
    if (patterns.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto parse_any_callee_call_expression =
        [](std::string_view line, std::string* callee, std::vector<std::string>* arguments) {
            if (callee == nullptr || arguments == nullptr) {
                return false;
            }

            line = TrimLuaWhitespace(StripLuaLineComment(line));
            if (line.empty() || !line.ends_with(')')) {
                return false;
            }

            const std::size_t open = line.find('(');
            if (open == std::string_view::npos || open == 0) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            bool escaped = false;
            int paren_depth = 0;
            std::size_t close = std::string_view::npos;
            for (std::size_t index = open; index < line.size(); ++index) {
                const char ch = line[index];
                if (escaped) {
                    escaped = false;
                    continue;
                }
                if ((in_single_quote || in_double_quote) && ch == '\\') {
                    escaped = true;
                    continue;
                }
                if (!in_double_quote && ch == '\'') {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (!in_single_quote && ch == '"') {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                } else if (ch == ')') {
                    --paren_depth;
                    if (paren_depth == 0) {
                        close = index;
                        break;
                    }
                }
            }
            if (close == std::string_view::npos || close + 1 != line.size()) {
                return false;
            }

            const std::string_view callee_view = TrimLuaWhitespace(line.substr(0, open));
            if (callee_view.empty()) {
                return false;
            }

            *callee = std::string(callee_view);
            return SplitRenderedTopLevelCommaChainLocal(line.substr(open + 1, close - open - 1),
                                                        arguments);
        };
    const auto next_top_level_nonempty =
        [&](const std::vector<std::string>& rendered_lines,
            const std::vector<RenderedFunctionBlock>& rendered_blocks,
            std::size_t start) {
            std::size_t block_index = 0;
            while (start < rendered_lines.size()) {
                while (block_index < rendered_blocks.size() &&
                       rendered_blocks[block_index].end < start) {
                    ++block_index;
                }
                if (block_index < rendered_blocks.size() &&
                    rendered_blocks[block_index].begin <= start &&
                    start <= rendered_blocks[block_index].end) {
                    start = rendered_blocks[block_index].end + 1;
                    continue;
                }
                if (!trim_line(rendered_lines[start]).empty()) {
                    return start;
                }
                ++start;
            }
            return rendered_lines.size();
        };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    bool changed = false;
    std::size_t cursor = 0;
    for (std::size_t line_index = next_top_level_nonempty(lines, blocks, 0);
         line_index < lines.size();
         line_index = next_top_level_nonempty(lines, blocks, line_index + 1)) {
        const std::string normalized_line =
            NormalizeRenderedTrivialParenthesizedLiterals(trim_line(lines[line_index]));
        if (cursor < patterns.size()) {
            const std::string normalized_pattern =
                NormalizeRenderedTrivialParenthesizedLiterals(patterns[cursor].expression);
            if (!normalized_pattern.empty() &&
                normalized_line.find(normalized_pattern) != std::string::npos) {
                ++cursor;
                continue;
            }
        }

        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseSingleRenderedAssignment(lines[line_index], &is_local, &lhs, &rhs)) {
            continue;
        }

        const std::optional<int> target_reg = ParseRenderedTemporaryRegisterIndex(lhs);
        if (!target_reg.has_value()) {
            continue;
        }

        std::string current_callee;
        std::vector<std::string> current_arguments;
        if (!parse_any_callee_call_expression(rhs, &current_callee, &current_arguments)) {
            continue;
        }
        if (IsRenderedAnonymousFunctionPlaceholder(rhs) ||
            ContainsRenderedEmptyStringPlaceholderExpression(rhs)) {
            continue;
        }

        const bool suspicious_current_expression =
            ContainsSuspiciousRenderedNestedCallBase(rhs);
        const bool suspicious_current_callee =
            IsSuspiciousRenderedDirectCallBase(current_callee);
        const std::string_view trimmed_current_callee = TrimLuaWhitespace(current_callee);
        const bool obvious_literal_current_callee =
            !trimmed_current_callee.empty() &&
            (trimmed_current_callee.front() == '{' ||
             trimmed_current_callee.starts_with("function"));
        const std::string current_terminal = ExtractRenderedCallTerminal(current_callee);
        const bool current_terminal_available = !current_terminal.empty();
        if (!suspicious_current_expression && !suspicious_current_callee &&
            !obvious_literal_current_callee) {
            continue;
        }

        std::vector<std::string> normalized_current_arguments;
        normalized_current_arguments.reserve(current_arguments.size());
        for (const std::string& argument : current_arguments) {
            normalized_current_arguments.push_back(
                NormalizeRenderedTrivialParenthesizedLiterals(argument));
        }
        const bool weak_placeholder_arguments =
            !normalized_current_arguments.empty() &&
            std::all_of(normalized_current_arguments.begin(),
                        normalized_current_arguments.end(),
                        [](std::string_view argument) {
                            argument = TrimLuaWhitespace(argument);
                            return argument == "nil" || argument == "{}" ||
                                   IsRenderTemporaryName(argument);
                        });

        const RenderedCallResultPattern* chosen_pattern = nullptr;
        std::size_t chosen_pattern_index = cursor;
        constexpr std::size_t kPatternSearchBackWindow = 16;
        constexpr std::size_t kPatternSearchForwardWindow = 64;
        std::size_t matching_pattern_count = 0;
        std::optional<std::string> consensus_callee;
        bool callee_consensus_mismatch = false;
        const std::size_t search_begin =
            cursor > kPatternSearchBackWindow ? cursor - kPatternSearchBackWindow : 0;
        const std::size_t search_end =
            std::min(patterns.size(), cursor + kPatternSearchForwardWindow);
        std::size_t best_distance = std::numeric_limits<std::size_t>::max();
        bool best_is_forward = false;
        for (std::size_t pattern_index = search_begin;
             pattern_index < search_end;
             ++pattern_index) {
            const RenderedCallResultPattern& candidate_pattern = patterns[pattern_index];
            if (candidate_pattern.target_reg != *target_reg || candidate_pattern.expression.empty()) {
                continue;
            }

            std::string candidate_callee;
            std::vector<std::string> candidate_arguments;
            if (!parse_any_callee_call_expression(candidate_pattern.expression,
                                                  &candidate_callee,
                                                  &candidate_arguments) ||
                candidate_arguments.size() != normalized_current_arguments.size() ||
                IsSuspiciousRenderedDirectCallBase(candidate_callee)) {
                continue;
            }

            bool candidate_matches = false;
            if (current_terminal_available &&
                !suspicious_current_expression &&
                !suspicious_current_callee &&
                !obvious_literal_current_callee) {
                candidate_matches =
                    ExtractRenderedCallTerminal(candidate_callee) == current_terminal;
            } else {
                candidate_matches = true;
                for (std::size_t argument_index = 0;
                     argument_index < candidate_arguments.size();
                     ++argument_index) {
                    const std::string normalized_candidate_argument =
                        NormalizeRenderedTrivialParenthesizedLiterals(candidate_arguments[argument_index]);
                    const std::string& normalized_current_argument =
                        normalized_current_arguments[argument_index];
                    const bool candidate_argument_matches =
                        normalized_candidate_argument == normalized_current_argument ||
                        (normalized_candidate_argument == "{}" &&
                         IsRenderTemporaryName(normalized_current_argument));
                    if (!candidate_argument_matches) {
                        candidate_matches =
                            suspicious_current_callee && weak_placeholder_arguments;
                        break;
                    }
                }
            }
            if (!candidate_matches) {
                continue;
            }

            ++matching_pattern_count;
            const std::string normalized_candidate_callee =
                NormalizeRenderedTrivialParenthesizedLiterals(candidate_callee);
            if (!consensus_callee.has_value()) {
                consensus_callee = normalized_candidate_callee;
            } else if (*consensus_callee != normalized_candidate_callee) {
                callee_consensus_mismatch = true;
            }

            const bool candidate_is_forward = pattern_index >= cursor;
            const std::size_t candidate_distance =
                candidate_is_forward ? pattern_index - cursor : cursor - pattern_index;
            if (chosen_pattern == nullptr ||
                candidate_distance < best_distance ||
                (candidate_distance == best_distance &&
                 candidate_is_forward && !best_is_forward)) {
                chosen_pattern = &candidate_pattern;
                chosen_pattern_index = pattern_index;
                best_distance = candidate_distance;
                best_is_forward = candidate_is_forward;
            }
        }

        if (chosen_pattern == nullptr) {
            continue;
        }

        std::string chosen_callee;
        std::vector<std::string> chosen_arguments;
        if (!parse_any_callee_call_expression(chosen_pattern->expression,
                                              &chosen_callee,
                                              &chosen_arguments)) {
            continue;
        }

        std::vector<std::string> rewritten_arguments = chosen_arguments;
        for (std::size_t argument_index = 0;
             argument_index < rewritten_arguments.size() &&
             argument_index < normalized_current_arguments.size();
             ++argument_index) {
            const std::string normalized_candidate_argument =
                NormalizeRenderedTrivialParenthesizedLiterals(rewritten_arguments[argument_index]);
            if (normalized_candidate_argument == "{}" &&
                IsRenderTemporaryName(normalized_current_arguments[argument_index])) {
                rewritten_arguments[argument_index] = current_arguments[argument_index];
            }
        }

        std::string rewritten_expression = chosen_callee + "(";
        for (std::size_t argument_index = 0; argument_index < rewritten_arguments.size(); ++argument_index) {
            if (argument_index != 0) {
                rewritten_expression += ", ";
            }
            rewritten_expression += rewritten_arguments[argument_index];
        }
        rewritten_expression += ")";
        CanonicalizeRenderedExplicitSelfMethodCall(chosen_callee,
                                                   rewritten_arguments,
                                                   &rewritten_expression);

        const std::string normalized_current_expression =
            NormalizeRenderedTrivialParenthesizedLiterals(rhs);
        const std::string normalized_rewritten_expression =
            NormalizeRenderedTrivialParenthesizedLiterals(rewritten_expression);
        if (normalized_current_expression == normalized_rewritten_expression) {
            continue;
        }

        const bool consensus_supports_replacement =
            matching_pattern_count != 0 && !callee_consensus_mismatch &&
            consensus_callee.has_value();
        if (!suspicious_current_expression &&
            !suspicious_current_callee &&
            !obvious_literal_current_callee &&
            !consensus_supports_replacement) {
            continue;
        }

        lines[line_index] =
            line_indent(lines[line_index]) +
            (is_local ? "local " : std::string()) + lhs + " = " + rewritten_expression;
        cursor = std::max(cursor, chosen_pattern_index + 1);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (source_was_valid && !ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note,
                        note_prefix,
                        "bytecode_top_level_call_result_assignment_receivers_restored");
    return true;
}

bool TryRestoreBytecodeTopLevelDirectStatementCallReceivers(const Lua51Function& function,
                                                            std::string_view chunk_name,
                                                            std::string* source,
                                                            std::string* note,
                                                            std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }
    const bool source_was_valid = ValidateLuaSource(*source, chunk_name).empty();

    const std::vector<std::string> parameter_names;
    const std::vector<RenderedDirectStatementCallPattern> patterns =
        CollectLua51RenderedDirectStatementCallPatterns(function, parameter_names);
    if (patterns.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto parse_any_callee_call_expression =
        [](std::string_view line, std::string* callee, std::vector<std::string>* arguments) {
            if (callee == nullptr || arguments == nullptr) {
                return false;
            }

            line = TrimLuaWhitespace(StripLuaLineComment(line));
            if (line.empty() || !line.ends_with(')')) {
                return false;
            }

            const std::size_t open = line.find('(');
            if (open == std::string_view::npos || open == 0) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            bool escaped = false;
            int paren_depth = 0;
            std::size_t close = std::string_view::npos;
            for (std::size_t index = open; index < line.size(); ++index) {
                const char ch = line[index];
                if (escaped) {
                    escaped = false;
                    continue;
                }
                if ((in_single_quote || in_double_quote) && ch == '\\') {
                    escaped = true;
                    continue;
                }
                if (!in_double_quote && ch == '\'') {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (!in_single_quote && ch == '"') {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }
                if (ch == '(') {
                    ++paren_depth;
                } else if (ch == ')') {
                    --paren_depth;
                    if (paren_depth == 0) {
                        close = index;
                        break;
                    }
                }
            }
            if (close == std::string_view::npos || close + 1 != line.size()) {
                return false;
            }

            const std::string_view callee_view = TrimLuaWhitespace(line.substr(0, open));
            if (callee_view.empty()) {
                return false;
            }

            *callee = std::string(callee_view);
            return SplitRenderedTopLevelCommaChainLocal(line.substr(open + 1, close - open - 1),
                                                        arguments);
        };
    const auto parse_statement_call_expression =
        [&](std::string_view line,
            bool* is_assignment,
            std::string* assignment_prefix,
            std::string* expression,
            std::string* callee,
            std::vector<std::string>* arguments) {
            if (is_assignment == nullptr || assignment_prefix == nullptr ||
                expression == nullptr || callee == nullptr || arguments == nullptr) {
                return false;
            }

            *is_assignment = false;
            assignment_prefix->clear();
            expression->clear();
            callee->clear();
            arguments->clear();

            std::string_view trimmed = trim_line(line);
            if (trimmed.empty()) {
                return false;
            }

            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (ParseSingleRenderedAssignment(trimmed, &assign_is_local, &assign_lhs, &assign_rhs) &&
                parse_any_callee_call_expression(assign_rhs, callee, arguments)) {
                *is_assignment = true;
                *assignment_prefix =
                    std::string(assign_is_local ? "local " : "") + assign_lhs + " = ";
                *expression = std::move(assign_rhs);
                return true;
            }

            if (parse_any_callee_call_expression(trimmed, callee, arguments)) {
                *expression = std::string(trimmed);
                return true;
            }

            if (trimmed.starts_with("{")) {
                const std::size_t equals = trimmed.find('=');
                if (equals != std::string_view::npos && equals + 1 < trimmed.size()) {
                    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equals + 1));
                    if (parse_any_callee_call_expression(rhs, callee, arguments)) {
                        *expression = std::string(trimmed);
                        return true;
                    }
                }
            }

            return false;
        };
    const auto next_top_level_nonempty =
        [&](const std::vector<std::string>& rendered_lines,
            const std::vector<RenderedFunctionBlock>& rendered_blocks,
            std::size_t start) {
            std::size_t block_index = 0;
            while (start < rendered_lines.size()) {
                while (block_index < rendered_blocks.size() &&
                       rendered_blocks[block_index].end < start) {
                    ++block_index;
                }
                if (block_index < rendered_blocks.size() &&
                    rendered_blocks[block_index].begin <= start &&
                    start <= rendered_blocks[block_index].end) {
                    start = rendered_blocks[block_index].end + 1;
                    continue;
                }
                if (!trim_line(rendered_lines[start]).empty()) {
                    return start;
                }
                ++start;
            }
            return rendered_lines.size();
        };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    bool changed = false;
    std::size_t cursor = 0;
    for (std::size_t line_index = next_top_level_nonempty(lines, blocks, 0);
         line_index < lines.size();
         line_index = next_top_level_nonempty(lines, blocks, line_index + 1)) {
        const std::string normalized_line =
            NormalizeRenderedTrivialParenthesizedLiterals(trim_line(lines[line_index]));
        if (cursor < patterns.size()) {
            const std::string normalized_pattern =
                NormalizeRenderedTrivialParenthesizedLiterals(patterns[cursor].expression);
            if (!normalized_pattern.empty() &&
                normalized_line.find(normalized_pattern) != std::string::npos) {
                ++cursor;
                continue;
            }
        }

        bool current_is_assignment = false;
        std::string current_assignment_prefix;
        std::string current_expression;
        std::string current_callee;
        std::vector<std::string> current_arguments;
            if (!parse_statement_call_expression(lines[line_index],
                                                 &current_is_assignment,
                                                 &current_assignment_prefix,
                                                 &current_expression,
                                                 &current_callee,
                                                 &current_arguments)) {
                continue;
            }
            if (IsRenderedAnonymousFunctionPlaceholder(current_expression) ||
                ContainsRenderedEmptyStringPlaceholderExpression(current_expression)) {
                continue;
            }

        const bool suspicious_current_expression =
            ContainsSuspiciousRenderedNestedCallBase(current_expression);
        const bool suspicious_current_callee =
            IsSuspiciousRenderedDirectCallBase(current_callee);
        const bool obvious_literal_current_expression =
            TrimLuaWhitespace(current_expression).starts_with("{");
        const std::string_view trimmed_current_callee = TrimLuaWhitespace(current_callee);
        const bool obvious_literal_current_callee =
            !trimmed_current_callee.empty() &&
            (trimmed_current_callee.front() == '{' ||
             trimmed_current_callee.starts_with("function"));
        const std::string current_terminal = ExtractRenderedCallTerminal(current_callee);
        const bool current_terminal_available = !current_terminal.empty();
        if (!suspicious_current_expression && !suspicious_current_callee &&
            !obvious_literal_current_callee && !obvious_literal_current_expression) {
            continue;
        }

        std::vector<std::string> normalized_current_arguments;
        normalized_current_arguments.reserve(current_arguments.size());
        for (const std::string& argument : current_arguments) {
            normalized_current_arguments.push_back(
                NormalizeRenderedTrivialParenthesizedLiterals(argument));
        }
        const bool weak_placeholder_arguments =
            !normalized_current_arguments.empty() &&
            std::all_of(normalized_current_arguments.begin(),
                        normalized_current_arguments.end(),
                        [](std::string_view argument) {
                            argument = TrimLuaWhitespace(argument);
                            return argument == "nil" || argument == "{}" ||
                                   IsRenderTemporaryName(argument);
                        });

        const RenderedDirectStatementCallPattern* chosen_pattern = nullptr;
        std::size_t chosen_pattern_index = cursor;
        constexpr std::size_t kPatternSearchBackWindow = 16;
        constexpr std::size_t kPatternSearchForwardWindow = 64;
        std::size_t matching_pattern_count = 0;
        std::optional<std::string> consensus_callee;
        bool callee_consensus_mismatch = false;
        const std::size_t search_begin =
            cursor > kPatternSearchBackWindow ? cursor - kPatternSearchBackWindow : 0;
        const std::size_t search_end =
            std::min(patterns.size(), cursor + kPatternSearchForwardWindow);
        std::size_t best_distance = std::numeric_limits<std::size_t>::max();
        bool best_is_forward = false;
        for (std::size_t pattern_index = search_begin;
             pattern_index < search_end;
             ++pattern_index) {
            const RenderedDirectStatementCallPattern& candidate_pattern = patterns[pattern_index];
            if (candidate_pattern.argument_count != current_arguments.size() ||
                IsSuspiciousRenderedDirectCallBase(candidate_pattern.callee)) {
                continue;
            }

            bool candidate_matches = false;
            if (current_terminal_available &&
                !suspicious_current_expression &&
                !suspicious_current_callee &&
                !obvious_literal_current_callee) {
                candidate_matches = !candidate_pattern.terminal_callee.empty() &&
                                    candidate_pattern.terminal_callee == current_terminal;
            } else {
                std::string candidate_callee;
                std::vector<std::string> candidate_arguments;
                if (ParseRenderedSingleCallExpression(candidate_pattern.expression,
                                                     &candidate_callee,
                                                     &candidate_arguments) &&
                    candidate_arguments.size() == normalized_current_arguments.size()) {
                    candidate_matches = true;
                    for (std::size_t argument_index = 0;
                         argument_index < candidate_arguments.size();
                         ++argument_index) {
                        const std::string normalized_candidate_argument =
                            NormalizeRenderedTrivialParenthesizedLiterals(candidate_arguments[argument_index]);
                        const std::string& normalized_current_argument =
                            normalized_current_arguments[argument_index];
                        const bool candidate_argument_matches =
                            normalized_candidate_argument == normalized_current_argument ||
                            (normalized_candidate_argument == "{}" &&
                             IsRenderTemporaryName(normalized_current_argument));
                        if (!candidate_argument_matches) {
                            candidate_matches =
                                suspicious_current_callee && weak_placeholder_arguments;
                            break;
                        }
                    }
                }
            }
            if (!candidate_matches) {
                continue;
            }

            ++matching_pattern_count;
            const std::string normalized_candidate_callee =
                NormalizeRenderedTrivialParenthesizedLiterals(candidate_pattern.callee);
            if (!consensus_callee.has_value()) {
                consensus_callee = normalized_candidate_callee;
            } else if (*consensus_callee != normalized_candidate_callee) {
                callee_consensus_mismatch = true;
            }

            const bool candidate_is_forward = pattern_index >= cursor;
            const std::size_t candidate_distance =
                candidate_is_forward ? pattern_index - cursor : cursor - pattern_index;
            if (chosen_pattern == nullptr ||
                candidate_distance < best_distance ||
                (candidate_distance == best_distance &&
                 candidate_is_forward && !best_is_forward)) {
                chosen_pattern = &candidate_pattern;
                chosen_pattern_index = pattern_index;
                best_distance = candidate_distance;
                best_is_forward = candidate_is_forward;
            }
        }

        if (chosen_pattern == nullptr) {
            continue;
        }

        std::string chosen_callee;
        std::vector<std::string> chosen_arguments;
        if (!parse_any_callee_call_expression(chosen_pattern->expression,
                                              &chosen_callee,
                                              &chosen_arguments)) {
            continue;
        }

        std::vector<std::string> rewritten_arguments = chosen_arguments;
        for (std::size_t argument_index = 0;
             argument_index < rewritten_arguments.size() &&
             argument_index < normalized_current_arguments.size();
             ++argument_index) {
            const std::string normalized_candidate_argument =
                NormalizeRenderedTrivialParenthesizedLiterals(rewritten_arguments[argument_index]);
            if (normalized_candidate_argument == "{}" &&
                IsRenderTemporaryName(normalized_current_arguments[argument_index])) {
                rewritten_arguments[argument_index] = current_arguments[argument_index];
            }
        }

        std::string rewritten_expression = chosen_callee + "(";
        for (std::size_t argument_index = 0; argument_index < rewritten_arguments.size(); ++argument_index) {
            if (argument_index != 0) {
                rewritten_expression += ", ";
            }
            rewritten_expression += rewritten_arguments[argument_index];
        }
        rewritten_expression += ")";
        CanonicalizeRenderedExplicitSelfMethodCall(chosen_callee,
                                                   rewritten_arguments,
                                                   &rewritten_expression);

        const std::string normalized_current_expression =
            NormalizeRenderedTrivialParenthesizedLiterals(current_expression);
        const std::string normalized_chosen_expression =
            NormalizeRenderedTrivialParenthesizedLiterals(rewritten_expression);
        if (normalized_current_expression == normalized_chosen_expression) {
            continue;
        }

        const bool consensus_supports_replacement =
            matching_pattern_count != 0 && !callee_consensus_mismatch &&
            consensus_callee.has_value();
        const bool replacement_loses_structure =
            WouldRenderedCallReplacementLoseStructure(current_expression,
                                                     rewritten_expression);
        if (!suspicious_current_expression &&
            !suspicious_current_callee &&
            !obvious_literal_current_callee &&
            !obvious_literal_current_expression &&
            (!consensus_supports_replacement || replacement_loses_structure)) {
            continue;
        }

        lines[line_index] =
            line_indent(lines[line_index]) + current_assignment_prefix + rewritten_expression;
        cursor = std::max(cursor, chosen_pattern_index + 1);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (source_was_valid && !ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_top_level_direct_call_receivers_restored");
    return true;
}

bool TryRestoreBytecodeTopLevelLiteralCalleeAdjacentCalls(const Lua51Function& function,
                                                          std::string_view chunk_name,
                                                          std::string* source,
                                                          std::string* note,
                                                          std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const std::vector<std::string> parameter_names;
    const std::vector<RenderedDirectStatementCallPattern> patterns =
        CollectLua51RenderedDirectStatementCallPatterns(function, parameter_names);
    if (patterns.empty()) {
        return false;
    }

    const bool source_was_valid = ValidateLuaSource(*source, chunk_name).empty();
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto parse_any_callee_call_expression =
        [](std::string_view line, std::string* callee, std::vector<std::string>* arguments) {
            if (callee == nullptr || arguments == nullptr) {
                return false;
            }

            line = TrimLuaWhitespace(StripLuaLineComment(line));
            if (line.empty() || !line.ends_with(')')) {
                return false;
            }

            const std::size_t open = line.find('(');
            const std::size_t close = line.rfind(')');
            if (open == std::string_view::npos || close == std::string_view::npos || open == 0 ||
                open >= close || close + 1 != line.size()) {
                return false;
            }

            const std::string_view callee_view = TrimLuaWhitespace(line.substr(0, open));
            if (callee_view.empty()) {
                return false;
            }

            *callee = std::string(callee_view);
            return SplitRenderedTopLevelCommaChainLocal(line.substr(open + 1, close - open - 1),
                                                        arguments);
        };
    const auto next_top_level_nonempty =
        [&](const std::vector<std::string>& rendered_lines,
            const std::vector<RenderedFunctionBlock>& rendered_blocks,
            std::size_t start) {
            std::size_t block_index = 0;
            while (start < rendered_lines.size()) {
                while (block_index < rendered_blocks.size() &&
                       rendered_blocks[block_index].end < start) {
                    ++block_index;
                }
                if (block_index < rendered_blocks.size() &&
                    rendered_blocks[block_index].begin <= start &&
                    start <= rendered_blocks[block_index].end) {
                    start = rendered_blocks[block_index].end + 1;
                    continue;
                }
                if (!trim_line(rendered_lines[start]).empty()) {
                    return start;
                }
                ++start;
            }
            return rendered_lines.size();
        };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    bool changed = false;
    std::size_t cursor = 0;
    std::size_t previous_nonempty = lines.size();
    for (std::size_t line_index = next_top_level_nonempty(lines, blocks, 0);
         line_index < lines.size();
         line_index = next_top_level_nonempty(lines, blocks, line_index + 1)) {
        const std::string normalized_line =
            NormalizeRenderedTrivialParenthesizedLiterals(trim_line(lines[line_index]));
        if (cursor < patterns.size()) {
            const std::string normalized_pattern =
                NormalizeRenderedTrivialParenthesizedLiterals(patterns[cursor].expression);
            if (!normalized_pattern.empty() &&
                normalized_line.find(normalized_pattern) != std::string::npos) {
                previous_nonempty = line_index;
                ++cursor;
                continue;
            }
        }

        if (previous_nonempty >= lines.size()) {
            previous_nonempty = line_index;
            continue;
        }

        bool previous_is_local = false;
        std::string previous_lhs;
        std::string previous_rhs;
        if (!ParseSingleRenderedAssignment(lines[previous_nonempty],
                                           &previous_is_local,
                                           &previous_lhs,
                                           &previous_rhs)) {
            previous_nonempty = line_index;
            continue;
        }

        std::string current_callee;
        std::vector<std::string> current_arguments;
        if (!parse_any_callee_call_expression(lines[line_index],
                                              &current_callee,
                                              &current_arguments)) {
            previous_nonempty = line_index;
            continue;
        }

        const std::string normalized_previous_rhs =
            NormalizeRenderedTrivialParenthesizedLiterals(previous_rhs);
        const std::string normalized_current_callee =
            NormalizeRenderedTrivialParenthesizedLiterals(current_callee);
        const std::string_view trimmed_current_callee =
            TrimLuaWhitespace(normalized_current_callee);
        if (normalized_previous_rhs != normalized_current_callee ||
            trimmed_current_callee.empty() ||
            (trimmed_current_callee.front() != '{' &&
             !trimmed_current_callee.starts_with("function"))) {
            previous_nonempty = line_index;
            continue;
        }

        const bool weak_placeholder_arguments =
            !current_arguments.empty() &&
            std::all_of(current_arguments.begin(),
                        current_arguments.end(),
                        [](std::string_view argument) {
                            argument = TrimLuaWhitespace(argument);
                            return argument == "nil" || argument == "{}" ||
                                   IsRenderTemporaryName(argument);
                        });
        if (!weak_placeholder_arguments) {
            previous_nonempty = line_index;
            continue;
        }

        const RenderedDirectStatementCallPattern* chosen_pattern = nullptr;
        std::size_t chosen_pattern_index = cursor;
        for (std::size_t pattern_index = cursor; pattern_index < patterns.size(); ++pattern_index) {
            const RenderedDirectStatementCallPattern& candidate_pattern = patterns[pattern_index];
            if (candidate_pattern.argument_count != current_arguments.size() ||
                IsSuspiciousRenderedDirectCallBase(candidate_pattern.callee)) {
                continue;
            }
            chosen_pattern = &candidate_pattern;
            chosen_pattern_index = pattern_index;
            break;
        }
        if (chosen_pattern == nullptr) {
            previous_nonempty = line_index;
            continue;
        }

        lines[line_index] = line_indent(lines[line_index]) + chosen_pattern->expression;
        cursor = std::max(cursor, chosen_pattern_index + 1);
        changed = true;
        previous_nonempty = line_index;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (source_was_valid && !ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note,
                        note_prefix,
                        "bytecode_top_level_literal_callee_calls_restored");
    return true;
}

bool TryRestoreBytecodeRepeatedCallResultSeeds(const Lua51Function& function,
                                               std::string_view chunk_name,
                                               std::string* source,
                                               std::string* note,
                                               std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedCallResultPattern> patterns =
            CollectLua51RenderedCallResultPatterns(function.child_functions[block_index],
                                                   parameter_names);
        if (patterns.empty()) {
            continue;
        }

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[line_index], &prefix, &condition, &suffix) ||
                !((suffix == " then" && prefix.ends_with("if ")) ||
                  (suffix == " do" && prefix.ends_with("while ")))) {
                continue;
            }

            const std::size_t window_end = std::min(function_end, line_index + 8);
            for (const RenderedCallResultPattern& pattern : patterns) {
                if (pattern.expression.empty() ||
                    condition.find(pattern.expression) == std::string::npos) {
                    continue;
                }

                const std::size_t local_probe_instruction =
                    std::min(pattern.instruction_index + 1,
                             function.child_functions[block_index].code.empty()
                                 ? pattern.instruction_index
                                 : function.child_functions[block_index].code.size() - 1);
                const std::optional<std::string> temp_name =
                    TryGetActiveLua51LocalRegisterName(function.child_functions[block_index],
                                                       local_probe_instruction,
                                                       pattern.target_reg);
                if (!temp_name.has_value()) {
                    continue;
                }

                bool saw_temp_use = false;
                bool already_seeded = false;
                for (std::size_t probe = line_index; probe <= window_end; ++probe) {
                    if (TrimLuaWhitespace(StripLuaLineComment(lines[probe])) ==
                        ("local " + *temp_name + " = " + pattern.expression)) {
                        already_seeded = true;
                        break;
                    }
                    if (CountIdentifierUses(lines[probe], *temp_name) != 0) {
                        saw_temp_use = true;
                    }
                }
                if (!saw_temp_use || already_seeded) {
                    continue;
                }

                bool replaced_any_condition = false;
                for (std::size_t probe = line_index; probe <= window_end; ++probe) {
                    std::string probe_prefix;
                    std::string probe_condition;
                    std::string probe_suffix;
                    if (!TryParseRenderedConditionHeader(lines[probe],
                                                         &probe_prefix,
                                                         &probe_condition,
                                                         &probe_suffix)) {
                        continue;
                    }
                    if (ReplaceAllRenderedSubstrings(&probe_condition,
                                                     pattern.expression,
                                                     *temp_name)) {
                        lines[probe] = probe_prefix + probe_condition + probe_suffix;
                        replaced_any_condition = true;
                    }
                }
                for (std::size_t probe = line_index; probe <= window_end; ++probe) {
                    const std::string trimmed_probe =
                        std::string(TrimLuaWhitespace(StripLuaLineComment(lines[probe])));
                    if (trimmed_probe.starts_with("local " + *temp_name + " = ")) {
                        continue;
                    }
                    if (ReplaceAllRenderedSubstrings(&lines[probe],
                                                     pattern.expression,
                                                     *temp_name)) {
                        replaced_any_condition = true;
                    }
                }
                if (!replaced_any_condition) {
                    continue;
                }

                const std::string indent = std::string(
                    lines[line_index].substr(0, lines[line_index].find_first_not_of(" \t")));
                lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                             indent + "local " + *temp_name + " = " + pattern.expression);
                changed = true;
                ++line_index;
                break;
            }
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_repeated_call_result_seeds_restored");
    return true;
}

bool TryRestoreBytecodeIteratorArgumentLocals(const Lua51Function& function,
                                              std::string_view chunk_name,
                                              std::string* source,
                                              std::string* note,
                                              std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedIteratorArgumentPattern> patterns =
            CollectLua51RenderedIteratorArgumentPatterns(function.child_functions[block_index],
                                                        parameter_names);
        if (patterns.empty()) {
            continue;
        }

        for (std::size_t line_index = block.begin + 1; line_index + 1 < function_end;
             ++line_index) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index], &is_local, &lhs, &rhs) ||
                !is_local) {
                continue;
            }

            const std::string normalized_lhs =
                NormalizeRenderedTrivialParenthesizedLiterals(TrimLuaWhitespace(lhs));
            const std::string normalized_rhs =
                NormalizeRenderedTrivialParenthesizedLiterals(TrimLuaWhitespace(rhs));
            if (normalized_lhs.empty()) {
                continue;
            }

            const RenderedIteratorArgumentPattern* chosen_pattern = nullptr;
            for (const RenderedIteratorArgumentPattern& pattern : patterns) {
                if (pattern.local_name == normalized_lhs &&
                    NormalizeRenderedTrivialParenthesizedLiterals(pattern.argument_expression) ==
                        normalized_rhs) {
                    chosen_pattern = &pattern;
                    break;
                }
            }
            if (chosen_pattern == nullptr) {
                continue;
            }

            const std::size_t next_nonempty =
                [&]() {
                    for (std::size_t probe = line_index + 1; probe < function_end; ++probe) {
                        if (!trim_line(lines[probe]).empty()) {
                            return probe;
                        }
                    }
                    return function_end;
                }();
            if (next_nonempty >= function_end) {
                continue;
            }

            std::string rewritten_header = lines[next_nonempty];
            bool header_changed = false;
            for (const std::string& iterator_name :
                 {std::string("ipairs"), std::string("pairs"), std::string("next")}) {
                const std::string needle = iterator_name + "(" + normalized_lhs + ")";
                const std::size_t found = rewritten_header.find(needle);
                if (found == std::string::npos) {
                    continue;
                }
                rewritten_header.replace(found,
                                         needle.size(),
                                         chosen_pattern->call_expression);
                header_changed = true;
                break;
            }
            if (!header_changed) {
                continue;
            }

            std::vector<std::string> candidate_lines = lines;
            candidate_lines[next_nonempty] = std::move(rewritten_header);

            bool used_later = false;
            for (std::size_t probe = line_index + 2; probe < function_end; ++probe) {
                if (CountIdentifierUses(candidate_lines[probe], normalized_lhs) != 0) {
                    used_later = true;
                    break;
                }
            }
            if (!used_later) {
                candidate_lines.erase(candidate_lines.begin() +
                                      static_cast<std::ptrdiff_t>(line_index));
            }

            NormalizeRenderedLuaWhitespace(&candidate_lines);
            std::string candidate_source = JoinRenderedLines(candidate_lines);
            if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
                continue;
            }

            lines = std::move(candidate_lines);
            changed = true;
            break;
        }
    }

    if (!changed) {
        return false;
    }

    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_iterator_argument_locals_restored");
    return true;
}

bool TryRestoreBytecodeTestedCallResultLocalSeeds(const Lua51Function& function,
                                                  std::string_view chunk_name,
                                                  std::string* source,
                                                  std::string* note,
                                                  std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedCallResultPattern> patterns =
            CollectLua51RenderedCallResultPatterns(function.child_functions[block_index],
                                                   parameter_names);
        if (patterns.empty()) {
            continue;
        }

        bool has_tested_call_pattern = false;
        for (const RenderedCallResultPattern& pattern : patterns) {
            has_tested_call_pattern |= pattern.tested_immediately && !pattern.expression.empty();
        }
        if (!has_tested_call_pattern) {
            continue;
        }

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index], &is_local, &lhs, &rhs) ||
                is_local || !IsValidLuaIdentifier(lhs) || IsLuaKeyword(lhs)) {
                continue;
            }

            if (!LooksLikeRenderedCallExpression(rhs)) {
                continue;
            }

            std::size_t next_nonempty = line_index + 1;
            while (next_nonempty < function_end &&
                   TrimLuaWhitespace(StripLuaLineComment(lines[next_nonempty])).empty()) {
                ++next_nonempty;
            }
            if (next_nonempty >= function_end) {
                continue;
            }

            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[next_nonempty], &prefix, &condition, &suffix) ||
                suffix != " then") {
                continue;
            }
            const std::string normalized_condition =
                NormalizeRenderedTrivialParenthesizedLiterals(condition);
            if (condition != lhs && normalized_condition != lhs &&
                condition != ("not " + lhs) && normalized_condition != ("not " + lhs)) {
                continue;
            }

            bool used_later = false;
            for (std::size_t probe = next_nonempty + 1; probe < function_end; ++probe) {
                if (CountIdentifierUses(lines[probe], lhs) != 0) {
                    used_later = true;
                    break;
                }
            }
            if (!used_later) {
                continue;
            }

            const std::size_t indent_size = lines[line_index].find_first_not_of(" \t");
            const std::string indent =
                indent_size == std::string::npos ? std::string()
                                                 : lines[line_index].substr(0, indent_size);
            lines[line_index] =
                indent + "local " + lhs + " = " +
                NormalizeRenderedTrivialParenthesizedLiterals(rhs);
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_tested_call_local_seeds_restored");
    return true;
}

bool TryRestoreBytecodeSeededLocalCallExpressions(const Lua51Function& function,
                                                  std::string_view chunk_name,
                                                  std::string* source,
                                                  std::string* note,
                                                  std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto join_arguments = [](const std::vector<std::string>& arguments) {
        std::ostringstream rebuilt;
        for (std::size_t argument_index = 0; argument_index < arguments.size(); ++argument_index) {
            if (argument_index != 0) {
                rebuilt << ", ";
            }
            rebuilt << TrimLuaWhitespace(arguments[argument_index]);
        }
        return rebuilt.str();
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const Lua51Function& child_function = function.child_functions[block_index];
        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedCallResultPattern> patterns =
            CollectLua51RenderedCallResultPatterns(child_function, parameter_names);
        if (patterns.empty()) {
            continue;
        }

        std::unordered_map<std::string, std::vector<std::string>> expressions_by_local;
        for (const RenderedCallResultPattern& pattern : patterns) {
            if (pattern.expression.empty()) {
                continue;
            }

            const std::size_t local_probe_instruction =
                std::min(pattern.instruction_index + 1,
                         child_function.code.empty() ? pattern.instruction_index
                                                     : child_function.code.size() - 1);
            const std::optional<std::string> local_name =
                TryGetActiveLua51LocalRegisterName(child_function,
                                                   local_probe_instruction,
                                                   pattern.target_reg);
            if (!local_name.has_value() || local_name->empty()) {
                continue;
            }

            std::vector<std::string>& expressions = expressions_by_local[*local_name];
            if (std::find(expressions.begin(), expressions.end(), pattern.expression) ==
                expressions.end()) {
                expressions.push_back(pattern.expression);
            }
        }

        for (std::size_t line_index = block.begin + 1; line_index + 1 < function_end;
             ++line_index) {
            bool seed_is_local = false;
            std::string seed_lhs;
            std::string seed_rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
                !IsValidLuaIdentifier(seed_lhs)) {
                continue;
            }

            const auto local_it = expressions_by_local.find(seed_lhs);
            if (local_it == expressions_by_local.end() || local_it->second.empty()) {
                continue;
            }

            std::size_t next_line = line_index + 1;
            while (next_line < function_end && trim_line(lines[next_line]).empty()) {
                ++next_line;
            }
            if (next_line >= function_end) {
                continue;
            }

            bool call_is_local = false;
            std::string call_lhs;
            std::string call_rhs;
            if (!ParseSingleRenderedAssignment(lines[next_line],
                                               &call_is_local,
                                               &call_lhs,
                                               &call_rhs) ||
                !call_is_local || call_lhs != seed_lhs) {
                continue;
            }

            std::string call_callee;
            std::vector<std::string> call_arguments;
            if (!ParseRenderedSingleCallExpression(call_rhs, &call_callee, &call_arguments) ||
                NormalizeRenderedTrivialParenthesizedLiterals(call_callee) != seed_lhs) {
                continue;
            }

            const std::string reconstructed_expression =
                NormalizeRenderedTrivialParenthesizedLiterals(seed_rhs) + "(" +
                join_arguments(call_arguments) + ")";
            const std::string normalized_reconstructed =
                NormalizeRenderedTrivialParenthesizedLiterals(reconstructed_expression);

            const std::string* matched_expression = nullptr;
            for (const std::string& candidate_expression : local_it->second) {
                if (NormalizeRenderedTrivialParenthesizedLiterals(candidate_expression) ==
                    normalized_reconstructed) {
                    matched_expression = &candidate_expression;
                    break;
                }
            }
            if (matched_expression == nullptr) {
                continue;
            }

            const std::string indent =
                std::string(lines[next_line].substr(0, lines[next_line].find_first_not_of(" \t")));
            lines[next_line] = indent + "local " + seed_lhs + " = " + *matched_expression;
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            if (line_index != 0) {
                --line_index;
            }
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_seeded_local_calls_restored");
    return true;
}

bool TryRestoreBytecodeTestedCallLocalGuards(const Lua51Function& function,
                                             std::string_view chunk_name,
                                             std::string* source,
                                             std::string* note,
                                             std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const Lua51Function& child_function = function.child_functions[block_index];
        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedCallResultPattern> patterns =
            CollectLua51RenderedCallResultPatterns(child_function, parameter_names);
        if (patterns.empty()) {
            continue;
        }

        struct TestedPattern {
            std::string local_name;
            std::string expression;
            std::string normalized_expression;
            std::string normalized_callee;
        };

        std::vector<TestedPattern> tested_patterns;
        for (const RenderedCallResultPattern& pattern : patterns) {
            if (!pattern.tested_immediately || pattern.expression.empty()) {
                continue;
            }

            const std::size_t local_probe_instruction =
                std::min(pattern.instruction_index + 1,
                         child_function.code.empty() ? pattern.instruction_index
                                                     : child_function.code.size() - 1);
            const std::optional<std::string> local_name =
                TryGetActiveLua51LocalRegisterName(child_function,
                                                   local_probe_instruction,
                                                   pattern.target_reg);
            if (!local_name.has_value() || local_name->empty()) {
                continue;
            }

            std::string normalized_callee;
            if (const std::optional<std::string> extracted_callee =
                    ExtractRenderedCallCallee(pattern.expression);
                extracted_callee.has_value()) {
                normalized_callee =
                    NormalizeRenderedTrivialParenthesizedLiterals(*extracted_callee);
            }

            tested_patterns.push_back(TestedPattern{
                .local_name = *local_name,
                .expression = pattern.expression,
                .normalized_expression =
                    NormalizeRenderedTrivialParenthesizedLiterals(pattern.expression),
                .normalized_callee = std::move(normalized_callee),
            });
        }
        if (tested_patterns.empty()) {
            continue;
        }

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            std::string local_name;
            std::string expression;
            if (!ParseRenderedLocalAssignment(lines[line_index], &local_name, &expression)) {
                continue;
            }

            const std::string normalized_expression =
                NormalizeRenderedTrivialParenthesizedLiterals(expression);
            const TestedPattern* matched_pattern = nullptr;
            for (const TestedPattern& candidate : tested_patterns) {
                if (candidate.local_name == local_name &&
                    candidate.normalized_expression == normalized_expression) {
                    matched_pattern = &candidate;
                    break;
                }
            }
            if (matched_pattern == nullptr) {
                continue;
            }

            std::size_t next_line = line_index + 1;
            while (next_line < function_end && trim_line(lines[next_line]).empty()) {
                ++next_line;
            }

            if (next_line < function_end) {
                bool duplicate_is_local = false;
                std::string duplicate_lhs;
                std::string duplicate_rhs;
                std::string duplicate_callee;
                std::vector<std::string> duplicate_arguments;
                if (ParseSingleRenderedAssignment(lines[next_line],
                                                 &duplicate_is_local,
                                                 &duplicate_lhs,
                                                 &duplicate_rhs) &&
                    duplicate_lhs == matched_pattern->local_name &&
                    ParseRenderedSingleCallExpression(duplicate_rhs,
                                                      &duplicate_callee,
                                                      &duplicate_arguments) &&
                    NormalizeRenderedTrivialParenthesizedLiterals(duplicate_callee) ==
                        matched_pattern->local_name) {
                    lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(next_line));
                    changed = true;
                }
            }

            next_line = line_index + 1;
            while (next_line < function_end && trim_line(lines[next_line]).empty()) {
                ++next_line;
            }
            if (next_line >= function_end) {
                continue;
            }

            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[next_line], &prefix, &condition, &suffix)) {
                continue;
            }

            std::string normalized_condition =
                NormalizeRenderedTrivialParenthesizedLiterals(condition);
            bool negate = false;
            if (normalized_condition.starts_with("not ")) {
                negate = true;
                normalized_condition = NormalizeRenderedTrivialParenthesizedLiterals(
                    normalized_condition.substr(4));
            }

            const bool matches_expression =
                normalized_condition == matched_pattern->normalized_expression;
            const bool matches_callee =
                !matched_pattern->normalized_callee.empty() &&
                normalized_condition == matched_pattern->normalized_callee;
            if (!matches_expression && !matches_callee) {
                continue;
            }

            lines[next_line] = prefix + (negate ? "not " : "") + matched_pattern->local_name +
                               suffix;
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_tested_call_local_guards_restored");
    return true;
}

bool TryRestoreBytecodeImmediateTempExpressionReuses(const Lua51Function& function,
                                                     std::string_view chunk_name,
                                                     std::string* source,
                                                     std::string* note,
                                                     std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedCallResultPattern> patterns =
            CollectLua51RenderedCallResultPatterns(function.child_functions[block_index],
                                                   parameter_names);
        if (patterns.empty()) {
            continue;
        }

        std::unordered_set<std::string> bytecode_call_expressions;
        bytecode_call_expressions.reserve(patterns.size());
        for (const RenderedCallResultPattern& pattern : patterns) {
            if (!pattern.expression.empty()) {
                bytecode_call_expressions.insert(pattern.expression);
                bytecode_call_expressions.insert(
                    NormalizeRenderedTrivialParenthesizedLiterals(pattern.expression));
            }
        }
        if (bytecode_call_expressions.empty()) {
            continue;
        }

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            std::string local_name;
            std::string expression;
            if (!ParseRenderedLocalAssignment(lines[line_index], &local_name, &expression)) {
                continue;
            }
            const std::string normalized_expression =
                NormalizeRenderedTrivialParenthesizedLiterals(expression);
            if (!bytecode_call_expressions.contains(expression) &&
                !bytecode_call_expressions.contains(normalized_expression)) {
                continue;
            }

            bool line_rewritten = false;
            std::size_t nonempty_seen = 0;
            for (std::size_t probe = line_index + 1;
                 probe < function_end && nonempty_seen < 4;
                 ++probe) {
                if (trim_line(lines[probe]).empty()) {
                    continue;
                }
                ++nonempty_seen;

                if (CountIdentifierUses(lines[probe], local_name) != 0) {
                    break;
                }

                std::string rewritten = lines[probe];
                bool replaced = ReplaceAllRenderedSubstrings(&rewritten, expression, local_name);
                if (!replaced && normalized_expression != expression) {
                    replaced = ReplaceAllRenderedSubstrings(&rewritten,
                                                            normalized_expression,
                                                            local_name);
                }
                if (!replaced || rewritten == lines[probe]) {
                    continue;
                }

                lines[probe] = std::move(rewritten);
                changed = true;
                line_rewritten = true;
                break;
            }
            if (!line_rewritten) {
                continue;
            }
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_immediate_temp_reuses_restored");
    return true;
}

bool TryRestoreBytecodeCallOperandCompareOrder(const Lua51Function& function,
                                               std::string_view chunk_name,
                                               std::string* source,
                                               std::string* note,
                                               std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedComparisonSwapPattern> patterns =
            CollectRenderedLuaCallOperandCompareSwapPatterns(function.child_functions[block_index],
                                                             parameter_names);
        if (patterns.empty()) {
            continue;
        }

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[line_index], &prefix, &condition, &suffix)) {
                continue;
            }

            const std::string normalized_condition =
                NormalizeRenderedTrivialParenthesizedLiterals(std::string(
                    TrimLuaWhitespace(condition)));
            std::string condition_lhs;
            std::string condition_comparator;
            std::string condition_rhs;
            const bool parsed_condition = TryParseRenderedComparatorExpression(condition,
                                                                              &condition_lhs,
                                                                              &condition_comparator,
                                                                              &condition_rhs);
            const std::string normalized_condition_lhs =
                parsed_condition ? NormalizeRenderedComparatorOperand(condition_lhs) : std::string();
            const std::string normalized_condition_rhs =
                parsed_condition ? NormalizeRenderedComparatorOperand(condition_rhs) : std::string();
            for (const RenderedComparisonSwapPattern& pattern : patterns) {
                const bool exact_expression_match =
                    NormalizeRenderedTrivialParenthesizedLiterals(pattern.expression) ==
                    normalized_condition;
                const bool parsed_operand_match =
                    parsed_condition &&
                    pattern.comparator == condition_comparator &&
                    pattern.normalized_lhs == normalized_condition_lhs &&
                    pattern.normalized_rhs == normalized_condition_rhs;
                if (!exact_expression_match && !parsed_operand_match) {
                    continue;
                }
                lines[line_index] = prefix + pattern.swapped_expression + suffix;
                changed = true;
                break;
            }
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_call_operand_compare_order_restored");
    return true;
}

bool TryRestoreBytecodeCompareConditionOrder(const Lua51Function& function,
                                             std::string_view chunk_name,
                                             std::string* source,
                                             std::string* note,
                                             std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectRenderedFunctionBlocksPreorder(lines);
    if (blocks.empty()) {
        return false;
    }

    std::vector<const Lua51Function*> child_functions;
    AppendLuaFunctionChildrenPreorder(function, &child_functions);
    const std::size_t block_count = std::min(blocks.size(), child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedComparisonSwapPattern> patterns =
            CollectRenderedLuaNonCallCompareSwapPatterns(*child_functions[block_index],
                                                         parameter_names);
        if (patterns.empty()) {
            continue;
        }

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[line_index], &prefix, &condition, &suffix)) {
                continue;
            }

            const std::string normalized_condition =
                NormalizeRenderedTrivialParenthesizedLiterals(
                    std::string(TrimLuaWhitespace(condition)));
            std::string condition_lhs;
            std::string condition_comparator;
            std::string condition_rhs;
            const bool parsed_condition = TryParseRenderedComparatorExpression(condition,
                                                                              &condition_lhs,
                                                                              &condition_comparator,
                                                                              &condition_rhs);
            const std::string normalized_condition_lhs =
                parsed_condition ? NormalizeRenderedComparatorOperand(condition_lhs) : std::string();
            const std::string normalized_condition_rhs =
                parsed_condition ? NormalizeRenderedComparatorOperand(condition_rhs) : std::string();
            for (const RenderedComparisonSwapPattern& pattern : patterns) {
                const bool expression_match =
                    pattern.expression == normalized_condition;
                const bool operand_match =
                    parsed_condition &&
                    pattern.comparator == condition_comparator &&
                    pattern.normalized_lhs == normalized_condition_lhs &&
                    pattern.normalized_rhs == normalized_condition_rhs;
                if (!expression_match && !operand_match) {
                    continue;
                }
                lines[line_index] = prefix + pattern.swapped_expression + suffix;
                changed = true;
                break;
            }
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_compare_condition_order_restored");
    return true;
}

bool TryRestoreBytecodeSiblingElseIfBlocks(const Lua51Function& function,
                                           std::string_view chunk_name,
                                           std::string* source,
                                           std::string* note,
                                           std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_line = [&](const std::vector<std::string>& rendered_lines,
                                        std::size_t start,
                                        std::size_t end_exclusive) {
        for (std::size_t probe = start;
             probe < rendered_lines.size() && probe < end_exclusive;
             ++probe) {
            if (!trim_line(rendered_lines[probe]).empty()) {
                return probe;
            }
        }
        return end_exclusive;
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedSiblingElseIfPattern> patterns =
            CollectRenderedLuaSiblingElseIfPatterns(function.child_functions[block_index],
                                                    parameter_names);
        if (patterns.empty()) {
            continue;
        }

        bool block_changed = false;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            std::string first_prefix;
            std::string first_condition;
            std::string first_suffix;
            if (!TryParseRenderedConditionHeader(lines[line_index],
                                                &first_prefix,
                                                &first_condition,
                                                &first_suffix) ||
                first_suffix != " then" ||
                (first_prefix.find("if ") == std::string::npos &&
                 first_prefix.find("elseif ") == std::string::npos)) {
                continue;
            }

            std::size_t first_end = function_end + 1;
            bool first_has_top_level_else = false;
            int depth = 0;
            for (std::size_t probe = line_index; probe <= function_end; ++probe) {
                const std::string_view trimmed = trim_line(lines[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (probe > line_index && depth == 1 &&
                    (trimmed == "else" || trimmed.starts_with("elseif "))) {
                    first_has_top_level_else = true;
                    break;
                }
                depth += GetRenderedLuaBlockDelta(trimmed);
                if (probe > line_index && depth == 0) {
                    first_end = probe;
                    break;
                }
            }
            if (first_has_top_level_else || first_end > function_end) {
                continue;
            }

            const std::size_t second_header =
                next_nonempty_line(lines, first_end + 1, function_end + 1);
            if (second_header > function_end ||
                line_indent(lines[second_header]) != line_indent(lines[line_index])) {
                continue;
            }

            std::string second_prefix;
            std::string second_condition;
            std::string second_suffix;
            if (!TryParseRenderedConditionHeader(lines[second_header],
                                                &second_prefix,
                                                &second_condition,
                                                &second_suffix) ||
                second_suffix != " then" ||
                second_prefix.find("if ") == std::string::npos) {
                continue;
            }

            std::size_t second_end = function_end + 1;
            bool second_has_top_level_else = false;
            depth = 0;
            for (std::size_t probe = second_header; probe <= function_end; ++probe) {
                const std::string_view trimmed = trim_line(lines[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (probe > second_header && depth == 1 &&
                    (trimmed == "else" || trimmed.starts_with("elseif "))) {
                    second_has_top_level_else = true;
                    break;
                }
                depth += GetRenderedLuaBlockDelta(trimmed);
                if (probe > second_header && depth == 0) {
                    second_end = probe;
                    break;
                }
            }
            if (second_has_top_level_else || second_end > function_end) {
                continue;
            }

            const std::string normalized_first =
                NormalizeRenderedTrivialParenthesizedLiterals(first_condition);
            const std::string normalized_second =
                NormalizeRenderedTrivialParenthesizedLiterals(second_condition);
            const auto is_state_machine_condition = [](std::string_view condition) {
                return condition.find("CurrentState") != std::string_view::npos ||
                       condition.find(".State") != std::string_view::npos ||
                       condition.find("QuestState") != std::string_view::npos ||
                       condition.find("QuestStage") != std::string_view::npos ||
                       condition.find(".Stage.") != std::string_view::npos;
            };
            const bool state_machine_condition_pair =
                is_state_machine_condition(normalized_first) &&
                is_state_machine_condition(normalized_second);
            if (state_machine_condition_pair) {
                continue;
            }
            const bool matched_pattern = std::any_of(
                patterns.begin(),
                patterns.end(),
                [&](const RenderedSiblingElseIfPattern& pattern) {
                    return NormalizeRenderedTrivialParenthesizedLiterals(
                               pattern.first_condition) == normalized_first &&
                           NormalizeRenderedTrivialParenthesizedLiterals(
                               pattern.second_condition) == normalized_second;
                });
            if (!matched_pattern) {
                continue;
            }

            const std::string indent = line_indent(lines[line_index]);
            lines[first_end] = indent + "elseif " + second_condition + " then";
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(second_header));
            changed = true;
            block_changed = true;
            break;
        }

        if (block_changed) {
            break;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_sibling_elseif_blocks_restored");
    return true;
}

bool TryInlineSyntheticSeededCallArgumentLocals(std::string_view chunk_name,
                                                std::string* source,
                                                std::string* note,
                                                std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    bool changed = false;

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseSingleRenderedAssignment(lines[line_index], &is_local, &lhs, &rhs) ||
            !is_local || !lhs.starts_with("refii_seeded_call_arg_")) {
            continue;
        }

        std::size_t next_index = line_index + 1;
        while (next_index < lines.size() &&
               TrimLuaWhitespace(StripLuaLineComment(lines[next_index])).empty()) {
            ++next_index;
        }
        if (next_index >= lines.size()) {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallExpression(lines[next_index], &callee, &arguments) ||
            arguments.empty() ||
            TrimLuaWhitespace(arguments.front()) != lhs) {
            continue;
        }

        bool used_elsewhere = false;
        for (std::size_t probe = next_index + 1; probe < lines.size(); ++probe) {
            if (CountIdentifierUses(lines[probe], lhs) != 0) {
                used_elsewhere = true;
                break;
            }
        }
        if (used_elsewhere) {
            continue;
        }

        arguments.front() = std::string(TrimLuaWhitespace(rhs));
        const std::size_t indent_size = lines[line_index].find_first_not_of(" \t");
        const std::string indent =
            indent_size == std::string::npos ? std::string()
                                             : lines[line_index].substr(0, indent_size);
        std::ostringstream rebuilt_call;
        rebuilt_call << indent;
        rebuilt_call << callee << "(";
        for (std::size_t arg_index = 0; arg_index < arguments.size(); ++arg_index) {
            if (arg_index != 0) {
                rebuilt_call << ", ";
            }
            rebuilt_call << arguments[arg_index];
        }
        rebuilt_call << ")";
        lines[next_index] = rebuilt_call.str();
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "synthetic_seeded_call_args_inlined");
    return true;
}

bool TryRestoreBytecodeLocalCallCarrySeeds(const Lua51Function& function,
                                           std::string_view chunk_name,
                                           std::string* source,
                                           std::string* note,
                                           std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent_size = [](std::string_view line) -> std::size_t {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        return first_non_space == std::string_view::npos ? 0 : first_non_space;
    };
    const auto has_sibling_branch_boundary = [&](const std::vector<std::string>& rendered_lines,
                                                 std::size_t begin_index,
                                                 std::size_t end_index,
                                                 std::size_t current_indent) {
        for (std::size_t probe = begin_index; probe < end_index && probe < rendered_lines.size();
             ++probe) {
            const std::string_view trimmed = trim_line(rendered_lines[probe]);
            if ((trimmed == "else" || trimmed.starts_with("elseif ")) &&
                line_indent_size(rendered_lines[probe]) <= current_indent) {
                return true;
            }
        }
        return false;
    };
    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const Lua51Function& child_function = function.child_functions[block_index];
        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);

        const std::unordered_set<std::string> bytecode_local_names =
            CollectRecoverableLua51LocalNames(child_function, parameter_names);
        if (bytecode_local_names.empty()) {
            continue;
        }

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            bool is_local = false;
            std::string lhs;
            std::string rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index], &is_local, &lhs, &rhs) ||
                is_local || !bytecode_local_names.contains(lhs) ||
                !LooksLikeRenderedCallExpression(rhs)) {
                continue;
            }

            const std::string normalized_rhs =
                NormalizeRenderedTrivialParenthesizedLiterals(rhs);

            bool used_soon = false;
            bool reassigned_before_use = false;
            const std::size_t window_end = std::min(function_end, line_index + 6);
            for (std::size_t probe = line_index + 1; probe < window_end; ++probe) {
                if (trim_line(lines[probe]).empty()) {
                    continue;
                }

                if (CountIdentifierUses(lines[probe], lhs) != 0) {
                    used_soon = true;
                    break;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseSingleRenderedAssignment(lines[probe],
                                                 &probe_is_local,
                                                 &probe_lhs,
                                                 &probe_rhs) &&
                    probe_lhs == lhs) {
                    reassigned_before_use = true;
                    break;
                }
            }
            if (!used_soon || reassigned_before_use) {
                continue;
            }

            bool already_declared = false;
            const std::size_t current_indent = line_index < lines.size()
                                                   ? line_indent_size(lines[line_index])
                                                   : 0;
            for (std::size_t probe = block.begin + 1; probe < line_index; ++probe) {
                std::string existing_local_name;
                std::string existing_local_expression;
                if (!ParseRenderedLocalAssignment(lines[probe],
                                                  &existing_local_name,
                                                  &existing_local_expression)) {
                    continue;
                }
                if (existing_local_name == lhs) {
                    if (has_sibling_branch_boundary(lines,
                                                    probe + 1,
                                                    line_index,
                                                    current_indent)) {
                        continue;
                    }
                    already_declared = true;
                    break;
                }
            }
            if (already_declared) {
                continue;
            }

            const std::size_t indent_size = lines[line_index].find_first_not_of(" \t");
            const std::string indent =
                indent_size == std::string::npos ? std::string()
                                                 : lines[line_index].substr(0, indent_size);
            lines[line_index] = indent + "local " + lhs + " = " + normalized_rhs;
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_local_call_carries_restored");
    return true;
}

bool TryRestoreBytecodeLocalMultiResultCallSeeds(const Lua51Function& function,
                                                 std::string_view chunk_name,
                                                 std::string* source,
                                                 std::string* note,
                                                 std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent_size = [](std::string_view line) -> std::size_t {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        return first_non_space == std::string_view::npos ? 0 : first_non_space;
    };
    const auto has_sibling_branch_boundary = [&](const std::vector<std::string>& rendered_lines,
                                                 std::size_t begin_index,
                                                 std::size_t end_index,
                                                 std::size_t current_indent) {
        for (std::size_t probe = begin_index; probe < end_index && probe < rendered_lines.size();
             ++probe) {
            const std::string_view trimmed = trim_line(rendered_lines[probe]);
            if ((trimmed == "else" || trimmed.starts_with("elseif ")) &&
                line_indent_size(rendered_lines[probe]) <= current_indent) {
                return true;
            }
        }
        return false;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const Lua51Function& child_function = function.child_functions[block_index];
        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);

        const std::unordered_set<std::string> bytecode_local_names =
            CollectRecoverableLua51LocalNames(child_function, parameter_names);
        if (bytecode_local_names.empty()) {
            continue;
        }

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            bool decl_is_local = false;
            std::vector<std::string> decl_names;
            std::string decl_rhs;
            if (!ParseRenderedAssignmentLine(lines[line_index],
                                             &decl_is_local,
                                             &decl_names,
                                             &decl_rhs) ||
                decl_is_local || decl_names.size() < 2 ||
                !LooksLikeRenderedCallExpression(decl_rhs)) {
                continue;
            }

            bool all_names_valid = true;
            for (const std::string& decl_name : decl_names) {
                if (!IsValidLuaIdentifier(decl_name) || IsLuaKeyword(decl_name) ||
                    !bytecode_local_names.contains(decl_name)) {
                    all_names_valid = false;
                    break;
                }
            }
            if (!all_names_valid) {
                continue;
            }

            bool used_soon = false;
            bool reassigned_before_use = false;
            const std::size_t window_end = std::min(function_end, line_index + 6);
            for (std::size_t probe = line_index + 1; probe < window_end; ++probe) {
                if (trim_line(lines[probe]).empty()) {
                    continue;
                }

                for (const std::string& decl_name : decl_names) {
                    if (CountIdentifierUses(lines[probe], decl_name) != 0) {
                        used_soon = true;
                        break;
                    }
                }
                if (used_soon) {
                    break;
                }

                bool probe_is_local = false;
                std::vector<std::string> probe_names;
                std::string probe_rhs;
                if (ParseRenderedAssignmentLine(lines[probe],
                                               &probe_is_local,
                                               &probe_names,
                                               &probe_rhs)) {
                    for (const std::string& decl_name : decl_names) {
                        if (std::find(probe_names.begin(), probe_names.end(), decl_name) !=
                            probe_names.end()) {
                            reassigned_before_use = true;
                            break;
                        }
                    }
                }
                if (reassigned_before_use) {
                    break;
                }
            }
            if (!used_soon || reassigned_before_use) {
                continue;
            }

            const std::size_t current_indent = line_indent_size(lines[line_index]);
            bool already_declared = false;
            for (const std::string& decl_name : decl_names) {
                for (std::size_t probe = block.begin + 1; probe < line_index; ++probe) {
                    bool existing_is_local = false;
                    std::vector<std::string> existing_names;
                    std::string existing_rhs;
                    if (!ParseRenderedAssignmentLine(lines[probe],
                                                     &existing_is_local,
                                                     &existing_names,
                                                     &existing_rhs) ||
                        !existing_is_local) {
                        continue;
                    }
                    if (std::find(existing_names.begin(), existing_names.end(), decl_name) ==
                        existing_names.end()) {
                        continue;
                    }
                    if (has_sibling_branch_boundary(lines,
                                                    probe + 1,
                                                    line_index,
                                                    current_indent)) {
                        continue;
                    }
                    already_declared = true;
                    break;
                }
                if (already_declared) {
                    break;
                }
            }
            if (already_declared) {
                continue;
            }

            const std::size_t indent_size = lines[line_index].find_first_not_of(" \t");
            const std::string indent =
                indent_size == std::string::npos ? std::string()
                                                 : lines[line_index].substr(0, indent_size);
            std::ostringstream rewritten;
            rewritten << indent << "local ";
            for (std::size_t name_index = 0; name_index < decl_names.size(); ++name_index) {
                if (name_index != 0) {
                    rewritten << ", ";
                }
                rewritten << decl_names[name_index];
            }
            rewritten << " = "
                      << NormalizeRenderedTrivialParenthesizedLiterals(decl_rhs);
            lines[line_index] = rewritten.str();
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_local_multi_call_seeds_restored");
    return true;
}

bool TryInlineSyntheticLocalCallTempSeeds(const Lua51Function& function,
                                          std::string_view chunk_name,
                                          std::string* source,
                                          std::string* note,
                                          std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const Lua51Function& child_function = function.child_functions[block_index];
        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::unordered_set<std::string> bytecode_local_names =
            CollectRecoverableLua51LocalNames(child_function, parameter_names);

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            std::string local_name;
            std::string expression;
            if (!ParseRenderedLocalAssignment(lines[line_index], &local_name, &expression) ||
                !LooksLikeRenderedCallExpression(expression) ||
                bytecode_local_names.contains(local_name) ||
                !ParseRenderedTemporaryRegisterIndex(local_name).has_value()) {
                continue;
            }

            bool replaced_any_use = false;
            bool saw_reassignment = false;
            for (std::size_t probe = line_index + 1; probe < function_end; ++probe) {
                if (trim_line(lines[probe]).empty()) {
                    continue;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseSingleRenderedAssignment(lines[probe],
                                                 &probe_is_local,
                                                 &probe_lhs,
                                                 &probe_rhs) &&
                    probe_lhs == local_name) {
                    saw_reassignment = true;
                    break;
                }

                if (CountIdentifierUses(lines[probe], local_name) == 0) {
                    continue;
                }

                std::string rewritten;
                if (!ReplaceRenderedIdentifierUsesLocal(lines[probe],
                                                       local_name,
                                                       expression,
                                                       &rewritten)) {
                    continue;
                }
                lines[probe] = std::move(rewritten);
                replaced_any_use = true;
            }

            if (!replaced_any_use || saw_reassignment) {
                continue;
            }

            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            --line_index;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_synthetic_call_temps_inlined");
    return true;
}

bool TryRestoreBytecodeLocalTableBuilderSeeds(const Lua51Function& function,
                                              std::string_view chunk_name,
                                              std::string* source,
                                              std::string* note,
                                              std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    struct TableBuilderPattern {
        int target_reg = -1;
        std::string temp_name;
        std::string local_name;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    bool changed = false;
    bool pass_changed = false;
    bool guard_triggered = false;
    std::size_t pass_count = 0;
    constexpr std::size_t kMaxRewritePasses = 128;
    std::unordered_set<std::size_t> seen_pass_states;
    seen_pass_states.reserve(kMaxRewritePasses);
    do {
        if (pass_count >= kMaxRewritePasses ||
            !seen_pass_states.insert(HashRenderedLines(lines)).second) {
            guard_triggered = true;
            break;
        }
        ++pass_count;

        pass_changed = false;
        const std::vector<RenderedFunctionBlock> blocks =
            CollectTopLevelRenderedFunctionBlocks(lines);
        if (blocks.empty()) {
            break;
        }
        const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
        for (std::size_t block_index = 0; block_index < block_count && !pass_changed;
             ++block_index) {
            const RenderedFunctionBlock& block = blocks[block_index];
            if (block.begin >= lines.size()) {
                continue;
            }

            const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
            if (function_end >= lines.size() || function_end <= block.begin) {
                continue;
            }

            const Lua51Function& child_function = function.child_functions[block_index];
            const std::vector<Lua51Instruction> instructions =
                DecodeLua51Instructions(child_function);
            if (instructions.size() < 3) {
                continue;
            }

            const std::vector<std::string> parameter_names =
                ParseRenderedFunctionParameterNames(lines[block.begin]);
            std::vector<TableBuilderPattern> patterns;
            for (std::size_t instruction_index = 1; instruction_index < instructions.size();
                 ++instruction_index) {
                const Lua51Instruction& instruction = instructions[instruction_index];
                if (instruction.opcode != Lua51Opcode::kCall || instruction.b != 2 ||
                    instruction.a <= 0 || instruction_index == 0) {
                    continue;
                }

                const Lua51Instruction& previous = instructions[instruction_index - 1];
                if (previous.opcode != Lua51Opcode::kMove ||
                    previous.a != instruction.a + 1 ||
                    previous.b < 0) {
                    continue;
                }

                bool saw_newtable = false;
                bool saw_table_mutation = false;
                bool saw_reassignment = false;
                for (std::size_t probe = instruction_index; probe-- > 0;) {
                    const Lua51Instruction& earlier = instructions[probe];
                    if (earlier.a == previous.b &&
                        earlier.opcode != Lua51Opcode::kSetTable &&
                        earlier.opcode != Lua51Opcode::kSetList &&
                        earlier.opcode != Lua51Opcode::kTest &&
                        earlier.opcode != Lua51Opcode::kTestSet &&
                        earlier.opcode != Lua51Opcode::kJmp &&
                        earlier.opcode != Lua51Opcode::kReturn) {
                        if (earlier.opcode == Lua51Opcode::kNewTable) {
                            saw_newtable = true;
                            break;
                        }
                        saw_reassignment = true;
                        break;
                    }

                    if ((earlier.opcode == Lua51Opcode::kSetTable ||
                         earlier.opcode == Lua51Opcode::kSetList) &&
                        earlier.a == previous.b) {
                        saw_table_mutation = true;
                    }
                }
                if (!saw_newtable || !saw_table_mutation || saw_reassignment) {
                    continue;
                }

                const std::optional<std::string> local_name =
                    TryGetActiveLua51LocalRegisterName(child_function,
                                                       instruction_index - 1,
                                                       previous.b);
                if (!local_name.has_value() || local_name->empty() ||
                    !IsValidLuaIdentifier(*local_name) || IsLuaKeyword(*local_name) ||
                    std::find(parameter_names.begin(), parameter_names.end(), *local_name) !=
                        parameter_names.end()) {
                    continue;
                }

                const std::string temp_name = "r" + std::to_string(previous.b);
                if (*local_name == temp_name) {
                    continue;
                }

                const bool duplicate_pattern = std::any_of(
                    patterns.begin(),
                    patterns.end(),
                    [&](const TableBuilderPattern& existing) {
                        return existing.temp_name == temp_name ||
                               existing.local_name == *local_name;
                    });
                if (duplicate_pattern) {
                    continue;
                }

                patterns.push_back(TableBuilderPattern{
                    .target_reg = previous.b,
                    .temp_name = temp_name,
                    .local_name = *local_name,
                });
            }
            if (patterns.empty()) {
                continue;
            }

            for (const TableBuilderPattern& pattern : patterns) {
                std::size_t seed_line = lines.size();
                for (std::size_t line_index = block.begin + 1; line_index < function_end;
                     ++line_index) {
                    std::string local_name;
                    std::string expression;
                    if (!ParseRenderedLocalAssignment(lines[line_index],
                                                      &local_name,
                                                      &expression) ||
                        local_name != pattern.temp_name ||
                        TrimLuaWhitespace(StripLuaLineComment(expression)) != "{}") {
                        continue;
                    }
                    seed_line = line_index;
                    break;
                }
                if (seed_line >= lines.size()) {
                    continue;
                }

                bool local_name_already_bound = false;
                for (std::size_t probe = block.begin + 1; probe < seed_line; ++probe) {
                    std::string existing_local_name;
                    std::string existing_expression;
                    if (ParseRenderedLocalAssignment(lines[probe],
                                                     &existing_local_name,
                                                     &existing_expression) &&
                        existing_local_name == pattern.local_name) {
                        local_name_already_bound = true;
                        break;
                    }
                }
                if (local_name_already_bound) {
                    continue;
                }

                bool rewrite_failed = false;
                bool replaced_any_use = false;
                for (std::size_t probe = seed_line + 1; probe < function_end; ++probe) {
                    bool probe_is_local = false;
                    std::string probe_lhs;
                    std::string probe_rhs;
                    if (ParseSingleRenderedAssignment(lines[probe],
                                                     &probe_is_local,
                                                     &probe_lhs,
                                                     &probe_rhs) &&
                        probe_lhs == pattern.temp_name) {
                        break;
                    }

                    if (CountIdentifierUses(lines[probe], pattern.temp_name) == 0) {
                    } else {
                        std::string rewritten;
                        if (!ReplaceRenderedIdentifierUsesLocal(lines[probe],
                                                               pattern.temp_name,
                                                               pattern.local_name,
                                                               &rewritten)) {
                            rewrite_failed = true;
                            break;
                        }
                        lines[probe] = std::move(rewritten);
                        replaced_any_use = true;
                    }

                    const std::string_view trimmed = trim_line(lines[probe]);
                    if (trimmed.find("function(") == std::string_view::npos &&
                        trimmed.find("function ") == std::string_view::npos) {
                        continue;
                    }

                    const std::size_t nested_end = FindRenderedFunctionBlockEnd(lines, probe);
                    if (nested_end <= probe || nested_end > function_end) {
                        continue;
                    }

                    for (std::size_t nested_probe = probe + 1; nested_probe < nested_end;
                         ++nested_probe) {
                        if (CountIdentifierUses(lines[nested_probe], pattern.temp_name) != 0 ||
                            CountIdentifierUses(lines[nested_probe], pattern.local_name) != 0) {
                            rewrite_failed = true;
                            break;
                        }
                    }
                    if (rewrite_failed) {
                        break;
                    }
                    probe = nested_end;
                }
                if (rewrite_failed || !replaced_any_use) {
                    continue;
                }

                const std::size_t indent_size = lines[seed_line].find_first_not_of(" \t");
                const std::string indent =
                    indent_size == std::string::npos
                        ? std::string()
                        : lines[seed_line].substr(0, indent_size);
                lines[seed_line] = indent + "local " + pattern.local_name + " = {}";
                changed = true;
                pass_changed = true;
                break;
            }
        }
    } while (pass_changed);
    if (guard_triggered) {
        AppendCandidateNote(note, note_prefix, "local_table_builder_seed_loop_guarded");
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_local_table_builder_seeds_restored");
    return true;
}

bool TryInlineSyntheticCallResultUses(const Lua51Function& function,
                                      std::string_view chunk_name,
                                      std::string* source,
                                      std::string* note,
                                      std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const Lua51Function& child_function = function.child_functions[block_index];
        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<RenderedCallResultPattern> patterns =
            CollectLua51RenderedCallResultPatterns(child_function, parameter_names);
        if (patterns.empty()) {
            continue;
        }

        std::unordered_map<int, std::vector<std::string>> expressions_by_reg;
        for (const RenderedCallResultPattern& pattern : patterns) {
            if (pattern.expression.empty()) {
                continue;
            }

            const std::size_t local_probe_instruction =
                std::min(pattern.instruction_index + 1,
                         child_function.code.empty() ? pattern.instruction_index
                                                     : child_function.code.size() - 1);
            if (TryGetActiveLua51LocalRegisterName(child_function,
                                                   local_probe_instruction,
                                                   pattern.target_reg)
                    .has_value()) {
                continue;
            }

            const std::string normalized_expression =
                NormalizeRenderedTrivialParenthesizedLiterals(pattern.expression);
            std::vector<std::string>& expressions = expressions_by_reg[pattern.target_reg];
            if (std::find(expressions.begin(), expressions.end(), normalized_expression) ==
                expressions.end()) {
                expressions.push_back(normalized_expression);
            }
        }

        for (const auto& [target_reg, replacement_expressions] : expressions_by_reg) {
            if (replacement_expressions.empty()) {
                continue;
            }
            const std::string temp_name = "r" + std::to_string(target_reg);
            std::vector<std::size_t> removable_seed_lines;
            bool conflicting_assignment = false;
            for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
                bool is_local = false;
                std::string lhs;
                std::string rhs;
                if (!ParseSingleRenderedAssignment(lines[line_index], &is_local, &lhs, &rhs) ||
                    lhs != temp_name) {
                    continue;
                }

                const std::string normalized_rhs =
                    NormalizeRenderedTrivialParenthesizedLiterals(rhs);
                const bool matches_replacement =
                    std::find(replacement_expressions.begin(),
                              replacement_expressions.end(),
                              normalized_rhs) != replacement_expressions.end();
                if (is_local && matches_replacement &&
                    ParseRenderedTemporaryRegisterIndex(lhs).has_value()) {
                    removable_seed_lines.push_back(line_index);
                    continue;
                }

                conflicting_assignment = true;
                break;
            }
            if (conflicting_assignment) {
                continue;
            }

            bool replaced_any_use = false;
            for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
                if (std::find(removable_seed_lines.begin(), removable_seed_lines.end(), line_index) !=
                    removable_seed_lines.end()) {
                    continue;
                }
                if (CountIdentifierUses(lines[line_index], temp_name) == 0) {
                    continue;
                }

                std::optional<std::string> chosen_expression;
                std::size_t best_distance = function_end + 1;
                bool ambiguous_choice = false;
                const std::size_t search_begin =
                    line_index > 8 ? std::max(block.begin + 1, line_index - 8) : block.begin + 1;
                for (std::size_t anchor = line_index; anchor-- > search_begin;) {
                    for (const std::string& replacement_expression : replacement_expressions) {
                        if (lines[anchor].find(replacement_expression) == std::string::npos) {
                            continue;
                        }

                        const std::size_t distance = line_index - anchor;
                        if (!chosen_expression.has_value() || distance < best_distance) {
                            chosen_expression = replacement_expression;
                            best_distance = distance;
                            ambiguous_choice = false;
                        } else if (distance == best_distance &&
                                   replacement_expression != *chosen_expression) {
                            ambiguous_choice = true;
                        }
                    }
                }
                if (!chosen_expression.has_value() || ambiguous_choice) {
                    continue;
                }

                std::string rewritten;
                if (!ReplaceRenderedIdentifierUsesLocal(lines[line_index],
                                                       temp_name,
                                                       *chosen_expression,
                                                       &rewritten)) {
                    continue;
                }

                lines[line_index] = std::move(rewritten);
                replaced_any_use = true;
            }

            if (!replaced_any_use) {
                continue;
            }

            bool temp_still_used = false;
            for (std::size_t probe = block.begin + 1; probe < function_end; ++probe) {
                if (CountIdentifierUses(lines[probe], temp_name) != 0) {
                    temp_still_used = true;
                    break;
                }
            }

            if (!temp_still_used) {
                for (std::size_t seed_index = removable_seed_lines.size(); seed_index > 0;
                     --seed_index) {
                    lines.erase(
                        lines.begin() +
                        static_cast<std::ptrdiff_t>(removable_seed_lines[seed_index - 1]));
                }
            }
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_synthetic_call_uses_inlined");
    return true;
}

bool TryRestoreBytecodeTrailingGlobalSeeds(const Lua51Function& function,
                                           std::string_view chunk_name,
                                           std::string* source,
                                           std::string* note,
                                           std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto next_nonempty_line = [&](const std::vector<std::string>& rendered_lines,
                                        std::size_t start,
                                        std::size_t end_exclusive) {
        for (std::size_t probe = start;
             probe < rendered_lines.size() && probe < end_exclusive;
             ++probe) {
            if (!trim_line(rendered_lines[probe]).empty()) {
                return probe;
            }
        }
        return end_exclusive;
    };
    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const Lua51Function& child_function = function.child_functions[block_index];
        const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(child_function);
        if (instructions.size() < 7) {
            continue;
        }

        const Lua51Instruction& tail_return = instructions[instructions.size() - 1];
        const Lua51Instruction& tail_jump = instructions[instructions.size() - 2];
        const Lua51Instruction& yield_call = instructions[instructions.size() - 3];
        const Lua51Instruction& yield_gettable = instructions[instructions.size() - 4];
        const Lua51Instruction& coroutine_getglobal = instructions[instructions.size() - 5];
        const Lua51Instruction& trailing_setglobal = instructions[instructions.size() - 6];
        const Lua51Instruction& trailing_loadk = instructions[instructions.size() - 7];
        if (tail_return.opcode != Lua51Opcode::kReturn || tail_jump.opcode != Lua51Opcode::kJmp ||
            tail_jump.sbx >= 0 || yield_call.opcode != Lua51Opcode::kCall ||
            yield_call.b != 1 || yield_call.c != 1 ||
            yield_gettable.opcode != Lua51Opcode::kGetTable ||
            coroutine_getglobal.opcode != Lua51Opcode::kGetGlobal ||
            trailing_setglobal.opcode != Lua51Opcode::kSetGlobal ||
            trailing_loadk.opcode != Lua51Opcode::kLoadK ||
            trailing_loadk.a != trailing_setglobal.a ||
            yield_gettable.a != coroutine_getglobal.a ||
            yield_gettable.b != coroutine_getglobal.a) {
            continue;
        }

        if (static_cast<std::size_t>(trailing_loadk.bx) >= child_function.constants.size() ||
            static_cast<std::size_t>(trailing_setglobal.bx) >= child_function.constants.size()) {
            continue;
        }

        const Lua51Constant& coroutine_name =
            child_function.constants[static_cast<std::size_t>(coroutine_getglobal.bx)];
        const Lua51Constant* yield_name = GetLua51RKConstant(child_function, yield_gettable.c);
        const Lua51Constant& global_target =
            child_function.constants[static_cast<std::size_t>(trailing_setglobal.bx)];
        if (coroutine_name.type != Lua51ConstantType::kString ||
            coroutine_name.string_value != "coroutine" || yield_name == nullptr ||
            yield_name->type != Lua51ConstantType::kString ||
            yield_name->string_value != "yield" ||
            global_target.type != Lua51ConstantType::kString ||
            !IsValidLuaIdentifier(global_target.string_value) ||
            IsLuaKeyword(global_target.string_value)) {
            continue;
        }

        const std::string rendered_constant =
            RenderLuaConstant(child_function.constants[static_cast<std::size_t>(trailing_loadk.bx)]);
        if (rendered_constant.empty()) {
            continue;
        }

        std::size_t while_line = function_end + 1;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            if (trim_line(lines[line_index]) != "while true do") {
                continue;
            }
            const std::size_t yield_line =
                next_nonempty_line(lines, line_index + 1, function_end);
            if (yield_line >= function_end ||
                trim_line(lines[yield_line]).find("coroutine.yield(") == std::string_view::npos) {
                continue;
            }
            while_line = line_index;
            break;
        }
        if (while_line > function_end) {
            continue;
        }

        std::size_t assignment_line = while_line;
        while (assignment_line > block.begin + 1) {
            --assignment_line;
            if (!trim_line(lines[assignment_line]).empty()) {
                break;
            }
        }
        if (assignment_line <= block.begin) {
            continue;
        }

        std::string local_name;
        std::string expression;
        if (!ParseRenderedLocalAssignment(lines[assignment_line], &local_name, &expression)) {
            continue;
        }
        const std::string normalized_expression =
            NormalizeRenderedTrivialParenthesizedLiterals(expression);
        if (expression != rendered_constant && normalized_expression != rendered_constant) {
            continue;
        }

        bool local_used_after = false;
        for (std::size_t probe = assignment_line + 1; probe < function_end; ++probe) {
            if (CountIdentifierUses(lines[probe], local_name) != 0) {
                local_used_after = true;
                break;
            }
        }
        if (local_used_after) {
            continue;
        }

        lines[assignment_line] = line_indent(lines[assignment_line]) + global_target.string_value +
                                 " = " + rendered_constant;
        changed = true;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_trailing_global_seeds_restored");
    return true;
}

bool TryRestoreBytecodeImmediateGlobalCallSeeds(const Lua51Function& function,
                                                std::string_view chunk_name,
                                                std::string* source,
                                                std::string* note,
                                                std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    struct GlobalCallSeedPattern {
        std::string target_name;
        std::string rendered_expression;
    };

    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 2) {
        return false;
    }

    const std::vector<std::string> parameter_names;
    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    std::vector<GlobalCallSeedPattern> patterns;
    for (std::size_t instruction_index = 0; instruction_index + 1 < instructions.size();
         ++instruction_index) {
        const Lua51Instruction& call = instructions[instruction_index];
        const Lua51Instruction& setglobal = instructions[instruction_index + 1];
        if (call.opcode != Lua51Opcode::kCall || setglobal.opcode != Lua51Opcode::kSetGlobal ||
            setglobal.a != call.a || call.c != 2 || call.b < 1) {
            StepLua51TrackedRegisterExpressions(function,
                                                parameter_names,
                                                call,
                                                instruction_index,
                                                &tracked_registers);
            continue;
        }
        if (static_cast<std::size_t>(setglobal.bx) >= function.constants.size()) {
            StepLua51TrackedRegisterExpressions(function,
                                                parameter_names,
                                                call,
                                                instruction_index,
                                                &tracked_registers);
            continue;
        }

        const Lua51Constant& target_constant =
            function.constants[static_cast<std::size_t>(setglobal.bx)];
        if (target_constant.type != Lua51ConstantType::kString ||
            !IsValidLuaIdentifier(target_constant.string_value) ||
            IsLuaKeyword(target_constant.string_value)) {
            StepLua51TrackedRegisterExpressions(function,
                                                parameter_names,
                                                call,
                                                instruction_index,
                                                &tracked_registers);
            continue;
        }

        const std::optional<std::string> rendered_expression =
            RenderLua51TrackedCallExpression(function,
                                             parameter_names,
                                             tracked_registers,
                                             instruction_index,
                                             call);
        if (!rendered_expression.has_value()) {
            StepLua51TrackedRegisterExpressions(function,
                                                parameter_names,
                                                call,
                                                instruction_index,
                                                &tracked_registers);
            continue;
        }

        const bool duplicate_pattern = std::any_of(
            patterns.begin(),
            patterns.end(),
            [&](const GlobalCallSeedPattern& existing) {
                return existing.target_name == target_constant.string_value;
            });
        if (!duplicate_pattern) {
            patterns.push_back(GlobalCallSeedPattern{
                .target_name = target_constant.string_value,
                .rendered_expression = *rendered_expression,
            });
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            call,
                                            instruction_index,
                                            &tracked_registers);
        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            setglobal,
                                            instruction_index + 1,
                                            &tracked_registers);
        ++instruction_index;
    }
    if (patterns.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_top_level_nonempty =
        [&](const std::vector<std::string>& rendered_lines,
            const std::vector<RenderedFunctionBlock>& rendered_blocks,
            std::size_t start) {
            std::size_t block_index = 0;
            while (start < rendered_lines.size()) {
                while (block_index < rendered_blocks.size() &&
                       rendered_blocks[block_index].end < start) {
                    ++block_index;
                }
                if (block_index < rendered_blocks.size() &&
                    rendered_blocks[block_index].begin <= start &&
                    start <= rendered_blocks[block_index].end) {
                    start = rendered_blocks[block_index].end + 1;
                    continue;
                }

                if (!trim_line(rendered_lines[start]).empty()) {
                    return start;
                }
                ++start;
            }
            return rendered_lines.size();
        };
    std::vector<std::string> lines = SplitRenderedLines(*source);
    bool changed = false;
    bool pass_changed = false;
    bool guard_triggered = false;
    std::size_t pass_count = 0;
    constexpr std::size_t kMaxRewritePasses = 128;
    std::unordered_set<std::size_t> seen_pass_states;
    seen_pass_states.reserve(kMaxRewritePasses);
    do {
        if (pass_count >= kMaxRewritePasses ||
            !seen_pass_states.insert(HashRenderedLines(lines)).second) {
            guard_triggered = true;
            break;
        }
        ++pass_count;
        pass_changed = false;
        const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
        for (std::size_t line_index = 0; line_index < lines.size() && !pass_changed; ++line_index) {
            const std::size_t alias_line = next_top_level_nonempty(lines, blocks, line_index + 1);
            if (alias_line >= lines.size()) {
                continue;
            }

            bool seed_is_local = false;
            std::string seed_lhs;
            std::string seed_rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
                !IsRenderTemporaryName(seed_lhs) ||
                !LooksLikeRenderedCallExpression(seed_rhs)) {
                continue;
            }

            bool alias_is_local = false;
            std::string alias_lhs;
            std::string alias_rhs;
            if (!ParseSingleRenderedAssignment(lines[alias_line],
                                               &alias_is_local,
                                               &alias_lhs,
                                               &alias_rhs) ||
                alias_is_local || alias_rhs != seed_lhs) {
                continue;
            }

            const std::string* matched_expression = nullptr;
            for (const GlobalCallSeedPattern& pattern : patterns) {
                if (alias_lhs == pattern.target_name) {
                    matched_expression = &pattern.rendered_expression;
                    break;
                }
            }
            if (matched_expression == nullptr || matched_expression->empty()) {
                continue;
            }

            bool used_later = false;
            for (std::size_t probe = next_top_level_nonempty(lines, blocks, alias_line + 1);
                 probe < lines.size();
                 probe = next_top_level_nonempty(lines, blocks, probe + 1)) {
                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseSingleRenderedAssignment(lines[probe],
                                                 &probe_is_local,
                                                 &probe_lhs,
                                                 &probe_rhs) &&
                    probe_lhs == seed_lhs) {
                    break;
                }
                if (CountIdentifierUses(lines[probe], seed_lhs) != 0) {
                    used_later = true;
                    break;
                }
            }
            if (used_later) {
                continue;
            }

            const std::size_t equal = lines[alias_line].find('=');
            if (equal == std::string::npos) {
                continue;
            }

            lines[alias_line] =
                lines[alias_line].substr(0, equal + 1) + " " +
                NormalizeRenderedTrivialParenthesizedLiterals(*matched_expression);
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            pass_changed = true;
        }
    } while (pass_changed);
    if (guard_triggered) {
        AppendCandidateNote(note, note_prefix, "immediate_global_call_seed_loop_guarded");
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_immediate_global_call_seeds_restored");
    return true;
}

bool TryRestoreBytecodeTailBranchAssignments(const Lua51Function& function,
                                            std::string_view chunk_name,
                                            std::string* source,
                                            std::string* note,
                                            std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto next_nonempty_line = [&](const std::vector<std::string>& rendered_lines,
                                        std::size_t start,
                                        std::size_t end_exclusive) {
        for (std::size_t probe = start;
             probe < rendered_lines.size() && probe < end_exclusive;
             ++probe) {
            if (!trim_line(rendered_lines[probe]).empty()) {
                return probe;
            }
        }
        return end_exclusive;
    };
    const auto prune_nearby_empty_else = [&](std::vector<std::string>* rendered_lines,
                                             std::size_t center,
                                             std::size_t end_exclusive) {
        if (rendered_lines == nullptr || rendered_lines->empty()) {
            return false;
        }

        const std::size_t search_begin = center > 2 ? center - 2 : 0;
        const std::size_t search_end =
            std::min(rendered_lines->size(), center + 3);
        for (std::size_t probe = search_begin; probe < search_end; ++probe) {
            if (trim_line((*rendered_lines)[probe]) != "else") {
                continue;
            }

            const std::size_t next_line =
                next_nonempty_line(*rendered_lines, probe + 1, end_exclusive);
            if (next_line >= rendered_lines->size() || next_line >= end_exclusive) {
                continue;
            }
            if (trim_line((*rendered_lines)[next_line]) != "end") {
                continue;
            }

            rendered_lines->erase(rendered_lines->begin() +
                                  static_cast<std::ptrdiff_t>(probe));
            return true;
        }

        return false;
    };
    const auto looks_like_call_statement = [&](std::string_view line) {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty() || !trimmed.ends_with(")")) {
            return false;
        }
        if (trimmed.find('=') != std::string_view::npos ||
            trimmed.starts_with("if ") || trimmed.starts_with("elseif ") ||
            trimmed.starts_with("while ") || trimmed.starts_with("for ") ||
            trimmed.starts_with("local ") || trimmed.starts_with("function ") ||
            trimmed.starts_with("return ")) {
            return false;
        }
        return true;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const Lua51Function& child_function = function.child_functions[block_index];
        const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(child_function);
        if (instructions.size() < 8) {
            continue;
        }

        const Lua51Instruction& tail_return = instructions[instructions.size() - 1];
        const Lua51Instruction& tail_jump = instructions[instructions.size() - 2];
        const Lua51Instruction& yield_call = instructions[instructions.size() - 3];
        const Lua51Instruction& yield_gettable = instructions[instructions.size() - 4];
        const Lua51Instruction& coroutine_getglobal = instructions[instructions.size() - 5];
        const Lua51Instruction& trailing_settable = instructions[instructions.size() - 6];
        const Lua51Instruction& preceding_call = instructions[instructions.size() - 7];
        if (tail_return.opcode != Lua51Opcode::kReturn || tail_jump.opcode != Lua51Opcode::kJmp ||
            tail_jump.sbx >= 0 || yield_call.opcode != Lua51Opcode::kCall ||
            yield_call.b != 1 || yield_call.c != 1 ||
            yield_gettable.opcode != Lua51Opcode::kGetTable ||
            coroutine_getglobal.opcode != Lua51Opcode::kGetGlobal ||
            trailing_settable.opcode != Lua51Opcode::kSetTable ||
            preceding_call.opcode != Lua51Opcode::kCall) {
            continue;
        }

        if (static_cast<std::size_t>(coroutine_getglobal.bx) >= child_function.constants.size()) {
            continue;
        }
        const Lua51Constant& coroutine_name =
            child_function.constants[static_cast<std::size_t>(coroutine_getglobal.bx)];
        const Lua51Constant* yield_name = GetLua51RKConstant(child_function, yield_gettable.c);
        if (coroutine_name.type != Lua51ConstantType::kString ||
            coroutine_name.string_value != "coroutine" || yield_name == nullptr ||
            yield_name->type != Lua51ConstantType::kString ||
            yield_name->string_value != "yield") {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        std::vector<std::optional<std::string>> tracked_registers(
            std::max<std::size_t>(child_function.max_stack_size, parameter_names.size()));
        std::optional<std::string> rendered_call;
        std::optional<std::string> rendered_assignment;
        for (std::size_t instruction_index = 0; instruction_index < instructions.size();
             ++instruction_index) {
            const Lua51Instruction& instruction = instructions[instruction_index];
            if (instruction_index == instructions.size() - 7) {
                rendered_call = RenderLua51TrackedCallExpression(child_function,
                                                                 parameter_names,
                                                                 tracked_registers,
                                                                 instruction_index,
                                                                 instruction);
            }
            if (instruction_index == instructions.size() - 6) {
                const std::optional<std::string> assignment_target =
                    RenderLua51TrackedTableAccess(child_function,
                                                  parameter_names,
                                                  tracked_registers,
                                                  instruction_index,
                                                  instruction.a,
                                                  instruction.b);
                const std::optional<std::string> assignment_value =
                    RenderLua51TrackedRKOperand(child_function,
                                                parameter_names,
                                                tracked_registers,
                                                instruction_index,
                                                instruction.c);
                if (assignment_target.has_value() && assignment_value.has_value()) {
                    rendered_assignment = *assignment_target + " = " + *assignment_value;
                }
            }

            StepLua51TrackedRegisterExpressions(child_function,
                                                parameter_names,
                                                instruction,
                                                instruction_index,
                                                &tracked_registers);
        }

        if (!rendered_assignment.has_value()) {
            continue;
        }

        std::size_t yield_line = function_end + 1;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            if (trim_line(lines[line_index]).find("coroutine.yield(") == std::string_view::npos) {
                continue;
            }
            yield_line = line_index;
            break;
        }
        if (yield_line > function_end) {
            continue;
        }

        const std::string normalized_rendered_assignment =
            NormalizeRenderedTrivialParenthesizedLiterals(*rendered_assignment);

        std::size_t assignment_line = function_end + 1;
        for (std::size_t line_index = yield_line; line_index > block.begin + 1; --line_index) {
            const std::size_t candidate_index = line_index - 1;
            const std::string normalized_candidate =
                NormalizeRenderedTrivialParenthesizedLiterals(
                    std::string(trim_line(lines[candidate_index])));
            if (trim_line(lines[candidate_index]) == *rendered_assignment ||
                normalized_candidate == normalized_rendered_assignment) {
                assignment_line = candidate_index;
                break;
            }
        }
        if (assignment_line > function_end) {
            continue;
        }

        std::size_t call_line = function_end + 1;
        if (rendered_call.has_value()) {
            std::size_t probe = assignment_line;
            while (probe > block.begin + 1) {
                --probe;
                if (trim_line(lines[probe]) == *rendered_call) {
                    call_line = probe;
                    break;
                }
            }
        }

        if (call_line > function_end) {
            std::size_t probe = assignment_line;
            while (probe > block.begin + 1) {
                --probe;
                const std::string_view trimmed = trim_line(lines[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (trimmed != "else") {
                    break;
                }

                bool skipped_end = false;
                while (probe > block.begin + 1) {
                    --probe;
                    const std::string_view anchor_trimmed = trim_line(lines[probe]);
                    if (anchor_trimmed.empty()) {
                        continue;
                    }
                    if (anchor_trimmed == "end") {
                        skipped_end = true;
                        continue;
                    }
                    if (skipped_end && looks_like_call_statement(lines[probe])) {
                        call_line = probe;
                    }
                    break;
                }
                break;
            }
        }

        if (call_line > function_end) {
            continue;
        }

        const std::size_t next_after_call =
            next_nonempty_line(lines, call_line + 1, assignment_line);
        if (next_after_call == assignment_line) {
            continue;
        }

        bool saw_control_boundary = false;
        for (std::size_t probe = call_line + 1; probe < assignment_line; ++probe) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (trimmed == "end" || trimmed == "else" || trimmed.starts_with("elseif ")) {
                saw_control_boundary = true;
                break;
            }
        }
        if (!saw_control_boundary) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        const std::string reassigned_line =
            line_indent(candidate_lines[call_line]) + normalized_rendered_assignment;
        candidate_lines.erase(candidate_lines.begin() +
                                  static_cast<std::ptrdiff_t>(assignment_line));
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(call_line + 1),
                               reassigned_line);
        prune_nearby_empty_else(&candidate_lines, assignment_line, function_end + 1);

        NormalizeRenderedLuaWhitespace(&candidate_lines);
        std::string candidate_source = JoinRenderedLines(candidate_lines);
        if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
            continue;
        }

        lines = std::move(candidate_lines);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_tail_branch_assignments_restored");
    return true;
}

using IndexedCallBootstrapEntries =
    std::unordered_map<std::string, std::vector<std::pair<int, std::string>>>;

using TopLevelTempSeedFieldPatterns =
    std::vector<std::pair<std::string, std::string>>;

TopLevelTempSeedFieldPatterns CollectBytecodeTopLevelTempSeedFieldPatterns(
    const Lua51Function& function) {
    const std::vector<std::string> parameter_names;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    TopLevelTempSeedFieldPatterns patterns;

    for (std::size_t instruction_index = 0; instruction_index < instructions.size();
         ++instruction_index) {
        const Lua51Instruction& instruction = instructions[instruction_index];
        if (instruction.opcode == Lua51Opcode::kSetTable) {
            const std::optional<std::string> owner =
                RenderLua51TrackedRegisterOperand(function,
                                                  parameter_names,
                                                  tracked_registers,
                                                  instruction_index,
                                                  instruction.a);
            const Lua51Constant* key_constant = GetLua51RKConstant(function, instruction.b);
            const std::optional<std::string> value =
                RenderLua51TrackedRKOperand(function,
                                            parameter_names,
                                            tracked_registers,
                                            instruction_index,
                                            instruction.c);
            if (owner.has_value() && key_constant != nullptr &&
                key_constant->type == Lua51ConstantType::kString &&
                IsValidLuaIdentifier(key_constant->string_value) &&
                !IsLuaKeyword(key_constant->string_value) && value.has_value() &&
                !ContainsGuessedLua51LocalBase(
                    NormalizeRenderedTrivialParenthesizedLiterals(*value)) &&
                value->find("nil.") == std::string::npos &&
                value->find("nil:") == std::string::npos) {
                patterns.emplace_back(key_constant->string_value, *value);
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instruction,
                                            instruction_index,
                                            &tracked_registers);
    }

    return patterns;
}

IndexedCallBootstrapEntries CollectBytecodeIndexedCallBootstrapEntries(
    const Lua51Function& function) {
    const std::vector<std::string> parameter_names;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    std::unordered_set<std::string> seeded_tables;
    IndexedCallBootstrapEntries indexed_entries;

    for (std::size_t instruction_index = 0; instruction_index < instructions.size();
         ++instruction_index) {
        const Lua51Instruction& instruction = instructions[instruction_index];

        if (instruction.opcode == Lua51Opcode::kSetGlobal && instruction.a >= 0) {
            const std::size_t reg_index = static_cast<std::size_t>(instruction.a);
            if (reg_index < tracked_registers.size() && tracked_registers[reg_index] == "{}" &&
                static_cast<std::size_t>(instruction.bx) < function.constants.size()) {
                const Lua51Constant& target_constant =
                    function.constants[static_cast<std::size_t>(instruction.bx)];
                if (target_constant.type == Lua51ConstantType::kString &&
                    IsValidLuaIdentifier(target_constant.string_value) &&
                    !IsLuaKeyword(target_constant.string_value)) {
                    seeded_tables.insert(target_constant.string_value);
                }
            }
        } else if (instruction.opcode == Lua51Opcode::kSetTable) {
            const std::optional<std::string> owner =
                RenderLua51TrackedRegisterOperand(function,
                                                  parameter_names,
                                                  tracked_registers,
                                                  instruction_index,
                                                  instruction.a);
            const std::optional<std::string> value =
                RenderLua51TrackedRegisterOperand(function,
                                                  parameter_names,
                                                  tracked_registers,
                                                  instruction_index,
                                                  instruction.c);
            const std::optional<int> numeric_key =
                ParseLua51RKPositiveIntegralConstant(function, instruction.b);
            if (owner.has_value() && value.has_value() && numeric_key.has_value() &&
                seeded_tables.contains(*owner) && LooksLikeRenderedCallExpression(*value)) {
                indexed_entries[*owner].push_back({*numeric_key, *value});
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instruction,
                                            instruction_index,
                                            &tracked_registers);
    }

    return indexed_entries;
}

bool TryRestoreBytecodeIndexedCallTableBootstraps(const Lua51Function& function,
                                                  std::string_view chunk_name,
                                                  std::string* source,
                                                  std::string* note,
                                                  std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const IndexedCallBootstrapEntries indexed_entries =
        CollectBytecodeIndexedCallBootstrapEntries(function);

    if (indexed_entries.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = trim_line(lines[line_index]);
        const std::size_t open_brace = trimmed.find("= {");
        if (open_brace == std::string_view::npos) {
            continue;
        }

        const std::string target_expression =
            std::string(TrimLuaWhitespace(trimmed.substr(0, open_brace)));
        const auto pattern_it = indexed_entries.find(target_expression);
        if (pattern_it == indexed_entries.end() || pattern_it->second.empty()) {
            continue;
        }

        const std::size_t literal_end = FindRenderedTableLiteralEnd(lines, line_index);
        if (literal_end == std::string::npos || literal_end <= line_index) {
            continue;
        }

        std::vector<std::string> literal_entries;
        bool literal_matches = true;
        for (std::size_t entry_line = line_index + 1; entry_line < literal_end; ++entry_line) {
            const std::string entry = TrimTrailingRenderedComma(lines[entry_line]);
            if (entry.empty()) {
                continue;
            }
            if (!LooksLikeRenderedCallExpression(entry)) {
                literal_matches = false;
                break;
            }
            literal_entries.push_back(entry);
        }

        if (!literal_matches || literal_entries.empty() ||
            literal_entries.size() > pattern_it->second.size()) {
            continue;
        }

        bool bytecode_matches = true;
        for (std::size_t entry_index = 0; entry_index < literal_entries.size(); ++entry_index) {
            const auto& [numeric_key, expression] = pattern_it->second[entry_index];
            if (numeric_key != static_cast<int>(entry_index + 1) ||
                expression != literal_entries[entry_index]) {
                bytecode_matches = false;
                break;
            }
        }
        if (!bytecode_matches) {
            continue;
        }

        std::vector<std::string> replacement_lines;
        const std::string indent = line_indent(lines[line_index]);
        replacement_lines.push_back(indent + target_expression + " = {}");
        for (std::size_t entry_index = 0; entry_index < literal_entries.size(); ++entry_index) {
            replacement_lines.push_back(indent + target_expression + "[" +
                                        std::to_string(entry_index + 1) + "] = " +
                                        literal_entries[entry_index]);
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(literal_end + 1));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement_lines.begin(),
                     replacement_lines.end());
        changed = true;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_indexed_call_bootstraps_restored");
    return true;
}

bool TryRestoreBytecodeIndexedMissingCallBootstrapSeeds(const Lua51Function& function,
                                                        std::string_view chunk_name,
                                                        std::string* source,
                                                        std::string* note,
                                                        std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const IndexedCallBootstrapEntries indexed_entries =
        CollectBytecodeIndexedCallBootstrapEntries(function);
    if (indexed_entries.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto starts_with_indexed_continuation = [](std::string_view statement,
                                                     std::string_view indexed_lhs) {
        statement = TrimLuaWhitespace(statement);
        return statement == indexed_lhs || statement.starts_with(std::string(indexed_lhs) + ".") ||
               statement.starts_with(std::string(indexed_lhs) + "[");
    };
    const auto parse_temp_seed_statement = [](std::string_view statement,
                                              std::string* temp_name) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseSingleRenderedAssignment(statement, &is_local, &lhs, &rhs) || rhs != "{}") {
            return false;
        }
        if (temp_name != nullptr) {
            *temp_name = std::move(lhs);
        }
        return true;
    };
    const auto parse_temp_index_seed_statement = [](std::string_view statement,
                                                    std::string_view temp_name,
                                                    int numeric_key) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseSingleRenderedAssignment(statement, &is_local, &lhs, &rhs)) {
            return false;
        }
        return lhs == std::string(temp_name) + "[" + std::to_string(numeric_key) + "]";
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    bool changed = false;
    bool pass_changed = false;
    bool guard_triggered = false;
    std::size_t pass_count = 0;
    constexpr std::size_t kMaxRewritePasses = 128;
    std::unordered_set<std::size_t> seen_pass_states;
    seen_pass_states.reserve(kMaxRewritePasses);
    do {
        if (pass_count >= kMaxRewritePasses ||
            !seen_pass_states.insert(HashRenderedLines(lines)).second) {
            guard_triggered = true;
            break;
        }
        ++pass_count;
        pass_changed = false;
        const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
        const auto next_top_level_nonempty =
            [&](std::size_t start) {
                std::size_t block_index = 0;
                while (start < lines.size()) {
                    while (block_index < blocks.size() && blocks[block_index].end < start) {
                        ++block_index;
                    }
                    if (block_index < blocks.size() && blocks[block_index].begin <= start &&
                        start <= blocks[block_index].end) {
                        start = blocks[block_index].end + 1;
                        continue;
                    }
                    if (!trim_line(lines[start]).empty()) {
                        return start;
                    }
                    ++start;
                }
                return lines.size();
            };
        const auto previous_top_level_nonempty =
            [&](std::size_t start) {
                if (start == 0) {
                    return lines.size();
                }
                std::size_t probe = start;
                while (probe > 0) {
                    --probe;
                    bool inside_block = false;
                    for (const RenderedFunctionBlock& block : blocks) {
                        if (block.begin <= probe && probe <= block.end) {
                            inside_block = true;
                            if (block.begin == 0) {
                                probe = 0;
                            } else {
                                probe = block.begin;
                            }
                            break;
                        }
                    }
                    if (inside_block) {
                        continue;
                    }
                    if (!trim_line(lines[probe]).empty()) {
                        return probe;
                    }
                }
                return lines.size();
            };

        for (std::size_t line_index = 0; line_index < lines.size() && !pass_changed; ++line_index) {
            bool seed_is_local = false;
            std::string target_expression;
            std::string target_rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index],
                                               &seed_is_local,
                                               &target_expression,
                                               &target_rhs) ||
                seed_is_local || target_rhs != "{}") {
                continue;
            }

            const auto pattern_it = indexed_entries.find(target_expression);
            if (pattern_it == indexed_entries.end() || pattern_it->second.empty()) {
                continue;
            }

            for (const auto& [numeric_key, rendered_expression] : pattern_it->second) {
                const std::string indexed_lhs =
                    target_expression + "[" + std::to_string(numeric_key) + "]";

                bool explicit_assignment_present = false;
                std::size_t continuation_line = lines.size();
                for (std::size_t probe = next_top_level_nonempty(line_index + 1);
                     probe < lines.size();
                     probe = next_top_level_nonempty(probe + 1)) {
                    const std::vector<std::string_view> statements =
                        SplitRenderedTopLevelSemicolonStatements(lines[probe]);
                    for (std::string_view statement : statements) {
                        bool statement_is_local = false;
                        std::string statement_lhs;
                        std::string statement_rhs;
                        if (ParseSingleRenderedAssignment(statement,
                                                         &statement_is_local,
                                                         &statement_lhs,
                                                         &statement_rhs) &&
                            statement_lhs == indexed_lhs) {
                            explicit_assignment_present = true;
                            break;
                        }
                        if (starts_with_indexed_continuation(statement, indexed_lhs)) {
                            continuation_line = probe;
                            break;
                        }
                    }
                    if (explicit_assignment_present || continuation_line != lines.size()) {
                        break;
                    }
                }

                if (explicit_assignment_present || continuation_line == lines.size()) {
                    continue;
                }

                const std::size_t previous_line = previous_top_level_nonempty(continuation_line);
                if (previous_line == lines.size() || previous_line <= line_index) {
                    continue;
                }

                std::size_t replacement_begin = lines.size();
                std::size_t replacement_end = lines.size();

                const std::vector<std::string_view> previous_statements =
                    SplitRenderedTopLevelSemicolonStatements(lines[previous_line]);
                if (previous_statements.size() >= 2) {
                    std::string temp_name;
                    if (parse_temp_seed_statement(previous_statements[0], &temp_name) &&
                        parse_temp_index_seed_statement(previous_statements[1],
                                                        temp_name,
                                                        numeric_key)) {
                        replacement_begin = previous_line;
                        replacement_end = previous_line;
                    }
                }

                if (replacement_begin == lines.size()) {
                    const std::size_t seed_line = previous_top_level_nonempty(previous_line);
                    if (seed_line == lines.size() || seed_line <= line_index) {
                        continue;
                    }

                    std::string temp_name;
                    if (!parse_temp_seed_statement(lines[seed_line], &temp_name) ||
                        !parse_temp_index_seed_statement(lines[previous_line],
                                                         temp_name,
                                                         numeric_key)) {
                        continue;
                    }

                    replacement_begin = seed_line;
                    replacement_end = previous_line;
                }

                const std::string replacement_line =
                    line_indent(lines[replacement_begin]) + indexed_lhs + " = " +
                    NormalizeRenderedTrivialParenthesizedLiterals(rendered_expression);
                lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(replacement_begin),
                            lines.begin() + static_cast<std::ptrdiff_t>(replacement_end + 1));
                lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(replacement_begin),
                             replacement_line);
                changed = true;
                pass_changed = true;
                break;
            }
        }
    } while (pass_changed);
    if (guard_triggered) {
        AppendCandidateNote(note, note_prefix, "indexed_missing_call_bootstrap_loop_guarded");
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_indexed_missing_call_bootstraps_restored");
    return true;
}

bool TryRestoreBytecodeTopLevelTempSeedFieldValues(const Lua51Function& function,
                                                   std::string_view chunk_name,
                                                   std::string* source,
                                                   std::string* note,
                                                   std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }
    const bool source_was_valid = ValidateLuaSource(*source, chunk_name).empty();

    const TopLevelTempSeedFieldPatterns patterns =
        CollectBytecodeTopLevelTempSeedFieldPatterns(function);
    if (patterns.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto looks_like_invalid_field_rhs = [](std::string_view expression) {
        expression = TrimLuaWhitespace(expression);
        if (expression.find("nil.") != std::string_view::npos ||
            expression.find("nil:") != std::string_view::npos) {
            return true;
        }
        const std::size_t access = expression.find_first_of(".:");
        if (access == std::string_view::npos || access == 0) {
            return false;
        }
        const std::string_view owner = TrimLuaWhitespace(expression.substr(0, access));
        return IsRenderTemporaryName(owner);
    };
    const auto next_top_level_nonempty =
        [&](const std::vector<std::string>& rendered_lines,
            const std::vector<RenderedFunctionBlock>& rendered_blocks,
            std::size_t start) {
            std::size_t block_index = 0;
            while (start < rendered_lines.size()) {
                while (block_index < rendered_blocks.size() &&
                       rendered_blocks[block_index].end < start) {
                    ++block_index;
                }
                if (block_index < rendered_blocks.size() &&
                    rendered_blocks[block_index].begin <= start &&
                    start <= rendered_blocks[block_index].end) {
                    start = rendered_blocks[block_index].end + 1;
                    continue;
                }
                if (!trim_line(rendered_lines[start]).empty()) {
                    return start;
                }
                ++start;
            }
            return rendered_lines.size();
        };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    bool changed = false;
    std::size_t cursor = 0;
    for (std::size_t line_index = next_top_level_nonempty(lines, blocks, 0);
         line_index < lines.size();
         line_index = next_top_level_nonempty(lines, blocks, line_index + 1)) {
        const std::vector<std::string_view> statements =
            SplitRenderedTopLevelSemicolonStatements(lines[line_index]);
        if (statements.size() != 2) {
            continue;
        }

        bool temp_is_local = false;
        std::string temp_lhs;
        std::string temp_rhs;
        if (!ParseSingleRenderedAssignment(statements[0],
                                           &temp_is_local,
                                           &temp_lhs,
                                           &temp_rhs) ||
            !IsRenderTemporaryName(temp_lhs)) {
            continue;
        }
        const std::string normalized_temp_rhs =
            NormalizeRenderedTrivialParenthesizedLiterals(temp_rhs);
        const std::string_view trimmed_temp_rhs = TrimLuaWhitespace(normalized_temp_rhs);
        if (trimmed_temp_rhs.size() < 2 || trimmed_temp_rhs.front() != '{' ||
            trimmed_temp_rhs.back() != '}') {
            continue;
        }

        std::string field_key;
        std::string field_rhs_expression;
        bool rhs_is_function = false;
        int array_index = -1;
        if (!ParseRenderedTableFieldAssignment(statements[1],
                                               temp_lhs,
                                               &field_key,
                                               &field_rhs_expression,
                                               &rhs_is_function,
                                               &array_index) ||
            rhs_is_function ||
            array_index != -1 || field_key.empty() ||
            !looks_like_invalid_field_rhs(field_rhs_expression)) {
            continue;
        }

        const std::string normalized_current_rhs =
            NormalizeRenderedTrivialParenthesizedLiterals(field_rhs_expression);
        const std::pair<std::string, std::string>* chosen_pattern = nullptr;
        std::size_t chosen_pattern_index = cursor;
        std::size_t best_distance = std::numeric_limits<std::size_t>::max();
        bool best_is_forward = false;
        const auto consider_pattern_range =
            [&](std::size_t search_begin, std::size_t search_end) {
                for (std::size_t pattern_index = search_begin;
                     pattern_index < search_end;
                     ++pattern_index) {
                    const auto& candidate_pattern = patterns[pattern_index];
                    if (candidate_pattern.first != field_key) {
                        continue;
                    }

                    const std::string normalized_candidate_rhs =
                        NormalizeRenderedTrivialParenthesizedLiterals(candidate_pattern.second);
                    if (normalized_candidate_rhs.empty() ||
                        normalized_candidate_rhs == normalized_current_rhs ||
                        looks_like_invalid_field_rhs(normalized_candidate_rhs)) {
                        continue;
                    }

                    const bool candidate_is_forward = pattern_index >= cursor;
                    const std::size_t candidate_distance =
                        candidate_is_forward ? pattern_index - cursor : cursor - pattern_index;
                    if (chosen_pattern == nullptr ||
                        candidate_distance < best_distance ||
                        (candidate_distance == best_distance &&
                         candidate_is_forward && !best_is_forward)) {
                        chosen_pattern = &candidate_pattern;
                        chosen_pattern_index = pattern_index;
                        best_distance = candidate_distance;
                        best_is_forward = candidate_is_forward;
                    }
                }
            };

        const std::size_t local_search_begin = cursor > 16 ? cursor - 16 : 0;
        const std::size_t local_search_end = std::min(patterns.size(), cursor + 64);
        consider_pattern_range(local_search_begin, local_search_end);
        if (chosen_pattern == nullptr && patterns.size() > local_search_end) {
            consider_pattern_range(0, patterns.size());
        }

        if (chosen_pattern == nullptr) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines[line_index] =
            line_indent(lines[line_index]) + std::string(TrimLuaWhitespace(statements[0])) +
            "; " + temp_lhs + "." + field_key + " = " + chosen_pattern->second;
        NormalizeRenderedLuaWhitespace(&candidate_lines);
        std::string candidate_source = JoinRenderedLines(candidate_lines);
        if (source_was_valid &&
            !ValidateLuaSource(candidate_source, chunk_name).empty()) {
            continue;
        }

        lines = std::move(candidate_lines);
        cursor = std::max(cursor, chosen_pattern_index + 1);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    *source = JoinRenderedLines(lines);
    AppendCandidateNote(note, note_prefix, "bytecode_top_level_seed_field_values_restored");
    return true;
}

bool TryRestoreBytecodeGuardedMemberCallAssignments(const Lua51Function& function,
                                                    std::string_view chunk_name,
                                                    std::string* source,
                                                    std::string* note,
                                                    std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    struct GuardedMemberAssignment {
        std::string target_expression;
        std::string member_expression;
        std::string temp_local_name;
    };

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const Lua51Function& child_function = function.child_functions[block_index];
        const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(child_function);
        if (instructions.size() < 6) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        std::vector<std::optional<std::string>> tracked_registers(
            std::max<std::size_t>(child_function.max_stack_size, parameter_names.size()));
        std::vector<std::vector<std::optional<std::string>>> tracked_before;
        tracked_before.reserve(instructions.size());
        for (std::size_t instruction_index = 0; instruction_index < instructions.size();
             ++instruction_index) {
            tracked_before.push_back(tracked_registers);
            StepLua51TrackedRegisterExpressions(child_function,
                                                parameter_names,
                                                instructions[instruction_index],
                                                instruction_index,
                                                &tracked_registers);
        }

        bool block_changed = false;
        for (std::size_t instruction_index = 0; instruction_index < instructions.size();
             ++instruction_index) {
            const Lua51Instruction& instruction = instructions[instruction_index];
            if (instruction.opcode != Lua51Opcode::kCall || instruction.c != 2) {
                continue;
            }

            const std::optional<std::string> object_expression =
                RenderLua51TrackedCallExpression(child_function,
                                                 parameter_names,
                                                 tracked_before[instruction_index],
                                                 instruction_index,
                                                 instruction);
            if (!object_expression.has_value() || object_expression->empty()) {
                continue;
            }

            std::string object_local_name;
            if (const std::optional<std::string> active_local =
                    TryGetActiveLua51LocalRegisterName(child_function,
                                                       std::min(instruction_index + 1,
                                                                instructions.size() - 1),
                                                       instruction.a);
                active_local.has_value() &&
                IsValidLuaIdentifier(*active_local) &&
                !LooksLikeGuessedLua51DeclarationName(*active_local)) {
                object_local_name = *active_local;
            }

            std::vector<GuardedMemberAssignment> member_assignments;
            for (std::size_t probe = instruction_index + 1; probe + 2 < instructions.size();) {
                const Lua51Instruction& self_instruction = instructions[probe];
                const Lua51Instruction& call_instruction = instructions[probe + 1];
                const Lua51Instruction& set_instruction = instructions[probe + 2];
                if (self_instruction.opcode != Lua51Opcode::kSelf ||
                    self_instruction.b != instruction.a ||
                    call_instruction.opcode != Lua51Opcode::kCall ||
                    call_instruction.a != self_instruction.a ||
                    call_instruction.b != 2 || call_instruction.c != 2 ||
                    set_instruction.opcode != Lua51Opcode::kSetTable ||
                    set_instruction.c != self_instruction.a) {
                    break;
                }

                const Lua51Constant* method_constant =
                    GetLua51RKConstant(child_function, self_instruction.c);
                const std::optional<std::string> target_expression =
                    RenderLua51TrackedTableAccess(child_function,
                                                  parameter_names,
                                                  tracked_before[probe + 2],
                                                  probe + 2,
                                                  set_instruction.a,
                                                  set_instruction.b);
                std::optional<std::string> member_base_expression;
                if (!object_local_name.empty()) {
                    member_base_expression = object_local_name;
                } else {
                    member_base_expression =
                        RenderLua51TrackedRegisterOperand(child_function,
                                                          parameter_names,
                                                          tracked_before[probe],
                                                          probe,
                                                          self_instruction.b);
                }

                if (method_constant == nullptr ||
                    method_constant->type != Lua51ConstantType::kString ||
                    !IsValidLuaIdentifier(method_constant->string_value) ||
                    target_expression == std::nullopt ||
                    member_base_expression == std::nullopt) {
                    break;
                }

                std::string temp_local_name;
                if (const std::optional<std::string> active_local =
                        TryGetActiveLua51LocalRegisterName(child_function,
                                                           std::min(probe + 2,
                                                                    instructions.size() - 1),
                                                           self_instruction.a);
                    active_local.has_value() &&
                    IsValidLuaIdentifier(*active_local) &&
                    !LooksLikeGuessedLua51DeclarationName(*active_local) &&
                    *active_local != object_local_name) {
                    temp_local_name = *active_local;
                }

                member_assignments.push_back(
                    GuardedMemberAssignment{*target_expression,
                                            *member_base_expression + ":" +
                                                method_constant->string_value + "()",
                                            std::move(temp_local_name)});
                probe += 3;
            }

            if (member_assignments.empty()) {
                continue;
            }

            for (std::size_t line_index = block.begin + 1; line_index < function_end;
                 ++line_index) {
                bool is_local = false;
                std::string lhs_expression;
                std::string rhs_expression;
                if (!ParseSingleRenderedAssignment(lines[line_index],
                                                  &is_local,
                                                  &lhs_expression,
                                                  &rhs_expression)) {
                    continue;
                }

                const std::string normalized_lhs =
                    NormalizeRenderedTrivialParenthesizedLiterals(
                        TrimLuaWhitespace(lhs_expression));
                const std::string normalized_rhs =
                    NormalizeRenderedTrivialParenthesizedLiterals(
                        TrimLuaWhitespace(rhs_expression));
                const std::string normalized_object_expression =
                    NormalizeRenderedTrivialParenthesizedLiterals(*object_expression);
                const std::string object_terminal =
                    ExtractRenderedCallTerminal(normalized_object_expression);
                std::string rhs_call_expression;
                const std::size_t and_pos = normalized_rhs.rfind(" and ");
                if (and_pos != std::string::npos) {
                    rhs_call_expression = std::string(
                        TrimLuaWhitespace(std::string_view(normalized_rhs).substr(and_pos + 5)));
                }
                const std::string rhs_terminal =
                    rhs_call_expression.empty() ? std::string()
                                                : ExtractRenderedCallTerminal(rhs_call_expression);
                if (normalized_lhs.empty() ||
                    (normalized_rhs.find(normalized_object_expression) == std::string::npos &&
                     (object_terminal.empty() || rhs_terminal != object_terminal)) ||
                    normalized_rhs.find(" and ") == std::string::npos) {
                    continue;
                }

                bool already_present = false;
                for (std::size_t probe = line_index + 1; probe < function_end; ++probe) {
                    const std::string_view trimmed_probe = trim_line(lines[probe]);
                    if (trimmed_probe == "end") {
                        break;
                    }
                    for (const GuardedMemberAssignment& member_assignment : member_assignments) {
                        const std::string& target_expression =
                            member_assignment.target_expression;
                        if (trimmed_probe.find(target_expression) != std::string_view::npos) {
                            already_present = true;
                            break;
                        }
                    }
                    if (already_present) {
                        break;
                    }
                }
                if (already_present) {
                    continue;
                }

                std::vector<std::string> candidate_lines = lines;
                std::size_t candidate_line_index = line_index;

                std::size_t guard_line_index = candidate_line_index;
                while (guard_line_index > block.begin + 1 &&
                       trim_line(candidate_lines[guard_line_index - 1]).empty()) {
                    --guard_line_index;
                }

                if (guard_line_index > block.begin + 1) {
                    const std::size_t maybe_guard_line = guard_line_index - 1;
                    bool guard_is_local = false;
                    std::string guard_lhs;
                    std::string guard_rhs;
                    if (ParseSingleRenderedAssignment(candidate_lines[maybe_guard_line],
                                                     &guard_is_local,
                                                     &guard_lhs,
                                                     &guard_rhs) &&
                        guard_is_local && IsValidLuaIdentifier(guard_lhs) &&
                        normalized_rhs.find(guard_lhs) != std::string::npos) {
                        std::size_t header_line_index = maybe_guard_line;
                        while (header_line_index > block.begin + 1 &&
                               trim_line(candidate_lines[header_line_index - 1]).empty()) {
                            --header_line_index;
                        }
                        if (header_line_index > block.begin + 1) {
                            --header_line_index;
                            std::string if_condition;
                            if (TryExtractRenderedIfConditionLocal(
                                    candidate_lines[header_line_index],
                                    &if_condition) &&
                                NormalizeRenderedTrivialParenthesizedLiterals(if_condition).find(
                                    NormalizeRenderedTrivialParenthesizedLiterals(guard_rhs)) ==
                                    std::string::npos) {
                                const std::string indent =
                                    line_indent(candidate_lines[header_line_index]);
                                const std::string normalized_if_condition =
                                    NormalizeRenderedTrivialParenthesizedLiterals(
                                        TrimLuaWhitespace(if_condition));
                                const std::string normalized_guard_rhs =
                                    NormalizeRenderedComparatorOperand(
                                        TrimLuaWhitespace(guard_rhs));
                                candidate_lines[header_line_index] =
                                    indent + "if " + normalized_if_condition + " and " +
                                    normalized_guard_rhs + " then";
                            }
                        }
                        candidate_lines.erase(candidate_lines.begin() +
                                              static_cast<std::ptrdiff_t>(maybe_guard_line));
                        --candidate_line_index;
                    }
                }

                const std::string indent = line_indent(candidate_lines[candidate_line_index]);
                bool rewritten_is_local = is_local;
                if (!object_local_name.empty() && normalized_lhs == object_local_name) {
                    rewritten_is_local = true;
                }
                candidate_lines[candidate_line_index] =
                    indent + (rewritten_is_local ? "local " : "") + normalized_lhs + " = " +
                    *object_expression;

                std::size_t insert_at = candidate_line_index + 1;
                std::string last_temp_local_name;
                for (const GuardedMemberAssignment& member_assignment : member_assignments) {
                    const std::string& target_expression = member_assignment.target_expression;
                    const std::string& member_expression = member_assignment.member_expression;
                    const std::string& temp_local_name = member_assignment.temp_local_name;
                    if (!temp_local_name.empty()) {
                        const bool first_temp_use = temp_local_name != last_temp_local_name;
                        candidate_lines.insert(candidate_lines.begin() +
                                                   static_cast<std::ptrdiff_t>(insert_at),
                                               indent +
                                                   (first_temp_use ? "local " : std::string()) +
                                                   temp_local_name + " = " + member_expression);
                        ++insert_at;
                        candidate_lines.insert(candidate_lines.begin() +
                                                   static_cast<std::ptrdiff_t>(insert_at),
                                               indent + target_expression + " = " +
                                                   temp_local_name);
                        last_temp_local_name = temp_local_name;
                    } else {
                        candidate_lines.insert(candidate_lines.begin() +
                                                   static_cast<std::ptrdiff_t>(insert_at),
                                               indent + target_expression + " = " +
                                                   member_expression);
                        last_temp_local_name.clear();
                    }
                    ++insert_at;
                }

                NormalizeRenderedLuaWhitespace(&candidate_lines);
                std::string candidate_source = JoinRenderedLines(candidate_lines);
                if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
                    continue;
                }

                lines = std::move(candidate_lines);
                changed = true;
                block_changed = true;
                break;
            }

            if (block_changed) {
                break;
            }
        }
    }

    if (!changed) {
        return false;
    }

    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note,
                        note_prefix,
                        "bytecode_guarded_member_call_assignments_restored");
    return true;
}

bool TryRestoreIndexedCallLiteralContinuations(std::string_view chunk_name,
                                               std::string* source,
                                               std::string* note,
                                               std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = trim_line(lines[line_index]);
        const std::size_t open_brace = trimmed.find("= {");
        if (open_brace == std::string_view::npos) {
            continue;
        }

        const std::string target_expression =
            std::string(TrimLuaWhitespace(trimmed.substr(0, open_brace)));
        if (target_expression.empty()) {
            continue;
        }

        const std::size_t literal_end = FindRenderedTableLiteralEnd(lines, line_index);
        if (literal_end == std::string::npos || literal_end <= line_index) {
            continue;
        }

        std::vector<std::string> literal_entries;
        bool literal_matches = true;
        for (std::size_t entry_line = line_index + 1; entry_line < literal_end; ++entry_line) {
            const std::string entry = TrimTrailingRenderedComma(lines[entry_line]);
            if (entry.empty()) {
                continue;
            }
            if (!LooksLikeRenderedCallExpression(entry)) {
                literal_matches = false;
                break;
            }
            literal_entries.push_back(entry);
        }
        if (!literal_matches || literal_entries.empty()) {
            continue;
        }

        std::size_t continuation_line = literal_end + 1;
        while (continuation_line < lines.size() && trim_line(lines[continuation_line]).empty()) {
            ++continuation_line;
        }
        if (continuation_line >= lines.size()) {
            continue;
        }

        const std::string first_index_prefix = target_expression + "[1]";
        const std::string_view continuation_trimmed = trim_line(lines[continuation_line]);
        if (!continuation_trimmed.starts_with(first_index_prefix + ".") &&
            !continuation_trimmed.starts_with(first_index_prefix + "[") &&
            continuation_trimmed != first_index_prefix) {
            continue;
        }

        std::vector<std::string> replacement_lines;
        const std::string indent = line_indent(lines[line_index]);
        replacement_lines.push_back(indent + target_expression + " = {}");
        for (std::size_t entry_index = 0; entry_index < literal_entries.size(); ++entry_index) {
            replacement_lines.push_back(indent + target_expression + "[" +
                                        std::to_string(entry_index + 1) + "] = " +
                                        literal_entries[entry_index]);
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(literal_end + 1));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement_lines.begin(),
                     replacement_lines.end());
        changed = true;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "indexed_call_literal_continuations_restored");
    return true;
}

bool TryRestoreBytecodeIndexedEmptyTableBootstraps(const Lua51Function& function,
                                                   std::string_view chunk_name,
                                                   std::string* source,
                                                   std::string* note,
                                                   std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::optional<IndexedEmptyTableBootstrapPattern> pattern =
            DetectIndexedEmptyTableBootstrapPattern(function.child_functions[block_index],
                                                    parameter_names);
        if (!pattern.has_value()) {
            continue;
        }

        const std::string table_literal_start = pattern->target_expression + " = {";
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            if (trim_line(lines[line_index]) != table_literal_start) {
                continue;
            }

            const std::size_t literal_end = FindRenderedTableLiteralEnd(lines, line_index);
            if (literal_end == std::string::npos || literal_end > function_end ||
                literal_end <= line_index) {
                continue;
            }

            std::vector<int> literal_keys;
            bool literal_matches = true;
            for (std::size_t entry_line = line_index + 1; entry_line < literal_end; ++entry_line) {
                std::string_view trimmed = trim_line(lines[entry_line]);
                if (trimmed == "{}" || trimmed == "{},") {
                    literal_keys.push_back(static_cast<int>(literal_keys.size() + 1));
                    continue;
                }
                literal_matches = false;
                break;
            }

            if (!literal_matches || literal_keys != pattern->numeric_keys) {
                continue;
            }

            std::vector<std::string> replacement_lines;
            replacement_lines.reserve(pattern->numeric_keys.size() + 1);
            const std::string indent = line_indent(lines[line_index]);
            replacement_lines.push_back(indent + pattern->target_expression + " = {}");
            for (int numeric_key : pattern->numeric_keys) {
                replacement_lines.push_back(indent + pattern->target_expression + "[" +
                                            std::to_string(numeric_key) + "] = {}");
            }

            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                        lines.begin() + static_cast<std::ptrdiff_t>(literal_end + 1));
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                         replacement_lines.begin(),
                         replacement_lines.end());
            changed = true;
            break;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_indexed_table_bootstraps_restored");
    return true;
}

bool TryRestoreBytecodeGlobalEmptyTableCallSeeds(const Lua51Function& function,
                                                 std::string_view chunk_name,
                                                 std::string* source,
                                                 std::string* note,
                                                 std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    struct GlobalEmptyTableCallSeedPattern {
        std::string seed_name;
        std::string rendered_expression;
        std::string placeholder_expression;
    };

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto replace_identifier_uses = [](std::string_view text,
                                            std::string_view identifier,
                                            std::string_view replacement,
                                            std::string* rewritten) {
        if (rewritten == nullptr || identifier.empty()) {
            return false;
        }

        std::string result;
        result.reserve(text.size() + replacement.size());
        std::size_t offset = 0;
        bool changed = false;
        bool in_single_quote = false;
        bool in_double_quote = false;

        for (std::size_t index = 0; index < text.size();) {
            const char ch = text[index];

            if (!in_single_quote && !in_double_quote && ch == '-' && index + 1 < text.size() &&
                text[index + 1] == '-') {
                break;
            }

            if (in_single_quote) {
                if (ch == '\\' && index + 1 < text.size()) {
                    index += 2;
                    continue;
                }
                if (ch == '\'') {
                    in_single_quote = false;
                }
                ++index;
                continue;
            }

            if (in_double_quote) {
                if (ch == '\\' && index + 1 < text.size()) {
                    index += 2;
                    continue;
                }
                if (ch == '"') {
                    in_double_quote = false;
                }
                ++index;
                continue;
            }

            if (ch == '\'') {
                in_single_quote = true;
                ++index;
                continue;
            }
            if (ch == '"') {
                in_double_quote = true;
                ++index;
                continue;
            }

            if (index + identifier.size() > text.size() ||
                text.substr(index, identifier.size()) != identifier) {
                ++index;
                continue;
            }

            const bool boundary_before =
                index == 0 ||
                ((!std::isalnum(static_cast<unsigned char>(text[index - 1])) &&
                  text[index - 1] != '_') &&
                 text[index - 1] != '.' && text[index - 1] != ':' &&
                 text[index - 1] != ']' && text[index - 1] != ')');
            const std::size_t end = index + identifier.size();
            const bool boundary_after =
                end >= text.size() ||
                ((!std::isalnum(static_cast<unsigned char>(text[end])) && text[end] != '_') &&
                 text[end] != '.' && text[end] != ':' && text[end] != '[' &&
                 text[end] != '(');
            if (!boundary_before || !boundary_after) {
                ++index;
                continue;
            }

            result.append(text.substr(offset, index - offset));
            result.append(replacement);
            offset = end;
            index = end;
            changed = true;
        }

        if (!changed) {
            return false;
        }

        result.append(text.substr(offset));
        *rewritten = std::move(result);
        return true;
    };
    const std::vector<std::string> parameter_names;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    std::vector<std::string> seed_order;
    std::unordered_set<std::string> known_seeds;
    std::vector<GlobalEmptyTableCallSeedPattern> call_seed_patterns;
    const auto render_tracked_call_expression_any_result =
        [&](std::size_t instruction_index,
            const Lua51Instruction& instruction) -> std::optional<std::string> {
        if (instruction.opcode != Lua51Opcode::kCall || instruction.b < 1) {
            return std::nullopt;
        }

        const std::optional<std::string> callee =
            RenderLua51TrackedRegisterOperand(function,
                                              parameter_names,
                                              tracked_registers,
                                              instruction_index,
                                              instruction.a);
        if (!callee.has_value()) {
            return std::nullopt;
        }

        std::vector<std::string> args;
        if (instruction.b >= 2) {
            args.reserve(static_cast<std::size_t>(instruction.b - 1));
        }
        for (int arg_reg = instruction.a + 1; arg_reg < instruction.a + instruction.b; ++arg_reg) {
            const std::optional<std::string> argument =
                RenderLua51TrackedRegisterOperand(function,
                                                  parameter_names,
                                                  tracked_registers,
                                                  instruction_index,
                                                  arg_reg);
            if (!argument.has_value()) {
                return std::nullopt;
            }
            args.push_back(*argument);
        }

        return BuildRenderedCallExpression(*callee, args);
    };

    for (std::size_t instruction_index = 0; instruction_index < instructions.size();
         ++instruction_index) {
        const Lua51Instruction& instruction = instructions[instruction_index];

        if (instruction.opcode == Lua51Opcode::kSetGlobal && instruction.a >= 0) {
            const std::size_t reg_index = static_cast<std::size_t>(instruction.a);
            if (reg_index < tracked_registers.size() && tracked_registers[reg_index] == "{}" &&
                static_cast<std::size_t>(instruction.bx) < function.constants.size()) {
                const Lua51Constant& target_constant =
                    function.constants[static_cast<std::size_t>(instruction.bx)];
                if (target_constant.type == Lua51ConstantType::kString &&
                    IsValidLuaIdentifier(target_constant.string_value) &&
                    !IsLuaKeyword(target_constant.string_value) &&
                    known_seeds.insert(target_constant.string_value).second) {
                    seed_order.push_back(target_constant.string_value);
                }
            }
        } else if (instruction.opcode == Lua51Opcode::kCall && instruction.b >= 1) {
            const std::optional<std::string> rendered_expression =
                render_tracked_call_expression_any_result(instruction_index, instruction);
            if (rendered_expression.has_value()) {
                for (const std::string& seed_name : seed_order) {
                    if (CountIdentifierUses(*rendered_expression, seed_name) == 0) {
                        continue;
                    }

                    std::string placeholder_expression;
                    if (!replace_identifier_uses(*rendered_expression,
                                                 seed_name,
                                                 "{}",
                                                 &placeholder_expression) ||
                        placeholder_expression == *rendered_expression) {
                        continue;
                    }

                    call_seed_patterns.push_back(GlobalEmptyTableCallSeedPattern{
                        .seed_name = seed_name,
                        .rendered_expression = *rendered_expression,
                        .placeholder_expression = std::move(placeholder_expression),
                    });
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instruction,
                                            instruction_index,
                                            &tracked_registers);
    }

    if (seed_order.empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    std::string root_indent;
    for (const std::string& line : lines) {
        if (trim_line(line).empty()) {
            continue;
        }
        root_indent = line_indent(line);
        break;
    }
    std::unordered_map<std::string, std::size_t> existing_seed_lines;
    std::unordered_map<std::string, std::size_t> existing_local_seed_lines;
    std::unordered_map<std::string, std::string> existing_local_seed_rhs;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view stripped = StripLuaLineComment(lines[line_index]);
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseSingleRenderedAssignment(stripped, &is_local, &lhs, &rhs) ||
            !IsValidLuaIdentifier(lhs) || IsLuaKeyword(lhs)) {
            continue;
        }

        const std::string_view trimmed_rhs = TrimLuaWhitespace(rhs);

        // Treat empty seeds, multiline literal starts, and one-line table
        // literals as already-present bootstrap roots.
        if (trimmed_rhs != "{}" && trimmed_rhs != "{" &&
            !(trimmed_rhs.size() >= 2 && trimmed_rhs.front() == '{' &&
              trimmed_rhs.back() == '}')) {
            continue;
        }

        if (is_local) {
            existing_local_seed_lines.emplace(lhs, line_index);
            existing_local_seed_rhs.emplace(lhs, rhs);
        } else {
            existing_seed_lines.emplace(lhs, line_index);
        }
    }

    bool changed = false;
    for (std::size_t seed_index = 0; seed_index < seed_order.size(); ++seed_index) {
        const std::string& seed_name = seed_order[seed_index];
        if (existing_seed_lines.contains(seed_name)) {
            continue;
        }

        const auto local_seed_it = existing_local_seed_lines.find(seed_name);
        if (local_seed_it != existing_local_seed_lines.end()) {
            const std::size_t line_index = local_seed_it->second;
            const std::string_view stripped = StripLuaLineComment(lines[line_index]);
            const std::string comment =
                stripped.size() < lines[line_index].size()
                    ? lines[line_index].substr(stripped.size())
                    : std::string();
            const std::string replacement_rhs =
                existing_local_seed_rhs.contains(seed_name) ? existing_local_seed_rhs[seed_name]
                                                            : "{}";
            lines[line_index] =
                line_indent(lines[line_index]) + seed_name + " = " + replacement_rhs + comment;
            existing_seed_lines.emplace(seed_name, line_index);
            existing_local_seed_lines.erase(local_seed_it);
            existing_local_seed_rhs.erase(seed_name);
            changed = true;
            continue;
        }

        std::size_t matched_line = lines.size();
        for (const GlobalEmptyTableCallSeedPattern& pattern : call_seed_patterns) {
            if (pattern.seed_name != seed_name) {
                continue;
            }

            for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
                const std::string_view stripped = StripLuaLineComment(lines[line_index]);
                const std::string comment =
                    stripped.size() < lines[line_index].size()
                        ? lines[line_index].substr(stripped.size())
                        : std::string();
                const std::string_view trimmed = TrimLuaWhitespace(stripped);
                if (trimmed == pattern.placeholder_expression) {
                    lines[line_index] = line_indent(lines[line_index]) + pattern.rendered_expression +
                                        comment;
                    matched_line = line_index;
                    changed = true;
                    break;
                }

                if (const std::size_t nested_offset =
                        std::string(stripped).find(pattern.placeholder_expression);
                    nested_offset != std::string::npos) {
                    std::string rebuilt(stripped);
                    rebuilt.replace(nested_offset,
                                    pattern.placeholder_expression.size(),
                                    pattern.rendered_expression);
                    lines[line_index] = rebuilt + comment;
                    matched_line = line_index;
                    changed = true;
                    break;
                }

                bool is_local = false;
                std::string lhs;
                std::string rhs;
                if (!ParseSingleRenderedAssignment(stripped, &is_local, &lhs, &rhs) ||
                    rhs != pattern.placeholder_expression) {
                    continue;
                }

                const std::string indent = line_indent(lines[line_index]);
                lines[line_index] =
                    indent + (is_local ? "local " : std::string()) + lhs + " = " +
                    pattern.rendered_expression + comment;
                matched_line = line_index;
                changed = true;
                break;
            }

            if (matched_line != lines.size()) {
                break;
            }
        }

        if (matched_line == lines.size()) {
            const std::string member_prefix = seed_name + ".";
            const std::string index_prefix = seed_name + "[";
            for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
                if (line_indent(lines[line_index]) != root_indent) {
                    continue;
                }

                const std::string_view trimmed = trim_line(lines[line_index]);
                if (trimmed.starts_with(member_prefix) || trimmed.starts_with(index_prefix) ||
                    trimmed.starts_with("function " + member_prefix) ||
                    trimmed.starts_with("function " + index_prefix)) {
                    matched_line = line_index;
                    break;
                }
            }
        }

        std::size_t insert_line = lines.size();
        for (std::size_t later_seed_index = seed_index + 1; later_seed_index < seed_order.size();
             ++later_seed_index) {
            const auto existing_it = existing_seed_lines.find(seed_order[later_seed_index]);
            if (existing_it != existing_seed_lines.end()) {
                insert_line = existing_it->second;
                break;
            }
        }
        if (insert_line == lines.size()) {
            for (std::size_t earlier_seed_index = seed_index; earlier_seed_index > 0;
                 --earlier_seed_index) {
                const auto existing_it = existing_seed_lines.find(seed_order[earlier_seed_index - 1]);
                if (existing_it != existing_seed_lines.end()) {
                    insert_line = existing_it->second + 1;
                    break;
                }
            }
        }
        if (insert_line == lines.size() && matched_line != lines.size()) {
            insert_line = matched_line;
        }
        if (insert_line > lines.size()) {
            insert_line = lines.size();
        }

        std::string indent;
        if (insert_line < lines.size()) {
            indent = line_indent(lines[insert_line]);
        } else if (!lines.empty()) {
            indent = line_indent(lines.back());
        }
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(insert_line),
                     indent + seed_name + " = {}");
        changed = true;

        for (auto& [existing_name, existing_line] : existing_seed_lines) {
            if (existing_line >= insert_line) {
                ++existing_line;
            }
        }
        existing_seed_lines[seed_name] = insert_line;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_global_empty_table_call_seeds_restored");
    return true;
}

bool TryRestoreBytecodeExpressionKeyedGlobalTableSeeds(const Lua51Function& function,
                                                       std::string_view chunk_name,
                                                       std::string* source,
                                                       std::string* note,
                                                       std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    struct ExpressionKeyedTableEntry {
        std::string key_expression;
        std::string value_expression;
    };

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        bool body_has_content = false;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            if (!trim_line(lines[line_index]).empty()) {
                body_has_content = true;
                break;
            }
        }
        if (body_has_content) {
            continue;
        }

        const Lua51Function& child_function = function.child_functions[block_index];
        const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(child_function);
        if (instructions.size() < 3) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        std::vector<std::optional<std::string>> tracked_registers(
            std::max<std::size_t>(child_function.max_stack_size, parameter_names.size()));

        std::optional<int> table_reg;
        std::optional<std::string> target_expression;
        std::vector<ExpressionKeyedTableEntry> entries;
        bool invalid_pattern = false;

        for (std::size_t instruction_index = 0; instruction_index < instructions.size();
             ++instruction_index) {
            const Lua51Instruction& instruction = instructions[instruction_index];

            if (instruction.opcode == Lua51Opcode::kNewTable) {
                if (!table_reg.has_value()) {
                    table_reg = instruction.a;
                } else if (instruction.a == *table_reg) {
                    invalid_pattern = true;
                    break;
                }
            } else if (instruction.opcode == Lua51Opcode::kSetTable && table_reg.has_value() &&
                       instruction.a == *table_reg) {
                const std::optional<std::string> key_expression =
                    RenderLua51TrackedRKOperand(child_function,
                                                parameter_names,
                                                tracked_registers,
                                                instruction_index,
                                                instruction.b);
                const std::optional<std::string> value_expression =
                    RenderLua51TrackedRKOperand(child_function,
                                                parameter_names,
                                                tracked_registers,
                                                instruction_index,
                                                instruction.c);
                if (!key_expression.has_value() || !value_expression.has_value()) {
                    invalid_pattern = true;
                    break;
                }
                entries.push_back(ExpressionKeyedTableEntry{
                    .key_expression = *key_expression,
                    .value_expression = *value_expression,
                });
            } else if (instruction.opcode == Lua51Opcode::kSetGlobal && table_reg.has_value() &&
                       instruction.a == *table_reg) {
                if (static_cast<std::size_t>(instruction.bx) >= child_function.constants.size()) {
                    invalid_pattern = true;
                    break;
                }
                const Lua51Constant& target_constant =
                    child_function.constants[static_cast<std::size_t>(instruction.bx)];
                if (target_constant.type != Lua51ConstantType::kString ||
                    !IsValidLuaIdentifier(target_constant.string_value) ||
                    IsLuaKeyword(target_constant.string_value)) {
                    invalid_pattern = true;
                    break;
                }
                target_expression = target_constant.string_value;
            }

            StepLua51TrackedRegisterExpressions(child_function,
                                                parameter_names,
                                                instruction,
                                                instruction_index,
                                                &tracked_registers);
        }

        if (invalid_pattern || !table_reg.has_value() || !target_expression.has_value() ||
            entries.empty()) {
            continue;
        }

        std::vector<std::string> replacement_lines;
        replacement_lines.reserve(entries.size() + 1);
        const std::string body_indent = line_indent(lines[block.begin]) + "  ";
        replacement_lines.push_back(body_indent + *target_expression + " = {}");
        for (const ExpressionKeyedTableEntry& entry : entries) {
            replacement_lines.push_back(body_indent + *target_expression + "[" +
                                        entry.key_expression + "] = " + entry.value_expression);
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(block.begin + 1),
                    lines.begin() + static_cast<std::ptrdiff_t>(function_end));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(block.begin + 1),
                     replacement_lines.begin(),
                     replacement_lines.end());
        changed = true;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_expression_keyed_global_table_seeds_restored");
    return true;
}

bool TryRestoreBytecodeGlobalFieldTableBootstraps(const Lua51Function& function,
                                                  std::string_view chunk_name,
                                                  std::string* source,
                                                  std::string* note,
                                                  std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    struct TableLiteralEntry {
        std::string key_expression;
        std::string value_expression;
    };
    struct GlobalFieldTableBootstrap {
        std::string field_suffix;
        std::vector<TableLiteralEntry> entries;
    };

    const auto render_field_suffix = [](const Lua51Constant& field_constant)
        -> std::optional<std::string> {
        if (field_constant.type == Lua51ConstantType::kString &&
            IsValidLuaIdentifier(field_constant.string_value) &&
            !IsLuaKeyword(field_constant.string_value)) {
            return "." + field_constant.string_value;
        }

        const std::string rendered = RenderLuaConstant(field_constant);
        if (rendered.empty()) {
            return std::nullopt;
        }
        return "[" + rendered + "]";
    };
    const auto render_table_literal_key = [](const Lua51Constant& key_constant)
        -> std::optional<std::string> {
        if (key_constant.type == Lua51ConstantType::kString &&
            IsValidLuaIdentifier(key_constant.string_value) &&
            !IsLuaKeyword(key_constant.string_value)) {
            return key_constant.string_value;
        }

        const std::string rendered = RenderLuaConstant(key_constant);
        if (rendered.empty()) {
            return std::nullopt;
        }
        return "[" + rendered + "]";
    };

    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 4) {
        return false;
    }

    std::string target_name;
    bool preserves_existing_table = false;
    std::size_t cursor = 0;

    if (instructions.size() >= 5) {
        const Lua51Instruction& getglobal = instructions[0];
        const Lua51Instruction& test = instructions[1];
        const Lua51Instruction& jump = instructions[2];
        const Lua51Instruction& newtable = instructions[3];
        const Lua51Instruction& setglobal = instructions[4];
        if (getglobal.opcode == Lua51Opcode::kGetGlobal &&
            test.opcode == Lua51Opcode::kTest && test.a == getglobal.a &&
            jump.opcode == Lua51Opcode::kJmp && jump.sbx == 1 &&
            newtable.opcode == Lua51Opcode::kNewTable &&
            newtable.a == getglobal.a &&
            setglobal.opcode == Lua51Opcode::kSetGlobal &&
            setglobal.a == getglobal.a && getglobal.bx == setglobal.bx &&
            static_cast<std::size_t>(getglobal.bx) < function.constants.size()) {
            const Lua51Constant& target_constant =
                function.constants[static_cast<std::size_t>(getglobal.bx)];
            if (target_constant.type == Lua51ConstantType::kString &&
                IsValidLuaIdentifier(target_constant.string_value) &&
                !IsLuaKeyword(target_constant.string_value)) {
                target_name = target_constant.string_value;
                preserves_existing_table = true;
                cursor = 5;
            }
        }
    }

    std::vector<GlobalFieldTableBootstrap> bootstraps;
    while (cursor < instructions.size()) {
        if (cursor == instructions.size() - 1) {
            const Lua51Instruction& tail = instructions[cursor];
            if (tail.opcode == Lua51Opcode::kReturn) {
                break;
            }
            return false;
        }

        if (cursor + 3 >= instructions.size()) {
            return false;
        }

        const Lua51Instruction& getglobal = instructions[cursor];
        const Lua51Instruction& newtable = instructions[cursor + 1];
        if (getglobal.opcode != Lua51Opcode::kGetGlobal ||
            newtable.opcode != Lua51Opcode::kNewTable ||
            newtable.a == getglobal.a ||
            static_cast<std::size_t>(getglobal.bx) >= function.constants.size()) {
            return false;
        }

        const Lua51Constant& target_constant =
            function.constants[static_cast<std::size_t>(getglobal.bx)];
        if (target_constant.type != Lua51ConstantType::kString ||
            !IsValidLuaIdentifier(target_constant.string_value) ||
            IsLuaKeyword(target_constant.string_value)) {
            return false;
        }
        if (target_name.empty()) {
            target_name = target_constant.string_value;
        } else if (target_name != target_constant.string_value) {
            return false;
        }

        std::vector<TableLiteralEntry> entries;
        std::size_t inner = cursor + 2;
        while (inner < instructions.size()) {
            const Lua51Instruction& field_set = instructions[inner];
            if (field_set.opcode != Lua51Opcode::kSetTable || field_set.a != newtable.a) {
                break;
            }

            const Lua51Constant* key_constant = GetLua51RKConstant(function, field_set.b);
            const Lua51Constant* value_constant = GetLua51RKConstant(function, field_set.c);
            if (key_constant == nullptr || value_constant == nullptr) {
                return false;
            }

            const std::optional<std::string> key_expression =
                render_table_literal_key(*key_constant);
            const std::string value_expression = RenderLuaConstant(*value_constant);
            if (!key_expression.has_value() || value_expression.empty()) {
                return false;
            }

            entries.push_back(TableLiteralEntry{
                .key_expression = *key_expression,
                .value_expression = value_expression,
            });
            ++inner;
        }
        if (entries.empty() || inner >= instructions.size()) {
            return false;
        }

        const Lua51Instruction& attach = instructions[inner];
        if (attach.opcode != Lua51Opcode::kSetTable || attach.a != getglobal.a ||
            attach.c != newtable.a) {
            return false;
        }

        const Lua51Constant* field_constant = GetLua51RKConstant(function, attach.b);
        if (field_constant == nullptr) {
            return false;
        }
        const std::optional<std::string> field_suffix = render_field_suffix(*field_constant);
        if (!field_suffix.has_value()) {
            return false;
        }

        bootstraps.push_back(GlobalFieldTableBootstrap{
            .field_suffix = *field_suffix,
            .entries = std::move(entries),
        });
        cursor = inner + 1;
    }

    if (target_name.empty() || bootstraps.empty()) {
        return false;
    }

    std::vector<std::string> replacement_lines;
    replacement_lines.reserve(bootstraps.size() + 1);
    replacement_lines.push_back(preserves_existing_table ? target_name + " = " + target_name +
                                                             " or {}"
                                                         : target_name + " = {}");
    for (const GlobalFieldTableBootstrap& bootstrap : bootstraps) {
        std::ostringstream literal;
        literal << target_name << bootstrap.field_suffix << " = {";
        for (std::size_t entry_index = 0; entry_index < bootstrap.entries.size(); ++entry_index) {
            if (entry_index != 0) {
                literal << ", ";
            }
            literal << bootstrap.entries[entry_index].key_expression << " = "
                    << bootstrap.entries[entry_index].value_expression;
        }
        literal << "}";
        replacement_lines.push_back(literal.str());
    }

    std::string candidate_source = JoinRenderedLines(replacement_lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_global_field_table_bootstraps_restored");
    return true;
}

bool TryStripSpuriousLeadingLiteralCallPrefixes(std::string_view chunk_name,
                                                std::string* source,
                                                std::string* note,
                                                std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string_view trimmed = trim_line(lines[line_index]);
        if (!trimmed.starts_with("{") || trimmed.find('}') != std::string_view::npos ||
            trimmed.ends_with(',')) {
            continue;
        }

        const std::size_t equals = trimmed.find('=');
        if (equals == std::string_view::npos || equals + 1 >= trimmed.size()) {
            continue;
        }

        const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equals + 1));
        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallExpression(rhs, &callee, &arguments)) {
            continue;
        }

        lines[line_index] = line_indent(lines[line_index]) + std::string(rhs);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "spurious_literal_call_prefixes_stripped");
    return true;
}

bool TryPruneSpuriousDuplicatedLiteralCallStatements(std::string_view chunk_name,
                                                     std::string* source,
                                                     std::string* note,
                                                     std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_top_level_nonempty =
        [&](const std::vector<std::string>& rendered_lines,
            const std::vector<RenderedFunctionBlock>& rendered_blocks,
            std::size_t start) {
            std::size_t block_index = 0;
            while (start < rendered_lines.size()) {
                while (block_index < rendered_blocks.size() &&
                       rendered_blocks[block_index].end < start) {
                    ++block_index;
                }
                if (block_index < rendered_blocks.size() &&
                    rendered_blocks[block_index].begin <= start &&
                    start <= rendered_blocks[block_index].end) {
                    start = rendered_blocks[block_index].end + 1;
                    continue;
                }
                if (!trim_line(rendered_lines[start]).empty()) {
                    return start;
                }
                ++start;
            }
            return rendered_lines.size();
        };
    const auto parse_any_callee_call_statement =
        [&](std::string_view line, std::string* callee_out) {
            if (callee_out == nullptr) {
                return false;
            }

            line = TrimLuaWhitespace(StripLuaLineComment(line));
            if (line.empty() || !line.ends_with(')')) {
                return false;
            }

            const std::size_t open = line.find('(');
            const std::size_t close = line.rfind(')');
            if (open == std::string_view::npos || close == std::string_view::npos || open == 0 ||
                open >= close || close + 1 != line.size()) {
                return false;
            }

            const std::string_view callee_view = TrimLuaWhitespace(line.substr(0, open));
            if (callee_view.empty()) {
                return false;
            }
            *callee_out = std::string(callee_view);
            return true;
        };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    bool changed = false;
    bool pass_changed = false;
    bool guard_triggered = false;
    std::size_t pass_count = 0;
    constexpr std::size_t kMaxRewritePasses = 128;
    std::unordered_set<std::size_t> seen_pass_states;
    seen_pass_states.reserve(kMaxRewritePasses);
    do {
        if (pass_count >= kMaxRewritePasses ||
            !seen_pass_states.insert(HashRenderedLines(lines)).second) {
            guard_triggered = true;
            break;
        }
        ++pass_count;
        pass_changed = false;
        const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
        for (std::size_t line_index = 0; line_index < lines.size() && !pass_changed; ++line_index) {
            const std::size_t call_line = next_top_level_nonempty(lines, blocks, line_index + 1);
            if (call_line >= lines.size()) {
                continue;
            }

            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
                assign_is_local) {
                continue;
            }

            const std::string normalized_rhs =
                NormalizeRenderedTrivialParenthesizedLiterals(assign_rhs);
            const std::string_view trimmed_rhs = TrimLuaWhitespace(normalized_rhs);
            if (trimmed_rhs.size() < 2 || trimmed_rhs.front() != '{' || trimmed_rhs.back() != '}') {
                continue;
            }

            std::string call_callee;
            if (!parse_any_callee_call_statement(lines[call_line], &call_callee) ||
                NormalizeRenderedTrivialParenthesizedLiterals(call_callee) != normalized_rhs) {
                continue;
            }

            std::vector<std::string> candidate_lines = lines;
            candidate_lines.erase(candidate_lines.begin() +
                                  static_cast<std::ptrdiff_t>(call_line));
            NormalizeRenderedLuaWhitespace(&candidate_lines);
            std::string candidate_source = JoinRenderedLines(candidate_lines);
            if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
                continue;
            }

            lines = std::move(candidate_lines);
            changed = true;
            pass_changed = true;
        }
    } while (pass_changed);
    if (guard_triggered) {
        AppendCandidateNote(note, note_prefix, "duplicated_literal_call_loop_guarded");
    }

    if (!changed) {
        return false;
    }

    *source = JoinRenderedLines(lines);
    AppendCandidateNote(note, note_prefix, "spurious_literal_call_statements_pruned");
    return true;
}

bool TryFoldTopLevelDuplicatedLiteralTempFieldSeeds(std::string_view chunk_name,
                                                    std::string* source,
                                                    std::string* note,
                                                    std::string_view note_prefix) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto next_top_level_nonempty =
        [&](const std::vector<std::string>& rendered_lines,
            const std::vector<RenderedFunctionBlock>& rendered_blocks,
            std::size_t start) {
            std::size_t block_index = 0;
            while (start < rendered_lines.size()) {
                while (block_index < rendered_blocks.size() &&
                       rendered_blocks[block_index].end < start) {
                    ++block_index;
                }
                if (block_index < rendered_blocks.size() &&
                    rendered_blocks[block_index].begin <= start &&
                    start <= rendered_blocks[block_index].end) {
                    start = rendered_blocks[block_index].end + 1;
                    continue;
                }
                if (!trim_line(rendered_lines[start]).empty()) {
                    return start;
                }
                ++start;
            }
            return rendered_lines.size();
        };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    bool changed = false;
    bool pass_changed = false;
    bool guard_triggered = false;
    std::size_t pass_count = 0;
    constexpr std::size_t kMaxRewritePasses = 128;
    std::unordered_set<std::size_t> seen_pass_states;
    seen_pass_states.reserve(kMaxRewritePasses);
    do {
        if (pass_count >= kMaxRewritePasses ||
            !seen_pass_states.insert(HashRenderedLines(lines)).second) {
            guard_triggered = true;
            break;
        }
        ++pass_count;
        pass_changed = false;
        const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
        for (std::size_t line_index = 0; line_index < lines.size() && !pass_changed; ++line_index) {
            const std::size_t seed_line = next_top_level_nonempty(lines, blocks, line_index + 1);
            if (seed_line >= lines.size()) {
                continue;
            }

            bool target_is_local = false;
            std::string target_lhs;
            std::string target_rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index],
                                               &target_is_local,
                                               &target_lhs,
                                               &target_rhs) ||
                target_is_local) {
                continue;
            }

            const std::string normalized_target_rhs =
                NormalizeRenderedTrivialParenthesizedLiterals(target_rhs);
            const std::string_view trimmed_target_rhs = TrimLuaWhitespace(normalized_target_rhs);
            if (trimmed_target_rhs.size() < 2 || trimmed_target_rhs.front() != '{' ||
                trimmed_target_rhs.back() != '}') {
                continue;
            }

            const std::vector<std::string_view> statements =
                SplitRenderedTopLevelSemicolonStatements(lines[seed_line]);
            if (statements.size() != 2) {
                continue;
            }

            bool temp_is_local = false;
            std::string temp_lhs;
            std::string temp_rhs;
            if (!ParseSingleRenderedAssignment(statements[0],
                                               &temp_is_local,
                                               &temp_lhs,
                                               &temp_rhs) ||
                !IsRenderTemporaryName(temp_lhs) ||
                NormalizeRenderedTrivialParenthesizedLiterals(temp_rhs) != normalized_target_rhs) {
                continue;
            }

            std::string field_key;
            std::string field_rhs_expression;
            bool rhs_is_function = false;
            int array_index = -1;
            if (!ParseRenderedTableFieldAssignment(statements[1],
                                                  temp_lhs,
                                                  &field_key,
                                                  &field_rhs_expression,
                                                  &rhs_is_function,
                                                  &array_index) ||
                rhs_is_function) {
                continue;
            }

            std::string rewritten_lhs = target_lhs;
            if (array_index > 0 || (!field_key.empty() && field_key.front() == '[')) {
                rewritten_lhs += field_key;
            } else {
                rewritten_lhs += "." + field_key;
            }

            std::vector<std::string> candidate_lines = lines;
            candidate_lines[seed_line] =
                line_indent(lines[seed_line]) + rewritten_lhs + " = " + field_rhs_expression;
            NormalizeRenderedLuaWhitespace(&candidate_lines);
            std::string candidate_source = JoinRenderedLines(candidate_lines);
            if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
                continue;
            }

            lines = std::move(candidate_lines);
            changed = true;
            pass_changed = true;
        }
    } while (pass_changed);
    if (guard_triggered) {
        AppendCandidateNote(note, note_prefix, "literal_temp_field_seed_loop_guarded");
    }

    if (!changed) {
        return false;
    }

    *source = JoinRenderedLines(lines);
    AppendCandidateNote(note, note_prefix, "top_level_duplicated_literal_temp_field_seeds_folded");
    return true;
}

bool TryRestoreBytecodeTailLoopBreaks(const Lua51Function& function,
                                      std::string_view chunk_name,
                                      std::string* source,
                                      std::string* note,
                                      std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto is_branch_header = [&](std::string_view line) {
        const std::string_view trimmed = trim_line(line);
        return (trimmed.starts_with("if ") || trimmed.starts_with("elseif ")) &&
               trimmed.ends_with(" then");
    };
    const auto next_nonempty_line = [&](const std::vector<std::string>& rendered_lines,
                                        std::size_t start,
                                        std::size_t end_exclusive) {
        for (std::size_t probe = start;
             probe < rendered_lines.size() && probe < end_exclusive;
             ++probe) {
            if (!trim_line(rendered_lines[probe]).empty()) {
                return probe;
            }
        }
        return end_exclusive;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<std::string> break_conditions =
            CollectRenderedLuaTailLoopBreakExpressions(function.child_functions[block_index],
                                                       parameter_names);
        if (break_conditions.empty()) {
            continue;
        }

        std::size_t yield_line = function_end + 1;
        bool saw_assert_false = false;
        std::vector<std::size_t> empty_branch_headers;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            const std::string_view trimmed = trim_line(lines[line_index]);
            if (trimmed == "assert(false)") {
                saw_assert_false = true;
            }
            if (yield_line == function_end + 1 &&
                trimmed.find("coroutine.yield(") != std::string_view::npos) {
                yield_line = line_index;
                break;
            }
            if (!is_branch_header(trimmed)) {
                continue;
            }

            const std::size_t next_line =
                next_nonempty_line(lines, line_index + 1, function_end);
            if (next_line >= function_end) {
                continue;
            }

            const std::string_view next_trimmed = trim_line(lines[next_line]);
            if (next_trimmed.starts_with("elseif ") || next_trimmed == "else") {
                empty_branch_headers.push_back(line_index);
            }
        }

        if (!saw_assert_false || yield_line > function_end ||
            empty_branch_headers.size() != break_conditions.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        for (std::size_t reverse_index = break_conditions.size(); reverse_index > 0;
             --reverse_index) {
            const std::size_t condition_index = reverse_index - 1;
            const std::size_t header_line = empty_branch_headers[condition_index];
            const std::size_t insert_index =
                next_nonempty_line(candidate_lines, header_line + 1, function_end + 1);
            if (insert_index > function_end + 1) {
                continue;
            }

            const std::string indent = line_indent(candidate_lines[header_line]);
            std::vector<std::string> inserted_lines;
            inserted_lines.push_back(indent + "  if " + break_conditions[condition_index] + " then");
            inserted_lines.push_back(indent + "    break");
            inserted_lines.push_back(indent + "  end");
            candidate_lines.insert(candidate_lines.begin() +
                                       static_cast<std::ptrdiff_t>(insert_index),
                                   inserted_lines.begin(),
                                   inserted_lines.end());
        }

        NormalizeRenderedLuaWhitespace(&candidate_lines);
        std::string candidate_source = JoinRenderedLines(candidate_lines);
        if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
            continue;
        }

        lines = std::move(candidate_lines);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_tail_loop_breaks_restored");
    return true;
}

bool TryRestoreBytecodeDirectTailBreakGuards(const Lua51Function& function,
                                             std::string_view chunk_name,
                                             std::string* source,
                                             std::string* note,
                                             std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string_view::npos) {
            return {};
        }
        return std::string(line.substr(0, first_non_space));
    };
    const auto normalize_expression = [](std::string_view expression) {
        return NormalizeRenderedTrivialParenthesizedLiterals(
            std::string(TrimLuaWhitespace(expression)));
    };
    const auto split_top_level_boolean_chain =
        [](std::string_view expression,
           std::string_view keyword,
           std::vector<std::string>* parts_out) -> bool {
            if (parts_out == nullptr || keyword.empty()) {
                return false;
            }

            const auto is_identifier_char = [](char ch) {
                const unsigned char value = static_cast<unsigned char>(ch);
                return std::isalnum(value) != 0 || ch == '_';
            };

            bool in_single_quote = false;
            bool in_double_quote = false;
            bool escaped = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t segment_start = 0;
            bool found = false;
            std::vector<std::string> parts;
            for (std::size_t index = 0; index < expression.size(); ++index) {
                const char ch = expression[index];
                if (escaped) {
                    escaped = false;
                    continue;
                }
                if ((in_single_quote || in_double_quote) && ch == '\\') {
                    escaped = true;
                    continue;
                }
                if (!in_double_quote && ch == '\'') {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (!in_single_quote && ch == '"') {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }

                switch (ch) {
                    case '(':
                        ++paren_depth;
                        continue;
                    case ')':
                        if (paren_depth > 0) {
                            --paren_depth;
                        }
                        continue;
                    case '{':
                        ++brace_depth;
                        continue;
                    case '}':
                        if (brace_depth > 0) {
                            --brace_depth;
                        }
                        continue;
                    case '[':
                        ++bracket_depth;
                        continue;
                    case ']':
                        if (bracket_depth > 0) {
                            --bracket_depth;
                        }
                        continue;
                    default:
                        break;
                }

                if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0 ||
                    index + keyword.size() > expression.size() ||
                    expression.substr(index, keyword.size()) != keyword) {
                    continue;
                }

                const bool left_boundary =
                    index == 0 || !is_identifier_char(expression[index - 1]);
                const bool right_boundary =
                    index + keyword.size() >= expression.size() ||
                    !is_identifier_char(expression[index + keyword.size()]);
                if (!left_boundary || !right_boundary) {
                    continue;
                }

                const std::string part =
                    std::string(TrimLuaWhitespace(expression.substr(segment_start,
                                                                     index - segment_start)));
                if (part.empty()) {
                    return false;
                }
                parts.push_back(part);
                segment_start = index + keyword.size();
                index += keyword.size() - 1;
                found = true;
            }

            if (!found) {
                return false;
            }

            const std::string tail =
                std::string(TrimLuaWhitespace(expression.substr(segment_start)));
            if (tail.empty()) {
                return false;
            }
            parts.push_back(tail);
            *parts_out = std::move(parts);
            return true;
        };
    const auto expression_mentions_condition =
        [&](std::string_view rendered_expression,
            std::string_view target_condition,
            const auto& self) -> bool {
            const std::string normalized_expression =
                normalize_expression(rendered_expression);
            if (normalized_expression == target_condition) {
                return true;
            }

            std::vector<std::string> parts;
            if (split_top_level_boolean_chain(normalized_expression, "or", &parts) &&
                parts.size() > 1) {
                for (const std::string& part : parts) {
                    if (self(part, target_condition, self)) {
                        return true;
                    }
                }
            }

            parts.clear();
            if (split_top_level_boolean_chain(normalized_expression, "and", &parts) &&
                parts.size() > 1) {
                for (const std::string& part : parts) {
                    if (self(part, target_condition, self)) {
                        return true;
                    }
                }
            }

            return false;
        };
    const auto normalize_positive_condition = [&](std::string_view expression)
        -> std::optional<std::string> {
        std::string normalized = normalize_expression(expression);
        if (normalized.starts_with("not ")) {
            normalized = std::string(TrimLuaWhitespace(normalized.substr(4)));
            if (normalized.size() >= 2 && normalized.front() == '(' &&
                normalized.back() == ')') {
                normalized = normalize_expression(
                    normalized.substr(1, normalized.size() - 2));
            }
            if (!normalized.empty()) {
                return normalized;
            }
        }
        return std::nullopt;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const auto positive_or_loop_subsumes_missing_break_guards =
        [&](std::size_t block_begin,
            std::size_t yield_line,
            const std::vector<std::string>& missing_conditions) -> bool {
            if (missing_conditions.size() < 2) {
                return false;
            }

            std::vector<std::string> positive_break_conditions;
            positive_break_conditions.reserve(missing_conditions.size());
            for (const std::string& missing_condition : missing_conditions) {
                std::optional<std::string> positive =
                    normalize_positive_condition(missing_condition);
                if (!positive.has_value() || positive->empty()) {
                    return false;
                }
                positive_break_conditions.push_back(std::move(*positive));
            }

            for (std::size_t line_index = block_begin + 1; line_index < yield_line;
                 ++line_index) {
                std::string prefix;
                std::string condition;
                std::string suffix;
                if (!TryParseRenderedConditionHeader(
                        lines[line_index], &prefix, &condition, &suffix) ||
                    suffix != " do") {
                    continue;
                }

                std::vector<std::string> loop_parts;
                if (!split_top_level_boolean_chain(
                        normalize_expression(condition), "or", &loop_parts) ||
                    loop_parts.size() != positive_break_conditions.size()) {
                    continue;
                }

                std::vector<bool> matched(loop_parts.size(), false);
                bool all_matched = true;
                for (const std::string& positive_break_condition :
                     positive_break_conditions) {
                    bool found = false;
                    for (std::size_t part_index = 0; part_index < loop_parts.size();
                         ++part_index) {
                        if (matched[part_index]) {
                            continue;
                        }
                        if (normalize_expression(loop_parts[part_index]) ==
                            positive_break_condition) {
                            matched[part_index] = true;
                            found = true;
                            break;
                        }
                    }
                    if (!found) {
                        all_matched = false;
                        break;
                    }
                }
                if (all_matched) {
                    return true;
                }
            }

            return false;
        };
    const auto positive_or_loop_subsumes_missing_break_guard =
        [&](std::size_t block_begin,
            std::size_t yield_line,
            const std::string& missing_condition) -> bool {
            std::optional<std::string> positive =
                normalize_positive_condition(missing_condition);
            if (!positive.has_value() || positive->empty()) {
                return false;
            }

            for (std::size_t line_index = block_begin + 1; line_index < yield_line;
                 ++line_index) {
                std::string prefix;
                std::string condition;
                std::string suffix;
                if (!TryParseRenderedConditionHeader(
                        lines[line_index], &prefix, &condition, &suffix) ||
                    suffix != " do") {
                    continue;
                }

                std::vector<std::string> loop_parts;
                if (!split_top_level_boolean_chain(
                        normalize_expression(condition), "or", &loop_parts) ||
                    loop_parts.size() < 2) {
                    continue;
                }

                for (const std::string& loop_part : loop_parts) {
                    if (normalize_expression(loop_part) == *positive) {
                        return true;
                    }
                }
            }

            return false;
        };
    const auto count_trailing_direct_break_guards =
        [&](std::size_t yield_line,
            const std::string& normalized_break_condition) {
            int count = 0;
            std::size_t line_index = yield_line;
            while (line_index >= 3) {
                const std::size_t header_line = line_index - 3;
                const std::size_t break_line = line_index - 2;
                const std::size_t end_line = line_index - 1;
                if (trim_line(lines[break_line]) != "break" ||
                    trim_line(lines[end_line]) != "end") {
                    break;
                }
                std::string prefix;
                std::string condition;
                std::string suffix;
                if (!TryParseRenderedConditionHeader(
                        lines[header_line], &prefix, &condition, &suffix) ||
                    suffix != " then") {
                    break;
                }
                if (normalize_expression(condition) == normalized_break_condition) {
                    ++count;
                }
                line_index -= 3;
            }
            return count;
        };
    const auto count_direct_break_guard_triplets =
        [&](std::size_t block_begin,
            std::size_t yield_line,
            const std::string& normalized_break_condition) {
            int count = 0;
            for (std::size_t header_line = block_begin + 1; header_line + 2 < yield_line;
                 ++header_line) {
                if (trim_line(lines[header_line + 1]) != "break" ||
                    trim_line(lines[header_line + 2]) != "end") {
                    continue;
                }
                std::string prefix;
                std::string condition;
                std::string suffix;
                if (!TryParseRenderedConditionHeader(
                        lines[header_line], &prefix, &condition, &suffix) ||
                    suffix != " then" ||
                    normalize_expression(condition) != normalized_break_condition) {
                    continue;
                }
                ++count;
            }
            return count;
        };
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<std::string> break_conditions =
            CollectRenderedLuaTailLoopBreakExpressions(function.child_functions[block_index],
                                                       parameter_names);
        if (break_conditions.empty()) {
            continue;
        }
        const std::optional<std::string> direct_loop_condition =
            CollectRenderedLuaSimpleYieldLoopCondition(function.child_functions[block_index],
                                                       parameter_names);

        std::size_t yield_line = function_end + 1;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            if (trim_line(lines[line_index]).find("coroutine.yield(") != std::string_view::npos) {
                yield_line = line_index;
                break;
            }
        }
        if (yield_line > function_end) {
            continue;
        }

        std::unordered_map<std::string, int> required_condition_counts;
        required_condition_counts.reserve(break_conditions.size());
        std::vector<std::string> normalized_break_conditions;
        normalized_break_conditions.reserve(break_conditions.size());
        for (const std::string& break_condition : break_conditions) {
            std::string normalized_break_condition =
                normalize_expression(break_condition);
            normalized_break_conditions.push_back(normalized_break_condition);
            ++required_condition_counts[normalized_break_condition];
        }

        std::unordered_map<std::string, int> rendered_condition_counts;
        rendered_condition_counts.reserve(required_condition_counts.size());
        for (const auto& [normalized_break_condition, required_count] :
             required_condition_counts) {
            int mention_count = 0;
            for (std::size_t line_index = block.begin + 1; line_index < yield_line;
                 ++line_index) {
                std::string prefix;
                std::string condition;
                std::string suffix;
                if (!TryParseRenderedConditionHeader(
                        lines[line_index], &prefix, &condition, &suffix)) {
                    continue;
                }
                if (expression_mentions_condition(condition,
                                                  normalized_break_condition,
                                                  expression_mentions_condition)) {
                    ++mention_count;
                }
            }
            mention_count = std::max(
                mention_count,
                count_trailing_direct_break_guards(yield_line,
                                                   normalized_break_condition));
            mention_count = std::max(
                mention_count,
                count_direct_break_guard_triplets(block.begin,
                                                  yield_line,
                                                  normalized_break_condition));
            rendered_condition_counts.emplace(normalized_break_condition, mention_count);
        }

        std::vector<std::string> missing_conditions;
        missing_conditions.reserve(break_conditions.size());
        for (std::size_t index = 0; index < break_conditions.size(); ++index) {
            const std::string& break_condition = break_conditions[index];
            const std::string& normalized_break_condition =
                normalized_break_conditions[index];
            int& rendered_count = rendered_condition_counts[normalized_break_condition];
            if (rendered_count > 0) {
                --rendered_count;
                continue;
            }
            if (positive_or_loop_subsumes_missing_break_guard(
                    block.begin, yield_line, break_condition)) {
                continue;
            }
            missing_conditions.push_back(break_condition);
        }
        if (missing_conditions.empty()) {
            continue;
        }
        if (positive_or_loop_subsumes_missing_break_guards(
                block.begin, yield_line, missing_conditions)) {
            continue;
        }

        if (missing_conditions.size() == 1 && direct_loop_condition.has_value() &&
            normalize_expression(*direct_loop_condition) ==
                normalize_expression(missing_conditions.front())) {
            std::size_t while_line = yield_line;
            while (while_line > block.begin + 1) {
                --while_line;
                if (!trim_line(lines[while_line]).empty()) {
                    break;
                }
            }
            const std::size_t loop_end = function_end + 1;
            std::size_t end_line = function_end + 1;
            for (std::size_t probe = yield_line + 1; probe < loop_end; ++probe) {
                if (!trim_line(lines[probe]).empty()) {
                    end_line = probe;
                    break;
                }
            }

            bool bare_yield_loop = while_line > block.begin &&
                                   end_line < loop_end &&
                                   trim_line(lines[while_line]) == "while true do" &&
                                   trim_line(lines[end_line]) == "end";
            if (bare_yield_loop) {
                for (std::size_t probe = while_line + 1; probe < end_line; ++probe) {
                    if (probe == yield_line) {
                        continue;
                    }
                    if (!trim_line(lines[probe]).empty()) {
                        bare_yield_loop = false;
                        break;
                    }
                }
            }

            if (bare_yield_loop) {
                lines[while_line] =
                    line_indent(lines[while_line]) + "while " + *direct_loop_condition + " do";
                changed = true;
                continue;
            }
        }

        const std::string indent = line_indent(lines[yield_line]);
        std::vector<std::string> inserted_lines;
        for (const std::string& break_condition : missing_conditions) {
            inserted_lines.push_back(indent + "if " + break_condition + " then");
            inserted_lines.push_back(indent + "  break");
            inserted_lines.push_back(indent + "end");
        }
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(yield_line),
                     inserted_lines.begin(),
                     inserted_lines.end());
        changed = true;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_direct_tail_break_guards_restored");
    return true;
}

std::vector<std::string> CollectRenderedLuaEmptyThenTailBreakExpressions(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<std::string> expressions;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 5) {
        return expressions;
    }

    const std::optional<std::size_t> yield_block_start =
        FindUniqueLua51CoroutineYieldBlockStart(function);
    if (!yield_block_start.has_value()) {
        return expressions;
    }

    std::size_t loop_body_start = 0;
    for (std::size_t index = 0; index < *yield_block_start; ++index) {
        const Lua51Instruction& instruction = instructions[index];
        if (instruction.opcode != Lua51Opcode::kJmp || instruction.sbx >= 0) {
            continue;
        }
        const long long jump_target =
            static_cast<long long>(index) + 1 + static_cast<long long>(instruction.sbx);
        if (jump_target >= 0 && static_cast<std::size_t>(jump_target) < *yield_block_start) {
            loop_body_start =
                std::max(loop_body_start, static_cast<std::size_t>(jump_target));
        }
    }

    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    for (std::size_t index = 0; index < instructions.size(); ++index) {
        if (index < loop_body_start) {
            StepLua51TrackedRegisterExpressions(function,
                                                parameter_names,
                                                instructions[index],
                                                index,
                                                &tracked_registers);
            continue;
        }

        if (index + 2 < *yield_block_start &&
            (instructions[index].opcode == Lua51Opcode::kLt ||
             instructions[index].opcode == Lua51Opcode::kLe ||
             instructions[index].opcode == Lua51Opcode::kEq) &&
            instructions[index + 1].opcode == Lua51Opcode::kJmp &&
            instructions[index + 2].opcode == Lua51Opcode::kJmp &&
            instructions[index + 1].sbx > 0 && instructions[index + 2].sbx > 0) {
            const long long else_target =
                static_cast<long long>(index + 1) + 1 +
                static_cast<long long>(instructions[index + 1].sbx);
            const long long break_target =
                static_cast<long long>(index + 2) + 1 +
                static_cast<long long>(instructions[index + 2].sbx);
            if (else_target >= 0 && break_target >= 0 &&
                static_cast<std::size_t>(else_target) > index + 2 &&
                static_cast<std::size_t>(else_target) < *yield_block_start &&
                static_cast<std::size_t>(break_target) >= *yield_block_start) {
                const std::optional<std::string> lhs =
                    RenderLua51TrackedRKOperand(function,
                                                parameter_names,
                                                tracked_registers,
                                                index,
                                                instructions[index].b);
                const std::optional<std::string> rhs =
                    RenderLua51TrackedRKOperand(function,
                                                parameter_names,
                                                tracked_registers,
                                                index,
                                                instructions[index].c);
                if (lhs.has_value() && rhs.has_value()) {
                    std::string comparator;
                    switch (instructions[index].opcode) {
                        case Lua51Opcode::kLt:
                            comparator = "<";
                            break;
                        case Lua51Opcode::kLe:
                            comparator = "<=";
                            break;
                        case Lua51Opcode::kEq:
                            comparator = "==";
                            break;
                        default:
                            break;
                    }
                    if (!comparator.empty()) {
                        std::string expression = *lhs + " " + comparator + " " + *rhs;
                        if (expressions.empty() || expressions.back() != expression) {
                            expressions.push_back(std::move(expression));
                        }
                    }
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[index],
                                            index,
                                            &tracked_registers);
    }

    return expressions;
}

std::size_t CountLua51EmptyThenTailBreakPatterns(const Lua51Function& function) {
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 5) {
        return 0;
    }

    const std::optional<std::size_t> yield_block_start =
        FindUniqueLua51CoroutineYieldBlockStart(function);
    if (!yield_block_start.has_value()) {
        return 0;
    }

    std::size_t loop_body_start = 0;
    for (std::size_t index = 0; index < *yield_block_start; ++index) {
        const Lua51Instruction& instruction = instructions[index];
        if (instruction.opcode != Lua51Opcode::kJmp || instruction.sbx >= 0) {
            continue;
        }
        const long long jump_target =
            static_cast<long long>(index) + 1 + static_cast<long long>(instruction.sbx);
        if (jump_target >= 0 && static_cast<std::size_t>(jump_target) < *yield_block_start) {
            loop_body_start =
                std::max(loop_body_start, static_cast<std::size_t>(jump_target));
        }
    }

    std::size_t pattern_count = 0;
    for (std::size_t index = loop_body_start; index + 2 < *yield_block_start; ++index) {
        if ((instructions[index].opcode != Lua51Opcode::kLt &&
             instructions[index].opcode != Lua51Opcode::kLe &&
             instructions[index].opcode != Lua51Opcode::kEq) ||
            instructions[index + 1].opcode != Lua51Opcode::kJmp ||
            instructions[index + 2].opcode != Lua51Opcode::kJmp ||
            instructions[index + 1].sbx <= 0 || instructions[index + 2].sbx <= 0) {
            continue;
        }

        const long long else_target =
            static_cast<long long>(index + 1) + 1 +
            static_cast<long long>(instructions[index + 1].sbx);
        const long long break_target =
            static_cast<long long>(index + 2) + 1 +
            static_cast<long long>(instructions[index + 2].sbx);
        if (else_target >= 0 && break_target >= 0 &&
            static_cast<std::size_t>(else_target) > index + 2 &&
            static_cast<std::size_t>(else_target) < *yield_block_start &&
            static_cast<std::size_t>(break_target) >= *yield_block_start) {
            ++pattern_count;
        }
    }

    return pattern_count;
}

std::vector<std::string> CollectRenderedLuaEmptyElseTailBreakExpressions(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<std::string> expressions;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 6) {
        return expressions;
    }

    const std::optional<std::size_t> yield_block_start =
        FindUniqueLua51CoroutineYieldBlockStart(function);
    if (!yield_block_start.has_value()) {
        return expressions;
    }

    std::size_t loop_body_start = 0;
    for (std::size_t index = 0; index < *yield_block_start; ++index) {
        const Lua51Instruction& instruction = instructions[index];
        if (instruction.opcode != Lua51Opcode::kJmp || instruction.sbx >= 0) {
            continue;
        }
        const long long jump_target =
            static_cast<long long>(index) + 1 + static_cast<long long>(instruction.sbx);
        if (jump_target >= 0 && static_cast<std::size_t>(jump_target) < *yield_block_start) {
            loop_body_start =
                std::max(loop_body_start, static_cast<std::size_t>(jump_target));
        }
    }

    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    for (std::size_t index = 0; index < instructions.size(); ++index) {
        if (index < loop_body_start) {
            StepLua51TrackedRegisterExpressions(function,
                                                parameter_names,
                                                instructions[index],
                                                index,
                                                &tracked_registers);
            continue;
        }

        if (index + 3 < instructions.size() &&
            index + 3 < *yield_block_start &&
            (instructions[index].opcode == Lua51Opcode::kLt ||
             instructions[index].opcode == Lua51Opcode::kLe ||
             instructions[index].opcode == Lua51Opcode::kEq) &&
            instructions[index + 1].opcode == Lua51Opcode::kJmp &&
            instructions[index + 2].opcode == Lua51Opcode::kJmp &&
            instructions[index + 3].opcode == Lua51Opcode::kJmp &&
            instructions[index + 1].sbx > 0 && instructions[index + 2].sbx > 0 &&
            instructions[index + 3].sbx == 0) {
            const long long else_target =
                static_cast<long long>(index + 1) + 1 +
                static_cast<long long>(instructions[index + 1].sbx);
            const long long break_target =
                static_cast<long long>(index + 2) + 1 +
                static_cast<long long>(instructions[index + 2].sbx);
            const long long noop_target =
                static_cast<long long>(index + 3) + 1 +
                static_cast<long long>(instructions[index + 3].sbx);
            if (else_target == static_cast<long long>(*yield_block_start) &&
                noop_target == static_cast<long long>(*yield_block_start) &&
                break_target >= static_cast<long long>(*yield_block_start)) {
                const std::optional<std::string> lhs =
                    RenderLua51TrackedRKOperand(function,
                                                parameter_names,
                                                tracked_registers,
                                                index,
                                                instructions[index].b);
                const std::optional<std::string> rhs =
                    RenderLua51TrackedRKOperand(function,
                                                parameter_names,
                                                tracked_registers,
                                                index,
                                                instructions[index].c);
                if (lhs.has_value() && rhs.has_value()) {
                    std::string comparator;
                    switch (instructions[index].opcode) {
                        case Lua51Opcode::kLt:
                            comparator = "<";
                            break;
                        case Lua51Opcode::kLe:
                            comparator = "<=";
                            break;
                        case Lua51Opcode::kEq:
                            comparator = "==";
                            break;
                        default:
                            break;
                    }
                    if (!comparator.empty()) {
                        std::string expression = *lhs + " " + comparator + " " + *rhs;
                        if (expressions.empty() || expressions.back() != expression) {
                            expressions.push_back(std::move(expression));
                        }
                    }
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[index],
                                            index,
                                            &tracked_registers);
    }

    return expressions;
}

std::size_t CountLua51EmptyElseTailBreakPatterns(const Lua51Function& function) {
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 6) {
        return 0;
    }

    const std::optional<std::size_t> yield_block_start =
        FindUniqueLua51CoroutineYieldBlockStart(function);
    if (!yield_block_start.has_value()) {
        return 0;
    }

    std::size_t loop_body_start = 0;
    for (std::size_t index = 0; index < *yield_block_start; ++index) {
        const Lua51Instruction& instruction = instructions[index];
        if (instruction.opcode != Lua51Opcode::kJmp || instruction.sbx >= 0) {
            continue;
        }
        const long long jump_target =
            static_cast<long long>(index) + 1 + static_cast<long long>(instruction.sbx);
        if (jump_target >= 0 && static_cast<std::size_t>(jump_target) < *yield_block_start) {
            loop_body_start =
                std::max(loop_body_start, static_cast<std::size_t>(jump_target));
        }
    }

    std::size_t pattern_count = 0;
    for (std::size_t index = loop_body_start;
         index + 3 < instructions.size() && index + 3 < *yield_block_start;
         ++index) {
        if ((instructions[index].opcode != Lua51Opcode::kLt &&
             instructions[index].opcode != Lua51Opcode::kLe &&
             instructions[index].opcode != Lua51Opcode::kEq) ||
            instructions[index + 1].opcode != Lua51Opcode::kJmp ||
            instructions[index + 2].opcode != Lua51Opcode::kJmp ||
            instructions[index + 3].opcode != Lua51Opcode::kJmp ||
            instructions[index + 1].sbx <= 0 || instructions[index + 2].sbx <= 0 ||
            instructions[index + 3].sbx != 0) {
            continue;
        }

        const long long else_target =
            static_cast<long long>(index + 1) + 1 +
            static_cast<long long>(instructions[index + 1].sbx);
        const long long break_target =
            static_cast<long long>(index + 2) + 1 +
            static_cast<long long>(instructions[index + 2].sbx);
        const long long noop_target =
            static_cast<long long>(index + 3) + 1 +
            static_cast<long long>(instructions[index + 3].sbx);
        if (else_target == static_cast<long long>(*yield_block_start) &&
            noop_target == static_cast<long long>(*yield_block_start) &&
            break_target >= static_cast<long long>(*yield_block_start)) {
            ++pattern_count;
        }
    }

    return pattern_count;
}

std::optional<std::string> InvertRenderedSimpleCondition(std::string_view condition) {
    const std::string trimmed = std::string(TrimLuaWhitespace(condition));
    const std::array<std::pair<std::string_view, std::string_view>, 6> comparators = {{
        {" <= ", " > "},
        {" >= ", " < "},
        {" ~= ", " == "},
        {" < ", " >= "},
        {" > ", " <= "},
        {" == ", " ~= "},
    }};

    for (const auto& [needle, inverse] : comparators) {
        const std::size_t position = trimmed.find(needle);
        if (position == std::string::npos) {
            continue;
        }
        const std::string lhs = std::string(TrimLuaWhitespace(trimmed.substr(0, position)));
        const std::string rhs = std::string(
            TrimLuaWhitespace(trimmed.substr(position + needle.size())));
        if (lhs.empty() || rhs.empty()) {
            return std::nullopt;
        }
        return lhs + std::string(inverse) + rhs;
    }

    return std::nullopt;
}

std::vector<std::string> CollectRenderedLuaTailElseBreakGuardExpressions(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<std::string> expressions;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 3) {
        return expressions;
    }

    const std::optional<std::size_t> yield_block_start =
        FindUniqueLua51CoroutineYieldBlockStart(function);
    if (!yield_block_start.has_value()) {
        return expressions;
    }

    std::size_t loop_body_start = 0;
    for (std::size_t index = 0; index < *yield_block_start; ++index) {
        const Lua51Instruction& instruction = instructions[index];
        if (instruction.opcode != Lua51Opcode::kJmp || instruction.sbx >= 0) {
            continue;
        }
        const long long jump_target =
            static_cast<long long>(index) + 1 + static_cast<long long>(instruction.sbx);
        if (jump_target >= 0 && static_cast<std::size_t>(jump_target) < *yield_block_start) {
            loop_body_start =
                std::max(loop_body_start, static_cast<std::size_t>(jump_target));
        }
    }

    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    for (std::size_t index = 0; index < instructions.size(); ++index) {
        if (index < loop_body_start) {
            StepLua51TrackedRegisterExpressions(function,
                                                parameter_names,
                                                instructions[index],
                                                index,
                                                &tracked_registers);
            continue;
        }

        if (index + 1 < instructions.size() &&
            (instructions[index].opcode == Lua51Opcode::kLt ||
             instructions[index].opcode == Lua51Opcode::kLe ||
             instructions[index].opcode == Lua51Opcode::kEq) &&
            instructions[index + 1].opcode == Lua51Opcode::kJmp &&
            instructions[index + 1].sbx > 0) {
            const long long break_target =
                static_cast<long long>(index + 1) + 1 +
                static_cast<long long>(instructions[index + 1].sbx);
            if (break_target > static_cast<long long>(*yield_block_start)) {
                const std::optional<std::string> lhs =
                    RenderLua51TrackedRKOperand(function,
                                                parameter_names,
                                                tracked_registers,
                                                index,
                                                instructions[index].b);
                const std::optional<std::string> rhs =
                    RenderLua51TrackedRKOperand(function,
                                                parameter_names,
                                                tracked_registers,
                                                index,
                                                instructions[index].c);
                if (lhs.has_value() && rhs.has_value()) {
                    std::string comparator;
                    switch (instructions[index].opcode) {
                        case Lua51Opcode::kLt:
                            comparator = "<";
                            break;
                        case Lua51Opcode::kLe:
                            comparator = "<=";
                            break;
                        case Lua51Opcode::kEq:
                            comparator = "==";
                            break;
                        default:
                            break;
                    }
                    if (!comparator.empty()) {
                        std::string expression = *lhs + " " + comparator + " " + *rhs;
                        if (expressions.empty() || expressions.back() != expression) {
                            expressions.push_back(std::move(expression));
                        }
                    }
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[index],
                                            index,
                                            &tracked_registers);
    }

    return expressions;
}

std::vector<std::string> CollectRenderedLuaNotEqTrueLoopExpressions(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names) {
    std::vector<std::string> expressions;
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (instructions.size() < 3) {
        return expressions;
    }

    std::vector<std::optional<std::string>> tracked_registers(
        std::max<std::size_t>(function.max_stack_size, parameter_names.size()));
    for (std::size_t index = 0; index < instructions.size(); ++index) {
        if (index + 2 < instructions.size() &&
            instructions[index].opcode == Lua51Opcode::kNot &&
            instructions[index + 1].opcode == Lua51Opcode::kEq &&
            instructions[index + 2].opcode == Lua51Opcode::kJmp &&
            instructions[index].a == instructions[index].b &&
            instructions[index + 1].b == instructions[index].a &&
            instructions[index + 2].sbx > 0) {
            const Lua51Constant* constant = GetLua51RKConstant(function, instructions[index + 1].c);
            if (constant != nullptr && constant->type == Lua51ConstantType::kBoolean &&
                constant->boolean_value) {
                const std::optional<std::string> expression =
                    RenderLua51TrackedRegisterOperand(function,
                                                      parameter_names,
                                                      tracked_registers,
                                                      index,
                                                      instructions[index].b);
                if (expression.has_value() &&
                    (expressions.empty() || expressions.back() != *expression)) {
                    expressions.push_back(*expression);
                }
            }
        }

        StepLua51TrackedRegisterExpressions(function,
                                            parameter_names,
                                            instructions[index],
                                            index,
                                            &tracked_registers);
    }

    return expressions;
}

bool TryRestoreBytecodeEmptyThenTailBreaks(const Lua51Function& function,
                                           std::string_view chunk_name,
                                           std::string* source,
                                           std::string* note,
                                           std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_line = [&](const std::vector<std::string>& rendered_lines,
                                        std::size_t start,
                                        std::size_t end_exclusive) {
        for (std::size_t probe = start;
             probe < rendered_lines.size() && probe < end_exclusive;
             ++probe) {
            if (!trim_line(rendered_lines[probe]).empty()) {
                return probe;
            }
        }
        return end_exclusive;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        std::vector<std::string> break_conditions =
            CollectRenderedLuaEmptyThenTailBreakExpressions(function.child_functions[block_index],
                                                            parameter_names);
        const std::size_t total_break_pattern_count =
            CountLua51EmptyThenTailBreakPatterns(function.child_functions[block_index]);
        if (total_break_pattern_count == 0 && break_conditions.empty()) {
            continue;
        }

        std::vector<std::size_t> candidate_empty_then_headers;
        std::vector<std::size_t> chosen_headers;
        std::unordered_set<std::size_t> chosen_header_set;
        std::size_t direct_break_header_count = 0;
        for (std::size_t line_index = block.begin + 1;
             line_index < function_end;
             ++line_index) {
            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[line_index], &prefix, &condition, &suffix) ||
                prefix.find("if ") == std::string::npos || suffix != " then") {
                continue;
            }

            const std::string normalized_condition =
                NormalizeRenderedTrivialParenthesizedLiterals(condition);
            auto match_it = std::find_if(break_conditions.begin(),
                                         break_conditions.end(),
                                         [&](const std::string& expression) {
                                             return expression == condition ||
                                                    NormalizeRenderedTrivialParenthesizedLiterals(
                                                        expression) ==
                                                        normalized_condition;
                                         });
            const std::size_t next_line =
                next_nonempty_line(lines, line_index + 1, function_end);
            if (next_line >= function_end) {
                continue;
            }

            const std::string_view next_trimmed = trim_line(lines[next_line]);
            if (next_trimmed == "break") {
                if (match_it != break_conditions.end()) {
                    break_conditions.erase(match_it);
                    ++direct_break_header_count;
                }
                continue;
            }
            if (next_trimmed != "else" && !next_trimmed.starts_with("elseif ")) {
                continue;
            }

            if (match_it != break_conditions.end()) {
                break_conditions.erase(match_it);
                candidate_empty_then_headers.push_back(line_index);
                if (chosen_header_set.insert(line_index).second) {
                    chosen_headers.push_back(line_index);
                }
                continue;
            }

            candidate_empty_then_headers.push_back(line_index);
        }

        const std::size_t expected_missing_break_count =
            total_break_pattern_count > direct_break_header_count
                ? total_break_pattern_count - direct_break_header_count
                : 0;
        if (expected_missing_break_count == 0) {
            continue;
        }

        const bool has_condition_anchored_candidates =
            !chosen_headers.empty() || !break_conditions.empty();
        if (!has_condition_anchored_candidates) {
            continue;
        }

        if (candidate_empty_then_headers.size() < expected_missing_break_count) {
            continue;
        }

        for (std::size_t reverse_index = candidate_empty_then_headers.size();
             reverse_index > 0;
             --reverse_index) {
            const std::size_t header_line = candidate_empty_then_headers[reverse_index - 1];
            if (chosen_headers.size() >= expected_missing_break_count) {
                break;
            }
            if (chosen_header_set.insert(header_line).second) {
                chosen_headers.push_back(header_line);
            }
        }

        if (chosen_headers.size() != expected_missing_break_count) {
            continue;
        }

        for (std::size_t reverse_index = chosen_headers.size(); reverse_index > 0;
             --reverse_index) {
            const std::size_t header_line = chosen_headers[reverse_index - 1];
            const std::size_t next_line =
                next_nonempty_line(lines, header_line + 1, function_end + 1);
            if (next_line >= function_end + 1) {
                continue;
            }

            const std::size_t indent_size = lines[header_line].find_first_not_of(" \t");
            const std::string indent =
                indent_size == std::string::npos ? std::string()
                                                 : lines[header_line].substr(0, indent_size);
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(next_line),
                         indent + "  break");
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_empty_then_tail_breaks_restored");
    return true;
}

bool TryRestoreBytecodeNotEqTrueLoopConditions(const Lua51Function& function,
                                               std::string_view chunk_name,
                                               std::string* source,
                                               std::string* note,
                                               std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<std::string> loop_expressions =
            CollectRenderedLuaNotEqTrueLoopExpressions(function.child_functions[block_index],
                                                       parameter_names);
        if (loop_expressions.empty()) {
            continue;
        }

        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[line_index], &prefix, &condition, &suffix) ||
                suffix != " do" || prefix.find("while ") == std::string::npos) {
                continue;
            }

            const std::string trimmed_condition =
                std::string(TrimLuaWhitespace(condition));
            std::string candidate_expression;
            if (trimmed_condition.starts_with("not ")) {
                candidate_expression =
                    std::string(TrimLuaWhitespace(trimmed_condition.substr(4)));
            } else {
                std::string lhs;
                std::string comparator;
                std::string rhs;
                if (!TryParseRenderedComparatorExpression(trimmed_condition,
                                                         &lhs,
                                                         &comparator,
                                                         &rhs) ||
                    comparator != " ~= " ||
                    NormalizeRenderedComparatorOperand(rhs) != "true") {
                    continue;
                }
                candidate_expression = lhs;
            }

            const std::string normalized_candidate_expression =
                NormalizeRenderedTrivialParenthesizedLiterals(candidate_expression);
            const bool matches_loop_expression = std::any_of(
                loop_expressions.begin(),
                loop_expressions.end(),
                [&](const std::string& expression) {
                    return expression == candidate_expression ||
                           NormalizeRenderedTrivialParenthesizedLiterals(expression) ==
                               normalized_candidate_expression;
                });
            if (!matches_loop_expression) {
                continue;
            }

            lines[line_index] =
                prefix + "not " + candidate_expression + " == true" + suffix;
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_not_eq_true_loop_conditions_restored");
    return true;
}

bool TryRestoreBytecodeTailElseBreakGuards(const Lua51Function& function,
                                           std::string_view chunk_name,
                                           std::string* source,
                                           std::string* note,
                                           std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_line = [&](const std::vector<std::string>& rendered_lines,
                                        std::size_t start,
                                        std::size_t end_exclusive) {
        for (std::size_t probe = start;
             probe < rendered_lines.size() && probe < end_exclusive;
             ++probe) {
            if (!trim_line(rendered_lines[probe]).empty()) {
                return probe;
            }
        }
        return end_exclusive;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<std::string> positive_guard_conditions =
            CollectRenderedLuaTailElseBreakGuardExpressions(function.child_functions[block_index],
                                                            parameter_names);
        if (positive_guard_conditions.empty()) {
            continue;
        }

        std::size_t yield_line = function_end + 1;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            if (trim_line(lines[line_index]).find("coroutine.yield(") != std::string_view::npos) {
                yield_line = line_index;
                break;
            }
        }
        if (yield_line > function_end) {
            continue;
        }

        bool block_changed = false;
        for (std::size_t line_index = block.begin + 1; line_index < yield_line; ++line_index) {
            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[line_index], &prefix, &condition, &suffix) ||
                prefix.find("if ") == std::string::npos || suffix != " then") {
                continue;
            }

            const std::optional<std::string> positive_condition =
                InvertRenderedSimpleCondition(condition);
            if (!positive_condition.has_value()) {
                continue;
            }

            const std::string normalized_positive_condition =
                NormalizeRenderedTrivialParenthesizedLiterals(*positive_condition);
            const bool matches_positive_guard = std::any_of(
                positive_guard_conditions.begin(),
                positive_guard_conditions.end(),
                [&](const std::string& expression) {
                    return expression == *positive_condition ||
                           NormalizeRenderedTrivialParenthesizedLiterals(expression) ==
                               normalized_positive_condition;
                });
            if (!matches_positive_guard) {
                continue;
            }

            const std::size_t break_line =
                next_nonempty_line(lines, line_index + 1, function_end + 1);
            if (break_line > function_end || trim_line(lines[break_line]) != "break") {
                continue;
            }

            bool saw_else = false;
            std::size_t end_line = function_end + 1;
            int depth = 0;
            for (std::size_t probe = line_index; probe <= function_end; ++probe) {
                const std::string_view trimmed = trim_line(lines[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (probe > line_index && depth == 1 &&
                    (trimmed == "else" || trimmed.starts_with("elseif "))) {
                    saw_else = true;
                    break;
                }
                depth += GetRenderedLuaBlockDelta(trimmed);
                if (probe > line_index && depth == 0) {
                    end_line = probe;
                    break;
                }
            }
            if (saw_else || end_line >= yield_line || end_line > function_end) {
                continue;
            }

            std::vector<std::string> moved_body_lines;
            for (std::size_t probe = end_line + 1; probe < yield_line; ++probe) {
                moved_body_lines.push_back(lines[probe]);
            }
            if (moved_body_lines.empty()) {
                continue;
            }

            const std::size_t indent_size = lines[line_index].find_first_not_of(" \t");
            const std::string indent =
                indent_size == std::string::npos ? std::string()
                                                 : lines[line_index].substr(0, indent_size);

            std::vector<std::string> replacement_lines;
            replacement_lines.push_back(prefix + *positive_condition + suffix);
            replacement_lines.insert(replacement_lines.end(),
                                     moved_body_lines.begin(),
                                     moved_body_lines.end());
            replacement_lines.push_back(indent + "else");
            replacement_lines.push_back(indent + "  break");
            replacement_lines.push_back(indent + "end");

            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                        lines.begin() + static_cast<std::ptrdiff_t>(yield_line));
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                         replacement_lines.begin(),
                         replacement_lines.end());
            block_changed = true;
            changed = true;
            break;
        }

        if (block_changed) {
            break;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_tail_else_break_guards_restored");
    return true;
}

bool TryRestoreBytecodeEmptyElseTailBreaks(const Lua51Function& function,
                                           std::string_view chunk_name,
                                           std::string* source,
                                           std::string* note,
                                           std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_line = [&](const std::vector<std::string>& rendered_lines,
                                        std::size_t start,
                                        std::size_t end_exclusive) {
        for (std::size_t probe = start;
             probe < rendered_lines.size() && probe < end_exclusive;
             ++probe) {
            if (!trim_line(rendered_lines[probe]).empty()) {
                return probe;
            }
        }
        return end_exclusive;
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        const std::vector<std::string> break_conditions =
            CollectRenderedLuaEmptyElseTailBreakExpressions(function.child_functions[block_index],
                                                            parameter_names);
        const std::size_t expected_empty_else_count =
            CountLua51EmptyElseTailBreakPatterns(function.child_functions[block_index]);
        if (break_conditions.empty() || expected_empty_else_count == 0) {
            continue;
        }

        struct EmptyElseCandidate {
            std::size_t header_line = 0;
            std::size_t end_line = 0;
        };

        std::vector<EmptyElseCandidate> candidates;
        for (std::size_t line_index = block.begin + 1; line_index < function_end; ++line_index) {
            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[line_index], &prefix, &condition, &suffix) ||
                prefix.find("if ") == std::string::npos || suffix != " then") {
                continue;
            }

            const std::string normalized_condition =
                NormalizeRenderedTrivialParenthesizedLiterals(condition);
            const bool matches_break_condition = std::any_of(
                break_conditions.begin(),
                break_conditions.end(),
                [&](const std::string& expression) {
                    return expression == condition ||
                           NormalizeRenderedTrivialParenthesizedLiterals(expression) ==
                               normalized_condition;
                });
            if (!matches_break_condition) {
                continue;
            }

            const std::size_t next_line =
                next_nonempty_line(lines, line_index + 1, function_end + 1);
            if (next_line > function_end || trim_line(lines[next_line]) != "break") {
                continue;
            }

            bool saw_else = false;
            std::size_t end_line = function_end + 1;
            int depth = 0;
            for (std::size_t probe = line_index; probe <= function_end; ++probe) {
                const std::string_view trimmed = trim_line(lines[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (probe > line_index && depth == 1 &&
                    (trimmed == "else" || trimmed.starts_with("elseif "))) {
                    saw_else = true;
                    break;
                }
                depth += GetRenderedLuaBlockDelta(trimmed);
                if (probe > line_index && depth == 0) {
                    end_line = probe;
                    break;
                }
            }
            if (saw_else || end_line > function_end) {
                continue;
            }

            bool saw_break = false;
            bool saw_other_body = false;
            int nested_depth = 0;
            for (std::size_t probe = line_index + 1; probe < end_line; ++probe) {
                const std::string_view trimmed = trim_line(lines[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (nested_depth == 0) {
                    if (!saw_break && trimmed == "break") {
                        saw_break = true;
                    } else {
                        saw_other_body = true;
                        break;
                    }
                }
                nested_depth += GetRenderedLuaBlockDelta(trimmed);
            }
            if (!saw_break || saw_other_body) {
                continue;
            }

            candidates.push_back({
                .header_line = line_index,
                .end_line = end_line,
            });
        }

        if (candidates.empty() || candidates.size() != expected_empty_else_count) {
            continue;
        }

        for (std::size_t reverse_index = candidates.size(); reverse_index > 0; --reverse_index) {
            const EmptyElseCandidate& candidate = candidates[reverse_index - 1];
            const std::size_t indent_size = lines[candidate.header_line].find_first_not_of(" \t");
            const std::string indent =
                indent_size == std::string::npos ? std::string()
                                                 : lines[candidate.header_line].substr(0, indent_size);
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(candidate.end_line),
                         indent + "else");
            changed = true;
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_empty_else_tail_breaks_restored");
    return true;
}

bool TryCollapseBytecodeTailYieldElseBranches(const Lua51Function& function,
                                              std::string_view chunk_name,
                                              std::string* source,
                                              std::string* note,
                                              std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto normalize_expression = [](std::string_view expression) {
        return NormalizeRenderedTrivialParenthesizedLiterals(
            std::string(TrimLuaWhitespace(expression)));
    };

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks = CollectTopLevelRenderedFunctionBlocks(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, block.begin);
        if (function_end >= lines.size() || function_end <= block.begin) {
            continue;
        }

        const std::vector<std::string> parameter_names =
            ParseRenderedFunctionParameterNames(lines[block.begin]);
        std::vector<std::string> break_conditions =
            CollectRenderedLuaTailLoopBreakExpressions(function.child_functions[block_index],
                                                       parameter_names);
        if (break_conditions.empty()) {
            continue;
        }

        std::unordered_set<std::string> remaining_conditions;
        for (const std::string& condition : break_conditions) {
            remaining_conditions.insert(normalize_expression(condition));
        }

        for (std::size_t line_index = block.begin + 1;
             line_index < function_end && !remaining_conditions.empty();
             ++line_index) {
            std::string prefix;
            std::string condition;
            std::string suffix;
            if (!TryParseRenderedConditionHeader(lines[line_index], &prefix, &condition, &suffix) ||
                suffix != " then" || prefix.find("if ") == std::string::npos) {
                continue;
            }

            const std::string normalized_condition = normalize_expression(condition);
            if (!remaining_conditions.contains(normalized_condition)) {
                continue;
            }

            std::size_t else_line = function_end + 1;
            std::size_t end_line = function_end + 1;
            int depth = 0;
            for (std::size_t probe = line_index; probe <= function_end; ++probe) {
                const std::string_view trimmed = trim_line(lines[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (probe > line_index && depth == 1 && trimmed == "else") {
                    else_line = probe;
                }
                depth += GetRenderedLuaBlockDelta(trimmed);
                if (probe > line_index && depth == 0) {
                    end_line = probe;
                    break;
                }
            }

            if (else_line > function_end || end_line > function_end || else_line >= end_line) {
                continue;
            }

            bool saw_top_level_break = false;
            int nested_depth = 0;
            for (std::size_t probe = line_index + 1; probe < else_line; ++probe) {
                const std::string_view trimmed = trim_line(lines[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (nested_depth == 0 && trimmed == "break") {
                    saw_top_level_break = true;
                }
                nested_depth += GetRenderedLuaBlockDelta(trimmed);
            }
            if (!saw_top_level_break) {
                continue;
            }

            std::vector<std::string> else_body_lines;
            bool else_body_supported = true;
            nested_depth = 0;
            for (std::size_t probe = else_line + 1; probe < end_line; ++probe) {
                const std::string_view trimmed = trim_line(lines[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (nested_depth != 0 || trimmed == "break" || trimmed == "else" ||
                    trimmed.starts_with("elseif ") || trimmed.starts_with("local ") ||
                    trimmed.starts_with("function ")) {
                    else_body_supported = false;
                    break;
                }
                else_body_lines.push_back(lines[probe]);
                nested_depth += GetRenderedLuaBlockDelta(trimmed);
                if (nested_depth != 0) {
                    else_body_supported = false;
                    break;
                }
            }

            if (!else_body_supported || else_body_lines.empty()) {
                continue;
            }

            bool has_tail_yield = false;
            for (const std::string& else_body_line : else_body_lines) {
                if (trim_line(else_body_line).find("coroutine.yield(") != std::string_view::npos) {
                    has_tail_yield = true;
                    break;
                }
            }
            if (!has_tail_yield) {
                continue;
            }

            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(else_line),
                        lines.begin() + static_cast<std::ptrdiff_t>(end_line));
            const std::size_t new_end_line = else_line;
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(new_end_line + 1),
                         else_body_lines.begin(),
                         else_body_lines.end());
            changed = true;
            remaining_conditions.erase(normalized_condition);
        }
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_tail_yield_else_branches_collapsed");
    return true;
}

bool TryApplyBytecodeBackedMainChunkRestorations(const Lua51Function& function,
                                                 std::string_view chunk_name,
                                                 std::string* source,
                                                 std::string* note,
                                                 std::string_view note_prefix) {
    bool changed = false;
    changed |= TryRestoreBytecodeTopLevelClosureBindings(function,
                                                         chunk_name,
                                                         source,
                                                         note,
                                                         note_prefix);
    changed |= TryRestoreBytecodeTopLevelAssignedClosureBlocks(function,
                                                               chunk_name,
                                                               source,
                                                               note,
                                                               note_prefix);
    changed |= TryRestoreBytecodeMissingTestedCallSeeds(function,
                                                        chunk_name,
                                                        source,
                                                        note,
                                                        note_prefix);
    changed |= TryRestoreBytecodeCallResultAssignmentReceivers(function,
                                                               chunk_name,
                                                               source,
                                                               note,
                                                               note_prefix);
    changed |= TryRestoreBytecodeTopLevelComparatorAsserts(function,
                                                           chunk_name,
                                                           source,
                                                           note,
                                                           note_prefix);
    changed |= TryRestoreBytecodeTopLevelCallResultAssignmentReceivers(function,
                                                                       chunk_name,
                                                                       source,
                                                                       note,
                                                                       note_prefix);
    changed |= TryRestoreBytecodeTopLevelDirectStatementCallReceivers(function,
                                                                      chunk_name,
                                                                      source,
                                                                      note,
                                                                      note_prefix);
    changed |= TryRestoreBytecodeTopLevelLiteralCalleeAdjacentCalls(function,
                                                                    chunk_name,
                                                                    source,
                                                                    note,
                                                                    note_prefix);
    changed |= TryRestoreBytecodeDirectStatementCallReceivers(function,
                                                              chunk_name,
                                                              source,
                                                              note,
                                                              note_prefix);
    changed |= TryRestoreBytecodeCallTestGuards(function,
                                                chunk_name,
                                                source,
                                                note,
                                                note_prefix);
    changed |= TryRestoreBytecodeRepeatedCallResultSeeds(function,
                                                         chunk_name,
                                                         source,
                                                         note,
                                                         note_prefix);
    changed |= TryRestoreBytecodeIteratorArgumentLocals(function,
                                                        chunk_name,
                                                        source,
                                                        note,
                                                        note_prefix);
    changed |= TryRestoreBytecodeGuardedMemberCallAssignments(function,
                                                              chunk_name,
                                                              source,
                                                              note,
                                                              note_prefix);
    changed |= TryRestoreBytecodeLocalCallCarrySeeds(function,
                                                     chunk_name,
                                                     source,
                                                     note,
                                                     note_prefix);
    changed |= TryRestoreBytecodeLocalMultiResultCallSeeds(function,
                                                           chunk_name,
                                                           source,
                                                           note,
                                                           note_prefix);
    changed |= TryRestoreBytecodeTestedCallResultLocalSeeds(function,
                                                            chunk_name,
                                                            source,
                                                            note,
                                                            note_prefix);
    changed |= TryRestoreBytecodeSeededLocalCallExpressions(function,
                                                            chunk_name,
                                                            source,
                                                            note,
                                                            note_prefix);
    changed |= TryRestoreBytecodeImmediateTempExpressionReuses(function,
                                                               chunk_name,
                                                               source,
                                                               note,
                                                               note_prefix);
    changed |= TryInlineSyntheticLocalCallTempSeeds(function,
                                                    chunk_name,
                                                    source,
                                                    note,
                                                    note_prefix);
    changed |= TryRestoreBytecodeLocalTableBuilderSeeds(function,
                                                        chunk_name,
                                                        source,
                                                        note,
                                                        note_prefix);
    changed |= TryInlineSyntheticCallResultUses(function,
                                                chunk_name,
                                                source,
                                                note,
                                                note_prefix);
    changed |= TryInlineSyntheticSeededCallArgumentLocals(chunk_name,
                                                          source,
                                                          note,
                                                          note_prefix);
    changed |= TryRestoreBytecodeTestedCallLocalGuards(function,
                                                       chunk_name,
                                                       source,
                                                       note,
                                                       note_prefix);
    changed |= TryRestoreBytecodeImmediateGlobalCallSeeds(function,
                                                          chunk_name,
                                                          source,
                                                          note,
                                                          note_prefix);
    changed |= TryRestoreBytecodeCompareConditionOrder(function,
                                                       chunk_name,
                                                       source,
                                                       note,
                                                       note_prefix);
    changed |= TryRestoreBytecodeCallOperandCompareOrder(function,
                                                         chunk_name,
                                                         source,
                                                         note,
                                                         note_prefix);
    changed |= TryRestoreBytecodeTrailingGlobalSeeds(function,
                                                     chunk_name,
                                                     source,
                                                     note,
                                                     note_prefix);
    changed |= TryRestoreBytecodeTailBranchAssignments(function,
                                                       chunk_name,
                                                       source,
                                                       note,
                                                       note_prefix);
    changed |= TryRestoreBytecodeEmptyThenTailBreaks(function,
                                                     chunk_name,
                                                     source,
                                                     note,
                                                     note_prefix);
    changed |= TryRestoreBytecodeComparatorAsserts(function,
                                                   chunk_name,
                                                   source,
                                                   note,
                                                   note_prefix);
    changed |= TryRestoreBytecodeIndexedCallTableBootstraps(function,
                                                            chunk_name,
                                                            source,
                                                            note,
                                                            note_prefix);
    changed |= TryRestoreBytecodeIndexedMissingCallBootstrapSeeds(function,
                                                                  chunk_name,
                                                                  source,
                                                                  note,
                                                                  note_prefix);
    changed |= TryRestoreBytecodeTopLevelTempSeedFieldValues(function,
                                                             chunk_name,
                                                             source,
                                                             note,
                                                             note_prefix);
    changed |= TryRestoreIndexedCallLiteralContinuations(chunk_name,
                                                         source,
                                                         note,
                                                         note_prefix);
    changed |= TryRestoreBytecodeIndexedEmptyTableBootstraps(function,
                                                             chunk_name,
                                                             source,
                                                             note,
                                                             note_prefix);
    changed |= TryRestoreBytecodeGlobalEmptyTableCallSeeds(function,
                                                           chunk_name,
                                                           source,
                                                           note,
                                                           note_prefix);
    changed |= TryRestoreBytecodeExpressionKeyedGlobalTableSeeds(function,
                                                                 chunk_name,
                                                                 source,
                                                                 note,
                                                                 note_prefix);
    changed |= TryRestoreBytecodeGlobalFieldTableBootstraps(function,
                                                            chunk_name,
                                                            source,
                                                            note,
                                                            note_prefix);
    changed |= TryStripSpuriousLeadingLiteralCallPrefixes(chunk_name,
                                                          source,
                                                          note,
                                                          note_prefix);
    changed |= TryPruneSpuriousDuplicatedLiteralCallStatements(chunk_name,
                                                               source,
                                                               note,
                                                               note_prefix);
    changed |= TryFoldTopLevelDuplicatedLiteralTempFieldSeeds(chunk_name,
                                                              source,
                                                              note,
                                                              note_prefix);
    changed |= TryRestoreBytecodeTailLoopBreaks(function,
                                                chunk_name,
                                                source,
                                                note,
                                                note_prefix);
    changed |= TryRestoreBytecodeDirectTailBreakGuards(function,
                                                       chunk_name,
                                                       source,
                                                       note,
                                                       note_prefix);
    changed |= TryRestoreBytecodeNotEqTrueLoopConditions(function,
                                                         chunk_name,
                                                         source,
                                                         note,
                                                         note_prefix);
    while (TryRestoreBytecodeTailElseBreakGuards(function,
                                                 chunk_name,
                                                 source,
                                                 note,
                                                 note_prefix)) {
        changed = true;
    }
    changed |= TryRestoreBytecodeEmptyElseTailBreaks(function,
                                                     chunk_name,
                                                     source,
                                                     note,
                                                     note_prefix);
    changed |= TryCollapseBytecodeTailYieldElseBranches(function,
                                                        chunk_name,
                                                        source,
                                                        note,
                                                        note_prefix);
    changed |= TryRestoreBytecodeBooleanReturnWrapperFunctionBlocks(function,
                                                                    chunk_name,
                                                                    source,
                                                                    note,
                                                                    note_prefix);
    changed |= TryRestoreBytecodeLeadingEmptyIfChecks(function,
                                                      chunk_name,
                                                      source,
                                                      note,
                                                      note_prefix);
    changed |= TryRestoreBytecodeEmptyEqualitySelectorBranches(function,
                                                               chunk_name,
                                                               source,
                                                               note,
                                                               note_prefix);
    while (TryRestoreBytecodeSiblingElseIfBlocks(function,
                                                 chunk_name,
                                                 source,
                                                 note,
                                                 note_prefix)) {
        changed = true;
    }

    if (source != nullptr && !source->empty()) {
        const std::string baseline_cleanup_error = ValidateLuaSource(*source, chunk_name);
        std::vector<std::string> cleanup_lines = SplitRenderedLines(*source);
        bool cleanup_changed = RunNativeFirstPassLoop(8, [&](int /*pass*/) {
            bool pass_changed = false;
            pass_changed |= SimplifyRenderedLuaImmediateLocalLiteralSeeds(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaLocalCallBootstrapGuards(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaSelfCallShadowLocals(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaAssignedFunctionDeclarations(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaExplicitSelfMethodArguments(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaRedundantColonSelfArguments(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaMethodArg0SelfParameters(&cleanup_lines);
            pass_changed |=
                SimplifyRenderedLuaInvalidEmptyStringMethodReceivers(&cleanup_lines);
            pass_changed |=
                SimplifyRenderedLuaImmediateAssignedConditionExpressionUses(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaNearbyAssignedStatementChainUses(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaImmediateRedundantConditionTemps(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaImmediateAssignedCallGuards(&cleanup_lines);
            pass_changed |=
                SimplifyRenderedLuaImmediateAssignedMethodCallGuards(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaRepeatedCallGuards(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaRepeatedCallResultGuards(&cleanup_lines);
            pass_changed |=
                SimplifyRenderedLuaImmediateAssignedRepeatedCallLoops(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaEmptyIfAssertGuards(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaAssertConditionBlocks(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaNamedNilAsserts(&cleanup_lines);
            pass_changed |= SimplifyRenderedLuaImpossibleAsserts(&cleanup_lines);
            return pass_changed;
        });

        if (cleanup_changed) {
            NormalizeRenderedLuaWhitespace(&cleanup_lines);
            std::string cleanup_source = JoinRenderedLines(cleanup_lines);
            const std::string cleanup_error = ValidateLuaSource(cleanup_source, chunk_name);
            if (cleanup_error.empty() ||
                (!baseline_cleanup_error.empty() &&
                 IsValidationOutcomeBetter(baseline_cleanup_error, cleanup_error))) {
                *source = std::move(cleanup_source);
                AppendCandidateNote(note,
                                    note_prefix,
                                    cleanup_error.empty()
                                        ? "bytecode_rendered_bootstrap_cleanup_applied"
                                        : "bytecode_rendered_bootstrap_cleanup_progress");
                changed = true;
            }
        }
    }

    return changed;
}

bool IsLuaKeywordToken(std::string_view token) {
    return token == "and" || token == "break" || token == "do" || token == "else" ||
           token == "elseif" || token == "end" || token == "false" || token == "for" ||
           token == "function" || token == "if" || token == "in" || token == "local" ||
           token == "nil" || token == "not" || token == "or" || token == "repeat" ||
           token == "return" || token == "then" || token == "true" || token == "until" ||
           token == "while";
}

bool IsLikelyGlobalLowercaseIdentifier(std::string_view token) {
    return token == "math" || token == "table" || token == "string" || token == "coroutine" ||
           token == "io" || token == "os" || token == "debug" || token == "package" ||
           token == "pairs" || token == "ipairs" || token == "next" || token == "type" ||
           token == "select" || token == "assert" || token == "error" || token == "print" ||
           token == "tostring" || token == "tonumber" || token == "rawget" ||
           token == "rawset" || token == "rawequal" || token == "getmetatable" ||
           token == "setmetatable" || token == "unpack";
}

void CollectRenderedIdentifierList(std::string_view text,
                                   std::unordered_set<std::string>* names) {
    if (names == nullptr) {
        return;
    }

    std::size_t start = 0;
    while (start < text.size()) {
        const std::size_t comma = text.find(',', start);
        const std::string_view part = TrimLuaWhitespace(
            text.substr(start,
                        comma == std::string_view::npos ? text.size() - start
                                                         : comma - start));
        if (!part.empty()) {
            const std::string_view name = ExtractLeadingIdentifier(part);
            if (!name.empty()) {
                names->insert(std::string(name));
            }
        }
        if (comma == std::string_view::npos) {
            break;
        }
        start = comma + 1;
    }
}

void CollectDeclaredNamesInFunctionBlock(const std::vector<std::string>& lines,
                                         const RenderedFunctionBlock& block,
                                         std::unordered_set<std::string>* declared_names) {
    if (declared_names == nullptr || block.begin >= lines.size() || block.end >= lines.size() ||
        block.begin > block.end) {
        return;
    }

    std::unordered_set<std::string> declared;

    const std::string_view header =
        TrimLuaWhitespace(StripLuaLineComment(lines[block.begin]));
    const std::size_t open_paren = header.find('(');
    const std::size_t close_paren = header.rfind(')');
    if (open_paren != std::string_view::npos && close_paren != std::string_view::npos &&
        close_paren > open_paren) {
        CollectRenderedIdentifierList(header.substr(open_paren + 1, close_paren - open_paren - 1),
                                      &declared);
    }

    for (std::size_t line_index = block.begin + 1;
         line_index <= block.end && line_index < lines.size();
         ++line_index) {
        std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(lines[line_index]));
        if (trimmed.empty()) {
            continue;
        }

        if (trimmed.starts_with("local ")) {
            std::string_view declaration = TrimLuaWhitespace(trimmed.substr(6));
            const std::size_t equal = declaration.find('=');
            if (equal != std::string_view::npos) {
                declaration = TrimLuaWhitespace(declaration.substr(0, equal));
            }
            CollectRenderedIdentifierList(declaration, &declared);
        }
    }
    *declared_names = std::move(declared);
}

void CollectAssignedTemporaryNamesInFunctionBlock(
    const std::vector<std::string>& lines,
    const RenderedFunctionBlock& block,
    std::unordered_set<std::string>* assigned_temporary_names) {
    if (assigned_temporary_names == nullptr || block.begin >= lines.size() ||
        block.end >= lines.size() || block.begin > block.end) {
        return;
    }

    std::unordered_set<std::string> assigned;
    const std::string_view header =
        TrimLuaWhitespace(StripLuaLineComment(lines[block.begin]));
    const std::size_t open_paren = header.find('(');
    const std::size_t close_paren = header.rfind(')');
    if (open_paren != std::string_view::npos && close_paren != std::string_view::npos &&
        close_paren > open_paren) {
        std::unordered_set<std::string> parameters;
        CollectRenderedIdentifierList(
            header.substr(open_paren + 1, close_paren - open_paren - 1),
            &parameters);
        for (const std::string& parameter : parameters) {
            if (IsRenderTemporaryName(parameter)) {
                assigned.insert(parameter);
            }
        }
    }

    for (std::size_t line_index = block.begin + 1;
         line_index <= block.end && line_index < lines.size();
         ++line_index) {
        std::vector<std::string> temp_names;
        std::string multi_rhs;
        if (ParseRenderedTemporaryMultiAssignment(lines[line_index], &temp_names, &multi_rhs)) {
            for (const std::string& name : temp_names) {
                assigned.insert(name);
            }
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseSingleRenderedAssignment(lines[line_index],
                                           &assign_is_local,
                                           &assign_lhs,
                                           &assign_rhs)) {
            continue;
        }

        const std::string normalized_lhs =
            std::string(TrimLuaWhitespace(assign_lhs));
        if (IsRenderTemporaryName(normalized_lhs)) {
            assigned.insert(normalized_lhs);
        }
    }

    *assigned_temporary_names = std::move(assigned);
}

std::size_t CountForwardReferencedTemporaryUsesInFunctionBlock(
    const std::vector<std::string>& lines,
    const RenderedFunctionBlock& block) {
    if (block.begin >= lines.size() || block.end >= lines.size() || block.begin > block.end) {
        return 0;
    }

    std::unordered_set<std::string> assigned_temporary_names;
    CollectAssignedTemporaryNamesInFunctionBlock(lines, block, &assigned_temporary_names);
    std::unordered_set<std::string> available_temporary_names;
    const std::string_view header =
        TrimLuaWhitespace(StripLuaLineComment(lines[block.begin]));
    const std::size_t open_paren = header.find('(');
    const std::size_t close_paren = header.rfind(')');
    if (open_paren != std::string_view::npos && close_paren != std::string_view::npos &&
        close_paren > open_paren) {
        std::unordered_set<std::string> parameters;
        CollectRenderedIdentifierList(
            header.substr(open_paren + 1, close_paren - open_paren - 1),
            &parameters);
        for (const std::string& parameter : parameters) {
            if (IsRenderTemporaryName(parameter)) {
                available_temporary_names.insert(parameter);
            }
        }
    }

    std::size_t suspicious = 0;
    for (std::size_t line_index = block.begin + 1;
         line_index <= block.end && line_index < lines.size();
         ++line_index) {
        const std::string& line = lines[line_index];
        std::string_view stripped = TrimLuaWhitespace(StripLuaLineComment(line));
        if (stripped.empty()) {
            continue;
        }

        std::string_view scan = stripped;
        const std::size_t equal = stripped.find('=');
        if (equal != std::string_view::npos && !stripped.starts_with("if ")) {
            scan = stripped.substr(equal + 1);
        }

        std::vector<std::string> assigned_temporaries_on_line;
        std::string current_assignment_lhs;
        std::string current_assignment_rhs;
        bool current_assignment_is_local = false;
        std::string multi_assignment_rhs;
        if (ParseRenderedTemporaryMultiAssignment(line,
                                                  &assigned_temporaries_on_line,
                                                  &multi_assignment_rhs)) {
            scan = multi_assignment_rhs;
        } else if (ParseSingleRenderedAssignment(line,
                                                 &current_assignment_is_local,
                                                 &current_assignment_lhs,
                                                 &current_assignment_rhs)) {
            current_assignment_lhs =
                std::string(TrimLuaWhitespace(current_assignment_lhs));
        }

        ForEachLuaToken(scan, [&](std::string_view token) {
            if (token.empty() || !IsRenderTemporaryName(token)) {
                return;
            }

            const std::string token_name(token);
            if (available_temporary_names.find(token_name) != available_temporary_names.end() ||
                assigned_temporary_names.find(token_name) == assigned_temporary_names.end() ||
                token_name == current_assignment_lhs) {
                return;
            }

            const std::size_t token_pos = scan.find(token);
            if (token_pos != std::string_view::npos && token_pos > 0) {
                const char previous = scan[token_pos - 1];
                if (previous == '.' || previous == ':') {
                    return;
                }
            }

            ++suspicious;
        });

        if (!assigned_temporaries_on_line.empty()) {
            for (const std::string& name : assigned_temporaries_on_line) {
                available_temporary_names.insert(name);
            }
        } else if (!current_assignment_lhs.empty() &&
                   IsRenderTemporaryName(current_assignment_lhs)) {
            available_temporary_names.insert(current_assignment_lhs);
        }
    }

    return suspicious;
}

std::size_t CountSuspiciousUndeclaredIdentifiersInFunctionBlock(
    const std::vector<std::string>& lines,
    const RenderedFunctionBlock& block) {
    std::unordered_set<std::string> declared_names;
    CollectDeclaredNamesInFunctionBlock(lines, block, &declared_names);
    std::size_t suspicious = CountForwardReferencedTemporaryUsesInFunctionBlock(lines, block);
    for (std::size_t line_index = block.begin + 1;
         line_index <= block.end && line_index < lines.size();
         ++line_index) {
        const std::string& line = lines[line_index];
        std::string_view stripped = TrimLuaWhitespace(StripLuaLineComment(line));
        if (stripped.empty()) {
            continue;
        }

        std::string_view scan = stripped;
        const std::size_t equal = stripped.find('=');
        if (equal != std::string_view::npos && !stripped.starts_with("if ")) {
            scan = stripped.substr(equal + 1);
        }
        ForEachLuaToken(scan, [&](std::string_view token) {
            if (token.empty() || IsLuaKeywordToken(token) ||
                IsLikelyGlobalLowercaseIdentifier(token)) {
                return;
            }

            const std::string token_name(token);
            if (declared_names.find(token_name) != declared_names.end()) {
                return;
            }

            const unsigned char first = static_cast<unsigned char>(token.front());
            if (std::islower(first) == 0) {
                return;
            }

            const std::size_t token_pos = scan.find(token);
            if (token_pos != std::string_view::npos && token_pos > 0) {
                const char previous = scan[token_pos - 1];
                if (previous == '.' || previous == ':') {
                    return;
                }
            }

            ++suspicious;
        });
    }

    return suspicious;
}

bool ParseRenderedTemporaryMultiAssignment(std::string_view line,
                                          std::vector<std::string>* names,
                                          std::string* rhs) {
    if (names == nullptr || rhs == nullptr) {
        return false;
    }

    names->clear();
    rhs->clear();

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!trimmed.starts_with("local ")) {
        return false;
    }
    trimmed = TrimLuaWhitespace(trimmed.substr(6));

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs_view = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs_view = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (lhs_view.empty() || rhs_view.empty() || lhs_view.find(',') == std::string_view::npos) {
        return false;
    }

    std::size_t start = 0;
    while (start < lhs_view.size()) {
        const std::size_t comma = lhs_view.find(',', start);
        const std::string_view part = TrimLuaWhitespace(lhs_view.substr(
            start,
            comma == std::string_view::npos ? lhs_view.size() - start : comma - start));
        if (part.empty() || !IsRenderTemporaryName(part)) {
            names->clear();
            return false;
        }
        names->push_back(std::string(part));
        if (comma == std::string_view::npos) {
            break;
        }
        start = comma + 1;
    }

    if (names->empty()) {
        return false;
    }

    *rhs = std::string(rhs_view);
    return true;
}

bool ParseSingleRenderedAssignment(std::string_view line,
                                   std::string* lhs,
                                   std::string* rhs) {
    if (lhs == nullptr || rhs == nullptr) {
        return false;
    }

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty()) {
        return false;
    }
    if (trimmed.starts_with("local ")) {
        trimmed = TrimLuaWhitespace(trimmed.substr(6));
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs_view = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs_view = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (lhs_view.empty() || rhs_view.empty() || lhs_view.find(',') != std::string_view::npos) {
        return false;
    }

    *lhs = std::string(lhs_view);
    *rhs = std::string(rhs_view);
    return true;
}

bool ParseSingleRenderedAssignment(std::string_view line,
                                   bool* is_local,
                                   std::string* lhs,
                                   std::string* rhs) {
    if (is_local == nullptr || lhs == nullptr || rhs == nullptr) {
        return false;
    }

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty()) {
        return false;
    }

    *is_local = false;
    if (trimmed.starts_with("local ")) {
        *is_local = true;
        trimmed = TrimLuaWhitespace(trimmed.substr(6));
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs_view = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs_view = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (lhs_view.empty() || rhs_view.empty() || lhs_view.find(',') != std::string_view::npos) {
        return false;
    }

    *lhs = std::string(lhs_view);
    *rhs = std::string(rhs_view);
    return true;
}

bool LooksLikeBooleanValuedExpression(std::string_view rhs) {
    const std::string normalized =
        NormalizeRenderedTrivialParenthesizedLiterals(std::string(TrimLuaWhitespace(rhs)));
    rhs = TrimLuaWhitespace(normalized);
    if (rhs.empty()) {
        return false;
    }

    if (rhs == "true" || rhs == "false" || rhs == "nil" || rhs.starts_with("not ")) {
        return true;
    }

    for (std::string_view token : {"==", "~=", "<=", ">=", "<", ">"}) {
        if (rhs.find(token) != std::string_view::npos) {
            return true;
        }
    }

    if (rhs.find(" and ") != std::string_view::npos ||
        rhs.find(" or ") != std::string_view::npos) {
        return true;
    }

    if (!rhs.ends_with(')')) {
        return false;
    }

    const std::size_t open = rhs.find('(');
    if (open == std::string_view::npos || open == 0) {
        return false;
    }

    std::string_view callee = TrimLuaWhitespace(rhs.substr(0, open));
    const std::size_t last_access = callee.find_last_of(".:");
    if (last_access != std::string_view::npos && last_access + 1 < callee.size()) {
        callee = callee.substr(last_access + 1);
    }

    return callee.starts_with("Is") || callee.starts_with("Has") ||
           callee.starts_with("Can") || callee.starts_with("Should") ||
           callee.starts_with("Needs") || callee.starts_with("Want");
}

bool FindRenderedMatchingParenLocal(std::string_view text,
                                    std::size_t open_index,
                                    std::size_t* close_index) {
    if (close_index == nullptr || open_index >= text.size() || text[open_index] != '(') {
        return false;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    int depth = 0;
    for (std::size_t index = open_index; index < text.size(); ++index) {
        const char ch = text[index];
        if (in_single_quote) {
            if (ch == '\\' && index + 1 < text.size()) {
                ++index;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && index + 1 < text.size()) {
                ++index;
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
            ++depth;
            continue;
        }
        if (ch == ')') {
            --depth;
            if (depth == 0) {
                *close_index = index;
                return true;
            }
        }
    }

    return false;
}

bool SplitRenderedTopLevelCommaChainLocal(std::string_view value,
                                          std::vector<std::string>* parts) {
    if (parts == nullptr) {
        return false;
    }

    value = TrimLuaWhitespace(value);
    parts->clear();

    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    std::size_t part_begin = 0;
    for (std::size_t i = 0; i < value.size(); ++i) {
        const char ch = value[i];
        if (ch == '\'' && !in_double_quote) {
            in_single_quote = !in_single_quote;
            continue;
        }
        if (ch == '"' && !in_single_quote) {
            in_double_quote = !in_double_quote;
            continue;
        }
        if (in_single_quote || in_double_quote) {
            continue;
        }
        switch (ch) {
        case '(':
            ++paren_depth;
            break;
        case ')':
            if (paren_depth > 0) {
                --paren_depth;
            }
            break;
        case '{':
            ++brace_depth;
            break;
        case '}':
            if (brace_depth > 0) {
                --brace_depth;
            }
            break;
        case '[':
            ++bracket_depth;
            break;
        case ']':
            if (bracket_depth > 0) {
                --bracket_depth;
            }
            break;
        case ',':
            if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0) {
                parts->push_back(
                    std::string(TrimLuaWhitespace(value.substr(part_begin, i - part_begin))));
                part_begin = i + 1;
            }
            break;
        default:
            break;
        }
    }

    parts->push_back(std::string(TrimLuaWhitespace(value.substr(part_begin))));
    return !parts->empty();
}

bool ParseRenderedSingleCallExpression(std::string_view line,
                                       std::string* callee,
                                       std::vector<std::string>* arguments) {
    if (callee == nullptr || arguments == nullptr) {
        return false;
    }

    line = TrimLuaWhitespace(StripLuaLineComment(line));
    if (line.empty() || line.find('=') != std::string_view::npos || !line.ends_with(')')) {
        return false;
    }

    const std::size_t open = line.find('(');
    if (open == std::string_view::npos || open == 0) {
        return false;
    }

    std::size_t close = std::string_view::npos;
    if (!FindRenderedMatchingParenLocal(line, open, &close) || close + 1 != line.size()) {
        return false;
    }

    const std::string_view callee_view = TrimLuaWhitespace(line.substr(0, open));
    if (callee_view.empty()) {
        return false;
    }

    *callee = std::string(callee_view);
    return SplitRenderedTopLevelCommaChainLocal(line.substr(open + 1, close - open - 1),
                                                arguments);
}

bool TryExtractRenderedIfConditionLocal(std::string_view line, std::string* condition) {
    if (condition == nullptr) {
        return false;
    }

    line = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!line.starts_with("if ") || !line.ends_with(" then")) {
        return false;
    }

    *condition = std::string(TrimLuaWhitespace(line.substr(3, line.size() - 8)));
    return !condition->empty();
}

bool ParseCallFirstArgument(std::string_view expression,
                            std::string* callee,
                            std::string* first_argument) {
    if (callee == nullptr || first_argument == nullptr) {
        return false;
    }

    expression = TrimLuaWhitespace(expression);
    if (expression.empty() || !expression.ends_with(')')) {
        return false;
    }

    const std::size_t open = expression.find('(');
    if (open == std::string_view::npos || open == 0) {
        return false;
    }

    const std::string_view callee_view = TrimLuaWhitespace(expression.substr(0, open));
    if (callee_view.empty()) {
        return false;
    }

    int paren_depth = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    std::size_t first_comma = std::string_view::npos;
    std::size_t close = std::string_view::npos;
    for (std::size_t i = open; i < expression.size(); ++i) {
        const char ch = expression[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < expression.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < expression.size()) {
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
            ++paren_depth;
            continue;
        }
        if (ch == ')') {
            --paren_depth;
            if (paren_depth == 0) {
                close = i;
                break;
            }
            continue;
        }
        if (ch == ',' && paren_depth == 1) {
            first_comma = i;
            break;
        }
    }

    if (close == std::string_view::npos && first_comma == std::string_view::npos) {
        close = expression.size() - 1;
    }
    if (close == std::string_view::npos) {
        for (std::size_t i = first_comma + 1; i < expression.size(); ++i) {
            if (expression[i] == ')') {
                close = i;
                break;
            }
        }
    }
    if (close == std::string_view::npos || close <= open + 1) {
        return false;
    }

    const std::size_t arg_end = first_comma == std::string_view::npos ? close : first_comma;
    *callee = std::string(callee_view);
    *first_argument =
        std::string(TrimLuaWhitespace(expression.substr(open + 1, arg_end - open - 1)));
    return !first_argument->empty();
}

bool IsRenderedSeedAssignmentFor(std::string_view line, std::string_view lhs) {
    std::string assign_lhs;
    std::string assign_rhs;
    if (!ParseSingleRenderedAssignment(line, &assign_lhs, &assign_rhs) || assign_lhs != lhs) {
        return false;
    }

    assign_rhs = std::string(TrimLuaWhitespace(assign_rhs));
    if (assign_rhs.starts_with("(") && assign_rhs.ends_with(")")) {
        assign_rhs = assign_rhs.substr(1, assign_rhs.size() - 2);
    }
    const std::string prefix = std::string(lhs) + " or ";
    return assign_rhs.starts_with(prefix);
}

bool ParseCallFirstTwoArguments(std::string_view expression,
                                std::string* callee,
                                std::string* first_argument,
                                std::string* second_argument) {
    if (callee == nullptr || first_argument == nullptr || second_argument == nullptr) {
        return false;
    }

    expression = TrimLuaWhitespace(expression);
    if (expression.empty() || !expression.ends_with(')')) {
        return false;
    }

    const std::size_t open = expression.find('(');
    if (open == std::string_view::npos || open == 0) {
        return false;
    }

    const std::string_view callee_view = TrimLuaWhitespace(expression.substr(0, open));
    if (callee_view.empty()) {
        return false;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    std::size_t close = std::string_view::npos;
    std::size_t first_comma = std::string_view::npos;
    std::size_t second_comma = std::string_view::npos;
    for (std::size_t i = open; i < expression.size(); ++i) {
        const char ch = expression[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < expression.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < expression.size()) {
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
        switch (ch) {
        case '(':
            ++paren_depth;
            break;
        case ')':
            --paren_depth;
            if (paren_depth == 0) {
                close = i;
            }
            break;
        case '{':
            ++brace_depth;
            break;
        case '}':
            if (brace_depth > 0) {
                --brace_depth;
            }
            break;
        case '[':
            ++bracket_depth;
            break;
        case ']':
            if (bracket_depth > 0) {
                --bracket_depth;
            }
            break;
        case ',':
            if (paren_depth == 1 && brace_depth == 0 && bracket_depth == 0) {
                if (first_comma == std::string_view::npos) {
                    first_comma = i;
                } else if (second_comma == std::string_view::npos) {
                    second_comma = i;
                }
            }
            break;
        default:
            break;
        }

        if (close != std::string_view::npos) {
            break;
        }
    }

    if (close == std::string_view::npos || first_comma == std::string_view::npos) {
        return false;
    }

    const std::size_t second_end = second_comma == std::string_view::npos ? close : second_comma;
    *callee = std::string(callee_view);
    *first_argument = std::string(
        TrimLuaWhitespace(expression.substr(open + 1, first_comma - open - 1)));
    *second_argument = std::string(
        TrimLuaWhitespace(expression.substr(first_comma + 1, second_end - first_comma - 1)));
    return !first_argument->empty() && !second_argument->empty();
}

bool ReplaceRenderedIdentifierUsesLocal(std::string_view text,
                                        std::string_view identifier,
                                        std::string_view replacement,
                                        std::string* rewritten) {
    if (rewritten == nullptr || identifier.empty()) {
        return false;
    }

    std::string result;
    result.reserve(text.size() + replacement.size());
    std::size_t offset = 0;
    bool changed = false;
    while (true) {
        const std::size_t found = FindIdentifierUse(text, identifier, offset);
        if (found == std::string_view::npos) {
            result.append(text.substr(offset));
            break;
        }

        result.append(text.substr(offset, found - offset));
        result.append(replacement);
        offset = found + identifier.size();
        changed = true;
    }

    if (!changed) {
        return false;
    }

    *rewritten = std::move(result);
    return true;
}

bool ExpandRenderedTemporaryUses(std::string_view expression,
                                 const std::unordered_map<std::string, std::string>& temp_values,
                                 std::string* expanded_expression,
                                 bool* changed,
                                 bool* unresolved_temps);

bool ExpandRenderedTemporaryUses(std::string_view expression,
                                 const std::unordered_map<std::string, std::string>& temp_values,
                                 std::string* expanded_expression,
                                 bool* changed,
                                 bool* unresolved_temps) {
    if (expanded_expression == nullptr) {
        return false;
    }

    std::string current(expression);
    bool any_change = false;
    RunNativeFirstPassLoop(8, [&](int /*pass*/) {
        bool pass_changed = false;
        const std::string scan = current;
        std::vector<std::pair<std::string, std::string>> replacements;
        std::unordered_set<std::string> seen_tokens;
        ForEachLuaToken(scan, [&](std::string_view token) {
            const std::string token_name(token);
            if (!seen_tokens.insert(token_name).second || !IsRenderTemporaryName(token)) {
                return;
            }
            const auto found = temp_values.find(token_name);
            if (found == temp_values.end()) {
                return;
            }
            replacements.emplace_back(token_name, WrapInlineExpression(found->second));
        });
        for (const auto& [token_name, replacement] : replacements) {
            std::string rewritten;
            if (!ReplaceRenderedIdentifierUsesLocal(current,
                                                    token_name,
                                                    replacement,
                                                    &rewritten)) {
                continue;
            }
            current = std::move(rewritten);
            pass_changed = true;
            any_change = true;
        }
        if (!pass_changed) {
            return false;
        }
        return true;
    });

    bool unresolved = false;
    ForEachLuaToken(current, [&](std::string_view token) {
        if (IsRenderTemporaryName(token) &&
            temp_values.find(std::string(token)) == temp_values.end()) {
            unresolved = true;
        }
    });

    if (changed != nullptr) {
        *changed = any_change;
    }
    if (unresolved_temps != nullptr) {
        *unresolved_temps = unresolved;
    }
    *expanded_expression = std::move(current);
    return true;
}

bool TryRepairExpandedAssignmentsFromRaw(std::string_view raw_source,
                                         std::string* clean_source) {
    if (clean_source == nullptr) {
        return false;
    }

    std::vector<std::string> raw_lines = SplitRenderedLines(raw_source);
    std::vector<std::string> clean_lines = SplitRenderedLines(*clean_source);
    const std::vector<RenderedFunctionBlock> raw_blocks =
        CollectTopLevelRenderedFunctionBlocks(raw_lines);
    const std::vector<RenderedFunctionBlock> clean_blocks =
        CollectTopLevelRenderedFunctionBlocks(clean_lines);
    std::unordered_map<std::string, std::string> temp_values;
    bool changed = false;

    for (std::size_t raw_index = 0; raw_index < raw_lines.size(); ++raw_index) {
        if (IsRenderedFunctionHeaderLine(raw_lines[raw_index])) {
            temp_values.clear();
            continue;
        }

        bool raw_is_local = false;
        std::string raw_lhs;
        std::string raw_rhs;
        if (!ParseSingleRenderedAssignment(raw_lines[raw_index],
                                           &raw_is_local,
                                           &raw_lhs,
                                           &raw_rhs)) {
            continue;
        }

        if (IsRenderTemporaryName(raw_lhs)) {
            if (raw_index + 3 < raw_lines.size()) {
                const std::string_view header =
                    TrimLuaWhitespace(StripLuaLineComment(raw_lines[raw_index + 1]));
                bool default_is_local = false;
                std::string default_lhs;
                std::string default_rhs;
                if (header == ("if not " + raw_lhs + " then") &&
                    ParseSingleRenderedAssignment(raw_lines[raw_index + 2],
                                                  &default_is_local,
                                                  &default_lhs,
                                                  &default_rhs) &&
                    !default_is_local && default_lhs == raw_lhs &&
                    TrimLuaWhitespace(StripLuaLineComment(raw_lines[raw_index + 3])) ==
                        "end") {
                    temp_values[raw_lhs] =
                        "(" + std::string(TrimLuaWhitespace(raw_rhs)) + " or " +
                        std::string(TrimLuaWhitespace(default_rhs)) + ")";
                    raw_index += 3;
                    continue;
                }
            }

            std::string expanded_rhs;
            ExpandRenderedTemporaryUses(raw_rhs, temp_values, &expanded_rhs, nullptr, nullptr);
            temp_values[raw_lhs] = std::move(expanded_rhs);
            continue;
        }

        std::string expanded_rhs;
        bool rhs_changed = false;
        bool unresolved_temps = false;
        ExpandRenderedTemporaryUses(raw_rhs,
                                    temp_values,
                                    &expanded_rhs,
                                    &rhs_changed,
                                    &unresolved_temps);
        if (!rhs_changed || unresolved_temps) {
            continue;
        }

        std::size_t clean_begin = 0;
        std::size_t clean_end = clean_lines.size();
        for (std::size_t block_index = 0; block_index < raw_blocks.size() &&
                                         block_index < clean_blocks.size();
             ++block_index) {
            const RenderedFunctionBlock& raw_block = raw_blocks[block_index];
            if (raw_index < raw_block.begin || raw_index > raw_block.end) {
                continue;
            }
            const RenderedFunctionBlock& clean_block = clean_blocks[block_index];
            if (raw_block.header_line == clean_block.header_line) {
                clean_begin = clean_block.begin;
                clean_end = clean_block.end + 1;
            }
            break;
        }

        std::size_t clean_match = std::string::npos;
        bool clean_match_is_local = false;
        std::string clean_match_rhs;
        for (std::size_t clean_index = clean_begin; clean_index < clean_end; ++clean_index) {
            bool clean_is_local = false;
            std::string clean_lhs;
            std::string clean_rhs;
            if (!ParseSingleRenderedAssignment(clean_lines[clean_index],
                                               &clean_is_local,
                                               &clean_lhs,
                                               &clean_rhs) ||
                clean_lhs != raw_lhs) {
                continue;
            }

            bool clean_uses_temp = false;
            ForEachLuaToken(clean_rhs, [&](std::string_view token) {
                if (IsRenderTemporaryName(token)) {
                    clean_uses_temp = true;
                }
            });
            if (!clean_uses_temp) {
                continue;
            }

            if (clean_match != std::string::npos) {
                clean_match = std::string::npos;
                break;
            }
            clean_match = clean_index;
            clean_match_is_local = clean_is_local;
            clean_match_rhs = std::move(clean_rhs);
        }

        if (clean_match == std::string::npos ||
            TrimLuaWhitespace(clean_match_rhs) == TrimLuaWhitespace(expanded_rhs)) {
            continue;
        }

        const std::string indent(clean_lines[clean_match].substr(
            0, clean_lines[clean_match].find_first_not_of(" \t")));
        clean_lines[clean_match] =
            indent + (clean_match_is_local ? "local " : "") + raw_lhs + " = " + expanded_rhs;
        changed = true;
    }

    if (!changed) {
        return false;
    }

    std::ostringstream rebuilt;
    for (const std::string& line : clean_lines) {
        rebuilt << line << '\n';
    }
    *clean_source = rebuilt.str();
    return true;
}

std::string ExtractRenderedFunctionTerminalNameForRepair(std::string_view header_line) {
    const std::string normalized = NormalizeRenderedFunctionHeaderLine(header_line);
    if (normalized.empty()) {
        return {};
    }

    if (normalized.starts_with("function ")) {
        std::string_view name_and_args = TrimLuaWhitespace(std::string_view(normalized).substr(9));
        const std::size_t open_paren = name_and_args.find('(');
        const std::string_view full_name =
            open_paren == std::string_view::npos ? name_and_args : name_and_args.substr(0, open_paren);
        const std::size_t last_access = full_name.find_last_of(".:");
        return std::string(last_access == std::string_view::npos
                               ? TrimLuaWhitespace(full_name)
                               : TrimLuaWhitespace(full_name.substr(last_access + 1)));
    }

    const std::size_t equal = normalized.find('=');
    if (equal == std::string_view::npos) {
        return {};
    }
    return std::string(TrimLuaWhitespace(std::string_view(normalized).substr(0, equal)));
}

bool TryRepairStatementTemporaryLocalsFromRaw(std::string_view raw_source,
                                              std::string* clean_source) {
    if (clean_source == nullptr) {
        return false;
    }
    if (raw_source.find("local r") == std::string_view::npos &&
        raw_source.find(" r") == std::string_view::npos) {
        return false;
    }

    const auto normalize_for_match = [](std::string expression) {
        const auto replace_all = [](std::string* text,
                                    std::string_view needle,
                                    std::string_view replacement) {
            if (text == nullptr || needle.empty()) {
                return false;
            }

            bool changed = false;
            std::size_t offset = 0;
            while (offset < text->size()) {
                const std::size_t found = text->find(needle, offset);
                if (found == std::string::npos) {
                    break;
                }
                text->replace(found, needle.size(), replacement);
                offset = found + replacement.size();
                changed = true;
            }
            return changed;
        };

        bool pass_changed = false;
        do {
            pass_changed = false;
            pass_changed |= replace_all(&expression, "[(", "[");
            pass_changed |= replace_all(&expression, ")]", "]");
        } while (pass_changed);
        return NormalizeRenderedTrivialParenthesizedLiterals(
            std::string(TrimLuaWhitespace(expression)));
    };

    struct RawTempAssignment {
        std::size_t line_index = 0;
        std::string line;
    };

    const std::vector<std::string> raw_lines = SplitRenderedLines(raw_source);
    std::vector<std::string> clean_lines = SplitRenderedLines(*clean_source);
    const std::vector<RenderedFunctionBlock> raw_blocks =
        CollectTopLevelRenderedFunctionBlocks(raw_lines);
    if (raw_blocks.empty()) {
        return false;
    }

    bool changed = false;
    std::size_t pass_count = 0;
    constexpr std::size_t kMaxRepairPasses = 160;
    std::unordered_set<std::size_t> seen_pass_states;
    seen_pass_states.reserve(kMaxRepairPasses);
    for (;;) {
        if (pass_count >= kMaxRepairPasses ||
            !seen_pass_states.insert(HashRenderedLines(clean_lines)).second) {
            break;
        }
        ++pass_count;
        const std::vector<RenderedFunctionBlock> clean_blocks =
            CollectTopLevelRenderedFunctionBlocks(clean_lines);
        if (clean_blocks.empty()) {
            break;
        }

        bool pass_changed = false;
        for (std::size_t block_index = 0; block_index < raw_blocks.size() && !pass_changed;
             ++block_index) {
            const RenderedFunctionBlock& raw_block = raw_blocks[block_index];
            const auto clean_match = [&]() -> const RenderedFunctionBlock* {
                for (const RenderedFunctionBlock& candidate : clean_blocks) {
                    if (candidate.header_line == raw_block.header_line) {
                        return &candidate;
                    }
                }

                const std::string raw_terminal_name =
                    ExtractRenderedFunctionTerminalNameForRepair(raw_block.header_line);
                if (raw_terminal_name.empty()) {
                    return nullptr;
                }

                const RenderedFunctionBlock* matched = nullptr;
                for (const RenderedFunctionBlock& candidate : clean_blocks) {
                    if (ExtractRenderedFunctionTerminalNameForRepair(candidate.header_line) !=
                        raw_terminal_name) {
                        continue;
                    }
                    if (matched != nullptr) {
                        return nullptr;
                    }
                    matched = &candidate;
                }
                return matched;
            }();
            if (clean_match == nullptr) {
                continue;
            }
            const RenderedFunctionBlock& clean_block = *clean_match;

            std::unordered_map<std::string, std::string> temp_values;
            std::unordered_map<std::string, RawTempAssignment> temp_assignments;
            for (std::size_t raw_index = raw_block.begin + 1;
                 raw_index <= raw_block.end && raw_index < raw_lines.size() && !pass_changed;
                 ++raw_index) {
                bool raw_is_local = false;
                std::string raw_lhs;
                std::string raw_rhs;
                if (ParseSingleRenderedAssignment(raw_lines[raw_index],
                                                  &raw_is_local,
                                                  &raw_lhs,
                                                  &raw_rhs) &&
                    IsRenderTemporaryName(raw_lhs)) {
                    std::string expanded_rhs = raw_rhs;
                    ExpandRenderedTemporaryUses(raw_rhs,
                                                temp_values,
                                                &expanded_rhs,
                                                nullptr,
                                                nullptr);
                    temp_values[raw_lhs] = std::move(expanded_rhs);
                    temp_assignments[raw_lhs] = {raw_index, raw_lines[raw_index]};
                }

                std::vector<std::string> used_temps;
                std::unordered_set<std::string> seen_temps;
                ForEachLuaToken(raw_lines[raw_index], [&](std::string_view token) {
                    const std::string temp_name(token);
                    if (!IsRenderTemporaryName(token) ||
                        !temp_assignments.contains(temp_name) ||
                        !seen_temps.insert(temp_name).second) {
                        return;
                    }
                    used_temps.push_back(temp_name);
                });
                if (used_temps.empty()) {
                    continue;
                }

                std::string expanded_raw_line = raw_lines[raw_index];
                bool line_changed = false;
                bool unresolved_temps = false;
                ExpandRenderedTemporaryUses(raw_lines[raw_index],
                                            temp_values,
                                            &expanded_raw_line,
                                            &line_changed,
                                            &unresolved_temps);
                if (!line_changed || unresolved_temps) {
                    continue;
                }

                const std::string normalized_expanded_line =
                    normalize_for_match(expanded_raw_line);
                const std::string normalized_raw_line =
                    normalize_for_match(raw_lines[raw_index]);
                if (normalized_expanded_line.empty() ||
                    normalized_expanded_line == normalized_raw_line) {
                    continue;
                }

                for (std::size_t clean_index = clean_block.begin + 1;
                     clean_index <= clean_block.end && clean_index < clean_lines.size();
                     ++clean_index) {
                    if (normalize_for_match(clean_lines[clean_index]) !=
                            normalized_expanded_line ||
                        normalize_for_match(clean_lines[clean_index]) ==
                            normalized_raw_line) {
                        continue;
                    }

                    std::vector<std::pair<std::size_t, std::string>> lines_to_insert;
                    for (const std::string& temp_name : used_temps) {
                        const auto found = temp_assignments.find(temp_name);
                        if (found == temp_assignments.end()) {
                            continue;
                        }

                        const std::string normalized_temp_line =
                            normalize_for_match(found->second.line);
                        bool already_present = false;
                        const std::size_t probe_begin =
                            clean_index > 6 ? clean_index - 6 : clean_block.begin + 1;
                        for (std::size_t probe = probe_begin;
                             probe < clean_index && probe < clean_lines.size();
                             ++probe) {
                            if (normalize_for_match(clean_lines[probe]) ==
                                normalized_temp_line) {
                                already_present = true;
                                break;
                            }
                        }
                        if (!already_present) {
                            lines_to_insert.emplace_back(found->second.line_index,
                                                         found->second.line);
                        }
                    }

                    std::sort(lines_to_insert.begin(),
                              lines_to_insert.end(),
                              [](const auto& lhs, const auto& rhs) {
                                  return lhs.first < rhs.first;
                              });
                    lines_to_insert.erase(
                        std::unique(lines_to_insert.begin(),
                                    lines_to_insert.end(),
                                    [](const auto& lhs, const auto& rhs) {
                                        return lhs.first == rhs.first;
                                    }),
                        lines_to_insert.end());

                    std::size_t insert_index = clean_index;
                    for (const auto& line_to_insert : lines_to_insert) {
                        clean_lines.insert(clean_lines.begin() +
                                               static_cast<std::ptrdiff_t>(insert_index),
                                           line_to_insert.second);
                        ++insert_index;
                    }
                    clean_lines[insert_index] = raw_lines[raw_index];
                    changed = true;
                    pass_changed = true;
                    break;
                }
            }
        }

        if (!pass_changed) {
            break;
        }
    }

    if (!changed) {
        return false;
    }

    std::ostringstream rebuilt;
    for (const std::string& line : clean_lines) {
        rebuilt << line << '\n';
    }
    *clean_source = rebuilt.str();
    return true;
}

bool TryRepairSuspiciousFunctionBlocksFromRaw(std::string_view raw_source,
                                              std::string* clean_source) {
    if (clean_source == nullptr) {
        return false;
    }

    const std::vector<std::string> raw_lines = SplitRenderedLines(raw_source);
    std::vector<std::string> clean_lines = SplitRenderedLines(*clean_source);
    const std::vector<RenderedFunctionBlock> raw_blocks =
        CollectTopLevelRenderedFunctionBlocks(raw_lines);
    std::vector<RenderedFunctionBlock> clean_blocks =
        CollectTopLevelRenderedFunctionBlocks(clean_lines);
    if (raw_blocks.empty() || clean_blocks.empty()) {
        return false;
    }

    bool changed = false;
    const auto parse_pairs_loop_header = [](std::string_view line) -> bool {
        line = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!line.starts_with("for ") || !line.ends_with(" do")) {
            return false;
        }
        const std::size_t in_pos = line.find(" in ");
        if (in_pos == std::string_view::npos) {
            return false;
        }
        const std::string_view rhs =
            TrimLuaWhitespace(line.substr(in_pos + 4, line.size() - (in_pos + 4) - 3));
        return rhs.starts_with("pairs(") && rhs.ends_with(')');
    };
    const auto block_has_guarded_loop_fallback =
        [&](const std::vector<std::string>& lines, const RenderedFunctionBlock& block) -> bool {
        for (std::size_t line_index = block.begin; line_index + 2 <= block.end &&
                                              line_index + 2 < lines.size();
             ++line_index) {
            if (!parse_pairs_loop_header(lines[line_index])) {
                continue;
            }

            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index + 1],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
                assign_is_local ||
                TrimLuaWhitespace(StripLuaLineComment(lines[line_index + 2])) != "end") {
                continue;
            }

            const std::string trimmed_rhs =
                std::string(TrimLuaWhitespace(StripLuaLineComment(assign_rhs)));
            if (trimmed_rhs == "(" + assign_lhs + " or value)" ||
                trimmed_rhs.find("(" + assign_lhs + " or ") == 0) {
                return true;
            }
        }
        return false;
    };
    const auto block_has_empty_pairs_loop =
        [&](const std::vector<std::string>& lines, const RenderedFunctionBlock& block) -> bool {
        for (std::size_t line_index = block.begin; line_index + 1 <= block.end &&
                                              line_index + 1 < lines.size();
             ++line_index) {
            if (!parse_pairs_loop_header(lines[line_index])) {
                continue;
            }

            std::size_t next_nonempty = line_index + 1;
            while (next_nonempty <= block.end && next_nonempty < lines.size() &&
                   TrimLuaWhitespace(StripLuaLineComment(lines[next_nonempty])).empty()) {
                ++next_nonempty;
            }
            if (next_nonempty <= block.end && next_nonempty < lines.size() &&
                TrimLuaWhitespace(StripLuaLineComment(lines[next_nonempty])) == "end") {
                return true;
            }
        }
        return false;
    };
    for (std::size_t block_index = raw_blocks.size(); block_index-- > 0;) {
        const RenderedFunctionBlock& raw_block = raw_blocks[block_index];
        const auto clean_match = std::find_if(
            clean_blocks.begin(),
            clean_blocks.end(),
            [&](const RenderedFunctionBlock& candidate) {
                return candidate.header_line == raw_block.header_line;
            });
        if (clean_match == clean_blocks.end()) {
            continue;
        }
        const RenderedFunctionBlock& clean_block = *clean_match;
        if (clean_block.begin >= clean_lines.size() || clean_block.end >= clean_lines.size() ||
            clean_block.begin > clean_block.end || raw_block.begin >= raw_lines.size() ||
            raw_block.end >= raw_lines.size() || raw_block.begin > raw_block.end) {
            continue;
        }

        const std::size_t raw_suspicious =
            CountSuspiciousUndeclaredIdentifiersInFunctionBlock(raw_lines, raw_block);
        const std::size_t clean_suspicious =
            CountSuspiciousUndeclaredIdentifiersInFunctionBlock(clean_lines, clean_block);
        const std::size_t raw_forward_temporary_uses =
            CountForwardReferencedTemporaryUsesInFunctionBlock(raw_lines, raw_block);
        const std::size_t clean_forward_temporary_uses =
            CountForwardReferencedTemporaryUsesInFunctionBlock(clean_lines, clean_block);
        std::string raw_block_text;
        for (std::size_t line_index = raw_block.begin;
             line_index <= raw_block.end && line_index < raw_lines.size();
             ++line_index) {
            raw_block_text += raw_lines[line_index];
            raw_block_text.push_back('\n');
        }
        std::string clean_block_text;
        for (std::size_t line_index = clean_block.begin;
             line_index <= clean_block.end && line_index < clean_lines.size();
             ++line_index) {
            clean_block_text += clean_lines[line_index];
            clean_block_text.push_back('\n');
        }

        const bool clean_has_nil_math_max =
            clean_block_text.find("math.max(nil") != std::string::npos;
        const bool raw_has_nil_math_max =
            raw_block_text.find("math.max(nil") != std::string::npos;
        const LuaReadabilitySummary raw_block_summary =
            SummarizeLuaReadability(raw_block_text);
        const LuaReadabilitySummary clean_block_summary =
            SummarizeLuaReadability(clean_block_text);
        const bool clean_block_has_active_readability_issue =
            clean_block_summary.temporary_identifier_count != 0 ||
            clean_block_summary.temporary_leading_line_count != 0 ||
            clean_block_summary.temporary_local_declaration_count != 0 ||
            clean_block_summary.temporary_assignment_count != 0 ||
            clean_block_summary.temporary_table_field_assignment_count != 0 ||
            clean_block_summary.temporary_self_reference_assignment_count != 0 ||
            clean_block_summary.suspicious_temporary_rhs_assignment_count != 0 ||
            clean_block_summary.suspicious_self_call_assignment_count != 0 ||
            clean_block_summary.obvious_nil_call_count != 0;
        const std::size_t raw_block_risk = ScoreLuaReadabilityRisk(raw_block_summary);
        const std::size_t clean_block_risk = ScoreLuaReadabilityRisk(clean_block_summary);
        const bool raw_block_has_material_readability_gain =
            raw_block_summary.obvious_nil_call_count <
                clean_block_summary.obvious_nil_call_count ||
            raw_block_summary.suspicious_temporary_rhs_assignment_count <
                clean_block_summary.suspicious_temporary_rhs_assignment_count ||
            raw_block_summary.suspicious_self_call_assignment_count <
                clean_block_summary.suspicious_self_call_assignment_count;
        const bool raw_block_risk_is_comparable =
            raw_block_risk <= clean_block_risk + 12;
        const bool raw_block_has_guarded_loop_fallback =
            block_has_guarded_loop_fallback(raw_lines, raw_block);
        const bool clean_block_has_empty_pairs_loop =
            block_has_empty_pairs_loop(clean_lines, clean_block);

        if (clean_suspicious <= raw_suspicious &&
            clean_forward_temporary_uses <= raw_forward_temporary_uses &&
            !(clean_has_nil_math_max && !raw_has_nil_math_max) &&
            !(clean_block_has_active_readability_issue &&
              raw_block_has_material_readability_gain &&
              raw_block_risk_is_comparable) &&
            !(raw_block_has_guarded_loop_fallback && clean_block_has_empty_pairs_loop)) {
            continue;
        }

        std::vector<std::string> replacement_lines(
            raw_lines.begin() + static_cast<std::ptrdiff_t>(raw_block.begin),
            raw_lines.begin() + static_cast<std::ptrdiff_t>(raw_block.end + 1));
        if (!replacement_lines.empty()) {
            replacement_lines.front() = clean_lines[clean_block.begin];
        }
        clean_lines.erase(clean_lines.begin() + static_cast<std::ptrdiff_t>(clean_block.begin),
                          clean_lines.begin() +
                              static_cast<std::ptrdiff_t>(clean_block.end + 1));
        clean_lines.insert(clean_lines.begin() +
                               static_cast<std::ptrdiff_t>(clean_block.begin),
                           replacement_lines.begin(),
                           replacement_lines.end());
        clean_blocks = CollectTopLevelRenderedFunctionBlocks(clean_lines);
        if (clean_blocks.empty()) {
            break;
        }
        changed = true;
    }

    if (!changed) {
        return false;
    }

    std::ostringstream rebuilt;
    for (const std::string& line : clean_lines) {
        rebuilt << line << '\n';
    }
    *clean_source = rebuilt.str();
    return true;
}


bool BlockHasTruthyProviderFallbackCandidate(const std::vector<std::string>& lines,
                                             const RenderedFunctionBlock& block) {
    for (std::size_t line_index = block.begin + 1;
         line_index <= block.end && line_index < lines.size();
         ++line_index) {
        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseSingleRenderedAssignment(lines[line_index],
                                           &fallback_is_local,
                                           &fallback_lhs,
                                           &fallback_rhs) ||
            fallback_is_local) {
            continue;
        }

        const std::string expected_prefix = "(" + fallback_lhs + " or ";
        const std::string_view trimmed_fallback_rhs =
            TrimLuaWhitespace(StripLuaLineComment(fallback_rhs));
        if (!trimmed_fallback_rhs.starts_with(expected_prefix) ||
            !trimmed_fallback_rhs.ends_with(')')) {
            continue;
        }

        const std::string_view provider_expr = TrimLuaWhitespace(
            trimmed_fallback_rhs.substr(expected_prefix.size(),
                                        trimmed_fallback_rhs.size() - expected_prefix.size() - 1));
        if (provider_expr.empty() || provider_expr == "nil") {
            continue;
        }

        for (std::size_t earlier = block.begin + 1; earlier < line_index; ++earlier) {
            bool earlier_is_local = false;
            std::string earlier_lhs;
            std::string earlier_rhs;
            if (!ParseSingleRenderedAssignment(lines[earlier],
                                               &earlier_is_local,
                                               &earlier_lhs,
                                               &earlier_rhs) ||
                earlier_is_local || earlier_lhs != fallback_lhs ||
                !LooksLikeBooleanValuedExpression(earlier_rhs)) {
                continue;
            }

            return true;
        }
    }

    return false;
}

bool BlockHasStandaloneReplacementCandidate(const std::vector<std::string>& lines,
                                            const RenderedFunctionBlock& block) {
    if (block.begin >= lines.size() || block.end >= lines.size() || block.begin > block.end) {
        return false;
    }

    for (std::size_t line_index = block.begin; line_index <= block.end; ++line_index) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment(lines[line_index]));
        if (trimmed.empty()) {
            continue;
        }

        if (trimmed.starts_with("for ")) {
            const std::size_t equal = trimmed.find('=');
            const std::size_t do_pos = trimmed.rfind(" do");
            if (equal != std::string_view::npos && do_pos != std::string_view::npos &&
                equal < do_pos) {
                const std::string_view lhs =
                    TrimLuaWhitespace(trimmed.substr(4, equal - 4));
                if (!IsValidLuaIdentifier(lhs)) {
                    return true;
                }
            }
        }

        const std::size_t equal = trimmed.find('=');
        const std::size_t colon = trimmed.find(':');
        const std::size_t dot =
            colon == std::string_view::npos ? std::string_view::npos
                                            : trimmed.find('.', colon + 1);
        if (equal != std::string_view::npos && colon != std::string_view::npos &&
            dot != std::string_view::npos && colon < equal && dot < equal) {
            return true;
        }
    }

    return false;
}

bool TryRepairInvalidFunctionBlocksFromStandaloneRender(const Lua51Function& function,
                                                        std::string_view chunk_name,
                                                        std::string* source,
                                                        std::string* note,
                                                        std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    const std::string baseline_error = ValidateLuaSource(*source, chunk_name);
    if (baseline_error.empty()) {
        return false;
    }

    std::vector<std::string> clean_lines = SplitRenderedLines(*source);
    std::vector<RenderedFunctionBlock> clean_blocks =
        CollectTopLevelRenderedFunctionBlocks(clean_lines);
    if (clean_blocks.empty()) {
        return false;
    }

    std::vector<const Lua51Function*> functions;
    functions.reserve(function.child_functions.size());
    for (const Lua51Function& child : function.child_functions) {
        functions.push_back(&child);
    }

    const std::size_t replacement_count = std::min(clean_blocks.size(), functions.size());
    std::string best_source;
    std::string best_error;
    bool found = false;

    for (std::size_t block_index = replacement_count; block_index-- > 0;) {
        const RenderedFunctionBlock& clean_block = clean_blocks[block_index];
        if (!BlockHasStandaloneReplacementCandidate(clean_lines, clean_block)) {
            continue;
        }

        std::string standalone_source;
        std::string standalone_error;
        if (!RenderLua51StraightLineFunction(*functions[block_index],
                                             &standalone_source,
                                             &standalone_error)) {
            continue;
        }
        if (!ValidateLuaSource(standalone_source, chunk_name).empty()) {
            continue;
        }

        std::vector<std::string> standalone_lines = SplitRenderedLines(standalone_source);
        if (standalone_lines.size() < 2) {
            continue;
        }

        standalone_lines.front() = clean_lines[clean_block.begin];
        std::vector<std::string> trial_lines = clean_lines;
        trial_lines.erase(trial_lines.begin() + static_cast<std::ptrdiff_t>(clean_block.begin),
                          trial_lines.begin() +
                              static_cast<std::ptrdiff_t>(clean_block.end + 1));
        trial_lines.insert(trial_lines.begin() +
                               static_cast<std::ptrdiff_t>(clean_block.begin),
                           standalone_lines.begin(),
                           standalone_lines.end());

        const std::string trial_source = JoinRenderedLines(trial_lines);
        const std::string trial_error = ValidateLuaSource(trial_source, chunk_name);
        if (!trial_error.empty() &&
            !IsValidationOutcomeBetter(baseline_error, trial_error) &&
            (!found || !IsValidationOutcomeBetter(best_error, trial_error))) {
            continue;
        }

        if (!found || trial_error.empty() ||
            IsValidationOutcomeBetter(best_error, trial_error)) {
            best_source = trial_source;
            best_error = trial_error;
            found = true;
            if (trial_error.empty()) {
                break;
            }
        }
    }

    if (!found) {
        return false;
    }

    *source = std::move(best_source);
    AppendCandidateNote(note, note_prefix, best_error.empty()
                                          ? "standalone_function_block_replaced"
                                          : "standalone_function_block_progress");
    return true;
}

bool TryRenderLuaStringConstantForAssignedClosureRepair(const Lua51Function& function,
                                                        int rk,
                                                        std::string* value_out) {
    if (value_out == nullptr) {
        return false;
    }

    int constant_index = rk;
    if (refii::lua::IsConstantReference(rk)) {
        constant_index &= 0xFF;
    }
    if (constant_index < 0 ||
        static_cast<std::size_t>(constant_index) >= function.constants.size()) {
        return false;
    }

    const Lua51Constant& constant =
        function.constants[static_cast<std::size_t>(constant_index)];
    if (constant.type != Lua51ConstantType::kString) {
        return false;
    }

    *value_out = constant.string_value;
    return true;
}

enum class TopLevelChildClosureBindingKind : uint8_t {
    kNone = 0,
    kAssignedPrefix,
    kLocalName,
};

struct TopLevelChildClosureBinding {
    TopLevelChildClosureBindingKind kind = TopLevelChildClosureBindingKind::kNone;
    std::string binding;
};

bool TryInferTopLevelChildClosureBinding(const Lua51Function& main_function,
                                         std::size_t child_index,
                                         TopLevelChildClosureBinding* binding_out) {
    if (binding_out == nullptr || child_index == 0 ||
        child_index > main_function.child_functions.size()) {
        return false;
    }

    *binding_out = {};
    const std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(main_function);
    std::vector<std::string> register_exprs(
        std::min<std::size_t>(main_function.max_stack_size, 250), std::string{});
    const auto active_local_after = [&](std::size_t instruction_index,
                                        int reg) -> const Lua51Declaration* {
        if (instructions.empty()) {
            return nullptr;
        }

        const std::size_t current_index = std::min(instruction_index, instructions.size() - 1);
        if (const Lua51Declaration* current_local =
                TryGetActiveLua51Declaration(main_function, current_index, reg);
            current_local != nullptr) {
            return current_local;
        }

        if (current_index + 1 < instructions.size()) {
            return TryGetActiveLua51Declaration(main_function, current_index + 1, reg);
        }

        return nullptr;
    };

    for (std::size_t instruction_index = 0; instruction_index < instructions.size();
         ++instruction_index) {
        const Lua51Instruction& instruction = instructions[instruction_index];
        switch (instruction.opcode) {
            case Lua51Opcode::kGetGlobal: {
                if (instruction.a < 0 ||
                    static_cast<std::size_t>(instruction.a) >= register_exprs.size()) {
                    break;
                }
                std::string global_name;
                if (TryRenderLuaStringConstantForAssignedClosureRepair(main_function,
                                                                       instruction.bx,
                                                                       &global_name)) {
                    register_exprs[static_cast<std::size_t>(instruction.a)] =
                        std::move(global_name);
                } else {
                    register_exprs[static_cast<std::size_t>(instruction.a)].clear();
                }
                break;
            }
            case Lua51Opcode::kMove: {
                if (instruction.a < 0 || instruction.b < 0 ||
                    static_cast<std::size_t>(instruction.a) >= register_exprs.size() ||
                    static_cast<std::size_t>(instruction.b) >= register_exprs.size()) {
                    break;
                }
                register_exprs[static_cast<std::size_t>(instruction.a)] =
                    register_exprs[static_cast<std::size_t>(instruction.b)];
                break;
            }
            case Lua51Opcode::kGetTable: {
                if (instruction.a < 0 || instruction.b < 0 ||
                    static_cast<std::size_t>(instruction.a) >= register_exprs.size() ||
                    static_cast<std::size_t>(instruction.b) >= register_exprs.size()) {
                    break;
                }

                std::string key;
                if (!TryRenderLuaStringConstantForAssignedClosureRepair(main_function,
                                                                        instruction.c,
                                                                        &key) ||
                    !IsValidLuaIdentifier(key) || IsLuaKeyword(key)) {
                    register_exprs[static_cast<std::size_t>(instruction.a)].clear();
                    break;
                }

                const std::string& base =
                    register_exprs[static_cast<std::size_t>(instruction.b)];
                if (base.empty()) {
                    register_exprs[static_cast<std::size_t>(instruction.a)].clear();
                    break;
                }

                register_exprs[static_cast<std::size_t>(instruction.a)] =
                    base + "." + key;
                break;
            }
            case Lua51Opcode::kClosure: {
                if (instruction.bx != static_cast<int>(child_index - 1)) {
                    break;
                }

                const Lua51Function& child =
                    main_function.child_functions[child_index - 1];
                std::unordered_set<int> closure_registers = {instruction.a};
                std::string guessed_local_fallback;
                std::size_t probe = instruction_index + 1 +
                                    static_cast<std::size_t>(child.upvalue_count);

                if (const Lua51Declaration* active_local = active_local_after(probe, instruction.a);
                    active_local != nullptr) {
                    if (IsConcreteLua51Declaration(*active_local)) {
                        binding_out->kind = TopLevelChildClosureBindingKind::kLocalName;
                        binding_out->binding = active_local->name;
                        return true;
                    }
                    if (guessed_local_fallback.empty() &&
                        IsRecoverableLua51LocalName(active_local->name)) {
                        guessed_local_fallback = active_local->name;
                    }
                }

                const std::size_t probe_limit =
                    std::min<std::size_t>(instructions.size(), probe + 8);
                for (; probe < probe_limit; ++probe) {
                    const Lua51Instruction& next = instructions[probe];
                    if (next.opcode == Lua51Opcode::kMove &&
                        closure_registers.contains(next.b)) {
                        closure_registers.insert(next.a);
                        if (const Lua51Declaration* moved_local =
                                active_local_after(probe, next.a);
                            moved_local != nullptr) {
                            if (IsConcreteLua51Declaration(*moved_local)) {
                                binding_out->kind = TopLevelChildClosureBindingKind::kLocalName;
                                binding_out->binding = moved_local->name;
                                return true;
                            }
                            if (guessed_local_fallback.empty() &&
                                IsRecoverableLua51LocalName(moved_local->name)) {
                                guessed_local_fallback = moved_local->name;
                            }
                        }
                        continue;
                    }

                    if (next.opcode == Lua51Opcode::kSetGlobal &&
                        closure_registers.contains(next.a)) {
                        std::string global_name;
                        if (!TryRenderLuaStringConstantForAssignedClosureRepair(main_function,
                                                                               next.bx,
                                                                               &global_name) ||
                            !IsValidLuaIdentifier(global_name) || IsLuaKeyword(global_name)) {
                            continue;
                        }
                        binding_out->kind = TopLevelChildClosureBindingKind::kAssignedPrefix;
                        binding_out->binding = std::move(global_name);
                        return true;
                    }

                    if (next.opcode == Lua51Opcode::kSetTable &&
                        closure_registers.contains(next.c) && next.a >= 0 &&
                        static_cast<std::size_t>(next.a) < register_exprs.size()) {
                        std::string key;
                        if (!TryRenderLuaStringConstantForAssignedClosureRepair(main_function,
                                                                                next.b,
                                                                                &key) ||
                            !IsValidLuaIdentifier(key) || IsLuaKeyword(key)) {
                            continue;
                        }
                        const std::string& base =
                            register_exprs[static_cast<std::size_t>(next.a)];
                        if (base.empty()) {
                            continue;
                        }
                        binding_out->kind = TopLevelChildClosureBindingKind::kAssignedPrefix;
                        binding_out->binding = base + "." + key;
                        return true;
                    }
                }
                if (!guessed_local_fallback.empty()) {
                    binding_out->kind = TopLevelChildClosureBindingKind::kLocalName;
                    binding_out->binding = std::move(guessed_local_fallback);
                    return true;
                }
                break;
            }
            default:
                break;
        }
    }

    return false;
}

bool TryInferTopLevelChildAssignedClosurePrefix(const Lua51Function& main_function,
                                                std::size_t child_index,
                                                std::string* prefix_out) {
    if (prefix_out == nullptr) {
        return false;
    }

    TopLevelChildClosureBinding binding;
    if (!TryInferTopLevelChildClosureBinding(main_function, child_index, &binding) ||
        binding.kind != TopLevelChildClosureBindingKind::kAssignedPrefix ||
        binding.binding.empty()) {
        return false;
    }

    *prefix_out = std::move(binding.binding);
    return true;
}

bool TryExtractRenderedAssignedClosurePrefix(std::string_view line,
                                            std::string* prefix_out) {
    if (prefix_out == nullptr) {
        return false;
    }

    const std::string_view stripped = StripLuaLineComment(line);
    const std::size_t function_pos =
        FindRenderedFunctionKeywordOffsetForBytecodeRepair(stripped);
    if (function_pos == std::string_view::npos || function_pos == 0) {
        return false;
    }

    std::string_view prefix = TrimLuaWhitespace(stripped.substr(0, function_pos));
    if (prefix.empty()) {
        return false;
    }
    if (prefix.back() == '=') {
        prefix = TrimLuaWhitespace(prefix.substr(0, prefix.size() - 1));
    }
    if (prefix.empty()) {
        return false;
    }

    *prefix_out = std::string(prefix);
    return true;
}

bool TryExtractNormalizedRenderedAssignedClosurePrefix(
    const std::vector<std::string>& lines,
    std::size_t header_line,
    std::string* prefix_out) {
    if (prefix_out == nullptr || header_line >= lines.size() ||
        !TryExtractRenderedAssignedClosurePrefix(lines[header_line], prefix_out)) {
        return false;
    }

    const std::size_t access = prefix_out->find_first_of(".:");
    if (access == std::string::npos || access == 0) {
        return true;
    }

    const std::string root = prefix_out->substr(0, access);
    if (!IsValidLuaIdentifier(root) || IsLuaKeyword(root)) {
        return true;
    }

    for (std::size_t probe = header_line; probe-- > 0;) {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(lines[probe]));
        if (trimmed.empty()) {
            continue;
        }
        if (IsRenderedFunctionStartLineForBytecodeRepair(trimmed)) {
            break;
        }

        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseSingleRenderedAssignment(trimmed, &is_local, &lhs, &rhs) ||
            lhs != root) {
            continue;
        }

        rhs = std::string(TrimLuaWhitespace(rhs));
        if (!IsValidLuaIdentifier(rhs) || IsLuaKeyword(rhs)) {
            continue;
        }

        prefix_out->replace(0, access, rhs);
        break;
    }

    return true;
}

bool TryRestoreBytecodeTopLevelAssignedClosureBlocks(const Lua51Function& function,
                                                     std::string_view chunk_name,
                                                     std::string* source,
                                                     std::string* note,
                                                     std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    std::string current_error = ValidateLuaSource(*source, chunk_name);
    if (current_error.empty() || source->find("= function(") == std::string::npos) {
        return false;
    }

    std::unordered_map<std::string, std::size_t> prefix_to_child_index;
    prefix_to_child_index.reserve(function.child_functions.size());
    for (std::size_t child_index = 1; child_index <= function.child_functions.size();
         ++child_index) {
        std::string prefix;
        if (TryInferTopLevelChildAssignedClosurePrefix(function, child_index, &prefix) &&
            !prefix.empty()) {
            prefix_to_child_index.try_emplace(std::move(prefix), child_index);
        }
    }
    if (prefix_to_child_index.empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    bool changed = false;
    RunNativeFirstPassLoop(8, [&](int /*pass*/) {
        const std::vector<RenderedFunctionBlock> blocks =
            CollectTopLevelRenderedFunctionBlocksForBytecodeRepair(lines);
        if (blocks.empty()) {
            return false;
        }

        bool pass_changed = false;
        for (std::size_t reverse_index = blocks.size(); reverse_index > 0; --reverse_index) {
            const RenderedFunctionBlock& block = blocks[reverse_index - 1];
            if (block.begin >= lines.size() || block.end >= lines.size() ||
                block.begin > block.end) {
                continue;
            }

            std::string assigned_prefix;
            if (!TryExtractNormalizedRenderedAssignedClosurePrefix(lines,
                                                                  block.begin,
                                                                  &assigned_prefix) ||
                assigned_prefix.empty()) {
                continue;
            }

            const auto found_child = prefix_to_child_index.find(assigned_prefix);
            if (found_child == prefix_to_child_index.end()) {
                continue;
            }

            std::string standalone_source;
            std::string standalone_error;
            if (!RenderLua51StraightLineFunction(
                    function.child_functions[found_child->second - 1],
                    &standalone_source,
                    &standalone_error) ||
                !ValidateLuaSource(standalone_source, chunk_name).empty()) {
                continue;
            }

            std::vector<std::string> replacement_lines = SplitRenderedLines(standalone_source);
            if (replacement_lines.empty()) {
                continue;
            }

            const std::string_view current_header = StripLuaLineComment(lines[block.begin]);
            const std::size_t current_function_pos =
                FindRenderedFunctionKeywordOffsetForBytecodeRepair(current_header);
            const std::size_t replacement_function_pos =
                FindRenderedFunctionKeywordOffsetForBytecodeRepair(replacement_lines.front());
            if (current_function_pos == std::string_view::npos ||
                replacement_function_pos == std::string_view::npos) {
                continue;
            }

            const std::size_t indent_length = current_header.find_first_not_of(" \t");
            const std::string indent = indent_length == std::string_view::npos
                                           ? std::string()
                                           : std::string(current_header.substr(0, indent_length));
            for (std::string& line : replacement_lines) {
                line = indent + line;
            }
            replacement_lines.front() =
                std::string(current_header.substr(0, current_function_pos)) +
                replacement_lines.front().substr(indent.size() + replacement_function_pos);

            std::vector<std::string> trial_lines = lines;
            trial_lines.erase(trial_lines.begin() +
                                  static_cast<std::ptrdiff_t>(block.begin),
                              trial_lines.begin() +
                                  static_cast<std::ptrdiff_t>(block.end + 1));
            trial_lines.insert(trial_lines.begin() +
                                   static_cast<std::ptrdiff_t>(block.begin),
                               replacement_lines.begin(),
                               replacement_lines.end());

            if (trial_lines == lines) {
                continue;
            }

            const std::string trial_source = JoinRenderedLines(trial_lines);
            const std::string trial_error = ValidateLuaSource(trial_source, chunk_name);
            if (!trial_error.empty() &&
                !IsValidationOutcomeBetter(current_error, trial_error)) {
                continue;
            }

            lines = std::move(trial_lines);
            current_error = trial_error;
            changed = true;
            pass_changed = true;
            if (current_error.empty()) {
                break;
            }
        }

        return pass_changed && !current_error.empty();
    });

    if (!changed) {
        return false;
    }

    *source = JoinRenderedLines(lines);
    AppendCandidateNote(note,
                        note_prefix,
                        current_error.empty()
                            ? "bytecode_top_level_assigned_closure_blocks_restored"
                            : "bytecode_top_level_assigned_closure_blocks_progress");
    return true;
}

bool TryRestoreBytecodeTopLevelClosureBindings(const Lua51Function& function,
                                               std::string_view chunk_name,
                                               std::string* source,
                                               std::string* note,
                                               std::string_view note_prefix) {
    if (source == nullptr || source->empty() || function.child_functions.empty()) {
        return false;
    }

    std::vector<std::string> lines = SplitRenderedLines(*source);
    const std::vector<RenderedFunctionBlock> blocks =
        CollectTopLevelRenderedFunctionBlocksForBytecodeRepair(lines);
    if (blocks.empty()) {
        return false;
    }

    const std::size_t block_count = std::min(blocks.size(), function.child_functions.size());
    bool changed = false;
    std::vector<std::size_t> removable_alias_lines;
    const auto extract_owner_prefix = [](std::string_view binding_expression) -> std::string {
        const std::size_t member_separator = binding_expression.find_last_of(".[:");
        if (member_separator == std::string_view::npos) {
            return {};
        }
        return std::string(TrimLuaWhitespace(binding_expression.substr(0, member_separator)));
    };
    for (std::size_t block_index = 0; block_index < block_count; ++block_index) {
        const RenderedFunctionBlock& block = blocks[block_index];
        if (block.begin >= lines.size()) {
            continue;
        }

        TopLevelChildClosureBinding binding;
        if (!TryInferTopLevelChildClosureBinding(function, block_index + 1, &binding) ||
            binding.kind == TopLevelChildClosureBindingKind::kNone ||
            binding.binding.empty()) {
            continue;
        }

        const std::string_view stripped = StripLuaLineComment(lines[block.begin]);
        const std::size_t function_pos =
            FindRenderedFunctionKeywordOffsetForBytecodeRepair(stripped);
        if (function_pos == std::string_view::npos) {
            continue;
        }

        const std::size_t indent_length = stripped.find_first_not_of(" \t");
        const std::string indent = indent_length == std::string_view::npos
                                       ? std::string()
                                       : std::string(stripped.substr(0, indent_length));
        const std::string function_suffix = std::string(stripped.substr(function_pos));

        std::string expected_header;
        switch (binding.kind) {
            case TopLevelChildClosureBindingKind::kAssignedPrefix:
                expected_header = indent + binding.binding + " = " + function_suffix;
                break;
            case TopLevelChildClosureBindingKind::kLocalName:
                expected_header = indent + "local " + binding.binding + " = " + function_suffix;
                break;
            case TopLevelChildClosureBindingKind::kNone:
            default:
                continue;
        }

        if (TrimLuaWhitespace(StripLuaLineComment(lines[block.begin])) !=
            TrimLuaWhitespace(expected_header)) {
            std::vector<std::string> trial_lines = lines;
            trial_lines[block.begin] = std::move(expected_header);
            const std::string trial_source = JoinRenderedLines(trial_lines);
            if (!ValidateLuaSource(trial_source, chunk_name).empty()) {
                continue;
            }

            lines = std::move(trial_lines);
            changed = true;
        }

        if (binding.kind != TopLevelChildClosureBindingKind::kAssignedPrefix) {
            continue;
        }

        const std::string owner_prefix = extract_owner_prefix(binding.binding);
        if (owner_prefix.empty() || block.begin == 0) {
            continue;
        }

        std::size_t alias_line = block.begin;
        while (alias_line > 0) {
            --alias_line;
            if (!TrimLuaWhitespace(StripLuaLineComment(lines[alias_line])).empty()) {
                break;
            }
        }
        if (alias_line >= block.begin ||
            TrimLuaWhitespace(StripLuaLineComment(lines[alias_line])).empty()) {
            continue;
        }

        bool alias_is_local = false;
        std::string alias_lhs;
        std::string alias_rhs;
        if (!ParseSingleRenderedAssignment(lines[alias_line],
                                           &alias_is_local,
                                           &alias_lhs,
                                           &alias_rhs) ||
            alias_is_local ||
            !LooksLikeGuessedLua51DeclarationName(
                TrimLuaWhitespace(StripLuaLineComment(alias_lhs))) ||
            NormalizeRenderedTrivialParenthesizedLiterals(alias_rhs) != owner_prefix) {
            continue;
        }

        bool alias_used = false;
        for (std::size_t probe = alias_line + 1; probe < block.begin; ++probe) {
            if (CountIdentifierUses(lines[probe], alias_lhs) != 0) {
                alias_used = true;
                break;
            }
        }
        if (!alias_used) {
            removable_alias_lines.push_back(alias_line);
        }
    }

    if (!removable_alias_lines.empty()) {
        std::sort(removable_alias_lines.begin(), removable_alias_lines.end());
        removable_alias_lines.erase(
            std::unique(removable_alias_lines.begin(), removable_alias_lines.end()),
            removable_alias_lines.end());
        for (auto it = removable_alias_lines.rbegin();
             it != removable_alias_lines.rend();
             ++it) {
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(*it));
        }
        changed = true;
    }

    if (!changed) {
        return false;
    }

    NormalizeRenderedLuaWhitespace(&lines);
    std::string candidate_source = JoinRenderedLines(lines);
    if (!ValidateLuaSource(candidate_source, chunk_name).empty()) {
        return false;
    }

    *source = std::move(candidate_source);
    AppendCandidateNote(note, note_prefix, "bytecode_top_level_closure_bindings_restored");
    return true;
}

bool LineHasNilArithmeticCandidate(std::string_view line) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty()) {
        return false;
    }

    return trimmed.find("* nil") != std::string_view::npos ||
           trimmed.find("nil *") != std::string_view::npos ||
           trimmed.find("/ nil") != std::string_view::npos ||
           trimmed.find("nil /") != std::string_view::npos;
}

enum class BooleanReturnWrapperMode : uint8_t {
    kNone = 0,
    kTrueThenFalse,
    kFalseThenTrue,
};

bool ParseRenderedSingleReturnExpression(std::string_view line, std::string* expression) {
    if (expression == nullptr) {
        return false;
    }

    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!StartsWithLuaKeyword(trimmed, "return")) {
        return false;
    }

    const std::string_view return_expression = TrimLuaWhitespace(trimmed.substr(6));
    if (return_expression.empty()) {
        return false;
    }

    std::vector<std::string> parts;
    if (!SplitRenderedTopLevelCommaChainLocal(return_expression, &parts) || parts.size() != 1) {
        return false;
    }

    *expression = std::move(parts.front());
    return true;
}

bool ParseRenderedLocalAssignment(std::string_view line,
                                  std::string* local_name,
                                  std::string* expression) {
    if (local_name == nullptr || expression == nullptr) {
        return false;
    }

    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!StartsWithLuaKeyword(trimmed, "local")) {
        return false;
    }

    const std::string_view remainder = TrimLuaWhitespace(trimmed.substr(5));
    const std::size_t equals_pos = remainder.find('=');
    if (equals_pos == std::string_view::npos) {
        return false;
    }

    const std::string_view name_view =
        TrimLuaWhitespace(remainder.substr(0, equals_pos));
    const std::string_view expression_view =
        TrimLuaWhitespace(remainder.substr(equals_pos + 1));
    if (name_view.empty() || expression_view.empty() ||
        name_view.find(',') != std::string_view::npos ||
        !IsValidLuaIdentifier(name_view) || IsLuaKeyword(name_view)) {
        return false;
    }

    *local_name = std::string(name_view);
    *expression = std::string(expression_view);
    return true;
}

bool ParseRenderedStandaloneLocalDeclaration(std::string_view line,
                                             std::string* local_name) {
    if (local_name == nullptr) {
        return false;
    }

    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!StartsWithLuaKeyword(trimmed, "local")) {
        return false;
    }

    const std::string_view remainder = TrimLuaWhitespace(trimmed.substr(5));
    if (remainder.empty() || remainder.find('=') != std::string_view::npos ||
        remainder.find(',') != std::string_view::npos ||
        !IsValidLuaIdentifier(remainder) || IsLuaKeyword(remainder)) {
        return false;
    }

    *local_name = std::string(remainder);
    return true;
}

bool IsRenderedFunctionTerminatorLine(std::string_view line) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!trimmed.starts_with("end")) {
        return false;
    }

    const std::string_view suffix = TrimLuaWhitespace(trimmed.substr(3));
    for (const char ch : suffix) {
        if (ch != ')' && ch != ']' && ch != '}' && ch != ',') {
            return false;
        }
    }
    return true;
}

bool BlockHasSingleReturnExpression(const std::vector<std::string>& lines,
                                    const RenderedFunctionBlock& block,
                                    std::string* expression) {
    if (expression == nullptr || block.begin >= lines.size() || block.end >= lines.size() ||
        block.begin >= block.end) {
        return false;
    }

    std::size_t return_line = lines.size();
    bool saw_end = false;
    for (std::size_t line_index = block.begin + 1; line_index <= block.end; ++line_index) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment(lines[line_index]));
        if (trimmed.empty()) {
            continue;
        }

        if (IsRenderedFunctionTerminatorLine(lines[line_index])) {
            if (saw_end) {
                return false;
            }
            saw_end = true;
            continue;
        }

        if (return_line != lines.size()) {
            return false;
        }
        return_line = line_index;
    }

    if (!saw_end || return_line == lines.size()) {
        return false;
    }

    return ParseRenderedSingleReturnExpression(lines[return_line], expression);
}

bool BlockHasSingleReturnExpressionWithLocalPrelude(
    const std::vector<std::string>& lines,
    const RenderedFunctionBlock& block,
    std::vector<std::string>* prelude_lines,
    std::string* expression) {
    if (expression == nullptr || block.begin >= lines.size() || block.end >= lines.size() ||
        block.begin >= block.end) {
        return false;
    }

    if (prelude_lines != nullptr) {
        prelude_lines->clear();
    }

    std::size_t return_line = lines.size();
    bool saw_end = false;
    for (std::size_t line_index = block.begin + 1; line_index <= block.end; ++line_index) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment(lines[line_index]));
        if (trimmed.empty()) {
            if (prelude_lines != nullptr && return_line == lines.size()) {
                prelude_lines->push_back(lines[line_index]);
            }
            continue;
        }

        if (IsRenderedFunctionTerminatorLine(lines[line_index])) {
            if (saw_end) {
                return false;
            }
            saw_end = true;
            continue;
        }

        if (return_line == lines.size()) {
            std::string return_expression;
            if (ParseRenderedSingleReturnExpression(lines[line_index], &return_expression)) {
                *expression = std::move(return_expression);
                return_line = line_index;
                continue;
            }

            bool assign_is_local = false;
            std::vector<std::string> assign_lhs_names;
            std::string assign_rhs;
            if (ParseRenderedAssignmentLine(lines[line_index],
                                            &assign_is_local,
                                            &assign_lhs_names,
                                            &assign_rhs) &&
                assign_is_local && assign_lhs_names.size() == 1) {
                if (prelude_lines != nullptr) {
                    prelude_lines->push_back(lines[line_index]);
                }
                continue;
            }
        }

        return false;
    }

    return saw_end && return_line != lines.size();
}

bool BlockIsInlineAnonymousFunction(const std::vector<std::string>& lines,
                                    const RenderedFunctionBlock& block) {
    if (block.begin >= lines.size()) {
        return false;
    }

    const std::string_view trimmed =
        TrimLuaWhitespace(StripLuaLineComment(lines[block.begin]));
    if (trimmed.empty() || IsRenderedFunctionHeaderLine(trimmed)) {
        return false;
    }

    return FindRenderedFunctionKeywordOffsetForBytecodeRepair(trimmed) != std::string_view::npos;
}

std::size_t CountMeaningfulRenderedLinesInBlock(const std::vector<std::string>& lines,
                                                const RenderedFunctionBlock& block) {
    if (block.begin >= lines.size() || block.end >= lines.size() || block.begin > block.end) {
        return 0;
    }

    std::size_t meaningful = 0;
    for (std::size_t line_index = block.begin; line_index <= block.end; ++line_index) {
        if (!TrimLuaWhitespace(StripLuaLineComment(lines[line_index])).empty()) {
            ++meaningful;
        }
    }
    return meaningful;
}

BooleanReturnWrapperMode DetectBooleanReturnWrapperMode(const Lua51Function& function) {
    std::vector<Lua51Instruction> instructions = DecodeLua51Instructions(function);
    if (!instructions.empty() && instructions.back().opcode == Lua51Opcode::kReturn &&
        instructions.back().a == 0 && instructions.back().b == 1) {
        instructions.pop_back();
    }

    if (instructions.size() < 6) {
        return BooleanReturnWrapperMode::kNone;
    }

    const Lua51Instruction& test = instructions[instructions.size() - 6];
    const Lua51Instruction& jump = instructions[instructions.size() - 5];
    const Lua51Instruction& load_first = instructions[instructions.size() - 4];
    const Lua51Instruction& return_first = instructions[instructions.size() - 3];
    const Lua51Instruction& load_second = instructions[instructions.size() - 2];
    const Lua51Instruction& return_second = instructions[instructions.size() - 1];
    const bool supported_guard_opcode =
        test.opcode == Lua51Opcode::kTest || test.opcode == Lua51Opcode::kEq ||
        test.opcode == Lua51Opcode::kLt || test.opcode == Lua51Opcode::kLe;
    if (!supported_guard_opcode || jump.opcode != Lua51Opcode::kJmp ||
        load_first.opcode != Lua51Opcode::kLoadBool ||
        return_first.opcode != Lua51Opcode::kReturn ||
        load_second.opcode != Lua51Opcode::kLoadBool ||
        return_second.opcode != Lua51Opcode::kReturn) {
        return BooleanReturnWrapperMode::kNone;
    }

    int result_register = load_first.a;
    if (test.opcode == Lua51Opcode::kTest) {
        result_register = test.a;
        if (load_first.a != result_register || return_first.a != result_register ||
            load_second.a != result_register || return_second.a != result_register ||
            return_first.b != 2 || return_second.b != 2) {
            return BooleanReturnWrapperMode::kNone;
        }
    } else if (return_first.a != result_register || load_second.a != result_register ||
               return_second.a != result_register || return_first.b != 2 ||
               return_second.b != 2) {
        return BooleanReturnWrapperMode::kNone;
    }

    const int jump_target = static_cast<int>(jump.line) + 1 + jump.sbx;
    if (jump_target != static_cast<int>(load_second.line)) {
        return BooleanReturnWrapperMode::kNone;
    }

    if (load_first.b == 1 && load_second.b == 0) {
        return BooleanReturnWrapperMode::kTrueThenFalse;
    }
    if (load_first.b == 0 && load_second.b == 1) {
        return BooleanReturnWrapperMode::kFalseThenTrue;
    }

    return BooleanReturnWrapperMode::kNone;
}

std::optional<std::string> RenderLua51CompareTailBooleanExpression(
    const Lua51Function& function,
    const std::vector<std::string>& parameter_names,
    const std::vector<Lua51Instruction>& instructions,
    const std::vector<std::vector<std::optional<std::string>>>& tracked_snapshots,
    std::size_t compare_index) {
    if (compare_index + 4 >= instructions.size() || compare_index >= tracked_snapshots.size()) {
        return std::nullopt;
    }

    const Lua51Instruction& compare = instructions[compare_index];
    const Lua51Instruction& jump = instructions[compare_index + 1];
    const Lua51Instruction& load_first = instructions[compare_index + 2];
    const Lua51Instruction& load_second = instructions[compare_index + 3];
    const Lua51Instruction& return_first = instructions[compare_index + 4];
    if ((compare.opcode != Lua51Opcode::kEq && compare.opcode != Lua51Opcode::kLt &&
         compare.opcode != Lua51Opcode::kLe) ||
        jump.opcode != Lua51Opcode::kJmp ||
        load_first.opcode != Lua51Opcode::kLoadBool ||
        load_second.opcode != Lua51Opcode::kLoadBool ||
        return_first.opcode != Lua51Opcode::kReturn || return_first.b != 2 ||
        load_first.a != return_first.a || load_second.a != return_first.a ||
        static_cast<int>(compare_index + 2) + jump.sbx != static_cast<int>(compare_index + 3)) {
        return std::nullopt;
    }

    const std::optional<std::string> compare_expression =
        RenderLua51TrackedCompareExpression(function,
                                            parameter_names,
                                            tracked_snapshots[compare_index],
                                            compare_index,
                                            compare);
    if (!compare_expression.has_value()) {
        return std::nullopt;
    }

    if (load_first.b == 1 && load_second.b == 0) {
        return compare_expression;
    }
    if (load_first.b == 0 && load_second.b == 1) {
        return "not (" + *compare_expression + ")";
    }

    return std::nullopt;
}

std::vector<std::string> CollectLuaRepairExpressionTokens(std::string_view expression) {
    std::vector<std::string> tokens;
    std::string current;
    const auto flush_current = [&]() {
        if (!current.empty()) {
            tokens.push_back(current);
            current.clear();
        }
    };

    for (const char ch : expression) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        if (std::isalnum(uch) != 0 || ch == '_') {
            current.push_back(ch);
            continue;
        }
        flush_current();
    }
    flush_current();
    return tokens;
}

int ScoreLuaRepairExpressionMatch(std::string_view current_expression,
                                  std::string_view repaired_expression) {
    const std::string normalized_current =
        NormalizeRenderedTrivialParenthesizedLiterals(std::string(current_expression));
    const std::string normalized_repaired =
        NormalizeRenderedTrivialParenthesizedLiterals(std::string(repaired_expression));
    if (normalized_current == normalized_repaired) {
        return 1000;
    }

    const std::vector<std::string> current_tokens =
        CollectLuaRepairExpressionTokens(normalized_current);
    const std::vector<std::string> repaired_tokens =
        CollectLuaRepairExpressionTokens(normalized_repaired);
    if (current_tokens.empty() || repaired_tokens.empty()) {
        return 0;
    }

    std::unordered_map<std::string, int> repaired_counts;
    for (const std::string& token : repaired_tokens) {
        ++repaired_counts[token];
    }

    int common_token_count = 0;
    for (const std::string& token : current_tokens) {
        auto repaired_it = repaired_counts.find(token);
        if (repaired_it == repaired_counts.end() || repaired_it->second <= 0) {
            continue;
        }
        --repaired_it->second;
        ++common_token_count;
    }

    int score = common_token_count * 10;
    if (!normalized_current.empty() &&
        normalized_repaired.find(normalized_current) != std::string::npos) {
        score += 20;
    }
    if (!normalized_repaired.empty() &&
        normalized_current.find(normalized_repaired) != std::string::npos) {
        score += 20;
    }

    const int length_delta =
        static_cast<int>(std::max(normalized_current.size(), normalized_repaired.size()) -
                         std::min(normalized_current.size(), normalized_repaired.size()));
    score -= length_delta / 8;
    return std::max(score, 0);
}

bool BlockHasNilArithmeticCandidate(const std::vector<std::string>& lines,
                                    const RenderedFunctionBlock& block) {
    if (block.begin >= lines.size() || block.end >= lines.size() || block.begin > block.end) {
        return false;
    }

    for (std::size_t line_index = block.begin; line_index <= block.end; ++line_index) {
        if (LineHasNilArithmeticCandidate(lines[line_index])) {
            return true;
        }
    }

    return false;
}

bool TryRestoreBytecodeBooleanReturnWrapperFunctionBlocks(const Lua51Function& function,
                                                          std::string_view chunk_name,
                                                          std::string* source,
                                                          std::string* note,
                                                          std::string_view note_prefix) {
    if (source == nullptr) {
        return false;
    }

    std::vector<std::string> clean_lines = SplitRenderedLines(*source);
    std::vector<RenderedFunctionBlock> clean_blocks =
        CollectRenderedFunctionBlocksPreorder(clean_lines);
    if (clean_blocks.empty()) {
        return false;
    }

    std::vector<const Lua51Function*> functions;
    AppendLuaFunctionChildrenPreorder(function, &functions);
    if (functions.empty()) {
        return false;
    }

    const std::size_t replacement_count = std::min(clean_blocks.size(), functions.size());
    bool changed = false;
    for (std::size_t block_index = replacement_count; block_index-- > 0;) {
        const BooleanReturnWrapperMode mode =
            DetectBooleanReturnWrapperMode(*functions[block_index]);
        if (mode == BooleanReturnWrapperMode::kNone) {
            continue;
        }

        const RenderedFunctionBlock& clean_block = clean_blocks[block_index];
        std::vector<std::string> prelude_lines;
        std::string return_expression;
        if (!BlockHasSingleReturnExpressionWithLocalPrelude(clean_lines,
                                                            clean_block,
                                                            &prelude_lines,
                                                            &return_expression) ||
            !LooksLikeBooleanValuedExpression(return_expression)) {
            continue;
        }

        const std::size_t indent_width = [&]() {
            const std::optional<std::size_t> header_indent =
                RenderedFunctionHeaderIndent(clean_lines[clean_block.begin]);
            if (header_indent.has_value()) {
                return *header_indent;
            }

            const std::size_t first_non_space =
                clean_lines[clean_block.begin].find_first_not_of(" \t");
            return first_non_space == std::string::npos ? std::size_t{0} : first_non_space;
        }();
        const std::string indent(indent_width, ' ');
        const std::string body_indent = indent + "  ";
        const std::string nested_indent = body_indent + "  ";
        std::vector<std::string> replacement_lines = {clean_lines[clean_block.begin]};
        replacement_lines.insert(replacement_lines.end(),
                                 prelude_lines.begin(),
                                 prelude_lines.end());
        replacement_lines.push_back(body_indent + "if " + return_expression + " then");
        if (mode == BooleanReturnWrapperMode::kTrueThenFalse) {
            replacement_lines.push_back(nested_indent + "return true");
            replacement_lines.push_back(body_indent + "end");
            replacement_lines.push_back(body_indent + "return false");
        } else {
            replacement_lines.push_back(nested_indent + "return false");
            replacement_lines.push_back(body_indent + "end");
            replacement_lines.push_back(body_indent + "return true");
        }
        replacement_lines.push_back(clean_lines[clean_block.end]);

        std::vector<std::string> trial_lines = clean_lines;
        trial_lines.erase(trial_lines.begin() + static_cast<std::ptrdiff_t>(clean_block.begin),
                          trial_lines.begin() +
                              static_cast<std::ptrdiff_t>(clean_block.end + 1));
        trial_lines.insert(trial_lines.begin() +
                               static_cast<std::ptrdiff_t>(clean_block.begin),
                           replacement_lines.begin(),
                           replacement_lines.end());

        const std::string trial_source = JoinRenderedLines(trial_lines);
        if (!ValidateLuaSource(trial_source, chunk_name).empty()) {
            continue;
        }

        clean_lines = std::move(trial_lines);
        clean_blocks = CollectRenderedFunctionBlocksPreorder(clean_lines);
        if (clean_blocks.empty()) {
            break;
        }
        changed = true;
    }

    if (!changed) {
        return false;
    }

    std::string rebuilt = JoinRenderedLines(clean_lines);
    if (!ValidateLuaSource(rebuilt, chunk_name).empty()) {
        return false;
    }

    *source = std::move(rebuilt);
    AppendCandidateNote(note, note_prefix, "bytecode_boolean_return_wrappers_restored");
    return true;
}

bool TryRepairBooleanReturnWrapperFunctionBlocksFromBytecode(
    const Lua51Chunk& chunk,
    std::string_view chunk_name,
    std::string* clean_source) {
    return TryRestoreBytecodeBooleanReturnWrapperFunctionBlocks(
        chunk.main, chunk_name, clean_source, nullptr, {});
}

LuaReadabilitySummary SummarizeLuaReadability(std::string_view source) {
    LuaReadabilitySummary summary;
    const std::vector<std::string> lines = SplitRenderedLines(source);
    std::unordered_set<std::string> unique_temporaries;

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string& raw_line = lines[line_index];
        const std::string_view stripped = StripLuaLineComment(raw_line);
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (trimmed.empty()) {
            continue;
        }

        ++summary.nonempty_line_count;

        std::size_t temporary_tokens_on_line = 0;
        ForEachLuaToken(stripped, [&](std::string_view token) {
            if (!IsRenderTemporaryName(token)) {
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
        if (!local_name.empty() && IsRenderTemporaryName(local_name)) {
            ++summary.temporary_local_declaration_count;
            ++summary.temporary_leading_line_count;
        } else {
            const std::string_view leading = ExtractLeadingIdentifier(trimmed);
            if (!leading.empty() && IsRenderTemporaryName(leading)) {
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

        std::string assign_lhs;
        std::string assign_rhs;
        std::string call_callee;
        std::string first_argument;
        if (ParseSingleRenderedAssignment(trimmed, &assign_lhs, &assign_rhs) &&
            !IsRenderTemporaryName(assign_lhs) &&
            IsRenderTemporaryName(TrimLuaWhitespace(assign_rhs))) {
            ++summary.suspicious_temporary_rhs_assignment_count;
        }

        if (trimmed.find("math.max(nil") != std::string_view::npos ||
            trimmed.find("math.min(nil") != std::string_view::npos) {
            ++summary.obvious_nil_call_count;
        }

        if (ParseSingleRenderedAssignment(trimmed, &assign_lhs, &assign_rhs) &&
            ParseCallFirstArgument(assign_rhs, &call_callee, &first_argument) &&
            first_argument == assign_lhs) {
            bool seeded = false;
            if (line_index > 0) {
                for (std::size_t probe = line_index; probe-- > 0;) {
                    const std::string_view previous_trimmed =
                        TrimLuaWhitespace(StripLuaLineComment(lines[probe]));
                    if (previous_trimmed.empty()) {
                        continue;
                    }
                    seeded = IsRenderedSeedAssignmentFor(previous_trimmed, assign_lhs);
                    break;
                }
            }
            if (!seeded) {
                ++summary.suspicious_self_call_assignment_count;
            }
        }
    }

    summary.unique_temporary_identifier_count = unique_temporaries.size();
    summary.risk_score = summary.temporary_leading_line_count * 5 +
                         summary.temporary_local_declaration_count * 4 +
                         summary.temporary_assignment_count * 3 +
                         summary.temporary_self_reference_assignment_count * 8 +
                         summary.multi_temporary_token_line_count * 2 +
                         summary.unique_temporary_identifier_count;
    return summary;
}

bool ShouldTryRawAlternativeForValidatedCandidate(std::string_view source) {
    const LuaReadabilitySummary summary = SummarizeLuaReadability(source);
    if (summary.suspicious_self_call_assignment_count != 0 ||
        summary.suspicious_temporary_rhs_assignment_count != 0) {
        return true;
    }
    return LooksLikeRegisterizedLua(summary) &&
           !IsRunnableLuaDespiteTemporaryNoise(summary) &&
           !IsStructuredTableInitializerLuaDespiteTemporaryNoise(summary) &&
           !IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(summary);
}

std::size_t CountUnresolvedStatementTempOwnershipPatterns(std::string_view source) {
    std::size_t count = 0;
    const std::vector<std::string> lines = SplitRenderedLines(source);
    for (const std::string& raw_line : lines) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseSingleRenderedAssignment(raw_line, &is_local, &lhs, &rhs)) {
            continue;
        }

        const std::string normalized_lhs =
            std::string(TrimLuaWhitespace(lhs));
        if (normalized_lhs.empty() ||
            !IsValidLuaIdentifier(normalized_lhs) ||
            IsLuaKeyword(normalized_lhs) ||
            IsRenderTemporaryName(normalized_lhs)) {
            continue;
        }

        bool has_temporary_token = false;
        ForEachLuaToken(rhs, [&](std::string_view token) {
            if (IsRenderTemporaryName(token)) {
                has_temporary_token = true;
            }
        });
        if (!has_temporary_token) {
            continue;
        }

        const std::string_view normalized_rhs = TrimLuaWhitespace(rhs);
        if (normalized_rhs.find(" and ") != std::string_view::npos ||
            normalized_rhs.find(" or ") != std::string_view::npos ||
            normalized_rhs.find("function()") != std::string_view::npos ||
            !is_local) {
            ++count;
        }
    }
    return count;
}

std::size_t CountNativeStatementTempCommits(std::string_view source) {
    std::size_t count = 0;
    const std::vector<std::string> lines = SplitRenderedLines(source);
    for (const std::string& raw_line : lines) {
        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseSingleRenderedAssignment(raw_line, &is_local, &lhs, &rhs) ||
            !is_local) {
            continue;
        }

        const std::string normalized_lhs =
            std::string(TrimLuaWhitespace(lhs));
        if (normalized_lhs.empty() ||
            !IsValidLuaIdentifier(normalized_lhs) ||
            IsRenderTemporaryName(normalized_lhs)) {
            continue;
        }

        const std::string_view normalized_rhs = TrimLuaWhitespace(rhs);
        if (normalized_rhs.find(" and ") == std::string_view::npos &&
            normalized_rhs.find(" or ") == std::string_view::npos) {
            continue;
        }

        bool has_temporary_token = false;
        ForEachLuaToken(rhs, [&](std::string_view token) {
            if (IsRenderTemporaryName(token)) {
                has_temporary_token = true;
            }
        });
        if (has_temporary_token) {
            ++count;
        }
    }
    return count;
}

bool ShouldTryStatementTempsFromRawForValidatedCandidate(
    std::string_view source,
    const LuaReadabilitySummary& clean_summary) {
    if (!ShouldTryExpandedAssignmentRepairForValidatedCandidate(clean_summary)) {
        return false;
    }

    const std::size_t unresolved_patterns =
        CountUnresolvedStatementTempOwnershipPatterns(source);
    if (unresolved_patterns == 0) {
        return false;
    }

    const std::size_t rough_line_count =
        static_cast<std::size_t>(std::count(source.begin(), source.end(), '\n')) +
        std::size_t{1};
    constexpr std::size_t kStatementTempRawRepairLineBudget = 800;
    if (rough_line_count > kStatementTempRawRepairLineBudget) {
        return false;
    }

    if (CountNativeStatementTempCommits(source) != 0) {
        return false;
    }

    return unresolved_patterns >= 4;
}

void AppendNotePart(std::string* note, std::string_view part) {
    if (note == nullptr || part.empty()) {
        return;
    }
    if (!note->empty()) {
        note->push_back(';');
    }
    note->append(part);
}

void RecordReadabilitySummary(std::string_view source, NativeMainChunkCandidate* candidate) {
    if (candidate == nullptr) {
        return;
    }

    const LuaReadabilitySummary summary = SummarizeLuaReadability(source);
    candidate->readability = summary;
    candidate->readability.risk_score = ScoreLuaReadabilityRisk(summary);
}

void RecordCandidateParity(const refii::lua::audit::LuaBytecodeParityReference* parity_reference,
                           std::string_view chunk_name,
                           NativeMainChunkCandidate* candidate) {
    if (candidate == nullptr || parity_reference == nullptr || !parity_reference->available ||
        !candidate->syntax_validated) {
        return;
    }

    refii::lua::audit::LuaBytecodeParityResult parity_result;
    if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(*parity_reference,
                                                            candidate->source,
                                                            chunk_name,
                                                            &parity_result)) {
        candidate->parity_checked = true;
        candidate->compiled_candidate = parity_result.compiled_candidate;
        candidate->first_mismatch = parity_result.first_mismatch;
        return;
    }

    candidate->parity_checked = true;
    candidate->main_stats_exact = parity_result.main_stats_exact;
    candidate->full_stats_exact = parity_result.full_stats_exact;
    candidate->exact_parity = parity_result.exact_bytecode;
    candidate->compiled_candidate = parity_result.compiled_candidate;
    candidate->first_mismatch = parity_result.first_mismatch;
}

bool TryRenderCandidate(const Lua51Function& function,
                        const refii::lua::audit::LuaBytecodeParityReference* parity_reference,
                        std::string_view chunk_name,
                        bool use_raw_renderer,
                        NativeMainChunkCandidate* candidate) {
    if (candidate == nullptr) {
        return false;
    }

    const std::string note_prefix =
        use_raw_renderer ? std::string("native_first_raw") : std::string("native_first");
    ResetLuaNativeStatementTempCommitCounter();
    ResetLuaNativeTableConstructorCounters();
    std::string rendered;
    std::string render_error;
    const bool rendered_ok =
        use_raw_renderer
            ? RenderLua51StraightLineFunctionBodyRaw(function, &rendered, &render_error)
            : RenderLua51StraightLineFunctionBody(function, &rendered, &render_error);
    const std::size_t native_statementtemp_commits_from_render =
        ConsumeLuaNativeStatementTempCommitCounter();
    const std::size_t native_callvalueownership_commits_from_render =
        ConsumeLuaNativeCallValueOwnershipCommitCounter();
    const std::size_t native_table_constructor_commits_from_render =
        ConsumeLuaNativeTableConstructorCommitCounter();
    const std::size_t native_setlist_constructor_links_from_render =
        ConsumeLuaNativeSetListConstructorLinkCounter();
    const std::size_t native_table_constructor_fallbacks_from_render =
        ConsumeLuaNativeTableConstructorFallbackCounter();
    if (!rendered_ok) {
        candidate->note =
            (use_raw_renderer ? "native_first_raw_render_failed=" : "native_first_render_failed=") +
            render_error;
        return false;
    }

    const bool allow_empty_body = IsTriviallyEmptyLuaMainChunk(function);
    if (rendered.empty() && !function.code.empty() && !allow_empty_body) {
        candidate->note =
            use_raw_renderer ? "native_first_raw_render_failed=empty_body"
                             : "native_first_render_failed=empty_body";
        return false;
    }

    std::string candidate_source = std::move(rendered);
    if (allow_empty_body && TrimLuaWhitespace(candidate_source).empty()) {
        candidate_source = "\n";
        AppendCandidateNote(&candidate->note,
                            use_raw_renderer ? "native_first_raw" : "native_first",
                            "empty_stub_body");
    }
    std::size_t native_statementtemp_commits = native_statementtemp_commits_from_render;
    std::size_t native_callvalueownership_commits =
        native_callvalueownership_commits_from_render;
    std::size_t native_table_constructor_commits =
        native_table_constructor_commits_from_render;
    std::size_t native_setlist_constructor_links =
        native_setlist_constructor_links_from_render;
    std::size_t native_table_constructor_fallbacks =
        native_table_constructor_fallbacks_from_render;
    TryApplyQuestStructuralCleanupBeforeValidation(chunk_name,
                                                   &candidate_source,
                                                   &candidate->note,
                                                   note_prefix);

    std::string validation_error = ValidateLuaSource(candidate_source, chunk_name);
    if (!validation_error.empty() &&
        TryRepairInvalidFunctionBlocksFromStandaloneRender(function,
                                                           chunk_name,
                                                           &candidate_source,
                                                           &candidate->note,
                                                           note_prefix)) {
        validation_error = ValidateLuaSource(candidate_source, chunk_name);
    }
    candidate->source = std::move(candidate_source);
    candidate->used_raw_renderer = use_raw_renderer;
    candidate->syntax_validated = validation_error.empty();
    RecordReadabilitySummary(candidate->source, candidate);
    if (candidate->syntax_validated &&
        TryApplyBytecodeBackedMainChunkRestorations(function,
                                                    chunk_name,
                                                    &candidate->source,
                                                    &candidate->note,
                                                    note_prefix)) {
        candidate->syntax_validated =
            ValidateLuaSource(candidate->source, chunk_name).empty();
        RecordReadabilitySummary(candidate->source, candidate);
    }
    if (candidate->syntax_validated) {
        RecordCandidateParity(parity_reference, chunk_name, candidate);
        AppendCandidateNote(&candidate->note, note_prefix, "validated");
    } else {
        if (!candidate->note.empty()) {
            candidate->note.push_back(';');
        }
        candidate->note.append(note_prefix);
        candidate->note.append("_invalid=");
        candidate->note.append(validation_error);
    }
    if (candidate->syntax_validated) {
        const std::string exact_source_before_cleanup =
            candidate->exact_parity ? candidate->source : std::string();
        if (TryApplyBytecodeBackedMainChunkRestorations(function,
                                                        chunk_name,
                                                        &candidate->source,
                                                        &candidate->note,
                                                        note_prefix)) {
            candidate->syntax_validated =
                ValidateLuaSource(candidate->source, chunk_name).empty();
        }
        if (candidate->syntax_validated &&
            TryRestoreBytecodeCallOperandCompareOrder(function,
                                                     chunk_name,
                                                     &candidate->source,
                                                     &candidate->note,
                                                     note_prefix)) {
            candidate->syntax_validated =
                ValidateLuaSource(candidate->source, chunk_name).empty();
        }
        if (candidate->syntax_validated &&
            TryApplyPostRestorationCleanup(chunk_name,
                                           &candidate->source,
                                           &candidate->note,
                                           note_prefix)) {
            candidate->syntax_validated =
                ValidateLuaSource(candidate->source, chunk_name).empty();
        }
        candidate->syntax_validated =
            ValidateLuaSource(candidate->source, chunk_name).empty();
        RecordReadabilitySummary(candidate->source, candidate);
        candidate->main_stats_exact = false;
        candidate->full_stats_exact = false;
        candidate->exact_parity = false;
        candidate->compiled_candidate = {};
        candidate->first_mismatch = {};
        RecordCandidateParity(parity_reference, chunk_name, candidate);
        if (!exact_source_before_cleanup.empty() && !candidate->exact_parity) {
            candidate->source = exact_source_before_cleanup;
            candidate->syntax_validated = true;
            RecordReadabilitySummary(candidate->source, candidate);
            RecordCandidateParity(parity_reference, chunk_name, candidate);
            AppendCandidateNote(&candidate->note,
                                note_prefix,
                                "validated_cleanup_reverted_nonparity");
        } else if (!candidate->exact_parity &&
                   TryApplyExactParityNormalization(parity_reference,
                                                   chunk_name,
                                                   &candidate->source,
                                                   &candidate->note,
                                                   note_prefix)) {
            candidate->syntax_validated = true;
            RecordReadabilitySummary(candidate->source, candidate);
            candidate->main_stats_exact = false;
            candidate->full_stats_exact = false;
            candidate->exact_parity = false;
            candidate->compiled_candidate = {};
            candidate->first_mismatch = {};
            RecordCandidateParity(parity_reference, chunk_name, candidate);
        }
    }
    native_statementtemp_commits =
        std::max(native_statementtemp_commits,
                 CountNativeStatementTempCommits(candidate->source));
    AppendCandidateNote(&candidate->note,
                        note_prefix,
                        "native_statementtemp_commits=" +
                            std::to_string(native_statementtemp_commits));
    AppendCandidateNote(&candidate->note,
                        note_prefix,
                        "native_callvalueownership_commits=" +
                            std::to_string(native_callvalueownership_commits));
    AppendCandidateNote(&candidate->note,
                        note_prefix,
                        "native_table_constructor_commits=" +
                            std::to_string(native_table_constructor_commits));
    AppendCandidateNote(&candidate->note,
                        note_prefix,
                        "native_setlist_constructor_links=" +
                            std::to_string(native_setlist_constructor_links));
    AppendCandidateNote(&candidate->note,
                        note_prefix,
                        "native_table_constructor_fallbacks=" +
                            std::to_string(native_table_constructor_fallbacks));
    return true;
}

bool TryRepairValidatedCandidateFromRawInternal(const Lua51Chunk& chunk,
                                                std::string_view chunk_name,
                                                std::string_view raw_source,
                                                std::string* clean_source,
                                                std::string* note,
                                                std::string_view note_prefix) {
    if (clean_source == nullptr) {
        return false;
    }

    const LuaReadabilitySummary clean_summary = SummarizeLuaReadability(*clean_source);
    std::string repaired_clean = *clean_source;
    const LuaScriptProfile chunk_profile = ClassifyLuaScriptProfile(chunk_name);
    const bool is_scoped_small_holdout =
        chunk_profile.Has(LuaScriptProfileFlag::kScopedSmallHoldout);
    const bool is_scoped_large_holdout =
        chunk_profile.Has(LuaScriptProfileFlag::kScopedLargeHoldout);
    const bool is_ai_core_hotspot_target =
        chunk_profile.Has(LuaScriptProfileFlag::kAiCoreHotspot);
    const bool is_priority_ownership_target =
        chunk_profile.Has(LuaScriptProfileFlag::kPriorityOwnership);
    const bool is_scoped_holdout =
        is_scoped_small_holdout || is_scoped_large_holdout;
    std::size_t native_function_boundary_parity_commits = 0;
    std::size_t native_local_commit_at_first_write = 0;
    std::size_t parity_candidate_rejected_extra_local = 0;
    bool repaired_function_boundary_parity = false;
    repaired_function_boundary_parity =
        TryApplyFunctionBoundaryParityNormalization(chunk_name,
                                                    &repaired_clean,
                                                    note,
                                                    note_prefix,
                                                    &native_function_boundary_parity_commits,
                                                    &native_local_commit_at_first_write,
                                                    &parity_candidate_rejected_extra_local);
    bool allow_scoped_raw_heavy_repairs = true;
    if ((is_scoped_holdout || is_ai_core_hotspot_target) &&
        repaired_function_boundary_parity) {
        const LuaReadabilitySummary summary = SummarizeLuaReadability(repaired_clean);
        allow_scoped_raw_heavy_repairs =
            summary.obvious_nil_call_count != 0 ||
            summary.suspicious_temporary_rhs_assignment_count != 0 ||
            summary.suspicious_self_call_assignment_count != 0 ||
            summary.temporary_table_field_assignment_count != 0;
    }
    const auto& disabled_validated_repairs = []() -> const std::unordered_set<std::string>& {
        static const std::unordered_set<std::string> disabled = [] {
            std::unordered_set<std::string> parsed;
            const char* env = std::getenv("REFII_LUA_DISABLE_VALIDATED_REPAIRS");
            if (env == nullptr || *env == '\0') {
                return parsed;
            }
            std::string value(env);
            std::size_t begin = 0;
            while (begin <= value.size()) {
                const std::size_t end = value.find(',', begin);
                std::string token =
                    end == std::string::npos ? value.substr(begin) : value.substr(begin, end - begin);
                const std::string_view trimmed = TrimLuaWhitespace(token);
                if (!trimmed.empty()) {
                    parsed.emplace(trimmed);
                }
                if (end == std::string::npos) {
                    break;
                }
                begin = end + 1;
            }
            return parsed;
        }();
        return disabled;
    }();
    const auto try_apply_validated_repair =
        [&](std::string_view repair_name, bool enabled, const auto& repair) -> bool {
        if (!enabled) {
            return false;
        }
        if (disabled_validated_repairs.find(std::string(repair_name)) !=
            disabled_validated_repairs.end()) {
            return false;
        }
        const LuaReadabilitySummary current_summary =
            SummarizeLuaReadability(repaired_clean);
        const std::size_t current_risk = ScoreLuaReadabilityRisk(current_summary);
        std::string trial = repaired_clean;
        if (!repair(&trial)) {
            return false;
        }
        const std::string repair_error = ValidateLuaSource(trial, chunk_name);
        if (!repair_error.empty()) {
            return false;
        }
        const LuaReadabilitySummary trial_summary = SummarizeLuaReadability(trial);
        const std::size_t trial_risk = ScoreLuaReadabilityRisk(trial_summary);
        const bool improved_nil_calls =
            trial_summary.obvious_nil_call_count <
            current_summary.obvious_nil_call_count;
        const bool improved_temp_rhs =
            trial_summary.suspicious_temporary_rhs_assignment_count <
            current_summary.suspicious_temporary_rhs_assignment_count;
        const bool improved_self_calls =
            trial_summary.suspicious_self_call_assignment_count <
            current_summary.suspicious_self_call_assignment_count;
        const bool improved_temp_table_fields =
            trial_summary.temporary_table_field_assignment_count <
            current_summary.temporary_table_field_assignment_count;
        const bool worsened_nil_calls =
            trial_summary.obvious_nil_call_count >
            current_summary.obvious_nil_call_count;
        const bool worsened_temp_rhs =
            trial_summary.suspicious_temporary_rhs_assignment_count >
            current_summary.suspicious_temporary_rhs_assignment_count;
        const bool worsened_self_calls =
            trial_summary.suspicious_self_call_assignment_count >
            current_summary.suspicious_self_call_assignment_count;
        const bool worsened_temp_table_fields =
            trial_summary.temporary_table_field_assignment_count >
            current_summary.temporary_table_field_assignment_count;
        const bool materially_higher_risk = trial_risk > current_risk + 12;
        const bool materially_lower_risk = trial_risk + 12 < current_risk;
        const bool allow_leading_helper_block_risk =
            repair_name == "booleanreturnwrapper" ||
            repair_name == "returnedcallunwrap";
        const bool parity_oriented_repair =
            repair_name == "statementtempsfromraw" ||
            repair_name == "expansion" ||
            repair_name == "functionblocks";
        const bool trial_has_artifact_markers =
            trial_summary.obvious_nil_call_count != 0 ||
            trial_summary.suspicious_temporary_rhs_assignment_count != 0 ||
            trial_summary.suspicious_self_call_assignment_count != 0;
        const bool allow_hotspot_quality_override =
            is_ai_core_hotspot_target &&
            parity_oriented_repair &&
            !trial_has_artifact_markers &&
            !materially_higher_risk;
        const bool allow_semantic_repair_without_readability_gain =
            repair_name == "statementtempsfromraw" &&
            (!is_priority_ownership_target || is_ai_core_hotspot_target);
        const bool allow_actions_condition_split_without_readability_gain =
            repair_name == "actionsflatandconditions";
        const bool has_material_improvement =
            improved_nil_calls || improved_temp_rhs || improved_self_calls ||
            improved_temp_table_fields || materially_lower_risk;
        if (!has_material_improvement && !allow_leading_helper_block_risk &&
            !allow_semantic_repair_without_readability_gain &&
            !allow_actions_condition_split_without_readability_gain &&
            !allow_hotspot_quality_override) {
            return false;
        }
        if ((worsened_nil_calls || worsened_temp_rhs || worsened_self_calls ||
             worsened_temp_table_fields || materially_higher_risk) &&
            !allow_leading_helper_block_risk &&
            !allow_semantic_repair_without_readability_gain &&
            !allow_actions_condition_split_without_readability_gain &&
            !allow_hotspot_quality_override &&
            !has_material_improvement) {
            return false;
        }
        repaired_clean = std::move(trial);
        return true;
    };

    const auto try_split_flat_and_condition_guards = [](std::string* source) {
        if (source == nullptr || source->empty()) {
            return false;
        }

        const auto split_top_level_and =
            [](std::string_view condition, std::string* lhs_out, std::string* rhs_out) {
                if (lhs_out == nullptr || rhs_out == nullptr) {
                    return false;
                }
                bool in_single_quote = false;
                bool in_double_quote = false;
                bool escaped = false;
                int paren_depth = 0;
                for (std::size_t index = 0; index + 5 <= condition.size(); ++index) {
                    const char ch = condition[index];
                    if (escaped) {
                        escaped = false;
                        continue;
                    }
                    if (ch == '\\') {
                        escaped = true;
                        continue;
                    }
                    if (!in_double_quote && ch == '\'') {
                        in_single_quote = !in_single_quote;
                        continue;
                    }
                    if (!in_single_quote && ch == '"') {
                        in_double_quote = !in_double_quote;
                        continue;
                    }
                    if (in_single_quote || in_double_quote) {
                        continue;
                    }
                    if (ch == '(') {
                        ++paren_depth;
                        continue;
                    }
                    if (ch == ')' && paren_depth > 0) {
                        --paren_depth;
                        continue;
                    }
                    if (paren_depth != 0 || condition.substr(index, 5) != " and ") {
                        continue;
                    }

                    const std::string_view lhs =
                        TrimLuaWhitespace(condition.substr(0, index));
                    const std::string_view rhs =
                        TrimLuaWhitespace(condition.substr(index + 5));
                    if (lhs.empty() || rhs.empty() ||
                        lhs.find(" and ") != std::string_view::npos ||
                        rhs.find(" and ") != std::string_view::npos ||
                        lhs.find(" or ") != std::string_view::npos ||
                        rhs.find(" or ") != std::string_view::npos) {
                        return false;
                    }
                    *lhs_out = std::string(lhs);
                    *rhs_out = std::string(rhs);
                    return true;
                }
                return false;
            };

        std::vector<std::string> lines = SplitRenderedLines(*source);
        bool changed = false;
        for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment(lines[line_index]));
            if (!trimmed.starts_with("if ") || !trimmed.ends_with(" then")) {
                continue;
            }

            std::string lhs_condition;
            std::string rhs_condition;
            const std::string_view raw_condition =
                TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
            if (!split_top_level_and(raw_condition, &lhs_condition, &rhs_condition)) {
                continue;
            }

            std::size_t end_line = std::string::npos;
            bool unsupported_body = false;
            for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
                const std::string_view body_trimmed =
                    TrimLuaWhitespace(StripLuaLineComment(lines[probe]));
                if (body_trimmed.empty()) {
                    continue;
                }
                if (body_trimmed == "end") {
                    end_line = probe;
                    break;
                }
                if (body_trimmed == "else" || body_trimmed.starts_with("elseif ") ||
                    body_trimmed.starts_with("if ") || body_trimmed.starts_with("for ") ||
                    body_trimmed.starts_with("while ") || body_trimmed == "repeat" ||
                    body_trimmed == "do" || body_trimmed.starts_with("function ") ||
                    body_trimmed.find(" = function(") != std::string_view::npos) {
                    unsupported_body = true;
                    break;
                }
            }
            if (unsupported_body || end_line == std::string::npos ||
                end_line <= line_index + 1) {
                continue;
            }

            const std::string& original_if_line = lines[line_index];
            const std::size_t first_non_space =
                original_if_line.find_first_not_of(" \t");
            const std::string base_indent =
                first_non_space == std::string::npos
                    ? std::string()
                    : original_if_line.substr(0, first_non_space);
            const std::string nested_indent = base_indent + "  ";
            for (std::size_t probe = line_index + 1; probe < end_line; ++probe) {
                lines[probe] = "  " + lines[probe];
            }
            lines[line_index] = base_indent + "if " + lhs_condition + " then";
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                         nested_indent + "if " + rhs_condition + " then");
            lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(end_line + 1),
                         nested_indent + "end");
            changed = true;
            line_index = end_line + 1;
        }
        if (!changed) {
            return false;
        }

        *source = JoinRenderedLines(lines);
        return true;
    };

    bool repaired_expanded_assignments = false;
    repaired_expanded_assignments = try_apply_validated_repair(
        "expansion",
        allow_scoped_raw_heavy_repairs &&
            ShouldTryExpandedAssignmentRepairForValidatedCandidate(clean_summary),
        [&](std::string* trial) {
            return TryRepairExpandedAssignmentsFromRaw(raw_source, trial);
        });
    bool repaired_statement_temporaries_from_raw = false;
    repaired_statement_temporaries_from_raw = try_apply_validated_repair(
        "statementtempsfromraw",
        allow_scoped_raw_heavy_repairs &&
            ShouldTryStatementTempsFromRawForValidatedCandidate(repaired_clean, clean_summary),
        [&](std::string* trial) {
            return TryRepairStatementTemporaryLocalsFromRaw(raw_source, trial);
        });
    bool repaired_suspicious_function_blocks = false;
    repaired_suspicious_function_blocks = try_apply_validated_repair(
        "functionblocks",
        allow_scoped_raw_heavy_repairs &&
            ShouldTryExpandedAssignmentRepairForValidatedCandidate(clean_summary),
        [&](std::string* trial) {
            return TryRepairSuspiciousFunctionBlocksFromRaw(raw_source, trial);
        });
    bool repaired_seeded_call_argument_locals = false;
    repaired_seeded_call_argument_locals =
        TryInlineSyntheticSeededCallArgumentLocals(chunk_name,
                                                   &repaired_clean,
                                                   note,
                                                   note_prefix);
    bool repaired_returned_call_unwrap = false;
    repaired_returned_call_unwrap = try_apply_validated_repair(
        "returnedcallunwrap",
        repaired_clean.find("return (") != std::string::npos,
        [&](std::string* trial) {
            std::vector<std::string> lines = SplitRenderedLines(*trial);
            if (!refii::lua::UnwrapRenderedLuaReturnedCallsForExactParity(&lines)) {
                return false;
            }
            *trial = JoinRenderedLines(lines);
            return true;
        });
    bool repaired_boolean_return_wrappers = false;
    repaired_boolean_return_wrappers = try_apply_validated_repair(
        "booleanreturnwrapper",
        true,
        [&](std::string* trial) {
            return TryRepairBooleanReturnWrapperFunctionBlocksFromBytecode(
                chunk,
                chunk_name,
                trial);
        });
    bool repaired_actions_flat_and_conditions = false;
    repaired_actions_flat_and_conditions = try_apply_validated_repair(
        "actionsflatandconditions",
        IsLuaInteractiveCutsceneActionsOwnershipPath(chunk_name),
        [&](std::string* trial) {
            return try_split_flat_and_condition_guards(trial);
        });
    const bool repaired_bytecode_main_chunk =
        TryApplyBytecodeBackedMainChunkRestorations(chunk.main,
                                                    chunk_name,
                                                    &repaired_clean,
                                                    note,
                                                    note_prefix);
    if (!repaired_statement_temporaries_from_raw) {
        const LuaReadabilitySummary refreshed_summary = SummarizeLuaReadability(repaired_clean);
        repaired_statement_temporaries_from_raw = try_apply_validated_repair(
            "statementtempsfromraw",
            allow_scoped_raw_heavy_repairs &&
                ShouldTryStatementTempsFromRawForValidatedCandidate(repaired_clean,
                                                                    refreshed_summary),
            [&](std::string* trial) {
                return TryRepairStatementTemporaryLocalsFromRaw(raw_source, trial);
            });
    }
    if (!(repaired_function_boundary_parity ||
          repaired_expanded_assignments ||
          repaired_statement_temporaries_from_raw ||
          repaired_suspicious_function_blocks ||
          repaired_seeded_call_argument_locals ||
          repaired_returned_call_unwrap ||
          repaired_actions_flat_and_conditions ||
          repaired_boolean_return_wrappers ||
          repaired_bytecode_main_chunk)) {
        return false;
    }

    const auto normalize_adjacent_duplicate_local_calls = [](std::string* source) {
        if (source == nullptr || source->empty()) {
            return false;
        }

        std::vector<std::string> lines = SplitRenderedLines(*source);
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index + 1 < lines.size(); ++line_index) {
            bool first_is_local = false;
            std::string first_lhs;
            std::string first_rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index],
                                               &first_is_local,
                                               &first_lhs,
                                               &first_rhs) ||
                !first_is_local || !IsValidLuaIdentifier(first_lhs) ||
                CountIdentifierUses(first_rhs, first_lhs) != 0) {
                continue;
            }

            bool second_is_local = false;
            std::string second_lhs;
            std::string second_rhs;
            if (!ParseSingleRenderedAssignment(lines[line_index + 1],
                                               &second_is_local,
                                               &second_lhs,
                                               &second_rhs) ||
                !second_is_local || second_lhs != first_lhs ||
                CountIdentifierUses(second_rhs, second_lhs) != 0) {
                continue;
            }

            std::string first_callee;
            std::vector<std::string> first_arguments;
            std::string second_callee;
            std::vector<std::string> second_arguments;
            if (!ParseRenderedSingleCallExpression(first_rhs, &first_callee, &first_arguments) ||
                !ParseRenderedSingleCallExpression(second_rhs, &second_callee, &second_arguments)) {
                continue;
            }

            const std::string normalized_first_rhs =
                NormalizeRenderedTrivialParenthesizedLiterals(first_rhs);
            const std::string normalized_second_rhs =
                NormalizeRenderedTrivialParenthesizedLiterals(second_rhs);
            if (normalized_first_rhs.empty() || normalized_first_rhs != normalized_second_rhs) {
                continue;
            }

            const std::string& original_line = lines[line_index + 1];
            const std::string_view stripped = StripLuaLineComment(original_line);
            const std::string_view comment =
                stripped.size() < original_line.size()
                    ? std::string_view(original_line).substr(stripped.size())
                    : std::string_view();
            const std::size_t first_non_space = original_line.find_first_not_of(" \t");
            const std::string indent =
                first_non_space == std::string::npos
                    ? std::string()
                    : original_line.substr(0, first_non_space);
            lines[line_index + 1] =
                indent + second_lhs + " = " + std::string(TrimLuaWhitespace(second_rhs)) +
                std::string(comment);
            pass_changed = true;
        }

        if (!pass_changed) {
            return false;
        }

        std::ostringstream rebuilt;
        for (const std::string& line : lines) {
            rebuilt << line << '\n';
        }
        *source = rebuilt.str();
        return true;
    };
    normalize_adjacent_duplicate_local_calls(&repaired_clean);

    *clean_source = std::move(repaired_clean);
    const struct {
        bool changed;
        std::string_view note_name;
    } repair_note_entries[] = {
        {repaired_function_boundary_parity,
         "repaired_from_rendered_functionboundaryparity"},
        {repaired_expanded_assignments, "repaired_from_raw_expansion"},
        {repaired_statement_temporaries_from_raw,
         "repaired_from_raw_statementtemps"},
        {repaired_suspicious_function_blocks, "repaired_from_raw_functionblocks"},
        {repaired_returned_call_unwrap,
         "repaired_from_rendered_returnedcallunwrap"},
        {repaired_actions_flat_and_conditions,
         "repaired_from_rendered_actionsflatandconditions"},
        {repaired_boolean_return_wrappers,
         "repaired_from_bytecode_booleanreturnwrapper"},
    };
    for (const auto& repair_note_entry : repair_note_entries) {
        if (repair_note_entry.changed) {
            if (note != nullptr && !repair_note_entry.note_name.empty()) {
                std::string part;
                if (!note_prefix.empty()) {
                    part = std::string(note_prefix);
                    part.push_back('_');
                }
                part.append(repair_note_entry.note_name);
                AppendNotePart(note, part);
            }
        }
    }
    if (note != nullptr) {
        if (is_scoped_holdout || repaired_function_boundary_parity) {
            AppendNotePart(
                note,
                "native_function_boundary_parity_commits=" +
                    std::to_string(native_function_boundary_parity_commits));
            AppendNotePart(
                note,
                "native_local_commit_at_first_write=" +
                    std::to_string(native_local_commit_at_first_write));
            AppendNotePart(
                note,
                "parity_candidate_rejected_extra_local=" +
                    std::to_string(parity_candidate_rejected_extra_local));
        }
        const std::size_t callvalue_fallback_repairs =
            repaired_statement_temporaries_from_raw ? std::size_t{1} : std::size_t{0};

        std::string part;
        if (!note_prefix.empty()) {
            part = std::string(note_prefix);
            part.push_back('_');
        }
        part += "statementtempsfromraw_applied=";
        part += repaired_statement_temporaries_from_raw ? "1" : "0";
        AppendNotePart(note, part);

        std::string callvalue_part;
        if (!note_prefix.empty()) {
            callvalue_part = std::string(note_prefix);
            callvalue_part.push_back('_');
        }
        callvalue_part += "callvalueownership_fallback_repairs=";
        callvalue_part += std::to_string(callvalue_fallback_repairs);
        AppendNotePart(note, callvalue_part);
    }
    return true;
}

}  // namespace

bool CouldValidatedCandidateBenefitFromRawRepair(std::string_view clean_source) {
    if (clean_source.empty()) {
        return false;
    }

    const LuaReadabilitySummary clean_summary = SummarizeLuaReadability(clean_source);
    return ShouldTryExpandedAssignmentRepairForValidatedCandidate(clean_summary) ||
           clean_summary.suspicious_self_call_assignment_count != 0 ||
           clean_summary.suspicious_temporary_rhs_assignment_count != 0 ||
           clean_summary.obvious_nil_call_count != 0 ||
           clean_summary.temporary_table_field_assignment_count != 0 ||
           clean_source.find("return (") != std::string_view::npos ||
           clean_source.find("SetLookAtCamera") != std::string_view::npos ||
           clean_source.find("SetFixedCamera") != std::string_view::npos ||
           clean_source.find("PointHeroCameraTowards") != std::string_view::npos ||
           clean_source.find("OverrideHeroCamera") != std::string_view::npos;
}

bool TryRepairValidatedCandidateFromRaw(const std::vector<char>& input_bytes,
                                        std::string_view chunk_name,
                                        std::string_view raw_source,
                                        std::string* clean_source,
                                        std::string* note,
                                        std::string_view note_prefix) {
    if (clean_source == nullptr || raw_source.empty() || !IsLuaBytecode(input_bytes)) {
        return false;
    }

    Lua51Chunk chunk;
    std::string parse_error;
    if (!ParseLua51Chunk(input_bytes, &chunk, &parse_error)) {
        return false;
    }

    return TryRepairValidatedCandidateFromRawInternal(
        chunk,
        chunk_name,
        raw_source,
        clean_source,
        note,
        note_prefix);
}

bool TryApplyBytecodeBackedMainChunkRestorations(const std::vector<char>& input_bytes,
                                                 std::string_view chunk_name,
                                                 std::string* source,
                                                 std::string* note,
                                                 std::string_view note_prefix) {
    if (source == nullptr || source->empty() || !IsLuaBytecode(input_bytes)) {
        return false;
    }

    Lua51Chunk chunk;
    std::string parse_error;
    if (!ParseLua51Chunk(input_bytes, &chunk, &parse_error)) {
        return false;
    }

    return TryApplyBytecodeBackedMainChunkRestorations(
        chunk.main, chunk_name, source, note, note_prefix);
}

bool TryRenderValidatedNativeMainChunk(const std::vector<char>& input_bytes,
                                       std::string_view chunk_name,
                                       NativeMainChunkCandidate* candidate) {
    if (candidate == nullptr) {
        return false;
    }

    *candidate = {};
    if (!IsLuaBytecode(input_bytes)) {
        candidate->note = "native_first_non_bytecode";
        return false;
    }

    Lua51Chunk chunk;
    std::string parse_error;
    if (!ParseLua51Chunk(input_bytes, &chunk, &parse_error)) {
        candidate->note = parse_error.empty() ? "native_first_parse_failed"
                                              : "native_first_parse_failed=" + parse_error;
        return false;
    }

    refii::lua::audit::LuaBytecodeParityReference parity_reference;
    const bool have_parity_reference =
        refii::lua::audit::BuildLuaBytecodeParityReference(input_bytes, &parity_reference);

    // Some very large main chunks are practical worst cases for the clean native
    // renderer. Skip the clean pass for those, but still try the raw renderer
    // before falling back to the external decompiler/disassembly path.
    const bool skip_clean_candidate = input_bytes.size() > 150000;

    NativeMainChunkCandidate best_valid_candidate;
    bool have_valid_candidate = false;
    std::optional<NativeMainChunkCandidate> clean_valid_candidate;
    std::optional<NativeMainChunkCandidate> raw_valid_candidate;
    std::optional<LuaReadabilitySummary> clean_valid_summary;
    std::optional<LuaReadabilitySummary> raw_valid_summary;
    std::optional<std::string> clean_invalid_note;
    std::optional<std::string> raw_invalid_note;
    NativeMainChunkCandidate best_invalid_candidate;
    bool have_invalid_candidate = false;
    const auto try_candidate = [&](bool use_raw_renderer) {
        NativeMainChunkCandidate rendered_candidate;
        if (!TryRenderCandidate(chunk.main,
                                have_parity_reference ? &parity_reference : nullptr,
                                chunk_name,
                                use_raw_renderer,
                                &rendered_candidate)) {
            return false;
        }

        if (rendered_candidate.syntax_validated) {
            const LuaReadabilitySummary rendered_summary =
                SummarizeLuaReadability(rendered_candidate.source);
            if (use_raw_renderer) {
                raw_valid_candidate = rendered_candidate;
                raw_valid_summary = rendered_summary;
            } else {
                clean_valid_candidate = rendered_candidate;
                clean_valid_summary = rendered_summary;
            }

            if (!have_valid_candidate ||
                IsPreferredValidatedCandidate(rendered_candidate, best_valid_candidate)) {
                best_valid_candidate = rendered_candidate;
                have_valid_candidate = true;
            }

            if (!use_raw_renderer &&
                !ShouldTryRawAlternativeForValidatedCandidate(rendered_candidate.source)) {
                *candidate = std::move(rendered_candidate);
                return true;
            }
            return false;
        }

        if (use_raw_renderer) {
            raw_invalid_note = rendered_candidate.note;
        } else {
            clean_invalid_note = rendered_candidate.note;
        }

        if (!have_invalid_candidate ||
            (best_invalid_candidate.used_raw_renderer && !rendered_candidate.used_raw_renderer)) {
            best_invalid_candidate = std::move(rendered_candidate);
            have_invalid_candidate = true;
        }
        return false;
    };

    if (!skip_clean_candidate) {
        if (try_candidate(false)) {
            return true;
        }
    } else {
        candidate->note = "native_first_skipped_clean_large_chunk";
    }

    if (try_candidate(true)) {
        return true;
    }

    if (clean_valid_candidate.has_value() && raw_valid_candidate.has_value()) {
        best_valid_candidate = IsPreferredValidatedCandidate(*raw_valid_candidate,
                                                             *clean_valid_candidate)
                                   ? *raw_valid_candidate
                                   : *clean_valid_candidate;
        have_valid_candidate = true;
        std::string repair_note;
        if (TryRepairValidatedCandidateFromRawInternal(chunk,
                                                       chunk_name,
                                                       raw_valid_candidate->source,
                                                       &clean_valid_candidate->source,
                                                       &repair_note,
                                                       "native_first_clean_candidate")) {
            RecordReadabilitySummary(clean_valid_candidate->source, &*clean_valid_candidate);
            clean_valid_candidate->main_stats_exact = false;
            clean_valid_candidate->full_stats_exact = false;
            clean_valid_candidate->exact_parity = false;
            clean_valid_candidate->compiled_candidate = {};
            clean_valid_candidate->first_mismatch = {};
            RecordCandidateParity(have_parity_reference ? &parity_reference : nullptr,
                                  chunk_name,
                                  &*clean_valid_candidate);
            if (!clean_valid_candidate->exact_parity && have_parity_reference) {
                TryApplyExactParityNormalization(&parity_reference,
                                                chunk_name,
                                                &clean_valid_candidate->source,
                                                &repair_note,
                                                "native_first_clean_candidate");
                RecordReadabilitySummary(clean_valid_candidate->source, &*clean_valid_candidate);
                clean_valid_candidate->main_stats_exact = false;
                clean_valid_candidate->full_stats_exact = false;
                clean_valid_candidate->exact_parity = false;
                clean_valid_candidate->compiled_candidate = {};
                clean_valid_candidate->first_mismatch = {};
                RecordCandidateParity(&parity_reference,
                                      chunk_name,
                                      &*clean_valid_candidate);
            }
            if (!have_parity_reference || clean_valid_candidate->exact_parity) {
                clean_valid_candidate->note = "native_first_validated";
                AppendNotePart(&clean_valid_candidate->note, repair_note);
                clean_valid_summary = clean_valid_candidate->readability;
                best_valid_candidate = *clean_valid_candidate;
                have_valid_candidate = true;
            } else {
                AppendNotePart(&clean_valid_candidate->note,
                               "native_first_clean_candidate_rejected_nonparity");
            }
        }
    }

    if (have_valid_candidate) {
        *candidate = std::move(best_valid_candidate);
        if (clean_valid_summary.has_value()) {
            AppendNotePart(&candidate->note,
                           "native_first_clean=" +
                               DescribeLuaReadability(*clean_valid_summary));
        }
        if (raw_valid_summary.has_value()) {
            AppendNotePart(&candidate->note,
                           "native_first_raw=" +
                               DescribeLuaReadability(*raw_valid_summary));
        }
        if (clean_invalid_note.has_value()) {
            AppendNotePart(&candidate->note,
                           "native_first_clean_rejected=" + *clean_invalid_note);
        }
        if (raw_invalid_note.has_value()) {
            AppendNotePart(&candidate->note,
                           "native_first_raw_rejected=" + *raw_invalid_note);
        }
        if (candidate->parity_checked) {
            if (candidate->exact_parity) {
                AppendNotePart(&candidate->note, "native_first_exact_parity");
            } else if (candidate->first_mismatch.available) {
                AppendNotePart(&candidate->note,
                               "native_first_parity_mismatch=" +
                                   refii::lua::audit::DescribeMismatchForSummary(
                                       candidate->first_mismatch));
            } else {
                AppendNotePart(&candidate->note, "native_first_nonexact_parity");
            }
        }
        AppendNotePart(&candidate->note,
                       candidate->used_raw_renderer ? "native_first_selected=raw"
                                                    : "native_first_selected=clean");
        return true;
    }

    if (have_invalid_candidate) {
        *candidate = std::move(best_invalid_candidate);
        if (clean_invalid_note.has_value()) {
            AppendNotePart(&candidate->note,
                           "native_first_clean_rejected=" + *clean_invalid_note);
        }
        if (raw_invalid_note.has_value()) {
            AppendNotePart(&candidate->note,
                           "native_first_raw_rejected=" + *raw_invalid_note);
        }
        return true;
    }

    return false;
}

}  // namespace refii::lua::decompiler
