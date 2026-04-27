#include "tools/decompiler/rendered_lua_text.h"
#include "tools/decompiler/straight_line_render.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <iomanip>
#include <optional>
#include <sstream>
#include <unordered_map>
#include <unordered_set>

namespace refii::lua {
namespace {

bool IsRenderedSafePrefixExpression(std::string_view candidate);

struct RenderedLuaCleanupStepRuntime {
    std::string_view step_name;
    int pass_iterations = 0;
    bool cap_hit = false;
};

thread_local RenderedLuaCleanupProfileTelemetry g_last_cleanup_profile_telemetry;
thread_local RenderedLuaCleanupStepRuntime* g_active_cleanup_step_runtime = nullptr;

int ParseRenderedLuaCleanupPassCapOverride() {
    static int cached_override = []() {
        const char* value = std::getenv("REFII_LUA_CLEANUP_PASS_CAP");
        if (value == nullptr || *value == '\0') {
            return 0;
        }

        char* end = nullptr;
        const long parsed = std::strtol(value, &end, 10);
        if (end == value || (end != nullptr && *end != '\0') || parsed <= 0) {
            return 0;
        }
        return static_cast<int>(std::min<long>(parsed, 256));
    }();
    return cached_override;
}

int GetRenderedLuaCleanupStepPassCap(std::string_view step_name, int default_cap) {
    int pass_cap = std::max(default_cap, 1);
    const std::string_view profile_name = g_last_cleanup_profile_telemetry.profile_name;
    if (profile_name == "quest") {
        pass_cap = std::min(pass_cap, 48);
    } else if (profile_name == "bootstrap") {
        pass_cap = std::min(pass_cap, 40);
    } else if (profile_name == "exact") {
        pass_cap = std::min(pass_cap, 32);
    } else {
        pass_cap = std::min(pass_cap, 32);
    }

    if (step_name.find("Unhandled") != std::string_view::npos ||
        step_name.find("NestedElseIfChains") != std::string_view::npos ||
        step_name.find("TerminalElseIfNestedIfBranches") != std::string_view::npos) {
        pass_cap = std::min(pass_cap, 24);
    } else if (step_name.find("Table") != std::string_view::npos ||
               step_name.find("Setlist") != std::string_view::npos ||
               step_name.find("LiteralSeed") != std::string_view::npos) {
        pass_cap = std::min(pass_cap, 24);
    } else if (step_name.find("Temporaries") != std::string_view::npos ||
               step_name.find("Temporary") != std::string_view::npos) {
        pass_cap = std::min(pass_cap, 32);
    }

    const int env_cap = ParseRenderedLuaCleanupPassCapOverride();
    if (env_cap > 0) {
        pass_cap = std::min(pass_cap, env_cap);
    }
    return std::max(pass_cap, 1);
}

void NoteRenderedLuaCleanupStepPass(std::string_view step_name) {
    if (g_active_cleanup_step_runtime != nullptr &&
        g_active_cleanup_step_runtime->step_name == step_name) {
        ++g_active_cleanup_step_runtime->pass_iterations;
    }
}

void NoteRenderedLuaCleanupStepCapHit(std::string_view step_name) {
    if (g_active_cleanup_step_runtime != nullptr &&
        g_active_cleanup_step_runtime->step_name == step_name) {
        g_active_cleanup_step_runtime->cap_hit = true;
    }
}

template <typename PassFn>
bool RunRenderedLuaCleanupPassLoop(std::string_view step_name,
                                   int default_cap,
                                   PassFn&& run_single_pass) {
    bool changed = false;
    const int pass_cap = GetRenderedLuaCleanupStepPassCap(step_name, default_cap);
    for (int pass = 0; pass < pass_cap; ++pass) {
        NoteRenderedLuaCleanupStepPass(step_name);
        const bool pass_changed = run_single_pass();
        if (!pass_changed) {
            break;
        }
        changed = true;
        if (pass + 1 >= pass_cap) {
            NoteRenderedLuaCleanupStepCapHit(step_name);
        }
    }
    return changed;
}

void AppendRenderedLuaCleanupCappedStep(std::string_view step_name) {
    if (step_name.empty()) {
        return;
    }
    if (!g_last_cleanup_profile_telemetry.capped_steps.empty()) {
        g_last_cleanup_profile_telemetry.capped_steps.push_back(',');
    }
    g_last_cleanup_profile_telemetry.capped_steps.append(step_name);
    ++g_last_cleanup_profile_telemetry.capped_step_count;
}

bool ParseRenderedLocalDeclarationLine(std::string_view line,
                                       std::vector<std::string>* names) {
    if (names == nullptr) {
        return false;
    }

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!trimmed.starts_with("local ")) {
        return false;
    }
    trimmed = TrimLuaWhitespace(trimmed.substr(6));
    if (trimmed.empty() || trimmed.find('=') != std::string_view::npos) {
        return false;
    }

    names->clear();
    std::size_t cursor = 0;
    while (cursor < trimmed.size()) {
        const std::size_t comma = trimmed.find(',', cursor);
        const std::string_view part = TrimLuaWhitespace(
            comma == std::string_view::npos ? trimmed.substr(cursor)
                                            : trimmed.substr(cursor, comma - cursor));
        if (part.empty() || !IsValidLuaIdentifier(part)) {
            names->clear();
            return false;
        }
        names->emplace_back(part);
        if (comma == std::string_view::npos) {
            break;
        }
        cursor = comma + 1;
    }

    return !names->empty();
}

bool IsRenderedShadowingLocalDeclaration(std::string_view line, std::string_view identifier) {
    if (identifier.empty()) {
        return false;
    }

    std::vector<std::string> names;
    if (ParseRenderedLocalDeclarationLine(line, &names)) {
        return std::find(names.begin(), names.end(), identifier) != names.end();
    }

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!trimmed.starts_with("local function ")) {
        return false;
    }

    trimmed = TrimLuaWhitespace(trimmed.substr(15));
    const std::size_t open = trimmed.find('(');
    if (open == std::string_view::npos) {
        return false;
    }

    const std::string_view name = TrimLuaWhitespace(trimmed.substr(0, open));
    return name == identifier;
}

bool IsRenderedTitleCaseCandidate(std::string_view name) {
    if (name.empty()) {
        return false;
    }

    const unsigned char first = static_cast<unsigned char>(name.front());
    if (std::isupper(first) == 0) {
        return false;
    }

    for (std::size_t i = 1; i < name.size(); ++i) {
        const unsigned char ch = static_cast<unsigned char>(name[i]);
        if (std::islower(ch) != 0) {
            return true;
        }
    }
    return false;
}

bool IsRenderedSyntheticRootWrapperFunction(const std::vector<std::string>& lines,
                                            std::size_t function_start,
                                            std::size_t function_end) {
    if (function_start >= lines.size() || function_end >= lines.size() ||
        function_end <= function_start) {
        return false;
    }

    const std::string_view header =
        TrimLuaWhitespace(StripLuaLineComment(lines[function_start]));
    if (!header.starts_with("function(")) {
        return false;
    }

    for (std::size_t probe = 0; probe < function_start; ++probe) {
        if (!TrimLuaWhitespace(StripLuaLineComment(lines[probe])).empty()) {
            return false;
        }
    }

    const std::string_view footer =
        TrimLuaWhitespace(StripLuaLineComment(lines[function_end]));
    if (footer != "end") {
        return false;
    }

    for (std::size_t probe = function_end + 1; probe < lines.size(); ++probe) {
        if (!TrimLuaWhitespace(StripLuaLineComment(lines[probe])).empty()) {
            return false;
        }
    }

    return true;
}

void SeedRenderedFunctionParameters(std::string_view line,
                                    std::unordered_set<std::string>* seen_names) {
    if (seen_names == nullptr) {
        return;
    }

    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    const std::size_t function_pos = trimmed.find("function(");
    if (function_pos == std::string_view::npos) {
        return;
    }

    const std::size_t params_begin = function_pos + 9;
    const std::size_t params_end = trimmed.find(')', params_begin);
    if (params_end == std::string_view::npos || params_end <= params_begin) {
        return;
    }

    const std::string_view params = trimmed.substr(params_begin, params_end - params_begin);
    std::size_t cursor = 0;
    while (cursor < params.size()) {
        const std::size_t comma = params.find(',', cursor);
        const std::string_view part = TrimLuaWhitespace(
            comma == std::string_view::npos ? params.substr(cursor)
                                            : params.substr(cursor, comma - cursor));
        if (!part.empty() && part != "..." && IsValidLuaIdentifier(part)) {
            seen_names->insert(std::string(part));
        }
        if (comma == std::string_view::npos) {
            break;
        }
        cursor = comma + 1;
    }
}

bool IsAssignmentToIdentifier(std::string_view line, std::string_view identifier) {
    std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.substr(0, 6) == "local ") {
        trimmed = TrimLuaWhitespace(trimmed.substr(6));
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    std::size_t cursor = 0;
    while (cursor < lhs.size()) {
        const std::size_t comma = lhs.find(',', cursor);
        const std::string_view part = TrimLuaWhitespace(
            comma == std::string_view::npos ? lhs.substr(cursor)
                                            : lhs.substr(cursor, comma - cursor));
        if (part == identifier) {
            return true;
        }
        if (comma == std::string_view::npos) {
            break;
        }
        cursor = comma + 1;
    }
    return false;
}

bool ParseRenderedSingleAssignmentLine(std::string_view line,
                                       bool* is_local,
                                       std::string* lhs,
                                       std::string* rhs) {
    if (lhs == nullptr || rhs == nullptr) {
        return false;
    }

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty()) {
        return false;
    }

    bool local_assignment = false;
    if (trimmed.starts_with("local ")) {
        local_assignment = true;
        trimmed = TrimLuaWhitespace(trimmed.substr(6));
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs_view = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs_view = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (lhs_view.empty() || rhs_view.empty() ||
        lhs_view.find(',') != std::string_view::npos) {
        return false;
    }

    if (!IsRenderedSafePrefixExpression(lhs_view)) {
        return false;
    }

    if (is_local != nullptr) {
        *is_local = local_assignment;
    }
    *lhs = std::string(lhs_view);
    *rhs = std::string(rhs_view);
    return true;
}

bool ParseRenderedIndexedTargetExpression(std::string_view expression,
                                          std::string* base,
                                          std::string* key,
                                          std::string* suffix) {
    expression = TrimLuaWhitespace(expression);
    if (expression.empty()) {
        return false;
    }

    const std::size_t open = expression.find('[');
    if (open == std::string_view::npos || open == 0) {
        return false;
    }

    const std::string_view base_view = TrimLuaWhitespace(expression.substr(0, open));
    if (base_view.empty() || !IsRenderedSafePrefixExpression(base_view) ||
        base_view.find('[') != std::string_view::npos ||
        base_view.find('(') != std::string_view::npos ||
        base_view.find(':') != std::string_view::npos) {
        return false;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    int bracket_depth = 1;
    std::size_t close = std::string_view::npos;
    for (std::size_t i = open + 1; i < expression.size(); ++i) {
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
        if (ch == '[') {
            ++bracket_depth;
            continue;
        }
        if (ch == ']') {
            --bracket_depth;
            if (bracket_depth == 0) {
                close = i;
                break;
            }
        }
    }

    if (close == std::string_view::npos || close <= open + 1) {
        return false;
    }

    const std::string_view key_view =
        TrimLuaWhitespace(expression.substr(open + 1, close - open - 1));
    const std::string_view suffix_view =
        TrimLuaWhitespace(expression.substr(close + 1));
    if (key_view.empty() ||
        (!suffix_view.empty() && suffix_view.front() != '.' &&
         suffix_view.front() != '[')) {
        return false;
    }

    if (base != nullptr) {
        *base = std::string(base_view);
    }
    if (key != nullptr) {
        *key = std::string(key_view);
    }
    if (suffix != nullptr) {
        *suffix = std::string(suffix_view);
    }
    return true;
}

std::string_view GetRenderedLuaIndent(std::string_view line) {
    const std::size_t first_non_ws = line.find_first_not_of(" \t");
    if (first_non_ws == std::string_view::npos) {
        return line;
    }
    return line.substr(0, first_non_ws);
}

bool RenderedLineBeginsScopedBlock(std::string_view trimmed) {
    trimmed = TrimLuaWhitespace(StripLuaLineComment(trimmed));
    return (trimmed.starts_with("if ") && trimmed.ends_with(" then")) ||
           (trimmed.starts_with("for ") && trimmed.ends_with(" do")) ||
           (trimmed.starts_with("while ") && trimmed.ends_with(" do")) ||
           trimmed == "do" ||
           trimmed == "repeat" ||
           trimmed.starts_with("function ") ||
           trimmed.starts_with("local function ");
}

bool RenderedLineBeginsLoopBlock(std::string_view trimmed) {
    trimmed = TrimLuaWhitespace(StripLuaLineComment(trimmed));
    return (trimmed.starts_with("for ") && trimmed.ends_with(" do")) ||
           (trimmed.starts_with("while ") && trimmed.ends_with(" do")) ||
           trimmed == "repeat";
}

void ComputeRenderedScopePaths(const std::vector<std::string>& lines,
                               std::vector<std::vector<std::size_t>>* scope_paths) {
    if (scope_paths == nullptr) {
        return;
    }

    scope_paths->assign(lines.size(), {});
    std::vector<std::size_t> scope_stack;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        (*scope_paths)[line_index] = scope_stack;
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment(lines[line_index]));
        if (trimmed.empty()) {
            continue;
        }
        if (trimmed == "end" || trimmed.starts_with("until ")) {
            if (!scope_stack.empty()) {
                scope_stack.pop_back();
            }
            continue;
        }
        if (trimmed == "else" || trimmed.starts_with("elseif ")) {
            if (!scope_stack.empty()) {
                scope_stack.back() = line_index;
            }
            continue;
        }
        if (RenderedLineBeginsScopedBlock(trimmed)) {
            scope_stack.push_back(line_index);
        }
    }
}

std::size_t FindEnclosingRenderedFunctionBlockEnd(const std::vector<std::string>& lines,
                                                  std::size_t line_index) {
    std::size_t best_start = lines.size();
    std::size_t best_end = lines.size();
    const std::size_t limit = std::min(line_index, lines.size());
    for (std::size_t candidate = 0; candidate < limit; ++candidate) {
        if (!IsRenderedFunctionStartLine(lines[candidate])) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, candidate);
        if (function_end >= lines.size() || function_end < line_index) {
            continue;
        }

        if (best_start == lines.size() || candidate > best_start) {
            best_start = candidate;
            best_end = function_end;
        }
    }

    return best_end;
}

std::size_t FindEnclosingRenderedFunctionBlockBegin(const std::vector<std::string>& lines,
                                                    std::size_t line_index) {
    std::size_t best_start = lines.size();
    const std::size_t limit = std::min(line_index, lines.size());
    for (std::size_t candidate = 0; candidate < limit; ++candidate) {
        if (!IsRenderedFunctionStartLine(lines[candidate])) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, candidate);
        if (function_end >= lines.size() || function_end < line_index) {
            continue;
        }

        if (best_start == lines.size() || candidate > best_start) {
            best_start = candidate;
        }
    }

    return best_start;
}

bool ShouldEmitLocalForRenderedIdentifierTarget(const std::vector<std::string>& lines,
                                                std::size_t line_index,
                                                std::string_view target_expression) {
    if (!IsValidLuaIdentifier(target_expression)) {
        return false;
    }

    const std::size_t function_begin =
        FindEnclosingRenderedFunctionBlockBegin(lines, line_index);
    std::size_t scan_begin = 0;
    if (function_begin != lines.size()) {
        scan_begin = function_begin;
        std::unordered_set<std::string> function_parameters;
        SeedRenderedFunctionParameters(lines[function_begin], &function_parameters);
        if (function_parameters.find(std::string(target_expression)) != function_parameters.end()) {
            return false;
        }
        ++scan_begin;
    }

    for (std::size_t probe = scan_begin; probe < line_index && probe < lines.size(); ++probe) {
        if (IsRenderedShadowingLocalDeclaration(lines[probe], target_expression)) {
            return false;
        }
        if (IsAssignmentToIdentifier(lines[probe], target_expression)) {
            return false;
        }
    }

    if (function_begin != lines.size()) {
        const std::size_t outer_function_begin =
            FindEnclosingRenderedFunctionBlockBegin(lines, function_begin);
        if (outer_function_begin != lines.size() && outer_function_begin < function_begin) {
            for (std::size_t probe = outer_function_begin + 1;
                 probe < function_begin && probe < lines.size();
                 ++probe) {
                if (IsRenderedFunctionStartLine(lines[probe])) {
                    const std::size_t nested_end = FindRenderedFunctionBlockEnd(lines, probe);
                    if (nested_end >= lines.size()) {
                        return false;
                    }
                    if (nested_end < function_begin) {
                        probe = nested_end;
                        continue;
                    }
                }

                if (IsRenderedShadowingLocalDeclaration(lines[probe], target_expression) ||
                    IsAssignmentToIdentifier(lines[probe], target_expression)) {
                    return false;
                }
            }
        }
    }

    return true;
}

bool IsRenderedExactReturnIdentifier(std::string_view line, std::string_view identifier) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!StartsWithLuaKeyword(trimmed, "return")) {
        return false;
    }
    return TrimLuaWhitespace(trimmed.substr(6)) == identifier;
}

bool TryParseRenderedTemporaryBackedTableField(const std::vector<std::string>& lines,
                                               std::size_t line_index,
                                               std::string_view table_name,
                                               std::string* field_key,
                                               std::string* rhs_expression,
                                               bool* rhs_is_function,
                                               int* array_index,
                                               std::size_t* consumed_lines);
bool IsRenderedSafePrefixExpression(std::string_view candidate);
bool IsRenderedAtomicExpression(std::string_view candidate);
bool FindRenderedMatchingParen(std::string_view text,
                               std::size_t open_index,
                               std::size_t* close_index);

bool LooksLikeRenderedNumericLiteral(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.empty()) {
        return false;
    }

    std::size_t index = 0;
    if (value[index] == '+' || value[index] == '-') {
        ++index;
    }
    bool saw_digit = false;
    bool saw_decimal = false;
    bool saw_exponent = false;
    for (; index < value.size(); ++index) {
        const char ch = value[index];
        if (std::isdigit(static_cast<unsigned char>(ch)) != 0) {
            saw_digit = true;
            continue;
        }
        if (ch == '.' && !saw_decimal && !saw_exponent) {
            saw_decimal = true;
            continue;
        }
        if ((ch == 'e' || ch == 'E') && saw_digit && !saw_exponent) {
            saw_exponent = true;
            saw_digit = false;
            if (index + 1 < value.size() && (value[index + 1] == '+' || value[index + 1] == '-')) {
                ++index;
            }
            continue;
        }
        return false;
    }
    return saw_digit;
}

bool HasRenderedTruthyOperator(std::string_view value) {
    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    for (std::size_t i = 0; i < value.size(); ++i) {
        const char ch = value[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
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
            continue;
        }
        if (ch == '{') {
            ++brace_depth;
            continue;
        }
        if (ch == '}') {
            --brace_depth;
            continue;
        }
        if (paren_depth != 0 || brace_depth != 0) {
            continue;
        }
        if (ch == '.' && i + 1 < value.size() && value[i + 1] == '.') {
            return true;
        }
        if (ch == '+' || ch == '*' || ch == '/' || ch == '%' || ch == '^' || ch == '#') {
            return true;
        }
        if (ch == '-') {
            if (i == 0) {
                return true;
            }
            const char previous = value[i - 1];
            if (previous != 'e' && previous != 'E') {
                return true;
            }
        }
    }
    return false;
}

bool IsRenderedDefinitelyTruthyExpression(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.empty() || value == "nil" || value == "false") {
        return false;
    }
    if (value == "true" || LooksLikeRenderedNumericLiteral(value) ||
        value.starts_with('"') || value.starts_with('\'') || value.starts_with('{') ||
        value.starts_with("function(")) {
        return true;
    }

    if (value.starts_with('(') && value.ends_with(')')) {
        return IsRenderedDefinitelyTruthyExpression(value.substr(1, value.size() - 2));
    }

    return HasRenderedTruthyOperator(value);
}

bool IsRenderedWholeParenthesized(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.size() < 2 || value.front() != '(' || value.back() != ')') {
        return false;
    }
    std::size_t close = std::string_view::npos;
    return FindRenderedMatchingParen(value, 0, &close) && close + 1 == value.size();
}

std::string StripRenderedWholeParentheses(std::string_view value, bool* changed) {
    value = TrimLuaWhitespace(value);
    while (IsRenderedWholeParenthesized(value)) {
        value = TrimLuaWhitespace(value.substr(1, value.size() - 2));
        if (changed != nullptr) {
            *changed = true;
        }
    }
    return std::string(value);
}

bool SplitRenderedTopLevelBooleanChain(std::string_view value,
                                       std::string_view keyword,
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
    bool found = false;
    for (std::size_t i = 0; i < value.size(); ++i) {
        const char ch = value[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
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
            continue;
        }
        if (ch == '{') {
            ++brace_depth;
            continue;
        }
        if (ch == '}') {
            --brace_depth;
            continue;
        }
        if (ch == '[') {
            ++bracket_depth;
            continue;
        }
        if (ch == ']') {
            --bracket_depth;
            continue;
        }
        if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
            continue;
        }

        if (i + keyword.size() <= value.size() &&
            value.substr(i, keyword.size()) == keyword) {
            const bool boundary_before =
                i == 0 || std::isspace(static_cast<unsigned char>(value[i - 1])) != 0;
            const std::size_t after = i + keyword.size();
            const bool boundary_after =
                after >= value.size() ||
                std::isspace(static_cast<unsigned char>(value[after])) != 0;
            if (boundary_before && boundary_after) {
                parts->push_back(
                    std::string(TrimLuaWhitespace(value.substr(part_begin, i - part_begin))));
                part_begin = after;
                found = true;
                i = after;
            }
        }
    }

    if (!found) {
        parts->clear();
        return false;
    }

    parts->push_back(std::string(TrimLuaWhitespace(value.substr(part_begin))));
    return std::all_of(parts->begin(), parts->end(), [](const std::string& part) {
        return !part.empty();
    });
}

bool FindRenderedTopLevelComparison(std::string_view value,
                                    std::size_t* position,
                                    std::string_view* op) {
    value = TrimLuaWhitespace(value);
    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    for (std::size_t i = 0; i < value.size(); ++i) {
        const char ch = value[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
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
            continue;
        }
        if (ch == '{') {
            ++brace_depth;
            continue;
        }
        if (ch == '}') {
            --brace_depth;
            continue;
        }
        if (ch == '[') {
            ++bracket_depth;
            continue;
        }
        if (ch == ']') {
            --bracket_depth;
            continue;
        }
        if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
            continue;
        }

        const std::string_view remaining = value.substr(i);
        std::string_view matched;
        if (remaining.starts_with("<=") || remaining.starts_with(">=") ||
            remaining.starts_with("==") || remaining.starts_with("~=")) {
            matched = remaining.substr(0, 2);
        } else if (remaining.starts_with("<") || remaining.starts_with(">")) {
            matched = remaining.substr(0, 1);
        }

        if (!matched.empty()) {
            if (position != nullptr) {
                *position = i;
            }
            if (op != nullptr) {
                *op = matched;
            }
            return true;
        }
    }
    return false;
}

std::string NormalizeRenderedConditionExpression(std::string_view value, bool* changed);

bool IsRenderedReusableConditionExpression(std::string_view value) {
    value = TrimLuaWhitespace(value);
    return IsValidLuaIdentifier(value) || value == "nil" || value == "true" ||
           value == "false" || LooksLikeRenderedNumericLiteral(value);
}

bool LooksLikeRenderedBooleanName(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.empty()) {
        return false;
    }

    std::string lowered;
    lowered.reserve(value.size());
    for (const char ch : value) {
        lowered.push_back(
            static_cast<char>(std::tolower(static_cast<unsigned char>(ch))));
    }

    static constexpr std::string_view kPrefixes[] = {
        "is",       "has",      "can",      "should", "was",      "were",
        "did",      "does",     "allow",    "allows", "need",     "needs",
        "show",     "unlock",   "lock",     "locked", "active",   "enable",
        "enabled",  "disable",  "disabled", "visible","alive",    "valid",
        "loaded",   "open",     "closed",   "ready",  "complete", "finished",
        "selected", "available","destroyed"
    };
    for (const std::string_view prefix : kPrefixes) {
        if (lowered.starts_with(prefix)) {
            return true;
        }
    }

    static constexpr std::string_view kSuffixes[] = {
        "flag",      "active",   "available", "alive",     "valid",
        "visible",   "enabled",  "disabled",  "loaded",    "locked",
        "complete",  "completed","ready",     "finished",  "destroyed",
        "selected"
    };
    for (const std::string_view suffix : kSuffixes) {
        if (lowered.size() >= suffix.size() &&
            lowered.ends_with(suffix)) {
            return true;
        }
    }

    return false;
}

bool LooksLikeRenderedBooleanExpression(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.empty()) {
        return false;
    }

    std::vector<std::string> parts;
    if (SplitRenderedTopLevelBooleanChain(value, "and", &parts) ||
        SplitRenderedTopLevelBooleanChain(value, "or", &parts)) {
        return !parts.empty() &&
               std::all_of(parts.begin(),
                           parts.end(),
                           [](const std::string& part) {
                               return LooksLikeRenderedBooleanExpression(part);
                           });
    }

    const std::string normalized = StripRenderedWholeParentheses(value, nullptr);
    if (normalized == "true" || normalized == "false") {
        return true;
    }
    if (StartsWithLuaKeyword(normalized, "not")) {
        return true;
    }

    std::size_t compare_position = std::string_view::npos;
    std::string_view compare_op;
    if (FindRenderedTopLevelComparison(normalized, &compare_position, &compare_op)) {
        return true;
    }

    std::string_view candidate = normalized;
    const std::size_t first_paren = candidate.find('(');
    if (first_paren != std::string_view::npos) {
        candidate = TrimLuaWhitespace(candidate.substr(0, first_paren));
    }

    if (!IsRenderedSafePrefixExpression(candidate)) {
        return false;
    }

    std::size_t end = candidate.size();
    while (end > 0) {
        const char ch = candidate[end - 1];
        if (std::isalnum(static_cast<unsigned char>(ch)) != 0 || ch == '_') {
            break;
        }
        --end;
    }
    if (end == 0) {
        return false;
    }

    std::size_t start = end;
    while (start > 0) {
        const char ch = candidate[start - 1];
        if (std::isalnum(static_cast<unsigned char>(ch)) == 0 && ch != '_') {
            break;
        }
        --start;
    }

    return LooksLikeRenderedBooleanName(candidate.substr(start, end - start));
}

std::string InvertRenderedComparison(std::string_view op) {
    if (op == "==") {
        return "~=";
    }
    if (op == "~=") {
        return "==";
    }
    if (op == "<") {
        return ">=";
    }
    if (op == "<=") {
        return ">";
    }
    if (op == ">") {
        return "<=";
    }
    if (op == ">=") {
        return "<";
    }
    return std::string(op);
}

std::string NegateRenderedConditionExpression(std::string_view value, bool* changed) {
    std::string expression = "not ";
    expression += std::string(value);
    return NormalizeRenderedConditionExpression(expression, changed);
}

std::string BuildRenderedSuccessConditionFromFailure(std::string_view failure_condition,
                                                     bool* changed) {
    std::vector<std::string> parts;
    std::string stripped_failure = StripRenderedWholeParentheses(failure_condition, changed);
    if (SplitRenderedTopLevelBooleanChain(stripped_failure, "or", &parts)) {
        std::vector<std::string> success_parts;
        success_parts.reserve(parts.size());
        for (const std::string& part : parts) {
            const std::string normalized_part =
                NormalizeRenderedConditionExpression(part, changed);
            if (!StartsWithLuaKeyword(normalized_part, "not")) {
                success_parts.clear();
                break;
            }

            const std::string positive_part = NormalizeRenderedConditionExpression(
                TrimLuaWhitespace(normalized_part.substr(3)), changed);
            if (positive_part.empty()) {
                success_parts.clear();
                break;
            }
            success_parts.push_back(positive_part);
        }

        if (!success_parts.empty()) {
            std::string success_condition = success_parts.front();
            for (std::size_t i = 1; i < success_parts.size(); ++i) {
                success_condition =
                    RenderLogicalExpression(success_condition, "and", success_parts[i]);
            }
            return success_condition;
        }
    }

    return NegateRenderedConditionExpression(failure_condition, changed);
}

std::string NormalizeRenderedConditionOperand(std::string_view value, bool* changed) {
    std::string normalized = StripRenderedWholeParentheses(value, changed);
    std::size_t compare_position = std::string_view::npos;
    std::string_view compare_op;
    if (FindRenderedTopLevelComparison(normalized, &compare_position, &compare_op)) {
        const std::string left = StripRenderedWholeParentheses(
            normalized.substr(0, compare_position), changed);
        const std::string right = StripRenderedWholeParentheses(
            normalized.substr(compare_position + compare_op.size()), changed);
        return left + " " + std::string(compare_op) + " " + right;
    }
    return normalized;
}

std::string NormalizeRenderedConditionExpression(std::string_view value, bool* changed) {
    std::string normalized = StripRenderedWholeParentheses(value, changed);

    std::vector<std::string> parts;
    if (SplitRenderedTopLevelBooleanChain(normalized, "or", &parts)) {
        std::vector<std::string> normalized_parts;
        for (const std::string& part : parts) {
            std::string normalized_part =
                NormalizeRenderedConditionExpression(part, changed);
            const bool duplicate =
                IsRenderedReusableConditionExpression(normalized_part) &&
                std::find(normalized_parts.begin(), normalized_parts.end(), normalized_part) !=
                    normalized_parts.end();
            if (duplicate) {
                if (changed != nullptr) {
                    *changed = true;
                }
                continue;
            }
            normalized_parts.push_back(std::move(normalized_part));
        }

        if (normalized_parts.size() == 1) {
            return normalized_parts.front();
        }

        std::string rebuilt;
        for (std::size_t i = 0; i < normalized_parts.size(); ++i) {
            if (i != 0) {
                rebuilt += " or ";
            }
            rebuilt += normalized_parts[i];
        }
        return rebuilt;
    }

    if (SplitRenderedTopLevelBooleanChain(normalized, "and", &parts)) {
        std::vector<std::string> normalized_parts;
        for (const std::string& part : parts) {
            std::string normalized_part =
                NormalizeRenderedConditionExpression(part, changed);
            const bool duplicate =
                IsRenderedReusableConditionExpression(normalized_part) &&
                std::find(normalized_parts.begin(), normalized_parts.end(), normalized_part) !=
                    normalized_parts.end();
            if (duplicate) {
                if (changed != nullptr) {
                    *changed = true;
                }
                continue;
            }
            normalized_parts.push_back(std::move(normalized_part));
        }

        if (normalized_parts.size() == 1) {
            return normalized_parts.front();
        }

        std::string rebuilt;
        for (std::size_t i = 0; i < normalized_parts.size(); ++i) {
            if (i != 0) {
                rebuilt += " and ";
            }
            std::string part = normalized_parts[i];
            std::vector<std::string> or_parts;
            if (SplitRenderedTopLevelBooleanChain(part, "or", &or_parts)) {
                rebuilt += "(" + part + ")";
            } else {
                rebuilt += part;
            }
        }
        return rebuilt;
    }

    if (StartsWithLuaKeyword(normalized, "not")) {
        std::string rest =
            NormalizeRenderedConditionExpression(TrimLuaWhitespace(normalized.substr(3)), changed);
        if (StartsWithLuaKeyword(rest, "not")) {
            if (changed != nullptr) {
                *changed = true;
            }
            return NormalizeRenderedConditionExpression(TrimLuaWhitespace(rest.substr(3)),
                                                        changed);
        }

        if (SplitRenderedTopLevelBooleanChain(rest, "and", &parts)) {
            if (changed != nullptr) {
                *changed = true;
            }

            std::string rebuilt;
            for (std::size_t i = 0; i < parts.size(); ++i) {
                if (i != 0) {
                    rebuilt += " or ";
                }
                rebuilt += NegateRenderedConditionExpression(parts[i], changed);
            }
            return NormalizeRenderedConditionExpression(rebuilt, changed);
        }

        if (SplitRenderedTopLevelBooleanChain(rest, "or", &parts)) {
            if (changed != nullptr) {
                *changed = true;
            }

            std::string rebuilt;
            for (std::size_t i = 0; i < parts.size(); ++i) {
                if (i != 0) {
                    rebuilt += " and ";
                }
                rebuilt += NegateRenderedConditionExpression(parts[i], changed);
            }
            return NormalizeRenderedConditionExpression(rebuilt, changed);
        }

        std::vector<std::string> boolean_parts;
        std::size_t compare_position = std::string_view::npos;
        std::string_view compare_op;
        const std::string comparison_candidate =
            StripRenderedWholeParentheses(rest, changed);
        if (!SplitRenderedTopLevelBooleanChain(rest, "and", &boolean_parts) &&
            !SplitRenderedTopLevelBooleanChain(rest, "or", &boolean_parts) &&
            FindRenderedTopLevelComparison(comparison_candidate,
                                           &compare_position,
                                           &compare_op)) {
            if (changed != nullptr) {
                *changed = true;
            }
            const std::string left = NormalizeRenderedConditionOperand(
                comparison_candidate.substr(0, compare_position), changed);
            const std::string right = NormalizeRenderedConditionOperand(
                comparison_candidate.substr(compare_position + compare_op.size()), changed);
            return left + " " + InvertRenderedComparison(compare_op) + " " + right;
        }

        const std::string operand = NormalizeRenderedConditionOperand(rest, changed);
        if (IsRenderedAtomicExpression(operand)) {
            return "not " + operand;
        }
        return "not (" + operand + ")";
    }

    std::size_t compare_position = std::string_view::npos;
    std::string_view compare_op;
    if ((normalized.find("true") != std::string::npos ||
         normalized.find("false") != std::string::npos) &&
        FindRenderedTopLevelComparison(normalized, &compare_position, &compare_op) &&
        (compare_op == "==" || compare_op == "~=")) {
        const std::string left = NormalizeRenderedConditionOperand(
            normalized.substr(0, compare_position), nullptr);
        const std::string right = NormalizeRenderedConditionOperand(
            normalized.substr(compare_position + compare_op.size()), nullptr);

        const auto try_simplify_literal_compare =
            [&](const std::string& expression,
                const std::string& literal) -> std::optional<std::string> {
            if ((literal != "true" && literal != "false") ||
                !LooksLikeRenderedBooleanExpression(expression)) {
                return std::nullopt;
            }

            if (changed != nullptr) {
                *changed = true;
            }

            const bool literal_true = literal == "true";
            const bool want_positive =
                (compare_op == "==" && literal_true) ||
                (compare_op == "~=" && !literal_true);
            if (want_positive) {
                return NormalizeRenderedConditionExpression(expression, changed);
            }
            return NegateRenderedConditionExpression(expression, changed);
        };

        if (const std::optional<std::string> simplified =
                try_simplify_literal_compare(left, right)) {
            return *simplified;
        }
        if (const std::optional<std::string> simplified =
                try_simplify_literal_compare(right, left)) {
            return *simplified;
        }
    }

    return NormalizeRenderedConditionOperand(normalized, changed);
}

bool IsRenderedSafePrefixExpression(std::string_view candidate) {
    candidate = TrimLuaWhitespace(candidate);
    const auto is_identifier_start = [](char ch) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        return std::isalpha(uch) != 0 || ch == '_';
    };
    const auto is_identifier_char = [&](char ch) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        return std::isalnum(uch) != 0 || ch == '_';
    };

    if (candidate.empty() || IsLuaKeyword(candidate) ||
        !is_identifier_start(candidate.front())) {
        return false;
    }

    auto skip_identifier = [&](std::size_t index) {
        while (index < candidate.size() && is_identifier_char(candidate[index])) {
            ++index;
        }
        return index;
    };

    auto skip_balanced = [&](std::size_t index, char open, char close) {
        if (index >= candidate.size() || candidate[index] != open) {
            return std::string_view::npos;
        }

        int depth = 0;
        bool in_single_quote = false;
        bool in_double_quote = false;
        for (; index < candidate.size(); ++index) {
            const char ch = candidate[index];
            if (in_single_quote) {
                if (ch == '\\' && index + 1 < candidate.size()) {
                    ++index;
                } else if (ch == '\'') {
                    in_single_quote = false;
                }
                continue;
            }
            if (in_double_quote) {
                if (ch == '\\' && index + 1 < candidate.size()) {
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
            if (ch == open) {
                ++depth;
            } else if (ch == close) {
                --depth;
                if (depth == 0) {
                    return index + 1;
                }
            }
        }

        return std::string_view::npos;
    };

    std::size_t index = skip_identifier(0);
    if (!IsValidLuaIdentifier(candidate.substr(0, index))) {
        return false;
    }
    while (index < candidate.size()) {
        const char ch = candidate[index];
        if (ch == '.') {
            ++index;
            if (index >= candidate.size() || !is_identifier_start(candidate[index])) {
                return false;
            }
            const std::size_t member_begin = index;
            index = skip_identifier(index);
            if (!IsValidLuaIdentifier(candidate.substr(member_begin, index - member_begin))) {
                return false;
            }
            continue;
        }
        if (ch == ':') {
            ++index;
            if (index >= candidate.size() || !is_identifier_start(candidate[index])) {
                return false;
            }
            const std::size_t member_begin = index;
            index = skip_identifier(index);
            if (!IsValidLuaIdentifier(candidate.substr(member_begin, index - member_begin))) {
                return false;
            }
            continue;
        }
        if (ch == '[') {
            index = skip_balanced(index, '[', ']');
            if (index == std::string_view::npos) {
                return false;
            }
            continue;
        }
        if (ch == '(') {
            index = skip_balanced(index, '(', ')');
            if (index == std::string_view::npos) {
                return false;
            }
            continue;
        }
        if (std::isspace(static_cast<unsigned char>(ch)) != 0) {
            ++index;
            continue;
        }
        return false;
    }

    return true;
}

bool IsRenderedSimpleMemberPath(std::string_view candidate) {
    candidate = TrimLuaWhitespace(candidate);
    if (!IsRenderedSafePrefixExpression(candidate) ||
        candidate.find('[') != std::string_view::npos ||
        candidate.find('(') != std::string_view::npos ||
        candidate.find(':') != std::string_view::npos) {
        return false;
    }

    const std::size_t dot = candidate.find('.');
    if (dot == std::string_view::npos) {
        return false;
    }

    std::size_t cursor = 0;
    while (cursor < candidate.size()) {
        const std::size_t next_dot = candidate.find('.', cursor);
        const std::string_view part =
            next_dot == std::string_view::npos
                ? candidate.substr(cursor)
                : candidate.substr(cursor, next_dot - cursor);
        if (!IsValidLuaIdentifier(part)) {
            return false;
        }
        if (next_dot == std::string_view::npos) {
            break;
        }
        cursor = next_dot + 1;
    }

    return true;
}

bool IsRenderedAtomicExpression(std::string_view candidate) {
    candidate = TrimLuaWhitespace(candidate);
    if (candidate.empty()) {
        return false;
    }
    return candidate == "nil" || candidate == "true" || candidate == "false" ||
           LooksLikeRenderedNumericLiteral(candidate) || candidate.starts_with('"') ||
           candidate.starts_with('\'') || IsValidLuaIdentifier(candidate) ||
           IsRenderedSafePrefixExpression(candidate);
}

bool FindRenderedMatchingParen(std::string_view text,
                               std::size_t open_index,
                               std::size_t* close_index) {
    if (close_index == nullptr || open_index >= text.size() || text[open_index] != '(') {
        return false;
    }

    int depth = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t i = open_index; i < text.size(); ++i) {
        const char ch = text[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
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
            ++depth;
        } else if (ch == ')') {
            --depth;
            if (depth == 0) {
                *close_index = i;
                return true;
            }
        }
    }

    return false;
}

bool TryParseRenderedConditionalTableField(const std::vector<std::string>& lines,
                                           std::size_t line_index,
                                           std::string_view table_name,
                                           std::string* condition,
                                           std::string* field_key,
                                           std::string* then_expression,
                                           std::string* else_expression,
                                           int* array_index,
                                           std::size_t* consumed_lines) {
    if (line_index + 4 >= lines.size()) {
        return false;
    }

    const std::string_view trimmed_header =
        TrimLuaWhitespace(StripLuaLineComment(lines[line_index]));
    if (!StartsWithLuaKeyword(trimmed_header, "if") || trimmed_header.size() < 8 ||
        !trimmed_header.ends_with("then")) {
        return false;
    }

    const std::string_view condition_view = TrimLuaWhitespace(
        trimmed_header.substr(2, trimmed_header.size() - 2 - 4));
    if (condition_view.empty()) {
        return false;
    }

    std::string parsed_then_field_key;
    std::string parsed_then_expression;
    bool parsed_then_is_function = false;
    int parsed_then_array_index = -1;
    std::size_t then_consumed_lines = 1;
    if (!ParseRenderedTableFieldAssignment(lines[line_index + 1],
                                           table_name,
                                           &parsed_then_field_key,
                                           &parsed_then_expression,
                                           &parsed_then_is_function,
                                           &parsed_then_array_index)) {
        if (!TryParseRenderedTemporaryBackedTableField(lines,
                                                       line_index + 1,
                                                       table_name,
                                                       &parsed_then_field_key,
                                                       &parsed_then_expression,
                                                       &parsed_then_is_function,
                                                       &parsed_then_array_index,
                                                       &then_consumed_lines)) {
            return false;
        }
    }

    const std::size_t else_line_index = line_index + 1 + then_consumed_lines;
    if (else_line_index + 2 >= lines.size() ||
        TrimLuaWhitespace(StripLuaLineComment(lines[else_line_index])) != "else") {
        return false;
    }

    std::string parsed_else_field_key;
    std::string parsed_else_expression;
    bool parsed_else_is_function = false;
    int parsed_else_array_index = -1;
    std::size_t else_consumed_lines = 1;
    if (!ParseRenderedTableFieldAssignment(lines[else_line_index + 1],
                                           table_name,
                                           &parsed_else_field_key,
                                           &parsed_else_expression,
                                           &parsed_else_is_function,
                                           &parsed_else_array_index)) {
        if (!TryParseRenderedTemporaryBackedTableField(lines,
                                                       else_line_index + 1,
                                                       table_name,
                                                       &parsed_else_field_key,
                                                       &parsed_else_expression,
                                                       &parsed_else_is_function,
                                                       &parsed_else_array_index,
                                                       &else_consumed_lines)) {
            return false;
        }
    }

    const std::size_t end_line_index = else_line_index + 1 + else_consumed_lines;
    if (TrimLuaWhitespace(StripLuaLineComment(lines[end_line_index])) != "end") {
        return false;
    }

    if (parsed_then_field_key != parsed_else_field_key ||
        parsed_then_array_index != parsed_else_array_index ||
        parsed_then_is_function || parsed_else_is_function ||
        parsed_then_expression == "{" || parsed_else_expression == "{") {
        return false;
    }

    int then_paren_balance = 0;
    int then_brace_balance = 0;
    int else_paren_balance = 0;
    int else_brace_balance = 0;
    if (HasPositiveLuaExpressionBalance(parsed_then_expression,
                                        &then_paren_balance,
                                        &then_brace_balance) ||
        HasPositiveLuaExpressionBalance(parsed_else_expression,
                                        &else_paren_balance,
                                        &else_brace_balance)) {
        return false;
    }

    if (condition != nullptr) {
        *condition = std::string(condition_view);
    }
    if (field_key != nullptr) {
        *field_key = std::move(parsed_then_field_key);
    }
    if (then_expression != nullptr) {
        *then_expression = std::move(parsed_then_expression);
    }
    if (else_expression != nullptr) {
        *else_expression = std::move(parsed_else_expression);
    }
    if (array_index != nullptr) {
        *array_index = parsed_then_array_index;
    }
    if (consumed_lines != nullptr) {
        *consumed_lines = 1 + then_consumed_lines + 1 + else_consumed_lines + 1;
    }
    return true;
}

bool TryParseRenderedTemporaryBackedTableField(const std::vector<std::string>& lines,
                                               std::size_t line_index,
                                               std::string_view table_name,
                                               std::string* field_key,
                                               std::string* rhs_expression,
                                               bool* rhs_is_function,
                                               int* array_index,
                                               std::size_t* consumed_lines) {
    if (line_index + 1 >= lines.size()) {
        return false;
    }

    std::string temporary_name;
    std::string temporary_expression;
    if (!IsTemporaryAssignmentLine(lines[line_index], &temporary_name, &temporary_expression)) {
        return false;
    }

    std::string parsed_field_key;
    std::string parsed_rhs_expression;
    bool parsed_rhs_is_function = false;
    int parsed_array_index = -1;
    if (!ParseRenderedTableFieldAssignment(lines[line_index + 1],
                                           table_name,
                                           &parsed_field_key,
                                           &parsed_rhs_expression,
                                           &parsed_rhs_is_function,
                                           &parsed_array_index)) {
        return false;
    }

    if (TrimLuaWhitespace(parsed_rhs_expression) != temporary_name) {
        return false;
    }

    if (field_key != nullptr) {
        *field_key = std::move(parsed_field_key);
    }
    if (rhs_expression != nullptr) {
        *rhs_expression = std::move(temporary_expression);
    }
    if (rhs_is_function != nullptr) {
        const std::string_view trimmed_rhs = TrimLuaWhitespace(*rhs_expression);
        *rhs_is_function = trimmed_rhs.size() >= 9 &&
                           trimmed_rhs.substr(0, 9) == std::string_view("function(");
    }
    if (array_index != nullptr) {
        *array_index = parsed_array_index;
    }
    if (consumed_lines != nullptr) {
        *consumed_lines = 2;
    }
    return true;
}

}  // namespace

const RenderedLuaCleanupProfileTelemetry& GetLastRenderedLuaCleanupProfileTelemetry() {
    return g_last_cleanup_profile_telemetry;
}

void ResetRenderedLuaCleanupProfileTelemetry() {
    g_last_cleanup_profile_telemetry = RenderedLuaCleanupProfileTelemetry{};
}

namespace {

void BeginRenderedLuaCleanupProfile(std::string_view profile_name) {
    ResetRenderedLuaCleanupProfileTelemetry();
    g_last_cleanup_profile_telemetry.profile_name = std::string(profile_name);
}

template <typename StepFn>
void ApplyRenderedLuaCleanupProfileStep(std::vector<std::string>* lines,
                                        const char* step_name,
                                        StepFn&& step,
                                        bool* changed_out,
                                        const char* trace_prefix) {
    const bool trace_cleanup = std::getenv("REFII_LUA_TRACE_CLEANUP") != nullptr;
    const char* skip_cleanup = std::getenv("REFII_LUA_SKIP_CLEANUP");
    if (skip_cleanup != nullptr &&
        std::string_view(skip_cleanup).find(step_name) != std::string_view::npos) {
        if (trace_cleanup) {
            std::fprintf(stderr, "%s_skipped: %s\n", trace_prefix, step_name);
        }
        return;
    }

    RenderedLuaCleanupStepRuntime step_runtime{
        .step_name = step_name,
    };
    RenderedLuaCleanupStepRuntime* const previous_runtime = g_active_cleanup_step_runtime;
    g_active_cleanup_step_runtime = &step_runtime;

    const auto started_at = std::chrono::steady_clock::now();
    const bool step_changed = step(lines);
    const auto elapsed =
        std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now() - started_at)
            .count();

    g_active_cleanup_step_runtime = previous_runtime;

    ++g_last_cleanup_profile_telemetry.step_count;
    if (step_changed) {
        ++g_last_cleanup_profile_telemetry.changed_steps;
        if (changed_out != nullptr) {
            *changed_out = true;
        }
    }
    g_last_cleanup_profile_telemetry.total_microseconds +=
        static_cast<std::uint64_t>(std::max<std::int64_t>(elapsed, 0));
    if (static_cast<std::uint64_t>(std::max<std::int64_t>(elapsed, 0)) >
        g_last_cleanup_profile_telemetry.hottest_step_microseconds) {
        g_last_cleanup_profile_telemetry.hottest_step_microseconds =
            static_cast<std::uint64_t>(std::max<std::int64_t>(elapsed, 0));
        g_last_cleanup_profile_telemetry.hottest_step = step_name;
    }
    if (step_runtime.cap_hit) {
        AppendRenderedLuaCleanupCappedStep(step_name);
    }

    if (trace_cleanup && (step_changed || step_runtime.cap_hit)) {
        std::fprintf(stderr,
                     "%s_%s: %s passes=%d us=%lld\n",
                     trace_prefix,
                     step_changed ? "changed" : "profiled",
                     step_name,
                     step_runtime.pass_iterations,
                     static_cast<long long>(elapsed));
    }
}

}  // namespace

bool IsLuaKeyword(std::string_view value) {
    static constexpr std::array<std::string_view, 21> kKeywords = {
        "and",   "break", "do",    "else",   "elseif", "end",   "false",
        "for",   "function", "if", "in",     "local",  "nil",   "not",
        "or",    "repeat", "return", "then", "true",   "until", "while",
    };
    return std::find(kKeywords.begin(), kKeywords.end(), value) != kKeywords.end();
}

bool IsValidLuaIdentifier(std::string_view value) {
    if (value.empty() || IsLuaKeyword(value)) {
        return false;
    }
    const unsigned char first = static_cast<unsigned char>(value.front());
    if (std::isalpha(first) == 0 && value.front() != '_') {
        return false;
    }
    for (std::size_t i = 1; i < value.size(); ++i) {
        const unsigned char ch = static_cast<unsigned char>(value[i]);
        if (std::isalnum(ch) == 0 && value[i] != '_') {
            return false;
        }
    }
    return true;
}

std::string EscapeLuaString(std::string_view value) {
    std::string escaped;
    escaped.reserve(value.size() + 2);
    escaped.push_back('"');
    for (char ch : value) {
        switch (ch) {
            case '\\':
                escaped += "\\\\";
                break;
            case '"':
                escaped += "\\\"";
                break;
            case '\n':
                escaped += "\\n";
                break;
            case '\r':
                escaped += "\\r";
                break;
            case '\t':
                escaped += "\\t";
                break;
            default:
                escaped.push_back(ch);
                break;
        }
    }
    escaped.push_back('"');
    return escaped;
}

std::string RenderLuaNumber(double value) {
    std::ostringstream stream;
    stream << std::setprecision(17) << value;
    return stream.str();
}

std::string RenderLuaConstant(const Lua51Constant& constant) {
    switch (constant.type) {
        case Lua51ConstantType::kNil:
            return "nil";
        case Lua51ConstantType::kBoolean:
            return constant.boolean_value ? "true" : "false";
        case Lua51ConstantType::kNumber:
            return RenderLuaNumber(constant.number_value);
        case Lua51ConstantType::kString:
            return EscapeLuaString(constant.string_value);
    }
    return "nil";
}

std::string RenderLuaGlobal(std::string_view name) {
    if (IsValidLuaIdentifier(name)) {
        return std::string(name);
    }
    return "_G[" + EscapeLuaString(name) + "]";
}

std::string RenderLuaUpvalue(const Lua51Function& function, int index) {
    if (index >= 0 && static_cast<std::size_t>(index) < function.upvalue_names.size() &&
        IsValidLuaIdentifier(function.upvalue_names[static_cast<std::size_t>(index)])) {
        return function.upvalue_names[static_cast<std::size_t>(index)];
    }
    return "upvalue_" + std::to_string(index);
}

std::vector<std::string> SplitRenderedLines(std::string_view text) {
    std::vector<std::string> lines;
    std::size_t start = 0;
    while (start < text.size()) {
        const std::size_t end = text.find('\n', start);
        if (end == std::string_view::npos) {
            lines.emplace_back(text.substr(start));
            break;
        }
        lines.emplace_back(text.substr(start, end - start));
        start = end + 1;
    }
    if (text.empty()) {
        lines.emplace_back();
    }
    return lines;
}

std::string_view TrimLuaWhitespace(std::string_view value) {
    while (!value.empty() &&
           std::isspace(static_cast<unsigned char>(value.front())) != 0) {
        value.remove_prefix(1);
    }
    while (!value.empty() &&
           std::isspace(static_cast<unsigned char>(value.back())) != 0) {
        value.remove_suffix(1);
    }
    return value;
}

bool IsRenderTemporaryName(std::string_view value) {
    if (value.size() >= 2 && value.front() == 'r') {
        return std::all_of(value.begin() + 1, value.end(), [](char ch) {
            return std::isdigit(static_cast<unsigned char>(ch)) != 0;
        });
    }

    const auto matches_segmented_digits =
        [](std::string_view name, std::size_t start) {
            bool saw_digit = false;
            bool saw_separator = false;
            for (std::size_t i = start; i < name.size(); ++i) {
                const char ch = name[i];
                if (ch == '_') {
                    saw_separator = true;
                    continue;
                }
                if (std::isdigit(static_cast<unsigned char>(ch)) == 0) {
                    return false;
                }
                saw_digit = true;
            }
            return saw_digit && saw_separator;
        };

    if (value.size() >= 4 && (value.front() == 'L' || value.front() == 'A')) {
        return matches_segmented_digits(value, 1);
    }

    if (value.size() >= 4 && value[0] == 'l' && value[1] == '_') {
        return matches_segmented_digits(value, 2);
    }

    return false;
}

std::size_t FindIdentifierUse(std::string_view text,
                              std::string_view identifier,
                              std::size_t offset) {
    if (identifier.empty() || offset >= text.size()) {
        return std::string_view::npos;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t i = 0; i < text.size();) {
        const char ch = text[i];

        if (!in_single_quote && !in_double_quote && ch == '-' && i + 1 < text.size() &&
            text[i + 1] == '-') {
            break;
        }

        if (in_single_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
                i += 2;
                continue;
            }
            if (ch == '\'') {
                in_single_quote = false;
            }
            ++i;
            continue;
        }

        if (in_double_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
                i += 2;
                continue;
            }
            if (ch == '"') {
                in_double_quote = false;
            }
            ++i;
            continue;
        }

        if (ch == '\'') {
            in_single_quote = true;
            ++i;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            ++i;
            continue;
        }

        if (i < offset || i + identifier.size() > text.size() ||
            text.substr(i, identifier.size()) != identifier) {
            ++i;
            continue;
        }

        const bool boundary_before =
            i == 0 ||
            ((!std::isalnum(static_cast<unsigned char>(text[i - 1])) && text[i - 1] != '_') &&
             text[i - 1] != '.' && text[i - 1] != ':');
        const std::size_t end = i + identifier.size();
        const bool boundary_after =
            end >= text.size() ||
            (!std::isalnum(static_cast<unsigned char>(text[end])) && text[end] != '_');
        if (boundary_before && boundary_after) {
            return i;
        }

        i = end;
    }

    return std::string_view::npos;
}

std::size_t CountIdentifierUses(std::string_view text, std::string_view identifier) {
    std::size_t count = 0;
    std::size_t offset = 0;
    while (true) {
        const std::size_t found = FindIdentifierUse(text, identifier, offset);
        if (found == std::string_view::npos) {
            return count;
        }
        ++count;
        offset = found + identifier.size();
    }
}

bool IsTemporaryAssignmentLine(std::string_view line,
                               std::string* name,
                               std::string* expression) {
    std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.empty()) {
        return false;
    }

    if (trimmed.substr(0, 6) == "local ") {
        trimmed.remove_prefix(6);
        trimmed = TrimLuaWhitespace(trimmed);
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (!IsRenderTemporaryName(lhs) || rhs.empty()) {
        return false;
    }

    if (name != nullptr) {
        *name = std::string(lhs);
    }
    if (expression != nullptr) {
        *expression = std::string(rhs);
    }
    return true;
}

bool IsLocalTemporaryAssignmentLine(std::string_view line,
                                    std::string* name,
                                    std::string* expression) {
    std::string_view trimmed = TrimLuaWhitespace(line);
    if (!trimmed.starts_with("local ")) {
        return false;
    }
    return IsTemporaryAssignmentLine(trimmed, name, expression);
}

bool IsAssignmentToTemporary(std::string_view line, std::string_view identifier) {
    std::string name;
    return IsTemporaryAssignmentLine(line, &name, nullptr) && name == identifier;
}

bool LocalizeRenderedLuaFirstAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    std::unordered_set<std::string> seen_temporaries;
    for (std::string& line : *lines) {
        std::vector<std::string> local_names;
        if (ParseRenderedLocalDeclarationLine(line, &local_names)) {
            for (const std::string& local_name : local_names) {
                if (IsRenderTemporaryName(local_name)) {
                    seen_temporaries.insert(local_name);
                }
            }
            continue;
        }

        std::string temporary_name;
        if (IsLocalTemporaryAssignmentLine(line, &temporary_name, nullptr)) {
            seen_temporaries.insert(std::move(temporary_name));
            continue;
        }

        if (!IsTemporaryAssignmentLine(line, &temporary_name, nullptr)) {
            continue;
        }

        if (!seen_temporaries.insert(temporary_name).second) {
            continue;
        }

        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string::npos) {
            continue;
        }

        line.insert(first_non_space, "local ");
        changed = true;
    }

    return changed;
}

bool FindIdentifierUseInAssignmentRhs(std::string_view line,
                                      std::string_view identifier,
                                      std::size_t* absolute_found,
                                      std::size_t* use_count) {
    const std::size_t equal = line.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view rhs = line.substr(equal + 1);
    const std::size_t rhs_use_count = CountIdentifierUses(rhs, identifier);
    if (rhs_use_count == 0) {
        return false;
    }

    const std::size_t rhs_found = FindIdentifierUse(rhs, identifier);
    if (rhs_found == std::string_view::npos) {
        return false;
    }

    if (absolute_found != nullptr) {
        *absolute_found = equal + 1 + rhs_found;
    }
    if (use_count != nullptr) {
        *use_count = rhs_use_count;
    }
    return true;
}

bool ReplaceRenderedIdentifierUses(std::string_view text,
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

bool ReplaceRenderedSafePrefixExpressionUses(std::string_view text,
                                             std::string_view expression,
                                             std::string_view replacement,
                                             std::string* rewritten) {
    if (rewritten == nullptr || expression.empty() ||
        !IsRenderedSafePrefixExpression(expression)) {
        return false;
    }

    std::string result;
    result.reserve(text.size() + replacement.size());
    std::size_t offset = 0;
    bool changed = false;
    bool in_single_quote = false;
    bool in_double_quote = false;

    for (std::size_t i = 0; i < text.size();) {
        const char ch = text[i];

        if (!in_single_quote && !in_double_quote && ch == '-' && i + 1 < text.size() &&
            text[i + 1] == '-') {
            break;
        }

        if (in_single_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
                i += 2;
                continue;
            }
            if (ch == '\'') {
                in_single_quote = false;
            }
            ++i;
            continue;
        }

        if (in_double_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
                i += 2;
                continue;
            }
            if (ch == '"') {
                in_double_quote = false;
            }
            ++i;
            continue;
        }

        if (ch == '\'') {
            in_single_quote = true;
            ++i;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            ++i;
            continue;
        }

        if (i + expression.size() > text.size() ||
            text.substr(i, expression.size()) != expression) {
            ++i;
            continue;
        }

        const bool boundary_before =
            i == 0 ||
            ((!std::isalnum(static_cast<unsigned char>(text[i - 1])) && text[i - 1] != '_') &&
             text[i - 1] != '.' && text[i - 1] != ':' && text[i - 1] != ']' &&
             text[i - 1] != ')');
        const std::size_t end = i + expression.size();
        const bool boundary_after =
            end >= text.size() ||
            ((!std::isalnum(static_cast<unsigned char>(text[end])) && text[end] != '_') &&
             text[end] != '.' && text[end] != ':' && text[end] != '[' &&
             text[end] != '(');
        if (!boundary_before || !boundary_after) {
            ++i;
            continue;
        }

        result.append(text.substr(offset, i - offset));
        result.append(replacement);
        offset = end;
        i = end;
        changed = true;
    }

    if (!changed) {
        return false;
    }

    result.append(text.substr(offset));
    *rewritten = std::move(result);
    return true;
}

bool ReplaceRenderedSafePrefixExpressionChainUses(std::string_view text,
                                                  std::string_view expression,
                                                  std::string_view replacement,
                                                  std::string* rewritten) {
    if (rewritten == nullptr || expression.empty() ||
        !IsRenderedSafePrefixExpression(expression)) {
        return false;
    }

    std::string result;
    result.reserve(text.size() + replacement.size());
    std::size_t offset = 0;
    bool changed = false;
    bool in_single_quote = false;
    bool in_double_quote = false;

    for (std::size_t i = 0; i < text.size();) {
        const char ch = text[i];

        if (!in_single_quote && !in_double_quote && ch == '-' && i + 1 < text.size() &&
            text[i + 1] == '-') {
            break;
        }

        if (in_single_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
                i += 2;
                continue;
            }
            if (ch == '\'') {
                in_single_quote = false;
            }
            ++i;
            continue;
        }

        if (in_double_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
                i += 2;
                continue;
            }
            if (ch == '"') {
                in_double_quote = false;
            }
            ++i;
            continue;
        }

        if (ch == '\'') {
            in_single_quote = true;
            ++i;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            ++i;
            continue;
        }

        if (i + expression.size() > text.size() ||
            text.substr(i, expression.size()) != expression) {
            ++i;
            continue;
        }

        const bool boundary_before =
            i == 0 ||
            ((!std::isalnum(static_cast<unsigned char>(text[i - 1])) && text[i - 1] != '_') &&
             text[i - 1] != '.' && text[i - 1] != ':' && text[i - 1] != ']' &&
             text[i - 1] != ')');
        const std::size_t end = i + expression.size();
        const bool boundary_after =
            end >= text.size() ||
            (!std::isalnum(static_cast<unsigned char>(text[end])) && text[end] != '_');
        if (!boundary_before || !boundary_after) {
            ++i;
            continue;
        }

        result.append(text.substr(offset, i - offset));
        result.append(replacement);
        offset = end;
        i = end;
        changed = true;
    }

    if (!changed) {
        return false;
    }

    result.append(text.substr(offset));
    *rewritten = std::move(result);
    return true;
}

bool ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine(
    std::string_view line,
    std::string_view expression,
    std::string_view replacement,
    std::string* rewritten) {
    if (rewritten == nullptr) {
        return false;
    }

    const std::string_view stripped = StripLuaLineComment(line);
    const std::string_view comment =
        stripped.size() < line.size() ? line.substr(stripped.size()) : std::string_view();
    const std::string_view trimmed = TrimLuaWhitespace(stripped);

    bool assign_is_local = false;
    std::string assign_lhs;
    std::string assign_rhs;
    if (ParseRenderedSingleAssignmentLine(stripped, &assign_is_local, &assign_lhs, &assign_rhs)) {
        const std::string normalized_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_lhs), nullptr);
        if (normalized_lhs == TrimLuaWhitespace(expression)) {
            std::string rewritten_rhs;
            if (!ReplaceRenderedSafePrefixExpressionChainUses(assign_rhs,
                                                              expression,
                                                              replacement,
                                                              &rewritten_rhs) ||
                rewritten_rhs == assign_rhs) {
                return false;
            }

            const std::size_t equal = stripped.find('=');
            if (equal == std::string_view::npos) {
                return false;
            }

            std::string rebuilt(stripped.substr(0, equal + 1));
            if (!rewritten_rhs.empty() && !rebuilt.empty() &&
                !std::isspace(static_cast<unsigned char>(rebuilt.back()))) {
                rebuilt.push_back(' ');
            }
            rebuilt += rewritten_rhs;
            rebuilt += std::string(comment);
            *rewritten = std::move(rebuilt);
            return true;
        }
    }

    if (trimmed.starts_with("for ") && trimmed.ends_with(" do")) {
        const std::size_t in_pos = stripped.find(" in ");
        const std::size_t do_pos = stripped.rfind(" do");
        if (in_pos != std::string_view::npos && do_pos != std::string_view::npos &&
            in_pos > 4 && in_pos < do_pos) {
            std::string rewritten_iterator;
            const std::string_view iterator_expression =
                stripped.substr(in_pos + 4, do_pos - (in_pos + 4));
            if (ReplaceRenderedSafePrefixExpressionChainUses(iterator_expression,
                                                             expression,
                                                             replacement,
                                                             &rewritten_iterator) &&
                rewritten_iterator != iterator_expression) {
                *rewritten = std::string(stripped.substr(0, in_pos + 4)) +
                             rewritten_iterator + std::string(stripped.substr(do_pos)) +
                             std::string(comment);
                return true;
            }
        }

        const std::size_t equal_pos = stripped.find('=');
        const std::size_t do_pos_numeric = stripped.rfind(" do");
        if (equal_pos != std::string_view::npos &&
            do_pos_numeric != std::string_view::npos &&
            equal_pos > 4 && equal_pos < do_pos_numeric) {
            std::string rewritten_range;
            const std::string_view range_expression =
                stripped.substr(equal_pos + 1, do_pos_numeric - (equal_pos + 1));
            if (ReplaceRenderedSafePrefixExpressionChainUses(range_expression,
                                                             expression,
                                                             replacement,
                                                             &rewritten_range) &&
                rewritten_range != range_expression) {
                *rewritten = std::string(stripped.substr(0, equal_pos + 1)) +
                             rewritten_range + std::string(stripped.substr(do_pos_numeric)) +
                             std::string(comment);
                return true;
            }
        }
    }

    std::string rewritten_body;
    if (!ReplaceRenderedSafePrefixExpressionChainUses(stripped,
                                                      expression,
                                                      replacement,
                                                      &rewritten_body) ||
        rewritten_body == stripped) {
        return false;
    }

    *rewritten = rewritten_body + std::string(comment);
    return true;
}

bool RewriteRenderedAliasTargetUsesInStatementLine(std::string_view line,
                                                   std::string_view alias_name,
                                                   std::string_view replacement,
                                                   std::string* rewritten) {
    if (rewritten == nullptr) {
        return false;
    }

    const std::string_view stripped = StripLuaLineComment(line);
    const std::string_view comment =
        stripped.size() < line.size() ? line.substr(stripped.size()) : std::string_view();

    bool assign_is_local = false;
    std::string assign_lhs;
    std::string assign_rhs;
    if (ParseRenderedSingleAssignmentLine(stripped, &assign_is_local, &assign_lhs, &assign_rhs)) {
        const std::string normalized_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_lhs), nullptr);
        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);

        std::string rewritten_lhs = normalized_lhs;
        std::string rewritten_rhs = normalized_rhs;
        bool changed = false;

        if (normalized_lhs == TrimLuaWhitespace(alias_name)) {
            if (ReplaceRenderedSafePrefixExpressionChainUses(assign_rhs,
                                                             alias_name,
                                                             replacement,
                                                             &rewritten_rhs) &&
                rewritten_rhs != assign_rhs) {
                changed = true;
            }
        } else if (normalized_lhs.size() > alias_name.size() &&
                   normalized_lhs.substr(0, alias_name.size()) == alias_name) {
            const char next = normalized_lhs[alias_name.size()];
            if (next == '.' || next == ':' || next == '[') {
                rewritten_lhs = std::string(replacement) +
                                normalized_lhs.substr(alias_name.size());
                changed = true;
            }
        }

        std::string rhs_alias_rewrite;
        if (ReplaceRenderedSafePrefixExpressionChainUses(assign_rhs,
                                                         alias_name,
                                                         replacement,
                                                         &rhs_alias_rewrite) &&
            rhs_alias_rewrite != assign_rhs) {
            rewritten_rhs = std::move(rhs_alias_rewrite);
            changed = true;
        }

        if (changed) {
            const std::string indent(GetRenderedLuaIndent(line));
            std::string rebuilt = indent;
            if (assign_is_local) {
                rebuilt += "local ";
            }
            rebuilt += rewritten_lhs;
            rebuilt += " = ";
            rebuilt += rewritten_rhs;
            rebuilt += std::string(comment);
            *rewritten = std::move(rebuilt);
            return true;
        }
    }

    return ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine(
        line, alias_name, replacement, rewritten);
}

bool ComposeRenderedTemporaryExpressionChain(const std::vector<std::string>& lines,
                                            std::size_t start_line,
                                            std::string_view temporary_name,
                                            std::string initial_expression,
                                            std::size_t* next_line,
                                            std::string* composed_expression) {
    std::string current_expression = std::move(initial_expression);
    std::size_t cursor = start_line;
    while (cursor < lines.size()) {
        std::string assignment_name;
        std::string assignment_expression;
        if (!IsTemporaryAssignmentLine(lines[cursor],
                                       &assignment_name,
                                       &assignment_expression) ||
            assignment_name != temporary_name) {
            break;
        }

        if (CountIdentifierUses(assignment_expression, temporary_name) != 1) {
            break;
        }

        std::string rewritten_expression;
        if (!ReplaceRenderedIdentifierUses(assignment_expression,
                                           temporary_name,
                                           WrapInlineExpression(current_expression),
                                           &rewritten_expression)) {
            break;
        }

        current_expression = std::move(rewritten_expression);
        ++cursor;
    }

    if (next_line != nullptr) {
        *next_line = cursor;
    }
    if (composed_expression != nullptr) {
        *composed_expression = std::move(current_expression);
    }
    return true;
}

bool TryCanonicalizeRenderedExplicitSelfMethodCall(
    std::string_view callee,
    const std::vector<std::string>& arguments,
    std::string* rewritten_out) {
    if (rewritten_out == nullptr || arguments.empty()) {
        return false;
    }

    const auto normalize_expression = [](std::string_view expression) {
        return StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
    };
    const auto normalize_identity_expression = [&](std::string_view expression) {
        const std::string normalized = normalize_expression(expression);
        if (normalized.empty() || !IsRenderedSafePrefixExpression(normalized)) {
            return std::string();
        }

        std::string canonical;
        canonical.reserve(normalized.size());
        for (std::size_t index = 0; index < normalized.size();) {
            if (normalized[index] != '[') {
                canonical.push_back(normalized[index]);
                ++index;
                continue;
            }

            std::size_t probe = index + 1;
            int bracket_depth = 1;
            bool in_single_quote = false;
            bool in_double_quote = false;
            bool escaped = false;
            for (; probe < normalized.size(); ++probe) {
                const char ch = normalized[probe];
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
                if (ch == '[') {
                    ++bracket_depth;
                    continue;
                }
                if (ch == ']') {
                    --bracket_depth;
                    if (bracket_depth == 0) {
                        break;
                    }
                }
            }
            if (probe >= normalized.size() || bracket_depth != 0) {
                canonical.push_back(normalized[index]);
                ++index;
                continue;
            }

            const std::string_view inner = TrimLuaWhitespace(
                std::string_view(normalized).substr(index + 1, probe - index - 1));
            bool folded = false;
            if (inner.size() >= 2 &&
                ((inner.front() == '"' && inner.back() == '"') ||
                 (inner.front() == '\'' && inner.back() == '\''))) {
                const std::string_view key = inner.substr(1, inner.size() - 2);
                if (IsValidLuaIdentifier(key)) {
                    canonical.push_back('.');
                    canonical.append(key);
                    folded = true;
                }
            }

            if (!folded) {
                canonical.append(normalized.substr(index, probe - index + 1));
            }
            index = probe + 1;
        }

        return canonical;
    };
    const auto split_method_callee = [](std::string_view value,
                                        std::string_view* receiver_out,
                                        std::string_view* method_out) -> bool {
        if (receiver_out == nullptr || method_out == nullptr) {
            return false;
        }

        value = TrimLuaWhitespace(value);
        if (value.empty()) {
            return false;
        }

        const auto assign_if_valid = [&](std::size_t split) -> bool {
            if (split == std::string_view::npos || split == 0 || split + 1 >= value.size()) {
                return false;
            }
            const std::string_view receiver = TrimLuaWhitespace(value.substr(0, split));
            const std::string_view method = TrimLuaWhitespace(value.substr(split + 1));
            if (receiver.empty() || !IsValidLuaIdentifier(method)) {
                return false;
            }
            *receiver_out = receiver;
            *method_out = method;
            return true;
        };

        if (assign_if_valid(value.rfind(':')) || assign_if_valid(value.rfind('.'))) {
            return true;
        }

        if (value.size() >= 4) {
            const std::size_t bracket = value.rfind("[\"");
            if (bracket != std::string_view::npos && value.ends_with("\"]")) {
                const std::string_view receiver = TrimLuaWhitespace(value.substr(0, bracket));
                const std::string_view method(value.data() + bracket + 2,
                                              value.size() - (bracket + 4));
                if (!receiver.empty() && IsValidLuaIdentifier(method)) {
                    *receiver_out = receiver;
                    *method_out = method;
                    return true;
                }
            }
        }

        return false;
    };

    std::string_view receiver;
    std::string_view method;
    if (!split_method_callee(callee, &receiver, &method)) {
        return false;
    }

    const std::string normalized_receiver = normalize_expression(receiver);
    const std::string normalized_first_argument =
        normalize_expression(arguments.front());
    if (normalized_receiver.empty() || normalized_first_argument.empty()) {
        return false;
    }
    if (!IsRenderedSafePrefixExpression(normalized_receiver) ||
        !IsRenderedSafePrefixExpression(normalized_first_argument)) {
        return false;
    }
    if (normalized_first_argument != normalized_receiver) {
        const std::string canonical_receiver =
            normalize_identity_expression(normalized_receiver);
        const std::string canonical_first_argument =
            normalize_identity_expression(normalized_first_argument);
        if (canonical_receiver.empty() ||
            canonical_receiver != canonical_first_argument) {
            return false;
        }
    }

    std::string rebuilt = std::string(receiver) + ":" + std::string(method) + "(";
    for (std::size_t index = 1; index < arguments.size(); ++index) {
        if (index != 1) {
            rebuilt += ", ";
        }
        rebuilt += std::string(TrimLuaWhitespace(arguments[index]));
    }
    rebuilt += ")";
    *rewritten_out = std::move(rebuilt);
    return true;
}

std::string WrapInlineExpression(std::string_view expression) {
    const std::string_view trimmed = TrimLuaWhitespace(expression);
    if (trimmed.empty()) {
        return "nil";
    }

    const auto is_safe_prefix_expression = [](std::string_view candidate) {
        candidate = TrimLuaWhitespace(candidate);
        const auto is_identifier_start = [](char ch) {
            const unsigned char uch = static_cast<unsigned char>(ch);
            return std::isalpha(uch) != 0 || ch == '_';
        };
        const auto is_identifier_char = [&](char ch) {
            const unsigned char uch = static_cast<unsigned char>(ch);
            return std::isalnum(uch) != 0 || ch == '_';
        };

        if (candidate.empty() || !is_identifier_start(candidate.front())) {
            return false;
        }

        auto skip_identifier = [&](std::size_t index) {
            while (index < candidate.size() && is_identifier_char(candidate[index])) {
                ++index;
            }
            return index;
        };

        auto skip_balanced = [&](std::size_t index, char open, char close) {
            if (index >= candidate.size() || candidate[index] != open) {
                return std::string_view::npos;
            }

            int depth = 0;
            bool in_single_quote = false;
            bool in_double_quote = false;
            for (; index < candidate.size(); ++index) {
                const char ch = candidate[index];
                if (in_single_quote) {
                    if (ch == '\\' && index + 1 < candidate.size()) {
                        ++index;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && index + 1 < candidate.size()) {
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
                if (ch == open) {
                    ++depth;
                } else if (ch == close) {
                    --depth;
                    if (depth == 0) {
                        return index + 1;
                    }
                }
            }

            return std::string_view::npos;
        };

        std::size_t index = skip_identifier(0);
        while (index < candidate.size()) {
            const char ch = candidate[index];
            if (ch == '.') {
                ++index;
                if (index >= candidate.size() || !is_identifier_start(candidate[index])) {
                    return false;
                }
                index = skip_identifier(index);
                continue;
            }
            if (ch == ':') {
                ++index;
                if (index >= candidate.size() || !is_identifier_start(candidate[index])) {
                    return false;
                }
                index = skip_identifier(index);
                continue;
            }
            if (ch == '[') {
                index = skip_balanced(index, '[', ']');
                if (index == std::string_view::npos) {
                    return false;
                }
                continue;
            }
            if (ch == '(') {
                index = skip_balanced(index, '(', ')');
                if (index == std::string_view::npos) {
                    return false;
                }
                continue;
            }
            if (std::isspace(static_cast<unsigned char>(ch)) != 0) {
                ++index;
                continue;
            }
            return false;
        }

        return true;
    };

    if (trimmed.front() == '{' || trimmed.front() == '"' || trimmed.front() == '\'' ||
        trimmed.front() == '(' || trimmed == "nil" || trimmed == "true" ||
        trimmed == "false" || IsValidLuaIdentifier(trimmed) ||
        is_safe_prefix_expression(trimmed)) {
        return std::string(trimmed);
    }

    return "(" + std::string(trimmed) + ")";
}

bool HasPositiveLuaExpressionBalance(std::string_view expression,
                                     int* paren_balance_out,
                                     int* brace_balance_out) {
    int paren_balance = 0;
    int brace_balance = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    std::string_view expression_without_comment(expression);
    for (std::size_t i = 0; i + 1 < expression_without_comment.size(); ++i) {
        const char ch = expression_without_comment[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < expression_without_comment.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < expression_without_comment.size()) {
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
        if (ch == '-' && expression_without_comment[i + 1] == '-') {
            expression_without_comment = expression_without_comment.substr(0, i);
            break;
        }
    }

    in_single_quote = false;
    in_double_quote = false;
    for (std::size_t i = 0; i < expression_without_comment.size(); ++i) {
        const char ch = expression_without_comment[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < expression_without_comment.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < expression_without_comment.size()) {
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

    if (paren_balance_out != nullptr) {
        *paren_balance_out = paren_balance;
    }
    if (brace_balance_out != nullptr) {
        *brace_balance_out = brace_balance;
    }
    return paren_balance > 0 || brace_balance > 0;
}

bool SimplifyRenderedLuaTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    return RunRenderedLuaCleanupPassLoop("SimplifyRenderedLuaTemporaries", 64, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string temporary_name;
            std::string temporary_expression;
            if (!IsTemporaryAssignmentLine((*lines)[line_index],
                                           &temporary_name,
                                           &temporary_expression)) {
                continue;
            }

            const std::string_view temporary_trimmed = TrimLuaWhitespace(temporary_expression);
            int paren_balance = 0;
            int brace_balance = 0;
            const bool has_positive_balance = HasPositiveLuaExpressionBalance(
                temporary_expression, &paren_balance, &brace_balance);
            if (temporary_expression.find('\n') != std::string::npos ||
                (!temporary_trimmed.empty() && temporary_trimmed.front() == '{') ||
                temporary_expression.find("function(") != std::string::npos ||
                has_positive_balance) {
                continue;
            }

            std::size_t use_line = lines->size();
            std::size_t use_count = 0;
            std::size_t use_found = std::string::npos;
            bool blocked_by_reassignment = false;
            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();
            for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                const std::string_view probe_trimmed = TrimLuaWhitespace((*lines)[probe]);
                if (probe_trimmed.find("function(") != std::string_view::npos) {
                    const std::size_t line_uses =
                        CountIdentifierUses((*lines)[probe], temporary_name);
                    if (line_uses != 0) {
                        if (use_count == 0 && line_uses == 1) {
                            use_found = FindIdentifierUse((*lines)[probe], temporary_name);
                        } else {
                            use_found = std::string::npos;
                        }
                        use_count += line_uses;
                        use_line = probe;
                        if (use_count > 1) {
                            break;
                        }
                    }
                    const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, probe);
                    if (function_end >= lines->size() || function_end <= probe) {
                        break;
                    }
                    probe = function_end;
                    continue;
                }

                if (IsRenderedShadowingLocalDeclaration((*lines)[probe], temporary_name)) {
                    blocked_by_reassignment = true;
                    break;
                }

                if (IsAssignmentToTemporary((*lines)[probe], temporary_name)) {
                    std::size_t rhs_found = std::string::npos;
                    std::size_t rhs_use_count = 0;
                    if (FindIdentifierUseInAssignmentRhs((*lines)[probe],
                                                         temporary_name,
                                                         &rhs_found,
                                                         &rhs_use_count)) {
                        if (use_count == 0 && rhs_use_count == 1) {
                            use_found = rhs_found;
                        } else {
                            use_found = std::string::npos;
                        }
                        use_count += rhs_use_count;
                        use_line = probe;
                    } else {
                        blocked_by_reassignment = true;
                    }
                    break;
                }

                const std::size_t line_uses =
                    CountIdentifierUses((*lines)[probe], temporary_name);
                if (line_uses == 0) {
                    continue;
                }
                if (use_count == 0 && line_uses == 1) {
                    use_found = FindIdentifierUse((*lines)[probe], temporary_name);
                } else {
                    use_found = std::string::npos;
                }
                use_count += line_uses;
                use_line = probe;
                if (use_count > 1) {
                    break;
                }
            }

            if (use_count == 0) {
                if (blocked_by_reassignment) {
                    continue;
                }
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
                pass_changed = true;
                break;
            }

            if (use_count != 1 || use_line >= lines->size()) {
                continue;
            }

            const std::size_t found = use_found != std::string::npos
                                          ? use_found
                                          : FindIdentifierUse((*lines)[use_line],
                                                              temporary_name);
            if (found == std::string::npos) {
                continue;
            }

            const std::string wrapped_expression =
                WrapInlineExpression(temporary_expression);
            const std::size_t first_non_space =
                (*lines)[use_line].find_first_not_of(" \t");
            if (first_non_space != std::string::npos && found == first_non_space &&
                !wrapped_expression.empty() && wrapped_expression.front() == '(') {
                const std::string_view suffix = TrimLuaWhitespace(
                    std::string_view((*lines)[use_line]).substr(found + temporary_name.size()));
                const bool safe_statement_leading_inline =
                    !suffix.empty() &&
                    (suffix.front() == '.' || suffix.front() == '[' ||
                     suffix.front() == ':' || suffix.front() == '(');
                if (!safe_statement_leading_inline) {
                    continue;
                }
            }

            std::string replacement_line = (*lines)[use_line];
            replacement_line.replace(found,
                                     temporary_name.size(),
                                     wrapped_expression);
            (*lines)[use_line] = std::move(replacement_line);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            pass_changed = true;
            break;
        }
        return pass_changed;
    });
}

bool SimplifyRenderedLuaReturnTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    return RunRenderedLuaCleanupPassLoop("SimplifyRenderedLuaReturnTemporaries", 64, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string temporary_name;
            std::string temporary_expression;
            if (!IsTemporaryAssignmentLine((*lines)[line_index],
                                           &temporary_name,
                                           &temporary_expression)) {
                continue;
            }

            int paren_balance = 0;
            int brace_balance = 0;
            if (temporary_expression.find('\n') != std::string::npos ||
                CountIdentifierUses(temporary_expression, temporary_name) != 0 ||
                HasPositiveLuaExpressionBalance(temporary_expression,
                                               &paren_balance,
                                               &brace_balance)) {
                continue;
            }

            std::size_t use_line = lines->size();
            std::size_t use_count = 0;
            std::size_t use_found = std::string::npos;
            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();
            for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                const std::string_view probe_trimmed = TrimLuaWhitespace((*lines)[probe]);
                if (probe_trimmed.find("function(") != std::string_view::npos) {
                    const std::size_t line_uses =
                        CountIdentifierUses((*lines)[probe], temporary_name);
                    if (line_uses != 0) {
                        use_count += line_uses;
                        use_line = probe;
                    }
                    const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, probe);
                    if (function_end >= lines->size() || function_end <= probe) {
                        break;
                    }
                    probe = function_end;
                    continue;
                }

                if (IsAssignmentToTemporary((*lines)[probe], temporary_name)) {
                    break;
                }

                const std::size_t line_uses =
                    CountIdentifierUses((*lines)[probe], temporary_name);
                if (line_uses == 0) {
                    continue;
                }
                if (use_count == 0 && line_uses == 1) {
                    use_found = FindIdentifierUse((*lines)[probe], temporary_name);
                } else {
                    use_found = std::string::npos;
                }
                use_count += line_uses;
                use_line = probe;
                if (use_count > 1) {
                    break;
                }
            }

            if (use_count != 1 || use_line >= lines->size()) {
                continue;
            }

            const std::string_view stripped_use = StripLuaLineComment((*lines)[use_line]);
            const std::string_view trimmed_use = TrimLuaWhitespace(stripped_use);
            if (!StartsWithLuaKeyword(trimmed_use, "return")) {
                continue;
            }
            const std::size_t found = use_found != std::string::npos
                                          ? use_found
                                          : FindIdentifierUse((*lines)[use_line],
                                                              temporary_name);
            if (found == std::string::npos) {
                continue;
            }

            const std::size_t return_prefix =
                (*lines)[use_line].find("return");
            if (return_prefix == std::string::npos || found < return_prefix + 6) {
                continue;
            }

            std::string replacement_line = (*lines)[use_line];
            replacement_line.replace(found,
                                     temporary_name.size(),
                                     WrapInlineExpression(temporary_expression));
            (*lines)[use_line] = std::move(replacement_line);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            pass_changed = true;
            break;
        }
        return pass_changed;
    });
}

std::size_t CountIdentifierKeyword(std::string_view line, std::string_view keyword) {
    return CountIdentifierUses(line, keyword);
}

bool StartsWithLuaKeyword(std::string_view text, std::string_view keyword) {
    if (text.size() < keyword.size() || text.substr(0, keyword.size()) != keyword) {
        return false;
    }
    if (text.size() == keyword.size()) {
        return true;
    }
    const char next = text[keyword.size()];
    return std::isspace(static_cast<unsigned char>(next)) != 0 || next == '(';
}

std::string_view StripLuaLineComment(std::string_view line) {
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t i = 0; i < line.size(); ++i) {
        const char ch = line[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < line.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < line.size()) {
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
        if (ch == '-' && i + 1 < line.size() && line[i + 1] == '-') {
            return line.substr(0, i);
        }
    }
    return line;
}

int GetRenderedLuaBlockDelta(std::string_view line) {
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

    if (trimmed == "end" || trimmed == "end," || StartsWithLuaKeyword(trimmed, "until")) {
        return -1;
    }

    if (trimmed == "do") {
        return 1;
    }

    if (StartsWithLuaKeyword(trimmed, "if") ||
        StartsWithLuaKeyword(trimmed, "for") ||
        StartsWithLuaKeyword(trimmed, "while") ||
        StartsWithLuaKeyword(trimmed, "function")) {
        return 1;
    }

    if (trimmed.rfind("return function(", 0) == 0) {
        return 1;
    }

    if (trimmed.find("= function(") != std::string_view::npos) {
        return 1;
    }

    return 0;
}

bool IsRenderedFunctionStartLine(std::string_view line) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty()) {
        return false;
    }
    return StartsWithLuaKeyword(trimmed, "function") ||
           trimmed.rfind("return function(", 0) == 0 ||
           trimmed.find("= function(") != std::string_view::npos;
}

std::size_t FindRenderedFunctionBlockEnd(const std::vector<std::string>& lines,
                                         std::size_t start_line) {
    if (start_line >= lines.size()) {
        return lines.size();
    }

    int block_balance = 0;
    for (std::size_t line_index = start_line; line_index < lines.size(); ++line_index) {
        block_balance += GetRenderedLuaBlockDelta(lines[line_index]);
        if (block_balance <= 0) {
            return line_index;
        }
    }
    return lines.size();
}

bool ParseRenderedTableInitializerLine(std::string_view line,
                                       std::string* indent,
                                       std::string* lhs_prefix,
                                       std::string* name) {
    const std::size_t first_non_space = line.find_first_not_of(" \t");
    const std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.empty()) {
        return false;
    }

    std::string_view remainder = trimmed;
    std::string local_prefix;
    if (remainder.substr(0, 6) == "local ") {
        local_prefix = "local ";
        remainder = TrimLuaWhitespace(remainder.substr(6));
    }

    const std::size_t equal = remainder.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(remainder.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(remainder.substr(equal + 1));
    if (!IsRenderedSafePrefixExpression(lhs) || rhs != "{}") {
        return false;
    }

    if (indent != nullptr) {
        *indent = first_non_space == std::string_view::npos
                      ? std::string()
                      : std::string(line.substr(0, first_non_space));
    }
    if (lhs_prefix != nullptr) {
        *lhs_prefix = (indent != nullptr ? *indent
                                         : (first_non_space == std::string_view::npos
                                                ? std::string()
                                                : std::string(line.substr(0, first_non_space)))) +
                      local_prefix + std::string(lhs) + " = ";
    }
    if (name != nullptr) {
        *name = std::string(lhs);
    }
    return true;
}

bool ParseRenderedTableLiteralStartLine(std::string_view line,
                                        std::string* indent,
                                        std::string* lhs_prefix,
                                        std::string* name) {
    const std::size_t first_non_space = line.find_first_not_of(" \t");
    const std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.empty()) {
        return false;
    }

    std::string_view remainder = trimmed;
    std::string local_prefix;
    if (remainder.substr(0, 6) == "local ") {
        local_prefix = "local ";
        remainder = TrimLuaWhitespace(remainder.substr(6));
    }

    const std::size_t equal = remainder.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(remainder.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(remainder.substr(equal + 1));
    if (!IsRenderedSafePrefixExpression(lhs) || rhs != "{") {
        return false;
    }

    if (indent != nullptr) {
        *indent = first_non_space == std::string_view::npos
                      ? std::string()
                      : std::string(line.substr(0, first_non_space));
    }
    if (lhs_prefix != nullptr) {
        *lhs_prefix = (indent != nullptr ? *indent
                                         : (first_non_space == std::string_view::npos
                                                ? std::string()
                                                : std::string(line.substr(0, first_non_space)))) +
                      local_prefix + std::string(lhs) + " = ";
    }
    if (name != nullptr) {
        *name = std::string(lhs);
    }
    return true;
}

bool ParseRenderedTableFieldAssignment(std::string_view line,
                                       std::string_view table_name,
                                       std::string* field_key,
                                       std::string* rhs_expression,
                                       bool* rhs_is_function,
                                       int* array_index) {
    std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.size() < table_name.size() ||
        trimmed.substr(0, table_name.size()) != table_name) {
        return false;
    }
    trimmed.remove_prefix(table_name.size());
    trimmed = TrimLuaWhitespace(trimmed);
    if (trimmed.empty()) {
        return false;
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (lhs.empty() || rhs.empty()) {
        return false;
    }

    std::string rendered_key;
    int parsed_array_index = -1;
    if (lhs.front() == '.') {
        const std::string_view key = TrimLuaWhitespace(lhs.substr(1));
        if (!IsValidLuaIdentifier(key)) {
            return false;
        }
        rendered_key = std::string(key);
    } else if (lhs.front() == '[' && lhs.back() == ']') {
        const std::string_view inner = TrimLuaWhitespace(lhs.substr(1, lhs.size() - 2));
        bool numeric_inner = !inner.empty();
        for (char ch : inner) {
            if (std::isdigit(static_cast<unsigned char>(ch)) == 0) {
                numeric_inner = false;
                break;
            }
        }
        if (numeric_inner) {
            parsed_array_index = std::stoi(std::string(inner));
            rendered_key = "[" + std::string(inner) + "]";
        } else if (inner.size() >= 2 &&
                   ((inner.front() == '"' && inner.back() == '"') ||
                    (inner.front() == '\'' && inner.back() == '\''))) {
            const std::string_view unquoted = inner.substr(1, inner.size() - 2);
            if (IsValidLuaIdentifier(unquoted)) {
                rendered_key = std::string(unquoted);
            } else {
                rendered_key = "[" + std::string(inner) + "]";
            }
        } else {
            rendered_key = "[" + std::string(inner) + "]";
        }
    } else {
        return false;
    }

    if (field_key != nullptr) {
        *field_key = std::move(rendered_key);
    }
    if (rhs_expression != nullptr) {
        *rhs_expression = std::string(rhs);
    }
    if (rhs_is_function != nullptr) {
        *rhs_is_function =
            rhs.size() >= 9 && rhs.substr(0, 9) == std::string_view("function(");
    }
    if (array_index != nullptr) {
        *array_index = parsed_array_index;
    }
    return true;
}

bool ParseRenderedAssignmentLine(std::string_view line,
                                 bool* is_local,
                                 std::vector<std::string>* lhs_names,
                                 std::string* rhs_expression) {
    if (lhs_names == nullptr || rhs_expression == nullptr) {
        return false;
    }

    std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.empty()) {
        return false;
    }

    if (is_local != nullptr) {
        *is_local = false;
    }
    if (trimmed.starts_with("local ")) {
        if (is_local != nullptr) {
            *is_local = true;
        }
        trimmed = TrimLuaWhitespace(trimmed.substr(6));
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (lhs.empty() || rhs.empty()) {
        return false;
    }

    std::vector<std::string> parsed_names;
    std::size_t cursor = 0;
    while (cursor < lhs.size()) {
        const std::size_t comma = lhs.find(',', cursor);
        const std::string_view part = TrimLuaWhitespace(
            comma == std::string_view::npos ? lhs.substr(cursor)
                                            : lhs.substr(cursor, comma - cursor));
        if (part.empty() || !IsValidLuaIdentifier(part)) {
            return false;
        }
        parsed_names.emplace_back(part);
        if (comma == std::string_view::npos) {
            break;
        }
        cursor = comma + 1;
    }

    if (parsed_names.empty()) {
        return false;
    }

    *lhs_names = std::move(parsed_names);
    *rhs_expression = std::string(rhs);
    return true;
}

bool RemoveRenderedLuaEmptyIfBlocks(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto condition_has_call_side_effects = [](std::string_view condition) {
        bool in_single_quote = false;
        bool in_double_quote = false;
        for (std::size_t index = 0; index < condition.size(); ++index) {
            const char ch = condition[index];
            if (ch == '\'' && !in_double_quote) {
                in_single_quote = !in_single_quote;
                continue;
            }
            if (ch == '"' && !in_single_quote) {
                in_double_quote = !in_double_quote;
                continue;
            }
            if (in_single_quote || in_double_quote || ch != '(' || index == 0) {
                continue;
            }

            std::size_t probe = index;
            while (probe > 0 &&
                   std::isspace(static_cast<unsigned char>(condition[probe - 1])) != 0) {
                --probe;
            }
            if (probe == 0) {
                continue;
            }

            const char previous = condition[probe - 1];
            if (std::isalnum(static_cast<unsigned char>(previous)) != 0 || previous == '_' ||
                previous == ')' || previous == ']') {
                return true;
            }
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size();) {
        const std::string_view current =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        const std::string_view next =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        std::string condition;
        if (current.starts_with("if ") && current.ends_with(" then") &&
            current.size() > 7 &&
            (condition = std::string(
                 TrimLuaWhitespace(current.substr(3, current.size() - 3 - 5))),
             true) &&
            next == "end" &&
            !condition_has_call_side_effects(condition)) {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2));
            changed = true;
            continue;
        }
        ++line_index;
    }

    return changed;
}

bool RemoveRenderedLuaNoOpAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size();) {
        std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (trimmed.starts_with("local ")) {
            ++line_index;
            continue;
        }

        const std::size_t equal = trimmed.find('=');
        if (equal == std::string_view::npos) {
            ++line_index;
            continue;
        }

        const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
        const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
        if (lhs.empty() || rhs.empty() || lhs.find(',') != std::string_view::npos ||
            rhs.find(',') != std::string_view::npos || lhs != rhs) {
            ++line_index;
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedTableInitializers(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size();) {
        const std::string_view header =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!header.starts_with("if ") || !header.ends_with(" then")) {
            ++line_index;
            continue;
        }

        const std::string_view condition =
            TrimLuaWhitespace(header.substr(2, header.size() - 2 - 4));
        if (!condition.starts_with("not ")) {
            ++line_index;
            continue;
        }

        const std::string_view target = TrimLuaWhitespace(condition.substr(4));
        if (target.empty() || !IsRenderedSafePrefixExpression(target)) {
            ++line_index;
            continue;
        }

        bool is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &is_local,
                                               &lhs,
                                               &rhs) ||
            is_local || lhs != target || TrimLuaWhitespace(rhs) != "{}") {
            ++line_index;
            continue;
        }

        const std::string_view end_line =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
        if (end_line != "end") {
            ++line_index;
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + std::string(target) + " = (" + std::string(target) + " or {})";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaDefaultNilAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size();) {
        const std::string_view header =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!header.starts_with("if ") || !header.ends_with(" then")) {
            ++line_index;
            continue;
        }

        const std::string_view condition =
            TrimLuaWhitespace(header.substr(2, header.size() - 2 - 4));
        if (!condition.starts_with("not ")) {
            ++line_index;
            continue;
        }

        const std::string_view target = TrimLuaWhitespace(condition.substr(4));
        if (target.empty() || !IsRenderedSafePrefixExpression(target)) {
            ++line_index;
            continue;
        }

        bool default_is_local = false;
        std::string default_lhs;
        std::string default_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &default_is_local,
                                               &default_lhs,
                                               &default_rhs) ||
            default_is_local || default_lhs != target) {
            ++line_index;
            continue;
        }

        std::size_t assignment_end = line_index + 1;
        const std::string_view rhs_view = TrimLuaWhitespace(default_rhs);
        if (rhs_view.starts_with("function(")) {
            assignment_end = FindRenderedFunctionBlockEnd(*lines, line_index + 1);
            if (assignment_end >= lines->size() || assignment_end <= line_index + 1) {
                ++line_index;
                continue;
            }
        } else if (rhs_view.starts_with("{")) {
            assignment_end = FindRenderedTableLiteralEnd(*lines, line_index + 1);
            if (assignment_end >= lines->size() || assignment_end < line_index + 1) {
                ++line_index;
                continue;
            }
        }

        if (assignment_end + 2 >= lines->size()) {
            ++line_index;
            continue;
        }

        const std::string_view end_line =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[assignment_end + 1]));
        if (end_line != "end") {
            ++line_index;
            continue;
        }

        bool reset_is_local = false;
        std::string reset_lhs;
        std::string reset_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[assignment_end + 2],
                                               &reset_is_local,
                                               &reset_lhs,
                                               &reset_rhs) ||
            reset_is_local || reset_lhs != target || reset_rhs != "nil") {
            ++line_index;
            continue;
        }

        lines->erase(lines->begin() +
                     static_cast<std::ptrdiff_t>(assignment_end + 2));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaNilLocalInitializers(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &is_local,
                                         &lhs_names,
                                         &rhs_expression) ||
            !is_local || TrimLuaWhitespace(rhs_expression) != "nil" || lhs_names.empty()) {
            continue;
        }

        const std::size_t first_non_space = (*lines)[line_index].find_first_not_of(" \t");
        if (first_non_space == std::string::npos) {
            continue;
        }

        std::ostringstream rebuilt;
        rebuilt << (*lines)[line_index].substr(0, first_non_space) << "local ";
        for (std::size_t i = 0; i < lhs_names.size(); ++i) {
            if (i != 0) {
                rebuilt << ", ";
            }
            rebuilt << lhs_names[i];
        }
        (*lines)[line_index] = rebuilt.str();
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateNilResets(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size();) {
        bool init_is_local = false;
        std::string init_lhs;
        std::string init_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &init_is_local,
                                               &init_lhs,
                                               &init_rhs) ||
            init_is_local) {
            ++line_index;
            continue;
        }

        const std::string_view init_rhs_trimmed = TrimLuaWhitespace(init_rhs);
        const std::string expected_default =
            "(" + init_lhs + " or {})";
        if (init_rhs_trimmed != "{}" && init_rhs_trimmed != expected_default) {
            ++line_index;
            continue;
        }

        bool reset_is_local = false;
        std::string reset_lhs;
        std::string reset_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &reset_is_local,
                                               &reset_lhs,
                                               &reset_rhs) ||
            reset_is_local || reset_lhs != init_lhs ||
            TrimLuaWhitespace(reset_rhs) != "nil") {
            ++line_index;
            continue;
        }

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        const std::size_t probe_limit =
            scope_end < lines->size() ? scope_end + 1 : lines->size();
        bool used_before_reassignment = false;
        for (std::size_t probe = line_index + 2; probe < probe_limit; ++probe) {
            if (IsAssignmentToIdentifier((*lines)[probe], init_lhs)) {
                break;
            }
            if (CountIdentifierUses((*lines)[probe], init_lhs) != 0) {
                used_before_reassignment = true;
                break;
            }
        }

        if (!used_before_reassignment) {
            ++line_index;
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaImpossibleAsserts(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size();) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (trimmed.starts_with("assert(nil,")) {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            continue;
        }
        ++line_index;
    }

    return changed;
}

bool ParseRenderedSingleCallLine(std::string_view line,
                                 std::string* callee,
                                 std::vector<std::string>* arguments);

bool SimplifyRenderedLuaNamedNilAsserts(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!trimmed.starts_with("assert(true,") || !trimmed.ends_with(')')) {
            continue;
        }

        const std::size_t nil_marker = trimmed.find("nil '");
        if (nil_marker == std::string_view::npos) {
            continue;
        }

        const std::size_t identifier_begin = nil_marker + 5;
        const std::size_t identifier_end = trimmed.find('\'', identifier_begin);
        if (identifier_end == std::string_view::npos || identifier_end <= identifier_begin) {
            continue;
        }

        const std::string identifier(
            trimmed.substr(identifier_begin, identifier_end - identifier_begin));
        if (!IsValidLuaIdentifier(identifier)) {
            continue;
        }

        const std::size_t comma = trimmed.find(',');
        if (comma == std::string_view::npos || comma + 1 >= trimmed.size() - 1) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent(line));
        const std::string message =
            std::string(TrimLuaWhitespace(trimmed.substr(comma + 1, trimmed.size() - comma - 2)));
        line = indent + "assert(" + identifier + " ~= nil, " + message + ")";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaBooleanAssertConditions(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto try_simplify_assert_condition = [](std::string_view expression,
                                                  std::string* simplified) -> bool {
        if (simplified == nullptr) {
            return false;
        }

        const auto fully_strip = [](std::string_view value) -> std::string {
            std::string current = std::string(TrimLuaWhitespace(value));
            while (true) {
                const std::string stripped =
                    StripRenderedWholeParentheses(current, nullptr);
                if (stripped == current) {
                    return current;
                }
                current = stripped;
            }
        };
        const auto normalize_compare_operand = [&](std::string_view operand) {
            std::string normalized =
                NormalizeRenderedConditionOperand(operand, nullptr);
            if (!normalized.empty()) {
                return normalized;
            }
            return fully_strip(operand);
        };

        const std::string normalized_expression = fully_strip(expression);
        expression = normalized_expression;
        std::vector<std::string> or_parts;
        if (!SplitRenderedTopLevelBooleanChain(expression, "or", &or_parts) ||
            or_parts.size() != 2 ||
            fully_strip(or_parts[1]) != "false") {
            return false;
        }

        std::vector<std::string> and_parts;
        const std::string and_expression = fully_strip(or_parts[0]);
        if (!SplitRenderedTopLevelBooleanChain(and_expression, "and", &and_parts) ||
            and_parts.size() != 2 ||
            fully_strip(and_parts[1]) != "true") {
            return false;
        }

        const std::string left_condition = fully_strip(and_parts[0]);
        const std::string normalized_left_condition =
            NormalizeRenderedConditionExpression(left_condition, nullptr);
        if (!normalized_left_condition.empty() &&
            normalized_left_condition != left_condition) {
            *simplified = normalized_left_condition;
            return true;
        }

        const std::string positive_left_condition =
            BuildRenderedSuccessConditionFromFailure(left_condition, nullptr);
        if (!positive_left_condition.empty() &&
            positive_left_condition != left_condition) {
            *simplified = positive_left_condition;
            return true;
        }

        if (StartsWithLuaKeyword(left_condition, "not")) {
            const std::string negated =
                fully_strip(TrimLuaWhitespace(left_condition.substr(3)));
            std::size_t compare_position = std::string::npos;
            std::string_view compare_op;
            if (FindRenderedTopLevelComparison(negated, &compare_position, &compare_op)) {
                const std::string left =
                    normalize_compare_operand(negated.substr(0, compare_position));
                const std::string right = normalize_compare_operand(
                    negated.substr(compare_position + compare_op.size()));
                if (!left.empty() && !right.empty()) {
                    *simplified = left + " " + InvertRenderedComparison(compare_op) + " " + right;
                    return true;
                }
            }
        }

        const std::string normalized =
            NormalizeRenderedConditionExpression(and_parts[0], nullptr);
        if (normalized.empty()) {
            goto fallback_text_shape;
        }

        *simplified = normalized;
        return true;

fallback_text_shape:
        {
            const std::string flattened = fully_strip(expression);
            constexpr std::string_view kFalseSuffix = " or false";
            constexpr std::string_view kTrueSuffix = " and true";
            if (!flattened.ends_with(kFalseSuffix)) {
                return false;
            }

            const std::string before_false =
                StripRenderedWholeParentheses(
                    TrimLuaWhitespace(flattened.substr(0, flattened.size() - kFalseSuffix.size())),
                    nullptr);
            if (!before_false.ends_with(kTrueSuffix)) {
                return false;
            }

            const std::string raw_condition(std::string(
                TrimLuaWhitespace(before_false.substr(0, before_false.size() - kTrueSuffix.size()))));
            const std::string stripped_raw_condition = fully_strip(raw_condition);
            if (StartsWithLuaKeyword(stripped_raw_condition, "not")) {
                const std::string negated = fully_strip(
                    TrimLuaWhitespace(stripped_raw_condition.substr(3)));
                const std::string comparison_candidate =
                    StripRenderedWholeParentheses(negated, nullptr);
                std::size_t compare_position = std::string::npos;
                std::string_view compare_op;
                if (FindRenderedTopLevelComparison(comparison_candidate,
                                                  &compare_position,
                                                  &compare_op)) {
                    const std::string left = normalize_compare_operand(
                        comparison_candidate.substr(0, compare_position));
                    const std::string right = normalize_compare_operand(
                        comparison_candidate.substr(compare_position + compare_op.size()));
                    if (!left.empty() && !right.empty()) {
                        *simplified =
                            left + " " + InvertRenderedComparison(compare_op) + " " + right;
                        return true;
                    }
                }
            }

            const std::string fallback_normalized =
                NormalizeRenderedConditionExpression(raw_condition, nullptr);
            if (fallback_normalized.empty()) {
                return false;
            }

            *simplified = fallback_normalized;
            return true;
        }
    };

    bool changed = false;
    for (std::string& line : *lines) {
        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallLine(line, &callee, &arguments) ||
            callee != "assert" || arguments.empty()) {
            continue;
        }

        std::string simplified_condition;
        if (!try_simplify_assert_condition(arguments.front(), &simplified_condition)) {
            bool normalized_changed = false;
            simplified_condition =
                NormalizeRenderedConditionExpression(arguments.front(), &normalized_changed);
            if (!normalized_changed || simplified_condition.empty()) {
                continue;
            }
        }

        arguments.front() = simplified_condition;
        std::ostringstream rebuilt_call;
        rebuilt_call << "assert(";
        for (std::size_t argument_index = 0; argument_index < arguments.size(); ++argument_index) {
            if (argument_index != 0) {
                rebuilt_call << ", ";
            }
            rebuilt_call << arguments[argument_index];
        }
        rebuilt_call << ")";

        const std::string indent(GetRenderedLuaIndent(line));
        line = indent + rebuilt_call.str();
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateAssignedAssertReuses(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto replace_all = [](std::string text,
                                const std::string& from,
                                const std::string& to) -> std::string {
        if (from.empty()) {
            return text;
        }
        std::size_t position = 0;
        while ((position = text.find(from, position)) != std::string::npos) {
            text.replace(position, from.size(), to);
            position += to.size();
        }
        return text;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !IsValidLuaIdentifier(assign_lhs)) {
            continue;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        if (normalized_rhs.empty() ||
            normalized_rhs == "true" ||
            normalized_rhs == "false" ||
            normalized_rhs == "nil" ||
            !IsRenderedSafePrefixExpression(normalized_rhs) ||
            CountIdentifierUses(normalized_rhs, assign_lhs) != 0) {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallLine((*lines)[line_index + 1], &callee, &arguments) ||
            callee != "assert" || arguments.empty() ||
            arguments.front().find(normalized_rhs) == std::string::npos ||
            CountIdentifierUses(arguments.front(), assign_lhs) != 0) {
            continue;
        }

        const std::string rewritten_condition =
            replace_all(arguments.front(), normalized_rhs, assign_lhs);
        if (rewritten_condition == arguments.front()) {
            continue;
        }

        arguments.front() = rewritten_condition;
        std::ostringstream rebuilt_call;
        rebuilt_call << "assert(";
        for (std::size_t argument_index = 0; argument_index < arguments.size(); ++argument_index) {
            if (argument_index != 0) {
                rebuilt_call << ", ";
            }
            rebuilt_call << arguments[argument_index];
        }
        rebuilt_call << ")";

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] = indent + rebuilt_call.str();
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaEmptyIfAssertGuards(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto extract_if_condition = [](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!trimmed.starts_with("if ") || !trimmed.ends_with(" then")) {
            return false;
        }

        *condition = std::string(
            TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 3 - 5)));
        return !condition->empty();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        std::string condition;
        if (!extract_if_condition((*lines)[line_index], &condition) ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1])) != "end") {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallLine((*lines)[line_index + 2], &callee, &arguments) ||
            callee != "assert" || arguments.empty()) {
            continue;
        }

        const std::string first_argument =
            StripRenderedWholeParentheses(TrimLuaWhitespace(arguments.front()), nullptr);
        if (first_argument != "true" && first_argument != "false") {
            continue;
        }

        const std::string normalized_condition =
            NormalizeRenderedConditionExpression(condition, nullptr);
        const std::string_view condition_view =
            normalized_condition.empty() ? std::string_view(condition)
                                         : std::string_view(normalized_condition);
        std::string assertion_condition =
            BuildRenderedSuccessConditionFromFailure(condition_view, nullptr);
        if (assertion_condition.empty()) {
            assertion_condition = "not " + WrapInlineExpression(condition_view);
        }
        if (first_argument == "false") {
            assertion_condition = "not " + WrapInlineExpression(assertion_condition);
        }

        arguments.front() = assertion_condition;
        std::ostringstream rebuilt_call;
        rebuilt_call << "assert(";
        for (std::size_t argument_index = 0; argument_index < arguments.size(); ++argument_index) {
            if (argument_index != 0) {
                rebuilt_call << ", ";
            }
            rebuilt_call << arguments[argument_index];
        }
        rebuilt_call << ")";

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + rebuilt_call.str();
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaEmptyGuardedNilAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        const std::string_view header =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        const std::string_view end_line =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!header.starts_with("if ") || !header.ends_with(" then") || end_line != "end") {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            TrimLuaWhitespace(assign_rhs) != "nil") {
            continue;
        }

        const std::string condition = StripRenderedWholeParentheses(
            TrimLuaWhitespace(header.substr(3, header.size() - 3 - 5)), nullptr);
        std::vector<std::string> parts;
        if (!SplitRenderedTopLevelBooleanChain(condition, "or", &parts) ||
            parts.size() < 2) {
            continue;
        }

        std::vector<std::string> positive_parts;
        positive_parts.reserve(parts.size());
        for (const std::string& part : parts) {
            const std::string normalized_part = StripRenderedWholeParentheses(part, nullptr);
            const std::string_view trimmed_part = TrimLuaWhitespace(normalized_part);
            if (!StartsWithLuaKeyword(trimmed_part, "not")) {
                positive_parts.clear();
                break;
            }

            const std::string operand =
                NormalizeRenderedConditionOperand(TrimLuaWhitespace(trimmed_part.substr(3)),
                                                  nullptr);
            if (operand.empty()) {
                positive_parts.clear();
                break;
            }
            positive_parts.push_back(operand);
        }
        if (positive_parts.empty()) {
            continue;
        }

        std::string positive_expression;
        for (std::size_t i = 0; i < positive_parts.size(); ++i) {
            if (i != 0) {
                positive_expression += " and ";
            }
            positive_expression += positive_parts[i];
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + (assign_is_local ? "local " : "") + assign_lhs + " = ((" +
            positive_expression + ") or nil)";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaEmptyConditionBooleanReturns(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        const std::string_view trimmed_outer_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        const std::string_view trimmed_inner_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!trimmed_outer_if.starts_with("if ") || !trimmed_outer_if.ends_with(" then") ||
            !trimmed_inner_if.starts_with("if ") || !trimmed_inner_if.ends_with(" then")) {
            continue;
        }

        const std::string outer_condition = std::string(TrimLuaWhitespace(
            trimmed_outer_if.substr(3, trimmed_outer_if.size() - 3 - 5)));
        const std::string inner_condition = std::string(TrimLuaWhitespace(
            trimmed_inner_if.substr(3, trimmed_inner_if.size() - 3 - 5)));
        if (outer_condition.empty() || inner_condition.empty()) {
            continue;
        }

        const std::string_view trimmed_else =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
        const std::string_view trimmed_inner_end =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]));
        const std::string_view trimmed_return =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 4]));
        const std::string_view trimmed_outer_end =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 5]));
        if (trimmed_else != "else" || trimmed_inner_end != "end" ||
            trimmed_outer_end != "end" || trimmed_return != "return true") {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = indent + "  ";
        const std::vector<std::string> replacement_lines = {
            indent + "if " + outer_condition + " then",
            inner_indent + "return not not (" + inner_condition + ")",
            indent + "end",
        };
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 6));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement_lines.begin(),
                      replacement_lines.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaConditionalReturns(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (line_index + 13 < lines->size()) {
            const std::string_view trimmed_outer_if =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
            const std::string_view trimmed_fail_if =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
            const std::string_view trimmed_secondary_if =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 4]));
            std::string primary_temp_name;
            std::string primary_expression;
            std::string secondary_temp_name;
            std::string secondary_expression;
            std::string then_temp_name;
            std::string then_expression;
            if (trimmed_outer_if.starts_with("if ") && trimmed_outer_if.ends_with(" then") &&
                trimmed_fail_if.starts_with("if ") && trimmed_fail_if.ends_with(" then") &&
                trimmed_secondary_if.starts_with("if ") &&
                trimmed_secondary_if.ends_with(" then") &&
                IsTemporaryAssignmentLine((*lines)[line_index + 1],
                                          &primary_temp_name,
                                          &primary_expression) &&
                IsTemporaryAssignmentLine((*lines)[line_index + 3],
                                          &secondary_temp_name,
                                          &secondary_expression) &&
                IsTemporaryAssignmentLine((*lines)[line_index + 5],
                                          &then_temp_name,
                                          &then_expression) &&
                secondary_temp_name == primary_temp_name &&
                then_temp_name == primary_temp_name) {
                std::size_t secondary_end_line = line_index + 6;
                std::string composed_then_expression = then_expression;
                ComposeRenderedTemporaryExpressionChain(*lines,
                                                       secondary_end_line,
                                                       primary_temp_name,
                                                       composed_then_expression,
                                                       &secondary_end_line,
                                                       &composed_then_expression);
                if (secondary_end_line + 7 >= lines->size()) {
                    continue;
                }

                const std::string_view trimmed_secondary_end =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[secondary_end_line]));
                const std::string_view trimmed_else =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[secondary_end_line + 1]));
                std::string else_temp_name;
                std::string else_expression;
                if (trimmed_secondary_end != "end" || trimmed_else != "else" ||
                    !IsTemporaryAssignmentLine((*lines)[secondary_end_line + 2],
                                              &else_temp_name,
                                              &else_expression) ||
                    else_temp_name != primary_temp_name) {
                    continue;
                }

                std::size_t fail_end_line = secondary_end_line + 3;
                std::string composed_else_expression = else_expression;
                ComposeRenderedTemporaryExpressionChain(*lines,
                                                       fail_end_line,
                                                       primary_temp_name,
                                                       composed_else_expression,
                                                       &fail_end_line,
                                                       &composed_else_expression);
                if (fail_end_line + 4 >= lines->size()) {
                    continue;
                }

                const std::string_view trimmed_fail_end =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[fail_end_line]));
                const std::string_view trimmed_return =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[fail_end_line + 1]));
                const std::string_view trimmed_outer_end =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[fail_end_line + 2]));
                const std::string_view trimmed_final_return =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[fail_end_line + 3]));
                if (trimmed_fail_end != "end" ||
                    !IsRenderedExactReturnIdentifier(trimmed_return, primary_temp_name) ||
                    trimmed_outer_end != "end" ||
                    trimmed_final_return != "return false" ||
                    composed_then_expression != composed_else_expression) {
                    continue;
                }

                const std::string_view outer_condition_view =
                    TrimLuaWhitespace(trimmed_outer_if.substr(3, trimmed_outer_if.size() - 3 - 5));
                const std::string_view fail_condition_view =
                    TrimLuaWhitespace(trimmed_fail_if.substr(3, trimmed_fail_if.size() - 3 - 5));
                const std::string_view secondary_condition_view = TrimLuaWhitespace(
                    trimmed_secondary_if.substr(3, trimmed_secondary_if.size() - 3 - 5));
                if (outer_condition_view.empty() || fail_condition_view.empty() ||
                    secondary_condition_view.empty()) {
                    continue;
                }

                bool negated_fail_changed = false;
                const std::string negated_fail_condition =
                    BuildRenderedSuccessConditionFromFailure(fail_condition_view,
                                                             &negated_fail_changed);
                if (negated_fail_condition.empty()) {
                    continue;
                }

                std::string outer_condition(outer_condition_view);
                std::size_t replacement_start = line_index;
                if (IsRenderTemporaryName(outer_condition_view) && line_index > 0) {
                    std::string outer_temp_name;
                    std::string outer_expression;
                    if (IsTemporaryAssignmentLine((*lines)[line_index - 1],
                                                  &outer_temp_name,
                                                  &outer_expression) &&
                        outer_temp_name == outer_condition_view) {
                        outer_condition = outer_expression;
                        replacement_start = line_index - 1;
                    }
                }

                bool outer_changed = false;
                bool success_changed = false;
                const std::string normalized_outer =
                    NormalizeRenderedConditionExpression(outer_condition, &outer_changed);
                const std::string normalized_secondary =
                    NormalizeRenderedConditionExpression(secondary_condition_view,
                                                         &success_changed);
                const std::string combined_success = RenderLogicalExpression(
                    negated_fail_condition,
                    "or",
                    normalized_secondary.empty()
                        ? std::string(secondary_condition_view)
                        : normalized_secondary);
                const std::string combined_condition = RenderLogicalExpression(
                    normalized_outer.empty() ? outer_condition : normalized_outer,
                    "and",
                    combined_success);
                if (combined_condition.empty()) {
                    continue;
                }

                const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
                const std::vector<std::string> replacement_lines = {
                    indent + "if " + combined_condition + " then",
                    indent + "return " + composed_then_expression,
                    indent + "end",
                    indent + "return false",
                };
                lines->erase(lines->begin() +
                                 static_cast<std::ptrdiff_t>(replacement_start),
                             lines->begin() +
                                 static_cast<std::ptrdiff_t>(fail_end_line + 4));
                lines->insert(lines->begin() +
                                  static_cast<std::ptrdiff_t>(replacement_start),
                              replacement_lines.begin(),
                              replacement_lines.end());
                changed = true;
                if (replacement_start != 0) {
                    line_index = replacement_start - 1;
                } else {
                    line_index = 0;
                }
                continue;
            }
        }

        if (line_index + 7 < lines->size()) {
            const std::string_view trimmed_outer_if =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
            if (trimmed_outer_if.starts_with("if ") &&
                trimmed_outer_if.ends_with(" then")) {
                std::string inner_temp_name;
                std::string inner_condition_expression;
                std::string return_temp_name;
                std::string return_expression;
                if (IsTemporaryAssignmentLine((*lines)[line_index + 1],
                                              &inner_temp_name,
                                              &inner_condition_expression) &&
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]))
                        .starts_with("if ") &&
                    IsTemporaryAssignmentLine((*lines)[line_index + 3],
                                              &return_temp_name,
                                              &return_expression) &&
                    return_temp_name == inner_temp_name) {
                    const std::string_view trimmed_inner_if =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
                    if (!trimmed_inner_if.ends_with(" then")) {
                        continue;
                    }

                    const std::string outer_condition = std::string(TrimLuaWhitespace(
                        trimmed_outer_if.substr(3, trimmed_outer_if.size() - 3 - 5)));
                    const std::string inner_condition = std::string(TrimLuaWhitespace(
                        trimmed_inner_if.substr(3, trimmed_inner_if.size() - 3 - 5)));
                    if (outer_condition.empty() || inner_condition.empty()) {
                        continue;
                    }

                    std::size_t inner_end_line = line_index + 4;
                    std::string final_return_expression = return_expression;
                    ComposeRenderedTemporaryExpressionChain(*lines,
                                                           inner_end_line,
                                                           inner_temp_name,
                                                           final_return_expression,
                                                           &inner_end_line,
                                                           &final_return_expression);

                    if (inner_end_line + 4 >= lines->size()) {
                        continue;
                    }

                    const std::string_view trimmed_inner_end =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[inner_end_line]));
                    const std::string_view trimmed_return =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[inner_end_line + 1]));
                    const std::string_view trimmed_outer_end =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[inner_end_line + 2]));
                    const std::string_view trimmed_final_return =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[inner_end_line + 3]));
                    if (trimmed_inner_end == "end" && trimmed_outer_end == "end" &&
                        trimmed_return == "return " + inner_temp_name &&
                        trimmed_final_return == "return false") {
                        bool outer_changed = false;
                        bool inner_changed = false;
                        const std::string normalized_outer =
                            NormalizeRenderedConditionExpression(outer_condition,
                                                                 &outer_changed);
                        const std::string normalized_inner =
                            NormalizeRenderedConditionExpression(inner_condition,
                                                                 &inner_changed);
                        const std::string combined_condition = RenderLogicalExpression(
                            normalized_outer.empty() ? outer_condition : normalized_outer,
                            "and",
                            normalized_inner.empty() ? inner_condition : normalized_inner);
                        if (!combined_condition.empty()) {
                            const std::string indent(
                                GetRenderedLuaIndent((*lines)[line_index]));
                            const std::vector<std::string> replacement_lines = {
                                indent + "if " + combined_condition + " then",
                                indent + "return " + final_return_expression,
                                indent + "end",
                                indent + "return false",
                            };
                            lines->erase(
                                lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                lines->begin() +
                                    static_cast<std::ptrdiff_t>(inner_end_line + 4));
                            lines->insert(
                                lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                replacement_lines.begin(),
                                replacement_lines.end());
                            changed = true;
                            if (line_index != 0) {
                                --line_index;
                            }
                            continue;
                        }
                    }
                }
            }
        }

        if (line_index + 9 < lines->size()) {
            std::string outer_temp_name;
            std::string outer_condition_expression;
            std::string inner_temp_name;
            std::string inner_condition_expression;
            std::string return_temp_name;
            std::string return_expression;
            if (IsTemporaryAssignmentLine((*lines)[line_index],
                                          &outer_temp_name,
                                          &outer_condition_expression) &&
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]))
                    .starts_with("if ") &&
                IsTemporaryAssignmentLine((*lines)[line_index + 2],
                                          &inner_temp_name,
                                          &inner_condition_expression) &&
                inner_temp_name == outer_temp_name &&
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]))
                    .starts_with("if ") &&
                IsTemporaryAssignmentLine((*lines)[line_index + 4],
                                          &return_temp_name,
                                          &return_expression) &&
                return_temp_name == outer_temp_name) {
                const std::string_view trimmed_outer_if =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
                const std::string_view trimmed_inner_if =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]));
                if (!trimmed_outer_if.ends_with(" then") ||
                    !trimmed_inner_if.ends_with(" then")) {
                    continue;
                }
                const std::string outer_condition = std::string(
                    TrimLuaWhitespace(trimmed_outer_if.substr(3, trimmed_outer_if.size() - 3 - 5)));
                const std::string inner_condition = std::string(
                    TrimLuaWhitespace(trimmed_inner_if.substr(3, trimmed_inner_if.size() - 3 - 5)));
                if (outer_condition.empty() || inner_condition.empty()) {
                    continue;
                }
                std::size_t inner_end_line = line_index + 5;
                std::string final_return_expression = return_expression;
                ComposeRenderedTemporaryExpressionChain(*lines,
                                                       inner_end_line,
                                                       outer_temp_name,
                                                       final_return_expression,
                                                       &inner_end_line,
                                                       &final_return_expression);

                const std::string_view trimmed_inner_end =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[inner_end_line]));
                const std::string_view trimmed_return =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[inner_end_line + 1]));
                const std::string_view trimmed_outer_end =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[inner_end_line + 2]));
                const std::string_view trimmed_final_return =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[inner_end_line + 3]));
                if (trimmed_inner_end == "end" &&
                    trimmed_outer_end == "end" &&
                    trimmed_return == "return " + outer_temp_name &&
                    trimmed_final_return == "return false") {
                    bool outer_changed = false;
                    bool inner_changed = false;
                    const std::string normalized_outer =
                        NormalizeRenderedConditionExpression(outer_condition, &outer_changed);
                    const std::string normalized_inner =
                        NormalizeRenderedConditionExpression(inner_condition, &inner_changed);
                    const std::string combined_condition = RenderLogicalExpression(
                        normalized_outer.empty() ? outer_condition
                                                 : normalized_outer,
                        "and",
                        normalized_inner.empty() ? inner_condition
                                                 : normalized_inner);
                    if (!combined_condition.empty()) {
                        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
                        const std::vector<std::string> replacement_lines = {
                            indent + "if " + combined_condition + " then",
                            indent + "return " + final_return_expression,
                            indent + "end",
                            indent + "return false",
                        };
                        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                     lines->begin() + static_cast<std::ptrdiff_t>(inner_end_line + 4));
                        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                      replacement_lines.begin(),
                                      replacement_lines.end());
                        changed = true;
                        if (line_index != 0) {
                            --line_index;
                        }
                        continue;
                    }
                }
            }
        }

        {
            bool initial_is_local = false;
            std::string temporary_name;
            std::string base_expression;
            if (ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                  &initial_is_local,
                                                  &temporary_name,
                                                  &base_expression) &&
                IsRenderTemporaryName(temporary_name)) {
                std::size_t cursor = line_index + 1;
                std::string composed_base_expression;
                ComposeRenderedTemporaryExpressionChain(*lines,
                                                       cursor,
                                                       temporary_name,
                                                       base_expression,
                                                       &cursor,
                                                       &composed_base_expression);
                if (cursor + 3 < lines->size()) {
                    const std::string_view trimmed_if =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor]));
                    const std::string_view trimmed_end =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor + 2]));
                    const std::string_view trimmed_final_return =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor + 3]));
                    bool then_is_local = false;
                    std::string then_name;
                    std::string then_expression;
                    if (trimmed_if.starts_with("if ") && trimmed_if.ends_with(" then") &&
                        ParseRenderedSingleAssignmentLine((*lines)[cursor + 1],
                                                          &then_is_local,
                                                          &then_name,
                                                          &then_expression) &&
                        !then_is_local && then_name == temporary_name &&
                        trimmed_end == "end" &&
                        IsRenderedExactReturnIdentifier(trimmed_final_return, temporary_name)) {
                        const std::string_view condition =
                            TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5));
                        if (!condition.empty()) {
                            bool condition_changed = false;
                            bool base_changed = false;
                            const std::string normalized_condition =
                                NormalizeRenderedConditionExpression(condition,
                                                                     &condition_changed);
                            const std::string normalized_base =
                                NormalizeRenderedConditionExpression(composed_base_expression,
                                                                     &base_changed);
                            const std::string canonical_condition =
                                normalized_condition.empty()
                                    ? std::string(condition)
                                    : normalized_condition;
                            const std::string canonical_base =
                                normalized_base.empty()
                                    ? composed_base_expression
                                    : normalized_base;
                            if (!canonical_condition.empty() &&
                                canonical_condition == canonical_base) {
                                const std::string indent(
                                    GetRenderedLuaIndent((*lines)[line_index]));
                                (*lines)[line_index] =
                                    indent + "return ((" + canonical_condition + ") and (" +
                                    then_expression + "))";
                                lines->erase(
                                    lines->begin() +
                                        static_cast<std::ptrdiff_t>(line_index + 1),
                                    lines->begin() +
                                        static_cast<std::ptrdiff_t>(cursor + 4));
                                changed = true;
                                if (line_index != 0) {
                                    --line_index;
                                }
                                continue;
                            }
                        }
                    }
                }
            }
        }

        {
            std::string temporary_name;
            std::string base_expression;
            if (IsTemporaryAssignmentLine((*lines)[line_index],
                                          &temporary_name,
                                          &base_expression)) {
                std::size_t cursor = line_index + 1;
                std::string composed_base_expression;
                ComposeRenderedTemporaryExpressionChain(*lines,
                                                       cursor,
                                                       temporary_name,
                                                       base_expression,
                                                       &cursor,
                                                       &composed_base_expression);
                if (cursor + 3 < lines->size()) {
                    const std::string_view trimmed_if =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor]));
                    const std::string_view trimmed_end =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor + 2]));
                    const std::string_view trimmed_final_return =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor + 3]));
                    std::string then_name;
                    std::string then_expression;
                    if (trimmed_if.starts_with("if ") && trimmed_if.ends_with(" then") &&
                        IsTemporaryAssignmentLine((*lines)[cursor + 1],
                                                  &then_name,
                                                  &then_expression) &&
                        then_name == temporary_name && trimmed_end == "end" &&
                        IsRenderedExactReturnIdentifier(trimmed_final_return,
                                                        temporary_name)) {
                        const std::string_view condition =
                            TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5));
                        if (!condition.empty()) {
                            bool condition_changed = false;
                            bool base_changed = false;
                            const std::string normalized_condition =
                                NormalizeRenderedConditionExpression(condition,
                                                                     &condition_changed);
                            const std::string normalized_base =
                                NormalizeRenderedConditionExpression(composed_base_expression,
                                                                     &base_changed);
                            const std::string canonical_condition =
                                normalized_condition.empty()
                                    ? std::string(condition)
                                    : normalized_condition;
                            const std::string canonical_base =
                                normalized_base.empty()
                                    ? composed_base_expression
                                    : normalized_base;
                            if (!canonical_condition.empty() &&
                                canonical_condition == canonical_base) {
                                const std::string indent(
                                    GetRenderedLuaIndent((*lines)[line_index]));
                                (*lines)[line_index] =
                                    indent + "return ((" + canonical_condition + ") and (" +
                                    then_expression + "))";
                                lines->erase(
                                    lines->begin() +
                                        static_cast<std::ptrdiff_t>(line_index + 1),
                                    lines->begin() +
                                        static_cast<std::ptrdiff_t>(cursor + 4));
                                changed = true;
                                if (line_index != 0) {
                                    --line_index;
                                }
                                continue;
                            }
                        }
                    }
                }
            }
        }

        if (line_index + 5 < lines->size()) {
            const std::string_view trimmed_outer_if =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
            const std::string_view trimmed_inner_if =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
            const std::string_view trimmed_then_return =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
            const std::string_view trimmed_inner_end =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]));
            const std::string_view trimmed_outer_end =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 4]));
            const std::string_view trimmed_final_return =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 5]));
            if (trimmed_outer_if.starts_with("if ") && trimmed_outer_if.ends_with(" then") &&
                trimmed_inner_if.starts_with("if ") && trimmed_inner_if.ends_with(" then") &&
                StartsWithLuaKeyword(trimmed_then_return, "return") &&
                trimmed_inner_end == "end" && trimmed_outer_end == "end" &&
                StartsWithLuaKeyword(trimmed_final_return, "return")) {
                const std::string_view outer_condition = TrimLuaWhitespace(
                    trimmed_outer_if.substr(3, trimmed_outer_if.size() - 3 - 5));
                const std::string_view inner_condition = TrimLuaWhitespace(
                    trimmed_inner_if.substr(3, trimmed_inner_if.size() - 3 - 5));
                if (!outer_condition.empty() && !inner_condition.empty()) {
                    bool outer_changed = false;
                    bool inner_changed = false;
                    const std::string normalized_outer =
                        NormalizeRenderedConditionExpression(outer_condition, &outer_changed);
                    const std::string normalized_inner =
                        NormalizeRenderedConditionExpression(inner_condition, &inner_changed);
                    const std::string combined_condition = RenderLogicalExpression(
                        normalized_outer.empty() ? std::string(outer_condition)
                                                 : normalized_outer,
                        "and",
                        normalized_inner.empty() ? std::string(inner_condition)
                                                 : normalized_inner);
                    if (!combined_condition.empty()) {
                        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
                        const std::vector<std::string> replacement_lines = {
                            indent + "if " + combined_condition + " then",
                            (*lines)[line_index + 2],
                            (*lines)[line_index + 4],
                            (*lines)[line_index + 5],
                        };
                        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 6));
                        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                      replacement_lines.begin(),
                                      replacement_lines.end());
                        changed = true;
                        if (line_index != 0) {
                            --line_index;
                        }
                        continue;
                    }
                }
            }
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then") ||
            trimmed_if.size() <= 7) {
            continue;
        }

        const std::string_view condition =
            TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5));
        if (condition.empty()) {
            continue;
        }

        bool negate_changed = false;
        const std::string negated_condition =
            NegateRenderedConditionExpression(condition, &negate_changed);
        if (negated_condition.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));

        if (line_index + 3 < lines->size()) {
            const std::string_view trimmed_return =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
            const std::string_view trimmed_end =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
            const std::string_view trimmed_final_return =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]));
            if (StartsWithLuaKeyword(trimmed_return, "return") && trimmed_end == "end" &&
                StartsWithLuaKeyword(trimmed_final_return, "return")) {
                const std::string_view then_expression =
                    TrimLuaWhitespace(trimmed_return.substr(6));
                const std::string_view else_expression =
                    TrimLuaWhitespace(trimmed_final_return.substr(6));
                if (!then_expression.empty() && !else_expression.empty()) {
                    bool rewrite = false;
                    std::string replacement;
                    if (then_expression == "true" && else_expression == "false") {
                        bool normalized_changed = false;
                        replacement = indent + "return " +
                                      NormalizeRenderedConditionExpression(condition,
                                                                          &normalized_changed);
                        rewrite = true;
                    } else if (then_expression == "false" && else_expression == "true") {
                        replacement = indent + "return " + negated_condition;
                        rewrite = true;
                    } else {
                        bool normalized_else_changed = false;
                        const std::string normalized_else =
                            NormalizeRenderedConditionExpression(else_expression,
                                                                 &normalized_else_changed);
                        if (normalized_else == negated_condition) {
                            replacement = indent + "return ((" + negated_condition + ") or (" +
                                          std::string(then_expression) + "))";
                            rewrite = true;
                        }
                    }

                    if (rewrite) {
                        (*lines)[line_index] = std::move(replacement);
                        lines->erase(lines->begin() +
                                         static_cast<std::ptrdiff_t>(line_index + 1),
                                     lines->begin() +
                                         static_cast<std::ptrdiff_t>(line_index + 4));
                        changed = true;
                        if (line_index != 0) {
                            --line_index;
                        }
                        continue;
                    }
                }
            }
        }

        if (line_index + 4 >= lines->size()) {
            continue;
        }

        bool is_local = false;
        std::string temporary_name;
        std::string base_expression;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &is_local,
                                               &temporary_name,
                                               &base_expression) ||
            !is_local || !IsRenderedSafePrefixExpression(base_expression)) {
            continue;
        }

        const std::string_view trimmed_then_return =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
        const std::string_view trimmed_end =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]));
        const std::string_view trimmed_final_return =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 4]));
        if (!StartsWithLuaKeyword(trimmed_then_return, "return") || trimmed_end != "end" ||
            !IsRenderedExactReturnIdentifier(trimmed_final_return, temporary_name)) {
            continue;
        }

        const std::string_view then_expression =
            TrimLuaWhitespace(trimmed_then_return.substr(6));
        if (then_expression.empty() ||
            CountIdentifierUses(then_expression, temporary_name) == 0) {
            continue;
        }

        std::string rewritten_then_expression;
        if (!ReplaceRenderedIdentifierUses(then_expression,
                                           temporary_name,
                                           base_expression,
                                           &rewritten_then_expression)) {
            continue;
        }

        (*lines)[line_index] = indent + "return ((" + negated_condition + ") or (" +
                               rewritten_then_expression + "))";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 5));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaStateElseAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) {
            continue;
        }

        bool then_is_local = false;
        std::string then_lhs;
        std::string then_rhs;
        bool else_is_local = false;
        std::string else_lhs;
        std::string else_rhs;
        const std::string_view trimmed_end =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &then_is_local,
                                               &then_lhs,
                                               &then_rhs) ||
            then_is_local || !ParseRenderedSingleAssignmentLine((*lines)[line_index + 3],
                                                                &else_is_local,
                                                                &else_lhs,
                                                                &else_rhs) ||
            else_is_local || trimmed_end != "end" || then_lhs != else_lhs ||
            then_rhs == else_rhs) {
            continue;
        }

        if (then_lhs != "self.CurrentState" && then_lhs != "state") {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index + 2] = indent + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4),
                      indent + "end");
        changed = true;
    }

    return changed;
}

bool TryParseRenderedTypeTableCondition(std::string_view condition,
                                        std::string* tested_expression) {
    if (tested_expression == nullptr) {
        return false;
    }

    condition = TrimLuaWhitespace(condition);
    constexpr std::string_view suffix = "== \"table\"";
    if (!condition.ends_with(suffix)) {
        return false;
    }

    std::string_view lhs =
        TrimLuaWhitespace(condition.substr(0, condition.size() - suffix.size()));
    if (!lhs.starts_with("type(") || lhs.size() < 7 || lhs.back() != ')') {
        return false;
    }

    lhs = TrimLuaWhitespace(lhs.substr(5, lhs.size() - 6));
    if (lhs.empty() || !IsRenderedSafePrefixExpression(lhs)) {
        return false;
    }

    *tested_expression = std::string(lhs);
    return true;
}

bool SimplifyRenderedLuaConditionalFallbackAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (line_index + 3 < lines->size()) {
            bool initial_is_local = false;
            std::string initial_lhs;
            std::string initial_rhs;
            const std::string_view trimmed_if =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
            const std::string_view trimmed_end =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]));
            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                 &initial_is_local,
                                                 &initial_lhs,
                                                 &initial_rhs) &&
                initial_is_local && initial_rhs == "nil" &&
                trimmed_if.starts_with("if ") && trimmed_if.ends_with(" then") &&
                trimmed_end == "end" &&
                ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                                 &assign_is_local,
                                                 &assign_lhs,
                                                 &assign_rhs) &&
                !assign_is_local && assign_lhs == initial_lhs) {
                std::vector<std::string> condition_parts;
                const std::string_view condition =
                    TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5));
                if (SplitRenderedTopLevelBooleanChain(std::string(condition), "or", &condition_parts) &&
                    condition_parts.size() == 2 &&
                    IsRenderedDefinitelyTruthyExpression(condition_parts[1])) {
                    const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
                    (*lines)[line_index] = indent + "local " + initial_lhs + " = ((" +
                                           std::string(TrimLuaWhitespace(condition_parts[0])) +
                                           ") and " + assign_rhs + " or " +
                                           std::string(TrimLuaWhitespace(condition_parts[1])) + ")";
                    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                                 lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
                    changed = true;
                    if (line_index != 0) {
                        --line_index;
                    }
                    continue;
                }
            }
        }

        if (line_index + 3 < lines->size()) {
            const std::string_view trimmed_if =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
            const std::string_view trimmed_else =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
            const std::string_view trimmed_end =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]));
            bool else_is_local = false;
            std::string else_lhs;
            std::string else_rhs;
            std::string tested_expression;
            if (trimmed_if.starts_with("if ") && trimmed_if.ends_with(" then") &&
                trimmed_else == "else" && trimmed_end == "end" &&
                ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                                 &else_is_local,
                                                 &else_lhs,
                                                 &else_rhs) &&
                else_is_local && else_rhs == "false" &&
                TryParseRenderedTypeTableCondition(
                    TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5)),
                    &tested_expression)) {
                const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
                (*lines)[line_index] = indent + "local " + else_lhs + " = ((" +
                                       std::string(TrimLuaWhitespace(
                                           trimmed_if.substr(3, trimmed_if.size() - 3 - 5))) +
                                       ") and " + tested_expression + " or false)";
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                             lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
                changed = true;
                if (line_index != 0) {
                    --line_index;
                }
                continue;
            }
        }

        if (line_index + 3 < lines->size()) {
            const std::string_view trimmed_if =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
            const std::string_view trimmed_else =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
            const std::string_view trimmed_end =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
            bool call_is_local = false;
            std::vector<std::string> call_lhs_names;
            std::string call_rhs;
            if (trimmed_if.starts_with("if ") && trimmed_if.ends_with(" then") &&
                trimmed_else == "else" && trimmed_end == "end" &&
                ParseRenderedAssignmentLine((*lines)[line_index + 3],
                                            &call_is_local,
                                            &call_lhs_names,
                                            &call_rhs)) {
                const std::string condition = std::string(
                    TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5)));
                const std::string suffix = ", true)";
                if (!condition.empty() &&
                    IsRenderedSafePrefixExpression(condition) &&
                    !call_lhs_names.empty() &&
                    call_rhs.size() >= suffix.size() &&
                    call_rhs.rfind(suffix) == call_rhs.size() - suffix.size()) {
                    const std::string replacement_rhs =
                        call_rhs.substr(0, call_rhs.size() - suffix.size()) + ", " +
                        condition + ")";
                    const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 3]));
                    std::string replacement = indent;
                    if (call_is_local) {
                        replacement += "local ";
                    }
                    for (std::size_t lhs_index = 0; lhs_index < call_lhs_names.size(); ++lhs_index) {
                        if (lhs_index != 0) {
                            replacement += ", ";
                        }
                        replacement += call_lhs_names[lhs_index];
                    }
                    replacement += " = ";
                    replacement += replacement_rhs;
                    (*lines)[line_index] = std::move(replacement);
                    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                                 lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
                    changed = true;
                    if (line_index != 0) {
                        --line_index;
                    }
                    continue;
                }
            }
        }

        if (line_index + 2 >= lines->size()) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        const std::string_view trimmed_end =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then") ||
            trimmed_end != "end") {
            continue;
        }

        bool assign_is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &assign_is_local,
                                               &lhs,
                                               &rhs) ||
            !assign_is_local ||
            IsRenderedTitleCaseCandidate(lhs)) {
            continue;
        }

        std::vector<std::string> condition_parts;
        const std::string_view condition =
            TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5));
        if (!SplitRenderedTopLevelBooleanChain(std::string(condition), "or", &condition_parts) ||
            condition_parts.size() != 2) {
            continue;
        }

        const std::string_view first_part = TrimLuaWhitespace(condition_parts[0]);
        const std::string_view second_part = TrimLuaWhitespace(condition_parts[1]);
        if (!StartsWithLuaKeyword(first_part, "not") || !StartsWithLuaKeyword(second_part, "not")) {
            bool direct_assign_is_local = false;
            std::string direct_assign_lhs;
            std::string direct_assign_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                                  &direct_assign_is_local,
                                                  &direct_assign_lhs,
                                                  &direct_assign_rhs) &&
                !direct_assign_is_local &&
                IsRenderedDefinitelyTruthyExpression(second_part)) {
                const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
                (*lines)[line_index] = indent + direct_assign_lhs + " = ((" +
                                       std::string(first_part) + ") and " + direct_assign_rhs +
                                       " or " + std::string(second_part) + ")";
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                             lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
                changed = true;
                if (line_index != 0) {
                    --line_index;
                }
            }
            continue;
        }

        const std::string truthy_expression = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(second_part.substr(3)), nullptr);
        const std::string guard_expression = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(first_part.substr(3)), nullptr);
        if (truthy_expression.empty() || guard_expression.empty() ||
            !IsRenderedSafePrefixExpression(guard_expression) ||
            !IsRenderedSafePrefixExpression(truthy_expression)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "local " + lhs + " = ((" + guard_expression +
                               " and " + truthy_expression + ") or " + rhs + ")";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
        continue;
    }

    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        bool initial_is_local = false;
        std::string initial_lhs;
        std::string initial_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &initial_is_local,
                                               &initial_lhs,
                                               &initial_rhs) ||
            !initial_is_local) {
            continue;
        }

        std::size_t overwrite_index = std::string::npos;
        bool overwrite_is_local = false;
        std::string overwrite_lhs;
        std::string overwrite_rhs;
        for (std::size_t probe = line_index + 1;
             probe <= std::min(lines->size() - 1, line_index + 2);
             ++probe) {
            if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                   &overwrite_is_local,
                                                   &overwrite_lhs,
                                                   &overwrite_rhs) ||
                overwrite_is_local || overwrite_lhs != initial_lhs) {
                continue;
            }

            bool used_in_between = false;
            for (std::size_t between = line_index + 1; between < probe; ++between) {
                if (CountIdentifierUses((*lines)[between], initial_lhs) != 0) {
                    used_in_between = true;
                    break;
                }
            }
            if (used_in_between) {
                continue;
            }

            const std::string_view next_trimmed =
                probe + 1 < lines->size()
                    ? TrimLuaWhitespace(StripLuaLineComment((*lines)[probe + 1]))
                    : std::string_view{};
            if (next_trimmed != "else" && next_trimmed != "end") {
                continue;
            }

            overwrite_index = probe;
            break;
        }

        if (overwrite_index == std::string::npos || overwrite_rhs.empty()) {
            continue;
        }

        std::string guard_condition;
        bool has_guard_condition = false;
        bool invalid_guard_shape = false;
        for (std::size_t between = line_index + 1; between < overwrite_index; ++between) {
            const std::string_view between_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[between]));
            if (between_trimmed.empty()) {
                continue;
            }
            if (between_trimmed == "end" || between_trimmed == "else") {
                continue;
            }

            if (!between_trimmed.starts_with("if ") || !between_trimmed.ends_with(" then")) {
                continue;
            }
            if (has_guard_condition) {
                invalid_guard_shape = true;
                break;
            }
            guard_condition = StripRenderedWholeParentheses(
                TrimLuaWhitespace(
                    between_trimmed.substr(3, between_trimmed.size() - 3 - 5)),
                nullptr);
            has_guard_condition = !guard_condition.empty();
        }
        if (invalid_guard_shape) {
            continue;
        }

        if (has_guard_condition) {
            const std::string normalized_overwrite_rhs = StripRenderedWholeParentheses(
                TrimLuaWhitespace(overwrite_rhs), nullptr);
            if (guard_condition != normalized_overwrite_rhs) {
                continue;
            }
        }

        if (IsRenderedDefinitelyTruthyExpression(overwrite_rhs)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "local " + initial_lhs + " = (" + overwrite_rhs +
                               " or " + initial_rhs + ")";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(overwrite_index));
        changed = true;
    }

    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        const std::string_view trimmed_end =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then") ||
            trimmed_end != "end") {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local) {
            continue;
        }

        const std::string_view condition =
            TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5));
        const std::size_t or_pos = condition.rfind(" or ");
        if (or_pos == std::string_view::npos) {
            continue;
        }

        const std::string_view left = TrimLuaWhitespace(condition.substr(0, or_pos));
        const std::string_view right = TrimLuaWhitespace(condition.substr(or_pos + 4));
        if (left.empty() || right.empty() || !IsRenderedDefinitelyTruthyExpression(right)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index] = indent + assign_lhs + " = ((" + std::string(left) + ") and " +
                               assign_rhs + " or " + std::string(right) + ")";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaSelfReferentialFallbackAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &lhs,
                                               &rhs) ||
            !assign_is_local || lhs.empty()) {
            continue;
        }

        std::vector<std::string> rhs_parts;
        const std::string normalized_rhs = StripRenderedWholeParentheses(rhs, nullptr);
        if (!SplitRenderedTopLevelBooleanChain(normalized_rhs, "or", &rhs_parts) ||
            rhs_parts.size() != 2) {
            continue;
        }

        const std::string left = StripRenderedWholeParentheses(rhs_parts[0], nullptr);
        const std::string right = StripRenderedWholeParentheses(rhs_parts[1], nullptr);
        const std::string self_prefix = lhs + ".";
        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        std::vector<std::string> fallback_parts;
        if (SplitRenderedTopLevelBooleanChain(right, "or", &fallback_parts) &&
            fallback_parts.size() == 2) {
            const std::string self_reference =
                StripRenderedWholeParentheses(fallback_parts[0], nullptr);
            const std::string fallback_base =
                StripRenderedWholeParentheses(fallback_parts[1], nullptr);
            if (!self_reference.starts_with(self_prefix) ||
                !IsRenderedSafePrefixExpression(fallback_base)) {
                continue;
            }

            const std::string member_suffix = self_reference.substr(lhs.size());
            if (member_suffix.empty()) {
                continue;
            }

            (*lines)[line_index] = indent + "local " + lhs + " = ((" + left + ") or " +
                                   fallback_base + member_suffix + ")";
            changed = true;
            continue;
        }

        if (!left.starts_with(self_prefix) || !IsRenderedSafePrefixExpression(right)) {
            continue;
        }

        const std::string member_suffix = left.substr(lhs.size());
        if (member_suffix.empty()) {
            continue;
        }

        (*lines)[line_index] =
            indent + "local " + lhs + " = " + right + member_suffix;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaRedundantConditionLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &lhs,
                                               &rhs) ||
            !assign_is_local) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) {
            continue;
        }

        const std::string condition = StripRenderedWholeParentheses(
            TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5)),
            nullptr);
        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs), nullptr);
        if (condition.empty() || normalized_rhs.empty() || condition != normalized_rhs) {
            continue;
        }

        bool used_after = false;
        for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
            if (CountIdentifierUses((*lines)[probe], lhs) != 0) {
                used_after = true;
                break;
            }
        }
        if (used_after) {
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaBranchLocalAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) {
            continue;
        }

        std::size_t end_index = std::string::npos;
        if (line_index + 2 < lines->size() &&
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) == "end") {
            end_index = line_index + 2;
        } else if (line_index + 3 < lines->size() &&
                   TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) == "end") {
            end_index = line_index + 3;
        } else {
            continue;
        }

        bool local_assign_is_local = false;
        std::string local_assign_lhs;
        std::string local_assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &local_assign_is_local,
                                               &local_assign_lhs,
                                               &local_assign_rhs) ||
            !local_assign_is_local) {
            continue;
        }

        for (std::size_t probe = line_index + 2; probe < end_index; ++probe) {
            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                   &probe_is_local,
                                                   &probe_lhs,
                                                   &probe_rhs) ||
                probe_is_local || probe_lhs != local_assign_lhs) {
                end_index = std::string::npos;
                break;
            }
        }
        if (end_index == std::string::npos || end_index + 1 >= lines->size()) {
            continue;
        }

        bool used_after = false;
        for (std::size_t probe = end_index + 1;
             probe < std::min(lines->size(), end_index + 4);
             ++probe) {
            if (CountIdentifierUses((*lines)[probe], local_assign_lhs) != 0) {
                used_after = true;
                break;
            }
        }
        if (!used_after) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      outer_indent + "local " + local_assign_lhs);
        (*lines)[line_index + 2] =
            inner_indent + local_assign_lhs + " = " + local_assign_rhs;
        changed = true;
        ++line_index;
    }

    return changed;
}

bool SimplifyRenderedLuaBranchShadowedLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto begins_block = [](std::string_view trimmed) -> bool {
        return (trimmed.starts_with("if ") && trimmed.ends_with(" then")) ||
               (trimmed.starts_with("for ") && trimmed.ends_with(" do")) ||
               (trimmed.starts_with("while ") && trimmed.ends_with(" do")) ||
               trimmed == "do" ||
               trimmed == "repeat" ||
               trimmed.starts_with("function ") ||
               trimmed.starts_with("local function ");
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        bool decl_is_local = false;
        std::vector<std::string> decl_lhs_names;
        std::string decl_rhs;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &decl_is_local,
                                         &decl_lhs_names,
                                         &decl_rhs) ||
            !decl_is_local || decl_lhs_names.empty()) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) {
            continue;
        }

        std::size_t end_index = std::string::npos;
        int depth = 1;
        for (std::size_t probe = line_index + 2; probe < lines->size(); ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (trimmed == "end") {
                --depth;
                if (depth == 0) {
                    end_index = probe;
                    break;
                }
                continue;
            }
            if (trimmed.starts_with("until ")) {
                --depth;
                if (depth == 0) {
                    end_index = probe;
                    break;
                }
                continue;
            }
            if (begins_block(trimmed)) {
                ++depth;
            }
        }
        if (end_index == std::string::npos) {
            continue;
        }

        depth = 1;
        for (std::size_t probe = line_index + 2; probe < end_index; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }

            if (depth == 1) {
                bool assign_is_local = false;
                std::string assign_lhs;
                std::string assign_rhs;
                if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                      &assign_is_local,
                                                      &assign_lhs,
                                                      &assign_rhs) &&
                    assign_is_local &&
                    std::find(decl_lhs_names.begin(), decl_lhs_names.end(), assign_lhs) !=
                        decl_lhs_names.end()) {
                    const std::string indent(GetRenderedLuaIndent((*lines)[probe]));
                    (*lines)[probe] = indent + assign_lhs + " = " + assign_rhs;
                    changed = true;
                }
            }

            if (trimmed == "end") {
                --depth;
                continue;
            }
            if (trimmed.starts_with("until ")) {
                --depth;
                continue;
            }
            if (begins_block(trimmed)) {
                ++depth;
            }
        }
    }

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool outer_is_local = false;
        std::string outer_lhs;
        std::string outer_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &outer_is_local,
                                               &outer_lhs,
                                               &outer_rhs) ||
            !outer_is_local || !IsValidLuaIdentifier(outer_lhs)) {
            continue;
        }

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        if (scope_end <= line_index || scope_end >= lines->size()) {
            continue;
        }

        int depth = 0;
        for (std::size_t probe = line_index + 1; probe < scope_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }

            if (trimmed == "end" || trimmed.starts_with("until ")) {
                depth = std::max(0, depth - 1);
                continue;
            }

            if (depth == 0 && IsAssignmentToIdentifier((*lines)[probe], outer_lhs)) {
                break;
            }

            bool inner_is_local = false;
            std::string inner_lhs;
            std::string inner_rhs;
            if (depth >= 1 &&
                ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                  &inner_is_local,
                                                  &inner_lhs,
                                                  &inner_rhs) &&
                inner_is_local && inner_lhs == outer_lhs &&
                CountIdentifierUses(inner_rhs, outer_lhs) == 0) {
                const int nested_depth = depth;
                int look_depth = depth;
                bool used_after_nested_block = false;
                bool first_touch_after_nested_is_assignment = false;
                for (std::size_t cursor = probe + 1; cursor < scope_end; ++cursor) {
                    const std::string_view cursor_trimmed =
                        TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor]));
                    if (cursor_trimmed.empty()) {
                        continue;
                    }

                    if (cursor_trimmed == "end" || cursor_trimmed.starts_with("until ")) {
                        look_depth = std::max(0, look_depth - 1);
                        continue;
                    }

                    if (look_depth < nested_depth) {
                        if (IsRenderedShadowingLocalDeclaration((*lines)[cursor], outer_lhs)) {
                            break;
                        }
                        if (IsAssignmentToIdentifier((*lines)[cursor], outer_lhs)) {
                            first_touch_after_nested_is_assignment = true;
                            break;
                        }
                        if (CountIdentifierUses((*lines)[cursor], outer_lhs) != 0) {
                            used_after_nested_block = true;
                            break;
                        }
                    }

                    if (begins_block(cursor_trimmed)) {
                        ++look_depth;
                    }
                }

                if (used_after_nested_block || first_touch_after_nested_is_assignment) {
                    const std::string indent(GetRenderedLuaIndent((*lines)[probe]));
                    (*lines)[probe] = indent + inner_lhs + " = " + inner_rhs;
                    changed = true;
                }
            }

            if (begins_block(trimmed)) {
                ++depth;
            }
        }
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedSourceLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        std::vector<std::string> local_names;
        if (!ParseRenderedLocalDeclarationLine((*lines)[line_index], &local_names) ||
            local_names.size() != 1) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!trimmed_if.starts_with("if not ") || !trimmed_if.ends_with(" then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end") {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local || assign_lhs != local_names.front()) {
            continue;
        }

        const std::string source_expression = StripRenderedWholeParentheses(
            TrimLuaWhitespace(trimmed_if.substr(7, trimmed_if.size() - 7 - 5)),
            nullptr);
        if (source_expression.empty() ||
            CountIdentifierUses(source_expression, assign_lhs) != 0 ||
            CountIdentifierUses(assign_rhs, assign_lhs) != 0) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent(GetRenderedLuaIndent((*lines)[line_index + 2]));
        (*lines)[line_index] = indent + "local " + assign_lhs + " = " + source_expression;
        (*lines)[line_index + 1] = indent + "if not " + assign_lhs + " then";
        (*lines)[line_index + 2] = inner_indent + assign_lhs + " = " + assign_rhs;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedSelfAssignmentSetters(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto normalize_identifier = [](std::string_view value) -> std::string {
        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(value), nullptr);
        if (!IsValidLuaIdentifier(normalized)) {
            return {};
        }
        return normalized;
    };

    const auto extract_base_identifier = [](std::string_view value) -> std::string {
        value = TrimLuaWhitespace(value);
        std::size_t end = 0;
        while (end < value.size()) {
            const char ch = value[end];
            if ((std::isalnum(static_cast<unsigned char>(ch)) != 0) || ch == '_') {
                ++end;
                continue;
            }
            break;
        }
        if (end == 0) {
            return {};
        }

        const std::string_view base = value.substr(0, end);
        if (!IsValidLuaIdentifier(base)) {
            return {};
        }
        return std::string(base);
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            seed_is_local) {
            continue;
        }

        const std::string seed_rhs_identifier = normalize_identifier(seed_rhs);
        if (seed_rhs_identifier.empty()) {
            continue;
        }

        const std::string seed_base = extract_base_identifier(seed_lhs);
        if (seed_base.empty()) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!trimmed_if.starts_with("if not ") || !trimmed_if.ends_with(" then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end") {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local) {
            continue;
        }

        const std::string normalized_assign_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_lhs), nullptr);
        const std::string normalized_assign_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        if (normalized_assign_lhs.empty() || normalized_assign_lhs != normalized_assign_rhs) {
            continue;
        }

        const std::string assign_base = extract_base_identifier(assign_lhs);
        if (assign_base.empty() || assign_base != seed_base) {
            continue;
        }

        const std::string guarded_parameter = normalize_identifier(
            trimmed_if.substr(7, trimmed_if.size() - 7 - 5));
        if (guarded_parameter.empty()) {
            continue;
        }

        const std::string rewritten_condition = guarded_parameter + " ~= nil";
        const std::string rewritten_rhs = guarded_parameter;

        if (rewritten_rhs == normalized_assign_lhs ||
            CountIdentifierUses(rewritten_condition, normalized_assign_lhs) != 0 ||
            CountIdentifierUses(rewritten_rhs, normalized_assign_lhs) != 0) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        const std::string inner_indent(GetRenderedLuaIndent((*lines)[line_index + 2]));
        (*lines)[line_index + 1] = indent + "if " + rewritten_condition + " then";
        (*lines)[line_index + 2] = inner_indent + assign_lhs + " = " + rewritten_rhs;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaSelfOrAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local) {
            continue;
        }

        const std::string normalized_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_lhs), nullptr);
        std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        if (normalized_lhs.empty() || normalized_rhs.empty()) {
            continue;
        }

        std::vector<std::string> rhs_parts;
        if (!SplitRenderedTopLevelBooleanChain(normalized_rhs, "or", &rhs_parts) ||
            rhs_parts.size() != 2) {
            continue;
        }

        const std::string guarded_parameter =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[0]), nullptr);
        const std::string fallback_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[1]), nullptr);
        if (!IsValidLuaIdentifier(guarded_parameter) ||
            fallback_expression != normalized_lhs ||
            CountIdentifierUses(guarded_parameter, normalized_lhs) != 0) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = indent + "  ";
        (*lines)[line_index] = indent + "if " + guarded_parameter + " ~= nil then";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      inner_indent + normalized_lhs + " = " + guarded_parameter);
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2),
                      indent + "end");
        changed = true;
        line_index += 2;
    }

    return changed;
}

bool SimplifyRenderedLuaNilSeededSelfFallbackAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_nil_seed_for = [&](std::size_t line_index,
                                     std::string_view name) -> bool {
        if (line_index >= lines->size() || name.empty()) {
            return false;
        }

        std::vector<std::string> local_names;
        if (ParseRenderedLocalDeclarationLine((*lines)[line_index], &local_names)) {
            return std::find(local_names.begin(), local_names.end(), name) != local_names.end();
        }

        bool seed_is_local = false;
        std::vector<std::string> seed_lhs_names;
        std::string seed_rhs_expression;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &seed_is_local,
                                         &seed_lhs_names,
                                         &seed_rhs_expression) ||
            TrimLuaWhitespace(seed_rhs_expression) != "nil") {
            return false;
        }

        return std::find(seed_lhs_names.begin(), seed_lhs_names.end(), name) !=
               seed_lhs_names.end();
    };

    bool changed = false;
    for (std::size_t line_index = 1; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !IsValidLuaIdentifier(assign_lhs) ||
            !is_nil_seed_for(line_index - 1, assign_lhs)) {
            continue;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        std::vector<std::string> rhs_parts;
        if (!SplitRenderedTopLevelBooleanChain(normalized_rhs, "or", &rhs_parts) ||
            rhs_parts.size() != 2) {
            continue;
        }

        const std::string primary_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[0]), nullptr);
        const std::string fallback_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[1]), nullptr);
        if (primary_expression.empty() || fallback_expression != assign_lhs ||
            CountIdentifierUses(primary_expression, assign_lhs) != 0) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string assignment_prefix =
            assign_is_local ? "local " + assign_lhs + " = " : assign_lhs + " = ";
        (*lines)[line_index] = indent + assignment_prefix + primary_expression;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaSeededShadowedLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !seed_is_local || !IsValidLuaIdentifier(seed_lhs)) {
            continue;
        }

        const std::string source_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr);
        if (source_expression.empty() || !IsRenderedSafePrefixExpression(source_expression)) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!trimmed_if.starts_with("if not ") || !trimmed_if.ends_with(" then") ||
            StripRenderedWholeParentheses(
                TrimLuaWhitespace(trimmed_if.substr(7, trimmed_if.size() - 7 - 5)),
                nullptr) != seed_lhs) {
            continue;
        }

        bool default_is_local = false;
        std::string default_lhs;
        std::string default_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &default_is_local,
                                               &default_lhs,
                                               &default_rhs) ||
            default_is_local || default_lhs != seed_lhs ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end") {
            continue;
        }

        bool shadow_is_local = false;
        std::string shadow_lhs;
        std::string shadow_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 4],
                                               &shadow_is_local,
                                               &shadow_lhs,
                                               &shadow_rhs) ||
            !shadow_is_local || shadow_lhs != seed_lhs ||
            CountIdentifierUses(shadow_rhs, seed_lhs) != 0) {
            continue;
        }

        std::string rewritten_rhs;
        if (!ReplaceRenderedSafePrefixExpressionUses(shadow_rhs,
                                                     source_expression,
                                                     seed_lhs,
                                                     &rewritten_rhs)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 4]));
        (*lines)[line_index + 4] = indent + seed_lhs + " = " + rewritten_rhs;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedDefaultLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if not ") || !trimmed_if.ends_with(" then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !assign_is_local) {
            continue;
        }

        const std::string condition_expression = StripRenderedWholeParentheses(
            TrimLuaWhitespace(trimmed_if.substr(7, trimmed_if.size() - 7 - 5)),
            nullptr);
        if (condition_expression.empty() ||
            CountIdentifierUses(condition_expression, assign_lhs) != 0 ||
            CountIdentifierUses(assign_rhs, assign_lhs) != 0) {
            continue;
        }

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        const std::size_t probe_limit =
            scope_end < lines->size() ? scope_end + 1 : lines->size();

        bool used_after = false;
        for (std::size_t probe = line_index + 3; probe < probe_limit; ++probe) {
            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], assign_lhs) ||
                IsAssignmentToIdentifier((*lines)[probe], assign_lhs)) {
                break;
            }
            if (CountIdentifierUses((*lines)[probe], assign_lhs) != 0) {
                used_after = true;
                break;
            }
        }
        if (!used_after) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + "local " + assign_lhs + " = (" + condition_expression + " or " +
            assign_rhs + ")";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
    }

    return changed;
}

#include "rendered/rendered_lua_text_guard_cleanup.inc"

bool SimplifyRenderedLuaGuardedMemberNonemptySelections(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_empty_string_equality = [](std::string_view expression,
                                                std::string* member_expression) -> bool {
        if (member_expression == nullptr) {
            return false;
        }

        expression = TrimLuaWhitespace(expression);
        const std::size_t eq_pos = expression.find(" == ");
        if (eq_pos == std::string_view::npos) {
            return false;
        }

        const std::string left = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(expression.substr(0, eq_pos)),
            nullptr);
        const std::string right = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(expression.substr(eq_pos + 4)),
            nullptr);
        if (left.empty() || right.empty()) {
            return false;
        }

        if (left == "\"\"" || left == "''") {
            *member_expression = right;
            return true;
        }
        if (right == "\"\"" || right == "''") {
            *member_expression = left;
            return true;
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_lhs.empty()) {
            continue;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        std::vector<std::string> parts;
        if (!SplitRenderedTopLevelBooleanChain(normalized_rhs, "or", &parts) ||
            parts.size() != 4) {
            continue;
        }

        const std::string fallback =
            NormalizeRenderedConditionOperand(TrimLuaWhitespace(parts[3]), nullptr);
        if (fallback != "nil") {
            continue;
        }

        const std::string base_expression =
            NormalizeRenderedConditionOperand(TrimLuaWhitespace(parts[0]), nullptr);
        if (base_expression.empty() ||
            StartsWithLuaKeyword(base_expression, "not")) {
            continue;
        }

        std::string member_expression;
        if (!parse_empty_string_equality(parts[1], &member_expression) ||
            member_expression.empty() ||
            CountIdentifierUses(member_expression, base_expression) == 0) {
            continue;
        }

        const std::string normalized_negated =
            NormalizeRenderedConditionExpression(TrimLuaWhitespace(parts[2]), nullptr);
        if (normalized_negated != ("not " + member_expression)) {
            continue;
        }

        const std::string_view base_view = TrimLuaWhitespace(base_expression);
        const std::string_view member_view = TrimLuaWhitespace(member_expression);
        const bool member_uses_base =
            member_view.starts_with(std::string(base_view) + ".") ||
            member_view.starts_with(std::string(base_view) + "[") ||
            member_view.starts_with(std::string(base_view) + ":");
        if (!member_uses_base) {
            continue;
        }

        const std::string member_nonempty = member_expression + " ~= \"\"";
        const std::string rebuilt = RenderLogicalExpression(
            RenderLogicalExpression(
                RenderLogicalExpression(base_expression, "and", member_nonempty),
                "and",
                member_expression),
            "or",
            "nil");

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + (assign_is_local ? "local " : "") + assign_lhs + " = " + rebuilt;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaNegatedSelfFallbackAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_lhs.empty()) {
            continue;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        std::vector<std::string> and_parts;
        if (!SplitRenderedTopLevelBooleanChain(normalized_rhs, "and", &and_parts) ||
            and_parts.size() != 2) {
            continue;
        }

        const std::string left = NormalizeRenderedConditionExpression(
            TrimLuaWhitespace(and_parts[0]),
            nullptr);
        const std::string right = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(and_parts[1]),
            nullptr);
        if (left != ("not " + assign_lhs) || right.empty() ||
            !IsRenderedSafePrefixExpression(right)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + (assign_is_local ? "local " : "") + assign_lhs + " = " +
            RenderLogicalExpression(assign_lhs, "or", right);
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaBooleanCoercionSelections(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto extract_boolean_coercion = [](std::string_view expression,
                                             std::string* coerced_expression) -> bool {
        if (coerced_expression == nullptr) {
            return false;
        }

        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        std::vector<std::string> or_parts;
        if (!SplitRenderedTopLevelBooleanChain(normalized, "or", &or_parts) ||
            or_parts.size() != 2 ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(or_parts[1]), nullptr) != "false") {
            return false;
        }

        const std::string guarded_true_branch =
            StripRenderedWholeParentheses(TrimLuaWhitespace(or_parts[0]), nullptr);
        std::vector<std::string> and_parts;
        if (!SplitRenderedTopLevelBooleanChain(guarded_true_branch, "and", &and_parts) ||
            and_parts.size() != 2 ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(and_parts[1]), nullptr) != "true") {
            return false;
        }

        const std::string left =
            StripRenderedWholeParentheses(TrimLuaWhitespace(and_parts[0]), nullptr);
        if (!StartsWithLuaKeyword(left, "not")) {
            return false;
        }
        std::string inner = NormalizeRenderedConditionExpression(
            TrimLuaWhitespace(left.substr(3)),
            nullptr);
        if (!StartsWithLuaKeyword(inner, "not")) {
            return false;
        }
        inner = NormalizeRenderedConditionExpression(
            TrimLuaWhitespace(inner.substr(3)),
            nullptr);
        if (inner.empty()) {
            return false;
        }

        *coerced_expression =
            RenderUnaryExpression("not", RenderUnaryExpression("not", std::move(inner)));
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_lhs.empty()) {
            continue;
        }

        std::vector<std::string> and_parts;
        if (!SplitRenderedTopLevelBooleanChain(assign_rhs, "and", &and_parts) ||
            and_parts.size() != 2) {
            continue;
        }

        const std::string guard_expression = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(and_parts[0]),
            nullptr);
        std::string coerced_expression;
        if (guard_expression.empty() ||
            !IsRenderedSafePrefixExpression(guard_expression) ||
            !extract_boolean_coercion(and_parts[1], &coerced_expression)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + (assign_is_local ? "local " : "") + assign_lhs + " = " +
            RenderLogicalExpression(guard_expression, "and", coerced_expression);
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaBooleanCoercionReturns(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_single_return_expression = [](std::string_view line,
                                                   std::string* expression) -> bool {
        if (expression == nullptr) {
            return false;
        }

        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!StartsWithLuaKeyword(trimmed, "return")) {
            return false;
        }

        const std::string_view rest = TrimLuaWhitespace(trimmed.substr(6));
        if (rest.empty()) {
            return false;
        }

        *expression = std::string(rest);
        return true;
    };
    const auto extract_boolean_coercion = [](std::string_view expression,
                                             std::string* coerced_expression) -> bool {
        if (coerced_expression == nullptr) {
            return false;
        }

        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        std::vector<std::string> or_parts;
        if (!SplitRenderedTopLevelBooleanChain(normalized, "or", &or_parts) ||
            or_parts.size() != 2 ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(or_parts[1]), nullptr) != "false") {
            return false;
        }

        const std::string guarded_true_branch =
            StripRenderedWholeParentheses(TrimLuaWhitespace(or_parts[0]), nullptr);
        std::vector<std::string> and_parts;
        if (!SplitRenderedTopLevelBooleanChain(guarded_true_branch, "and", &and_parts) ||
            and_parts.size() != 2 ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(and_parts[1]), nullptr) != "true") {
            return false;
        }

        const std::string condition = NormalizeRenderedConditionExpression(
            TrimLuaWhitespace(and_parts[0]),
            nullptr);
        if (condition.empty()) {
            return false;
        }

        *coerced_expression = condition;
        return true;
    };
    const auto simplify_boolean_coercion_expression =
        [&](std::string_view expression, std::string* simplified_expression) -> bool {
            if (simplified_expression == nullptr) {
                return false;
            }

            std::string direct_coercion;
            if (extract_boolean_coercion(expression, &direct_coercion)) {
                *simplified_expression = std::move(direct_coercion);
                return true;
            }

            const std::string normalized =
                StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
            std::vector<std::string> or_parts;
            if (!SplitRenderedTopLevelBooleanChain(normalized, "or", &or_parts) ||
                or_parts.size() < 2) {
                return false;
            }

            bool changed = false;
            std::string rebuilt;
            for (std::size_t index = 0; index < or_parts.size(); ++index) {
                std::string simplified_part;
                if (extract_boolean_coercion(or_parts[index], &simplified_part)) {
                    changed = true;
                } else {
                    simplified_part =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(or_parts[index]), nullptr);
                }

                if (index == 0) {
                    rebuilt = std::move(simplified_part);
                } else {
                    rebuilt = RenderLogicalExpression(rebuilt, "or", simplified_part);
                }
            }

            if (!changed || rebuilt.empty()) {
                return false;
            }

            *simplified_expression = std::move(rebuilt);
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string return_expression;
        if (!parse_single_return_expression((*lines)[line_index], &return_expression)) {
            continue;
        }

        std::string simplified_expression;
        if (!simplify_boolean_coercion_expression(return_expression, &simplified_expression) ||
            simplified_expression.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "return " + simplified_expression;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedCollectionAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_collection_use = [](std::string_view line,
                                      std::string_view identifier) -> bool {
        const std::string stripped(StripLuaLineComment(line));
        if (FindIdentifierUse(stripped, identifier) == std::string_view::npos) {
            return false;
        }
        const std::string name(identifier);
        return stripped.find("ipairs(" + name + ")") != std::string::npos ||
               stripped.find("pairs(" + name + ")") != std::string::npos ||
               stripped.find("table.insert(" + name + ",") != std::string::npos ||
               stripped.find("table.remove(" + name + ",") != std::string::npos ||
               stripped.find('#' + name) != std::string::npos;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) {
            continue;
        }
        if (TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }

        bool assign_is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &assign_is_local,
                                               &lhs,
                                               &rhs) ||
            !assign_is_local) {
            continue;
        }

        bool collection_used_after = false;
        for (std::size_t probe = line_index + 3; probe < lines->size(); ++probe) {
            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                  &probe_is_local,
                                                  &probe_lhs,
                                                  &probe_rhs) &&
                probe_lhs == lhs) {
                break;
            }
            if (is_collection_use((*lines)[probe], lhs)) {
                collection_used_after = true;
                break;
            }
        }
        if (!collection_used_after) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      outer_indent + "local " + lhs + " = {}");
        (*lines)[line_index + 2] = inner_indent + lhs + " = " + rhs;
        changed = true;
        ++line_index;
    }

    return changed;
}

bool SimplifyRenderedLuaUndeclaredCollectionLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_collection_use = [](std::string_view line,
                                      std::string_view identifier) -> bool {
        const std::string stripped(StripLuaLineComment(line));
        if (FindIdentifierUse(stripped, identifier) == std::string_view::npos) {
            return false;
        }
        const std::string name(identifier);
        return stripped.find("ipairs(" + name + ")") != std::string::npos ||
               stripped.find("pairs(" + name + ")") != std::string::npos ||
               stripped.find("table.insert(" + name + ",") != std::string::npos ||
               stripped.find("table.remove(" + name + ",") != std::string::npos ||
               stripped.find(name + "[") != std::string::npos ||
               stripped.find('#' + name) != std::string::npos;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string lhs;
        std::string rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &lhs,
                                               &rhs) ||
            assign_is_local ||
            IsRenderedTitleCaseCandidate(lhs) ||
            TrimLuaWhitespace(rhs) != "{}" ||
            !ShouldEmitLocalForRenderedIdentifierTarget(*lines, line_index, lhs)) {
            continue;
        }

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        if (scope_end >= lines->size()) {
            continue;
        }

        bool collection_used_after = false;
        for (std::size_t probe = line_index + 1; probe < scope_end; ++probe) {
            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], lhs)) {
                break;
            }
            if (is_collection_use((*lines)[probe], lhs)) {
                collection_used_after = true;
                break;
            }
        }
        if (!collection_used_after) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "local " + lhs + " = {}";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaEmptyGuardedSeedAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_call_expression = [](std::string_view expression,
                                          std::string* callee,
                                          std::vector<std::string>* arguments) -> bool {
        if (callee == nullptr || arguments == nullptr) {
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

        std::size_t close = std::string_view::npos;
        if (!FindRenderedMatchingParen(expression, open, &close) ||
            close + 1 != expression.size()) {
            return false;
        }

        const std::string_view callee_view = TrimLuaWhitespace(expression.substr(0, open));
        if (callee_view.empty()) {
            return false;
        }

        *callee = std::string(callee_view);
        std::string_view value =
            TrimLuaWhitespace(expression.substr(open + 1, close - open - 1));
        arguments->clear();

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
                    arguments->push_back(std::string(
                        TrimLuaWhitespace(value.substr(part_begin, i - part_begin))));
                    part_begin = i + 1;
                }
                break;
            default:
                break;
            }
        }

        arguments->push_back(std::string(TrimLuaWhitespace(value.substr(part_begin))));
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if not ") || !trimmed_if.ends_with(" then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1])) != "end") {
            continue;
        }

        const std::string target = StripRenderedWholeParentheses(
            TrimLuaWhitespace(trimmed_if.substr(7, trimmed_if.size() - 7 - 5)), nullptr);
        if (target.empty()) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local || assign_lhs != target) {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!parse_call_expression(assign_rhs, &callee, &arguments) || arguments.size() < 2) {
            continue;
        }

        const std::string first_argument = StripRenderedWholeParentheses(
            TrimLuaWhitespace(arguments[0]), nullptr);
        const std::string seed_expression = StripRenderedWholeParentheses(
            TrimLuaWhitespace(arguments[1]), nullptr);
        if (first_argument != target || seed_expression.empty() ||
            CountIdentifierUses(seed_expression, target) != 0) {
            continue;
        }

        bool later_argument_uses_target = false;
        for (std::size_t arg_index = 1; arg_index < arguments.size(); ++arg_index) {
            if (CountIdentifierUses(arguments[arg_index], target) != 0) {
                later_argument_uses_target = true;
                break;
            }
        }
        if (later_argument_uses_target) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + target + " = (" + target + " or " + seed_expression + ")";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        changed = true;
    }

    return changed;
}

bool TryParseRenderedTrailingCall(std::string_view line,
                                  std::string_view* callee,
                                  std::string_view* arguments);

bool SimplifyRenderedLuaImmediateCallLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_single_call_expression = [](std::string_view line,
                                                 std::string* callee,
                                                 std::string* arguments_text) -> bool {
        if (callee == nullptr || arguments_text == nullptr) {
            return false;
        }

        std::string_view callee_view;
        std::string_view arguments_view;
        if (!TryParseRenderedTrailingCall(line, &callee_view, &arguments_view)) {
            return false;
        }

        *callee = std::string(callee_view);
        *arguments_text = std::string(TrimLuaWhitespace(arguments_view));
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !seed_is_local || !IsValidLuaIdentifier(seed_lhs) ||
            !IsRenderedSafePrefixExpression(seed_rhs) ||
            CountIdentifierUses(seed_rhs, seed_lhs) != 0) {
            continue;
        }

        bool call_is_local = false;
        std::string call_lhs;
        std::string call_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &call_is_local,
                                               &call_lhs,
                                               &call_rhs) ||
            call_is_local || call_lhs != seed_lhs) {
            continue;
        }

        std::string callee;
        std::string arguments_text;
        if (!parse_single_call_expression(call_rhs, &callee, &arguments_text) ||
            callee != seed_lhs) {
            continue;
        }

        if (CountIdentifierUses(arguments_text, seed_lhs) != 0) {
            continue;
        }

        const std::size_t open = call_rhs.find('(');
        const std::size_t close = call_rhs.rfind(')');
        if (open == std::string::npos || close == std::string::npos || close < open) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + "local " + seed_lhs + " = " + seed_rhs +
            call_rhs.substr(open, close - open + 1);
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateShadowedLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool first_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        bool second_is_local = false;
        std::string second_lhs;
        std::string second_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &first_is_local,
                                               &first_lhs,
                                               &first_rhs) ||
            !ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &second_is_local,
                                               &second_lhs,
                                               &second_rhs) ||
            !first_is_local || !second_is_local || first_lhs != second_lhs) {
            continue;
        }

        const std::string first_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string second_indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        if (first_indent != second_indent) {
            continue;
        }

        (*lines)[line_index + 1] = second_indent + second_lhs + " = " + second_rhs;
        changed = true;
    }

    return changed;
}

#include "rendered/rendered_lua_text_local_cleanup.inc"
#include "rendered/rendered_lua_text_receiver_cleanup.inc"
#include "rendered/rendered_lua_text_return_cleanup.inc"

#include "rendered/families/rendered_lua_text_temporary_alias_cleanup.inc"

bool TryExtractRenderedIfCondition(std::string_view line, std::string* condition) {
    if (condition == nullptr) {
        return false;
    }

    line = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!line.starts_with("if ") || !line.ends_with(" then")) {
        return false;
    }

    *condition = std::string(TrimLuaWhitespace(line.substr(3, line.size() - 3 - 5)));
    return !condition->empty();
}

bool TryExtractRenderedWhileCondition(std::string_view line, std::string* condition) {
    if (condition == nullptr) {
        return false;
    }

    line = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!line.starts_with("while ") || !line.ends_with(" do")) {
        return false;
    }

    *condition = std::string(TrimLuaWhitespace(line.substr(6, line.size() - 6 - 3)));
    return !condition->empty();
}

bool SimplifyRenderedLuaEmptyGuardedCarryAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1])) != "end") {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr) != "nil") {
            continue;
        }

        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition)) {
            continue;
        }

        std::vector<std::string> condition_parts;
        if (!SplitRenderedTopLevelBooleanChain(condition, "or", &condition_parts) ||
            condition_parts.size() != 2) {
            continue;
        }

        const std::string_view left = TrimLuaWhitespace(condition_parts[0]);
        if (!StartsWithLuaKeyword(left, "not")) {
            continue;
        }

        const std::string guard_expression = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(left.substr(3)), nullptr);
        const std::string carry_expression = StripRenderedWholeParentheses(
            TrimLuaWhitespace(condition_parts[1]), nullptr);
        const std::string normalized_lhs = StripRenderedWholeParentheses(
            TrimLuaWhitespace(assign_lhs), nullptr);
        if (guard_expression.empty() || carry_expression.empty() || normalized_lhs.empty() ||
            CountIdentifierUses(guard_expression, normalized_lhs) != 0 ||
            CountIdentifierUses(carry_expression, normalized_lhs) != 0) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + normalized_lhs + " = ((" + guard_expression +
                               " and " + carry_expression + ") or " + normalized_lhs + ")";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedLoopFallbackAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_pairs_loop_header = [](std::string_view line,
                                            std::string* key_name,
                                            std::string* value_name) -> bool {
        if (key_name == nullptr || value_name == nullptr) {
            return false;
        }

        line = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!line.starts_with("for ") || !line.ends_with(" do")) {
            return false;
        }

        const std::size_t in_pos = line.find(" in ");
        if (in_pos == std::string_view::npos) {
            return false;
        }

        const std::string_view lhs = TrimLuaWhitespace(line.substr(4, in_pos - 4));
        const std::string_view rhs =
            TrimLuaWhitespace(line.substr(in_pos + 4, line.size() - (in_pos + 4) - 3));
        if (!rhs.starts_with("pairs(") || !rhs.ends_with(')')) {
            return false;
        }

        std::vector<std::string> parts;
        if (!SplitRenderedTopLevelCommaChain(lhs, &parts) || parts.size() != 2) {
            return false;
        }

        const std::string key = StripRenderedWholeParentheses(parts[0], nullptr);
        const std::string value = StripRenderedWholeParentheses(parts[1], nullptr);
        if (!IsValidLuaIdentifier(key) || !IsValidLuaIdentifier(value)) {
            return false;
        }

        *key_name = key;
        *value_name = value;
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        std::string key_name;
        std::string value_name;
        if (!parse_pairs_loop_header((*lines)[line_index], &key_name, &value_name)) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!trimmed_if.starts_with("if not ") || !trimmed_if.ends_with(" then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end") {
            continue;
        }

        const std::string target_expression = StripRenderedWholeParentheses(
            TrimLuaWhitespace(trimmed_if.substr(7, trimmed_if.size() - 7 - 5)), nullptr);
        const std::string key_suffix = "[" + key_name + "]";
        if (!target_expression.ends_with(key_suffix) ||
            CountIdentifierUses(target_expression, value_name) != 0) {
            continue;
        }

        const std::string inner_indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] = inner_indent + target_expression + " = (" +
                                   target_expression + " or " + value_name + ")";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaTemporaryMultiResultCopies(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::vector<std::string> temp_names;
        std::string assign_rhs;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &assign_is_local,
                                         &temp_names,
                                         &assign_rhs) ||
            !assign_is_local ||
            temp_names.size() < 2) {
            continue;
        }

        bool all_temps = true;
        for (const std::string& temp_name : temp_names) {
            if (!IsRenderTemporaryName(temp_name)) {
                all_temps = false;
                break;
            }
        }
        if (!all_temps) {
            continue;
        }

        std::vector<std::string> targets(temp_names.size());
        std::size_t assigned_count = 0;
        std::size_t cursor = line_index + 1;
        for (; cursor < lines->size(); ++cursor) {
            bool copy_is_local = false;
            std::string copy_lhs;
            std::string copy_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[cursor],
                                                   &copy_is_local,
                                                   &copy_lhs,
                                                   &copy_rhs) ||
                copy_is_local) {
                break;
            }

            const std::string normalized_rhs = StripRenderedWholeParentheses(
                TrimLuaWhitespace(copy_rhs),
                nullptr);
            const auto temp_it = std::find(temp_names.begin(), temp_names.end(), normalized_rhs);
            if (temp_it == temp_names.end()) {
                break;
            }

            const std::size_t temp_index =
                static_cast<std::size_t>(std::distance(temp_names.begin(), temp_it));
            if (!targets[temp_index].empty()) {
                assigned_count = 0;
                break;
            }

            targets[temp_index] =
                StripRenderedWholeParentheses(TrimLuaWhitespace(copy_lhs), nullptr);
            ++assigned_count;
        }

        if (assigned_count != temp_names.size()) {
            continue;
        }

        std::ostringstream lhs_builder;
        for (std::size_t target_index = 0; target_index < targets.size(); ++target_index) {
            if (target_index != 0) {
                lhs_builder << ", ";
            }
            lhs_builder << targets[target_index];
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + lhs_builder.str() + " = " + assign_rhs;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(cursor));
        changed = true;

        bool all_identifier_targets = true;
        for (const std::string& target : targets) {
            if (!IsValidLuaIdentifier(target)) {
                all_identifier_targets = false;
                break;
            }
        }
        if (!all_identifier_targets) {
            continue;
        }

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        const std::size_t probe_limit =
            scope_end < lines->size() ? scope_end + 1 : lines->size();
        std::vector<bool> active_targets(targets.size(), true);
        for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
            if (IsRenderedFunctionStartLine((*lines)[probe])) {
                const std::size_t nested_end = FindRenderedFunctionBlockEnd(*lines, probe);
                if (nested_end < lines->size() && nested_end > probe) {
                    probe = nested_end;
                    continue;
                }
            }

            for (std::size_t target_index = 0; target_index < targets.size(); ++target_index) {
                if (!active_targets[target_index]) {
                    continue;
                }

                const std::string& temp_name = temp_names[target_index];
                const std::string& target_name = targets[target_index];
                if (IsRenderedShadowingLocalDeclaration((*lines)[probe], temp_name) ||
                    IsAssignmentToIdentifier((*lines)[probe], temp_name)) {
                    active_targets[target_index] = false;
                    continue;
                }

                if (CountIdentifierUses((*lines)[probe], temp_name) != 0) {
                    std::string rewritten_line;
                    if (ReplaceRenderedIdentifierUses((*lines)[probe],
                                                     temp_name,
                                                     target_name,
                                                     &rewritten_line)) {
                        (*lines)[probe] = std::move(rewritten_line);
                        changed = true;
                    }
                }

                if (IsRenderedShadowingLocalDeclaration((*lines)[probe], target_name) ||
                    IsAssignmentToIdentifier((*lines)[probe], target_name)) {
                    active_targets[target_index] = false;
                }
            }
        }
    }

    return changed;
}

bool SimplifyRenderedLuaSplitGenericForIteratorLoops(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_single_assignment = [&](std::size_t line_index,
                                             bool* is_local,
                                             std::string* lhs,
                                             std::string* rhs) -> bool {
        if (line_index >= lines->size() || lhs == nullptr || rhs == nullptr) {
            return false;
        }

        std::vector<std::string> lhs_names;
        if (!ParseRenderedAssignmentLine((*lines)[line_index], is_local, &lhs_names, rhs) ||
            lhs_names.size() != 1) {
            return false;
        }

        *lhs = StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_names.front()), nullptr);
        *rhs = StripRenderedWholeParentheses(TrimLuaWhitespace(*rhs), nullptr);
        return IsValidLuaIdentifier(*lhs) && !rhs->empty();
    };

    const auto parse_simple_call = [](std::string_view expression,
                                      std::string* callee,
                                      std::string* argument_text) -> bool {
        if (callee == nullptr || argument_text == nullptr) {
            return false;
        }

        expression = TrimLuaWhitespace(StripLuaLineComment(expression));
        const std::size_t open_paren = expression.find('(');
        if (open_paren == std::string_view::npos || !expression.ends_with(')')) {
            return false;
        }

        const std::string normalized_callee = StripRenderedWholeParentheses(
            TrimLuaWhitespace(expression.substr(0, open_paren)), nullptr);
        if (!IsValidLuaIdentifier(normalized_callee)) {
            return false;
        }

        std::string_view args = expression.substr(open_paren + 1, expression.size() - open_paren - 2);
        int paren_depth = 0;
        int brace_depth = 0;
        int bracket_depth = 0;
        bool in_single_quote = false;
        bool in_double_quote = false;
        bool escaped = false;
        for (char ch : args) {
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
            } else if (ch == '{') {
                ++brace_depth;
            } else if (ch == '}') {
                --brace_depth;
            } else if (ch == '[') {
                ++bracket_depth;
            } else if (ch == ']') {
                --bracket_depth;
            }
        }
        if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0 ||
            in_single_quote || in_double_quote) {
            return false;
        }

        *callee = normalized_callee;
        *argument_text =
            StripRenderedWholeParentheses(TrimLuaWhitespace(args), nullptr);
        return !argument_text->empty();
    };

    const auto parse_for_iterator = [](std::string_view line,
                                       std::string* header,
                                       std::string* iterator_name) -> bool {
        if (header == nullptr || iterator_name == nullptr) {
            return false;
        }

        line = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!line.starts_with("for ") || !line.ends_with(" do")) {
            return false;
        }

        const std::size_t in_pos = line.find(" in ");
        if (in_pos == std::string_view::npos || in_pos <= 4) {
            return false;
        }

        const std::string normalized_header(
            TrimLuaWhitespace(line.substr(4, in_pos - 4)));
        const std::string normalized_iterator = StripRenderedWholeParentheses(
            TrimLuaWhitespace(line.substr(in_pos + 4, line.size() - (in_pos + 4) - 3)),
            nullptr);
        if (normalized_header.empty() || !IsValidLuaIdentifier(normalized_iterator)) {
            return false;
        }

        *header = normalized_header;
        *iterator_name = normalized_iterator;
        return true;
    };

    const auto future_identifier_uses = [&](std::string_view identifier,
                                            std::size_t start_line) -> std::size_t {
        std::size_t count = 0;
        for (std::size_t probe = start_line; probe < lines->size(); ++probe) {
            count += CountIdentifierUses((*lines)[probe], identifier);
        }
        return count;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string header;
        std::string iterator_name;
        if (!parse_for_iterator((*lines)[line_index], &header, &iterator_name) ||
            line_index < 1) {
            continue;
        }

        bool call_is_local = false;
        std::string call_lhs;
        std::string call_rhs;
        if (!parse_single_assignment(line_index - 1,
                                     &call_is_local,
                                     &call_lhs,
                                     &call_rhs) ||
            call_is_local || call_lhs != iterator_name) {
            continue;
        }

        std::string call_callee;
        std::string call_argument;
        if (!parse_simple_call(call_rhs, &call_callee, &call_argument)) {
            continue;
        }

        bool generator_is_local = false;
        std::string generator_lhs;
        std::string generator_rhs;
        std::size_t generator_line = line_index;
        if (line_index >= 2 &&
            parse_single_assignment(line_index - 2,
                                    &generator_is_local,
                                    &generator_lhs,
                                    &generator_rhs) &&
            !generator_is_local &&
            generator_lhs == call_callee &&
            (generator_rhs == "pairs" || generator_rhs == "ipairs" ||
             generator_rhs == "next")) {
            generator_line = line_index - 2;
            call_callee = generator_rhs;
        }

        if (!(call_callee == "pairs" || call_callee == "ipairs" || call_callee == "next")) {
            continue;
        }

        std::string iterator_argument = call_argument;
        std::size_t argument_line = line_index;
        if (IsValidLuaIdentifier(call_argument) && line_index >= 2) {
            for (std::size_t probe = line_index - 2; probe + 1 > 0; --probe) {
                if (probe == generator_line) {
                    if (probe == 0) {
                        break;
                    }
                    continue;
                }

                bool argument_is_local = false;
                std::string argument_lhs;
                std::string argument_rhs;
                if (!parse_single_assignment(probe,
                                             &argument_is_local,
                                             &argument_lhs,
                                             &argument_rhs) ||
                    argument_lhs != call_argument) {
                    if (probe == 0) {
                        break;
                    }
                    continue;
                }

                if (future_identifier_uses(call_argument, line_index + 1) == 0 &&
                    CountIdentifierUses(argument_rhs, call_argument) == 0) {
                    iterator_argument = argument_rhs;
                    argument_line = probe;
                }
                break;
            }
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + "for " + header + " in " + call_callee + "(" + iterator_argument + ") do";

        std::vector<std::size_t> erase_lines;
        if (future_identifier_uses(iterator_name, line_index + 1) == 0) {
            erase_lines.push_back(line_index - 1);
            if (generator_line < line_index) {
                erase_lines.push_back(generator_line);
            }
            if (argument_line < line_index && argument_line != generator_line &&
                argument_line != line_index - 1) {
                erase_lines.push_back(argument_line);
            }
        }

        std::sort(erase_lines.begin(), erase_lines.end());
        erase_lines.erase(std::unique(erase_lines.begin(), erase_lines.end()), erase_lines.end());
        for (auto it = erase_lines.rbegin(); it != erase_lines.rend(); ++it) {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(*it));
        }

        changed = true;
        const std::size_t erased_before_for =
            std::count_if(erase_lines.begin(),
                          erase_lines.end(),
                          [&](std::size_t erased_line) { return erased_line < line_index; });
        if (line_index >= erased_before_for && erased_before_for != 0) {
            line_index -= erased_before_for;
        }
    }

    return changed;
}

#include "rendered/rendered_lua_text_call_fallback_cleanup.inc"

bool SimplifyRenderedLuaEmptyNilGuardLiteralAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_nil_guard_operand = [](std::string_view condition,
                                            std::string* operand) -> bool {
        if (operand == nullptr) {
            return false;
        }

        condition = TrimLuaWhitespace(condition);
        const std::size_t eq_pos = condition.find("==");
        if (eq_pos == std::string_view::npos) {
            return false;
        }

        const std::string lhs = StripRenderedWholeParentheses(
            TrimLuaWhitespace(condition.substr(0, eq_pos)),
            nullptr);
        const std::string rhs = StripRenderedWholeParentheses(
            TrimLuaWhitespace(condition.substr(eq_pos + 2)),
            nullptr);
        if (lhs.empty() || rhs.empty()) {
            return false;
        }

        if (lhs == "nil" && IsRenderedSafePrefixExpression(rhs)) {
            *operand = rhs;
            return true;
        }
        if (rhs == "nil" && IsRenderedSafePrefixExpression(lhs)) {
            *operand = lhs;
            return true;
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition) ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1])) != "else" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2])) != "end") {
            continue;
        }

        std::string operand;
        if (!parse_nil_guard_operand(condition, &operand)) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 3],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs)) {
            continue;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        if (normalized_rhs.empty() || CountIdentifierUses(normalized_rhs, operand) != 0) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + (assign_is_local ? "local " : "") + assign_lhs +
                               " = " +
                               RenderLogicalExpression(
                                   RenderLogicalExpression("(" + condition + ")",
                                                           "and",
                                                           normalized_rhs),
                                   "or",
                                   operand);
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        changed = true;
    }

    return changed;
}

bool LooksLikeRenderedControlBlockHeader(std::string_view line) {
    line = TrimLuaWhitespace(StripLuaLineComment(line));
    return (line.starts_with("if ") && line.ends_with(" then")) ||
           (line.starts_with("for ") && line.ends_with(" do")) ||
           (line.starts_with("while ") && line.ends_with(" do")) ||
           StartsWithLuaKeyword(line, "repeat") ||
           StartsWithLuaKeyword(line, "function") ||
           line.starts_with("local function ");
}

std::size_t FindRenderedCleanupBlockEnd(const std::vector<std::string>& lines,
                                        std::size_t start_line) {
    if (start_line >= lines.size()) {
        return lines.size();
    }

    int balance = 0;
    for (std::size_t line_index = start_line; line_index < lines.size(); ++line_index) {
        balance += GetRenderedLuaBlockDelta(
            TrimLuaWhitespace(StripLuaLineComment(lines[line_index])));
        if (line_index > start_line && balance == 0) {
            return line_index;
        }
    }

    return lines.size();
}

bool RenderedLuaBlockHasTopLevelElse(const std::vector<std::string>& lines,
                                     std::size_t start_line,
                                     std::size_t end_line) {
    if (start_line >= lines.size() || end_line >= lines.size() || end_line <= start_line) {
        return false;
    }

    int depth = 1;
    for (std::size_t probe = start_line + 1; probe < end_line; ++probe) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment(lines[probe]));
        if (trimmed.empty()) {
            continue;
        }
        if (depth == 1 && (trimmed == "else" || trimmed.starts_with("elseif "))) {
            return true;
        }
        depth += GetRenderedLuaBlockDelta(trimmed);
    }

    return false;
}

std::size_t FindRenderedLastNonEmptyLine(const std::vector<std::string>& lines,
                                         std::size_t begin,
                                         std::size_t end) {
    if (begin >= end || end > lines.size()) {
        return lines.size();
    }

    for (std::size_t line_index = end; line_index-- > begin;) {
        if (!TrimLuaWhitespace(StripLuaLineComment(lines[line_index])).empty()) {
            return line_index;
        }
    }

    return lines.size();
}

#include "rendered/families/rendered_lua_text_loop_cleanup.inc"

#include "rendered/rendered_lua_text_condition_chain_cleanup.inc"

bool SimplifyRenderedLuaImmediateSingleUseGuardTemps(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_guard_header = [](std::string_view line,
                                       std::string* prefix,
                                       std::string* condition,
                                       std::string* suffix) {
        if (prefix == nullptr || condition == nullptr || suffix == nullptr) {
            return false;
        }

        const std::string indent(std::string(GetRenderedLuaIndent(line)));
        if (TryExtractRenderedIfCondition(line, condition)) {
            *prefix = indent + "if ";
            *suffix = " then";
            return true;
        }
        if (TryExtractRenderedWhileCondition(line, condition)) {
            *prefix = indent + "while ";
            *suffix = " do";
            return true;
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !assign_is_local || !IsValidLuaIdentifier(assign_lhs) ||
            assign_rhs.find('\n') != std::string::npos ||
            assign_rhs.find("function(") != std::string::npos) {
            continue;
        }

        int paren_balance = 0;
        int brace_balance = 0;
        if (HasPositiveLuaExpressionBalance(assign_rhs, &paren_balance, &brace_balance)) {
            continue;
        }

        std::string guard_prefix;
        std::string guard_condition;
        std::string guard_suffix;
        if (!parse_guard_header((*lines)[line_index + 1],
                                &guard_prefix,
                                &guard_condition,
                                &guard_suffix)) {
            continue;
        }

        bool negated = false;
        std::string normalized_condition =
            StripRenderedWholeParentheses(TrimLuaWhitespace(guard_condition), nullptr);
        if (StartsWithLuaKeyword(normalized_condition, "not")) {
            negated = true;
            normalized_condition = StripRenderedWholeParentheses(
                TrimLuaWhitespace(std::string_view(normalized_condition).substr(3)),
                nullptr);
        }
        if (normalized_condition != assign_lhs) {
            continue;
        }

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        bool used_later = false;
        for (std::size_t probe = line_index + 2;
             probe < lines->size() &&
             (scope_end >= lines->size() || probe <= scope_end);
             ++probe) {
            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], assign_lhs) ||
                IsAssignmentToIdentifier((*lines)[probe], assign_lhs)) {
                break;
            }
            if (CountIdentifierUses((*lines)[probe], assign_lhs) != 0) {
                used_later = true;
                break;
            }
        }
        if (used_later) {
            continue;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        if (normalized_rhs.empty()) {
            continue;
        }

        (*lines)[line_index + 1] =
            guard_prefix + (negated ? "not " : "") + normalized_rhs + guard_suffix;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

#include "rendered/rendered_lua_text_condition_cleanup.inc"

#include "rendered/rendered_lua_text_nested_guard_cleanup.inc"

#include "rendered/families/rendered_lua_text_unhandled_if_cleanup.inc"

#include "rendered/families/rendered_lua_text_loop_flow_cleanup.inc"

#include "rendered/families/rendered_lua_text_else_cleanup.inc"

#include "rendered/families/rendered_lua_text_localize_table_cleanup.inc"

#include "rendered/families/rendered_lua_text_table_outlier_cleanup.inc"

#include "rendered/families/rendered_lua_text_cleanup_common_helpers.inc"
#include "rendered/families/rendered_lua_text_table_setlist_cleanup.inc"

#include "rendered/families/rendered_lua_text_subclass_setlist_cleanup.inc"

#include "rendered/families/rendered_lua_text_table_carry_cleanup.inc"
#include "rendered/families/rendered_lua_text_literal_seed_cleanup.inc"
#include "rendered/families/rendered_lua_text_literal_seed_forward_cleanup.inc"
#include "rendered/families/rendered_lua_text_table_nested_index_cleanup.inc"
#include "rendered/families/rendered_lua_text_table_temp_placeholder_cleanup.inc"
#include "rendered/families/rendered_lua_text_table_builder_cluster_cleanup.inc"
#include "rendered/rendered_lua_text_table_cleanup.inc"
#include "rendered/families/rendered_lua_text_table_placeholder_expand_cleanup.inc"
#include "rendered/families/rendered_lua_text_table_qualified_cleanup.inc"
#include "rendered/families/rendered_lua_text_table_temp_cleanup.inc"
#include "rendered/families/rendered_lua_text_expression_cleanup.inc"
#include "rendered/families/rendered_lua_text_structure_cleanup.inc"
#include "rendered/families/rendered_lua_text_artifact_cleanup.inc"
#include "rendered/families/rendered_lua_text_placeholder_cleanup.inc"
#include "rendered/families/rendered_lua_text_gameface_cleanup.inc"
#include "rendered/families/rendered_lua_text_function_cleanup.inc"
#include "rendered/rendered_lua_text_cleanup_profiles.inc"
#include "rendered/rendered_lua_text_shared_cleanup_orchestration.inc"
#include "rendered/families/rendered_lua_text_postprocess_cleanup.inc"

}  // namespace refii::lua
