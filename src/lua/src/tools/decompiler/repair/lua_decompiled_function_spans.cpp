#include "tools/decompiler/repair/lua_decompiled_function_spans.h"

#include "tools/decompiler/lua51_analysis.h"
#include "tools/decompiler/rendered/rendered_readability.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/straight_line_render.h"
#include "tools/decompiler/lua_decompiler_internal.h"
#include "tools/decompiler/repair/lua_rendered_function_helpers.h"

#include <algorithm>
#include <cctype>
#include <limits>
#include <string_view>
#include <unordered_set>
#include <utility>

namespace refii::lua::internal {
namespace {

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
    const LuaTokenScanOptions scan_options{
        .mode = LuaTokenScanMode::kCommentAware,
        .parse_quoted_literals = true,
        .use_stable_copy = false,
    };
    ForEachLuaIdentifierToken(line, scan_options, callback);
}

std::vector<std::string> CollectUsefulChunkParameterNames(
    const refii::lua::Lua51Function& function) {
    std::vector<std::string> names;
    names.reserve(function.parameter_count);
    if (function.parameter_count == 0) {
        return names;
    }

    for (const refii::lua::Lua51Local& local : function.locals) {
        if (local.start_pc != 0) {
            continue;
        }
        if (!refii::lua::IsValidLuaIdentifier(local.name) ||
            refii::lua::IsRenderTemporaryName(local.name)) {
            return {};
        }
        names.push_back(local.name);
        if (names.size() == function.parameter_count) {
            break;
        }
    }

    if (names.size() != function.parameter_count) {
        names.clear();
    }
    return names;
}

bool IsIdentifierChar(char ch) {
    const unsigned char uch = static_cast<unsigned char>(ch);
    return std::isalnum(uch) != 0 || ch == '_';
}

bool IsLuaIdentifier(std::string_view value) {
    if (value.empty()) {
        return false;
    }
    const unsigned char first = static_cast<unsigned char>(value.front());
    if (std::isalpha(first) == 0 && value.front() != '_') {
        return false;
    }
    for (char ch : value) {
        if (!IsIdentifierChar(ch)) {
            return false;
        }
    }
    return true;
}

std::string TryRenderLuaStringConstant(const refii::lua::Lua51Function& function, int rk) {
    int constant_index = rk;
    if (refii::lua::IsConstantReference(rk)) {
        constant_index &= 0xFF;
    }
    if (constant_index < 0 ||
        static_cast<std::size_t>(constant_index) >= function.constants.size()) {
        return {};
    }
    const refii::lua::Lua51Constant& constant =
        function.constants[static_cast<std::size_t>(constant_index)];
    if (constant.type != refii::lua::Lua51ConstantType::kString) {
        return {};
    }
    return constant.string_value;
}

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
        if (!StartsWith(trimmed, "if ") || !trimmed.ends_with(" then")) {
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

bool TryExtractFunctionSpanFromStartLine(const std::vector<std::string>& lines,
                                         std::size_t start_line,
                                         DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || start_line >= lines.size()) {
        return false;
    }

    const std::string& start_text = lines[start_line];
    const std::size_t function_pos = start_text.find("function");
    if (function_pos == std::string::npos) {
        return false;
    }

    DecompiledFunctionSpan span;
    span.start_line = start_line;
    span.end_line = start_line;
    span.prefix = start_text.substr(0, function_pos);

    int end_balance = 0;
    int until_balance = 0;
    for (std::size_t line_index = start_line; line_index < lines.size(); ++line_index) {
        const std::string_view stripped = StripLineComment(lines[line_index]);
        std::size_t last_end_token_end = std::string_view::npos;
        ForEachLuaToken(stripped, [&](std::string_view token, std::size_t, std::size_t end) {
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
                last_end_token_end = end;
            }
        });

        if (end_balance == 0 && until_balance == 0) {
            span.end_line = line_index;
            if (last_end_token_end != std::string_view::npos) {
                span.suffix = std::string(stripped.substr(last_end_token_end));
            }
            *span_out = std::move(span);
            return true;
        }
    }

    return false;
}

}  // namespace

bool TryInferClosureAssignmentPrefix(const refii::lua::Lua51Function& main_function,
                                     std::size_t function_index,
                                     std::string* prefix_out) {
    if (prefix_out == nullptr || function_index == 0 ||
        function_index > main_function.child_functions.size()) {
        return false;
    }

    const std::vector<refii::lua::Lua51Instruction> instructions =
        refii::lua::DecodeLua51Instructions(main_function);
    std::vector<std::string> register_exprs(
        std::min<std::size_t>(main_function.max_stack_size, 250), std::string{});
    for (std::size_t instruction_index = 0; instruction_index < instructions.size();
         ++instruction_index) {
        const refii::lua::Lua51Instruction& instruction = instructions[instruction_index];
        switch (instruction.opcode) {
            case refii::lua::Lua51Opcode::kGetGlobal: {
                if (instruction.a >= 0 &&
                    static_cast<std::size_t>(instruction.a) < register_exprs.size()) {
                    register_exprs[static_cast<std::size_t>(instruction.a)] =
                        TryRenderLuaStringConstant(main_function, instruction.bx);
                }
                break;
            }
            case refii::lua::Lua51Opcode::kMove: {
                if (instruction.a >= 0 && instruction.b >= 0 &&
                    static_cast<std::size_t>(instruction.a) < register_exprs.size() &&
                    static_cast<std::size_t>(instruction.b) < register_exprs.size()) {
                    register_exprs[static_cast<std::size_t>(instruction.a)] =
                        register_exprs[static_cast<std::size_t>(instruction.b)];
                }
                break;
            }
            case refii::lua::Lua51Opcode::kGetTable: {
                if (instruction.a >= 0 && instruction.b >= 0 &&
                    static_cast<std::size_t>(instruction.a) < register_exprs.size() &&
                    static_cast<std::size_t>(instruction.b) < register_exprs.size()) {
                    const std::string base =
                        register_exprs[static_cast<std::size_t>(instruction.b)];
                    const std::string key =
                        TryRenderLuaStringConstant(main_function, instruction.c);
                    if (!base.empty() && !key.empty() && IsLuaIdentifier(key)) {
                        register_exprs[static_cast<std::size_t>(instruction.a)] =
                            base + "." + key;
                    } else {
                        register_exprs[static_cast<std::size_t>(instruction.a)].clear();
                    }
                }
                break;
            }
            case refii::lua::Lua51Opcode::kClosure: {
                if (instruction.bx != static_cast<int>(function_index - 1)) {
                    break;
                }

                const refii::lua::Lua51Function& child =
                    main_function.child_functions[function_index - 1];
                std::unordered_set<int> closure_registers = {instruction.a};
                std::size_t probe = instruction_index + 1 +
                                    static_cast<std::size_t>(child.upvalue_count);
                const std::size_t probe_limit =
                    std::min<std::size_t>(instructions.size(), probe + 8);
                for (; probe < probe_limit; ++probe) {
                    const refii::lua::Lua51Instruction& next = instructions[probe];
                    if (next.opcode == refii::lua::Lua51Opcode::kMove &&
                        closure_registers.contains(next.b)) {
                        closure_registers.insert(next.a);
                        continue;
                    }
                    if (next.opcode == refii::lua::Lua51Opcode::kSetGlobal &&
                        closure_registers.contains(next.a)) {
                        *prefix_out = TryRenderLuaStringConstant(main_function, next.bx);
                        return !prefix_out->empty();
                    }
                    if (next.opcode == refii::lua::Lua51Opcode::kSetTable &&
                        closure_registers.contains(next.c) && next.a >= 0 &&
                        static_cast<std::size_t>(next.a) < register_exprs.size()) {
                        const std::string base =
                            register_exprs[static_cast<std::size_t>(next.a)];
                        const std::string key =
                            TryRenderLuaStringConstant(main_function, next.b);
                        if (!base.empty() && !key.empty() && IsLuaIdentifier(key)) {
                            *prefix_out = base + "." + key;
                            return true;
                        }
                    }
                }
                break;
            }
            default:
                break;
        }
    }

    return false;
}

bool TryExtractAssignedPrefixFromSpanHeader(const std::vector<std::string>& lines,
                                            const DecompiledFunctionSpan& span,
                                            std::string* prefix_out) {
    if (prefix_out == nullptr || span.start_line >= lines.size()) {
        return false;
    }

    const std::string_view line = lines[span.start_line];
    const std::size_t function_pos = line.find("function");
    if (function_pos == std::string::npos || function_pos == 0) {
        return false;
    }

    std::string_view prefix = TrimWhitespace(line.substr(0, function_pos));
    if (prefix.empty()) {
        return false;
    }
    if (prefix.back() == '=') {
        prefix = TrimWhitespace(prefix.substr(0, prefix.size() - 1));
    }
    if (prefix.empty()) {
        return false;
    }

    *prefix_out = std::string(prefix);
    return true;
}

bool TryExtractFunctionSpanByHeaderPrefix(const std::vector<std::string>& lines,
                                          std::string_view assigned_prefix,
                                          DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || assigned_prefix.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        DecompiledFunctionSpan header_span;
        header_span.start_line = line_index;
        std::string header_prefix;
        if (!TryExtractAssignedPrefixFromSpanHeader(lines, header_span, &header_prefix) ||
            header_prefix != assigned_prefix) {
            continue;
        }
        if (TryExtractFunctionSpanFromStartLine(lines, line_index, span_out)) {
            return true;
        }
    }
    return false;
}

bool TryExtractTopLevelFunctionSpanByPrefixUntilNextBoundary(
    const std::vector<std::string>& lines,
    std::string_view assigned_prefix,
    DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || assigned_prefix.empty()) {
        return false;
    }

    const auto is_top_level_function_header = [&](std::size_t line_index) {
        DecompiledFunctionSpan header_span;
        header_span.start_line = line_index;
        std::string header_prefix;
        return TryExtractAssignedPrefixFromSpanHeader(lines, header_span, &header_prefix);
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        DecompiledFunctionSpan header_span;
        header_span.start_line = line_index;
        std::string header_prefix;
        if (!TryExtractAssignedPrefixFromSpanHeader(lines, header_span, &header_prefix) ||
            header_prefix != assigned_prefix) {
            continue;
        }

        const std::size_t function_pos = lines[line_index].find("function");
        if (function_pos == std::string::npos) {
            continue;
        }

        DecompiledFunctionSpan span;
        span.start_line = line_index;
        span.end_line = lines.empty() ? 0 : (lines.size() - 1);
        span.prefix = lines[line_index].substr(0, function_pos);
        span.top_level = true;
        const std::size_t indent_size = GetLuaLeadingIndent(lines[line_index]).size();

        for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
            if (!is_top_level_function_header(probe) ||
                GetLuaLeadingIndent(lines[probe]).size() > indent_size) {
                continue;
            }

            std::size_t end_line = probe - 1;
            while (end_line > line_index &&
                   TrimWhitespace(StripLineComment(lines[end_line])).empty()) {
                --end_line;
            }
            span.end_line = end_line;
            break;
        }

        *span_out = std::move(span);
        return true;
    }

    return false;
}

bool IsTopLevelAnonymousBootstrapSpan(const DecompiledFunctionSpan& span) {
    if (!span.top_level) {
        return false;
    }

    const std::string_view trimmed_prefix = TrimWhitespace(span.prefix);
    return trimmed_prefix.empty() || trimmed_prefix == "(" || trimmed_prefix == ";(";
}

bool SpanUsesTemporaryParameterNamesWhenChunkHasNamedParameters(
    const std::vector<std::string>& lines,
    const DecompiledFunctionSpan& span,
    const refii::lua::Lua51Function& function) {
    if (span.start_line >= lines.size() || function.parameter_count == 0) {
        return false;
    }
    if (span.start_line >= span.end_line) {
        return false;
    }

    std::size_t scanned = 0;
    bool looks_like_closure_factory = false;
    for (std::size_t line_index = span.start_line + 1;
         line_index <= span.end_line && line_index < lines.size() && scanned < 4;
         ++line_index) {
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(lines[line_index]));
        if (trimmed.empty()) {
            continue;
        }
        ++scanned;
        if (StartsWith(trimmed, "return function(") ||
            StartsWith(trimmed, "return function ")) {
            looks_like_closure_factory = true;
            break;
        }
        if (trimmed != "local" && !StartsWith(trimmed, "local ")) {
            break;
        }
    }
    if (!looks_like_closure_factory) {
        return false;
    }

    std::vector<std::string> rendered_parameters;
    if (!TryExtractRenderedFunctionHeaderParameters(lines[span.start_line],
                                                    &rendered_parameters) ||
        rendered_parameters.size() != function.parameter_count) {
        return false;
    }

    bool all_rendered_temporary = true;
    for (const std::string& parameter : rendered_parameters) {
        if (!refii::lua::IsRenderTemporaryName(parameter)) {
            all_rendered_temporary = false;
            break;
        }
    }
    if (!all_rendered_temporary) {
        return false;
    }

    const std::vector<std::string> chunk_parameters =
        CollectUsefulChunkParameterNames(function);
    if (chunk_parameters.size() != rendered_parameters.size()) {
        return false;
    }

    for (std::size_t index = 0; index < rendered_parameters.size(); ++index) {
        if (rendered_parameters[index] != chunk_parameters[index]) {
            return true;
        }
    }
    return false;
}

std::unordered_map<std::string, DecompiledFunctionSpan> BuildAssignedPrefixSpanMap(
    const std::vector<std::string>& lines,
    const std::vector<DecompiledFunctionSpan>& spans) {
    std::unordered_map<std::string, DecompiledFunctionSpan> prefix_to_span;
    for (const DecompiledFunctionSpan& span : spans) {
        std::string assigned_prefix;
        if (!TryExtractAssignedPrefixFromSpanHeader(lines, span, &assigned_prefix) ||
            assigned_prefix.empty()) {
            continue;
        }
        prefix_to_span.try_emplace(std::move(assigned_prefix), span);
    }
    return prefix_to_span;
}

bool TryFindFunctionSpanByPrefix(const std::vector<std::string>& lines,
                                 std::string_view assigned_prefix,
                                 DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || assigned_prefix.empty()) {
        return false;
    }

    const std::vector<DecompiledFunctionSpan> spans = CollectDecompiledFunctionSpans(lines);
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        DecompiledFunctionSpan header_span;
        header_span.start_line = line_index;
        std::string header_prefix;
        if (!TryExtractAssignedPrefixFromSpanHeader(lines, header_span, &header_prefix) ||
            header_prefix != assigned_prefix) {
            continue;
        }
        for (const DecompiledFunctionSpan& span : spans) {
            if (span.start_line == line_index) {
                *span_out = span;
                return true;
            }
        }
        if (TryExtractFunctionSpanFromStartLine(lines, line_index, span_out)) {
            return true;
        }
    }
    return TryFindFunctionSpanByTemporaryAssignmentPrefix(lines, assigned_prefix, span_out);
}

bool TryResolveFunctionSpanForIndex(const refii::lua::Lua51Function& main_function,
                                    std::size_t preorder_function_index,
                                    std::size_t root_child_index,
                                    const std::vector<std::string>& lines,
                                    const std::vector<DecompiledFunctionSpan>& spans,
                                    const std::unordered_map<std::string, DecompiledFunctionSpan>*
                                        prefix_span_map,
                                    DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || preorder_function_index == 0) {
        return false;
    }

    std::string assigned_prefix;
    if (root_child_index != 0 &&
        TryInferClosureAssignmentPrefix(main_function, root_child_index, &assigned_prefix)) {
        DecompiledFunctionSpan annotated_span;
        bool have_annotated_span = false;
        if (prefix_span_map != nullptr) {
            const auto found = prefix_span_map->find(assigned_prefix);
            if (found != prefix_span_map->end()) {
                annotated_span = found->second;
                have_annotated_span = true;
            }
        }
        DecompiledFunctionSpan expanded_span;
        if (TryExtractFunctionSpanByHeaderPrefix(lines, assigned_prefix, &expanded_span)) {
            if (have_annotated_span) {
                expanded_span.top_level =
                    expanded_span.top_level || annotated_span.top_level;
                expanded_span.has_error =
                    expanded_span.has_error || annotated_span.has_error;
                expanded_span.looks_suspicious =
                    expanded_span.looks_suspicious || annotated_span.looks_suspicious;
            }
            *span_out = std::move(expanded_span);
            return true;
        }
        if (TryExtractTopLevelFunctionSpanByPrefixUntilNextBoundary(lines,
                                                                    assigned_prefix,
                                                                    &expanded_span)) {
            if (have_annotated_span) {
                expanded_span.top_level =
                    expanded_span.top_level || annotated_span.top_level;
                expanded_span.has_error =
                    expanded_span.has_error || annotated_span.has_error;
                expanded_span.looks_suspicious =
                    expanded_span.looks_suspicious || annotated_span.looks_suspicious;
            }
            *span_out = std::move(expanded_span);
            return true;
        }
        if (have_annotated_span) {
            *span_out = std::move(annotated_span);
            return true;
        }
        DecompiledFunctionSpan exact_span;
        if (TryFindFunctionSpanByPrefix(lines, assigned_prefix, &exact_span)) {
            *span_out = std::move(exact_span);
            return true;
        }
    }

    if (preorder_function_index <= spans.size()) {
        *span_out = spans[preorder_function_index - 1];
        return true;
    }

    return false;
}

bool TryFindFunctionSpanByTemporaryAssignmentPrefix(
    const std::vector<std::string>& lines,
    std::string_view assigned_prefix,
    DecompiledFunctionSpan* span_out) {
    if (span_out == nullptr || assigned_prefix.empty()) {
        return false;
    }

    const std::vector<DecompiledFunctionSpan> spans = CollectDecompiledFunctionSpans(lines);
    if (spans.empty()) {
        return false;
    }

    const auto resolve_temp_based_lhs_near_header =
        [&](std::size_t header_line,
            std::string_view expression,
            std::string* resolved_out) -> bool {
        if (resolved_out == nullptr) {
            return false;
        }

        expression = TrimWhitespace(expression);
        if (expression.empty()) {
            return false;
        }

        std::size_t identifier_end = 0;
        while (identifier_end < expression.size() &&
               IsIdentifierChar(expression[identifier_end])) {
            ++identifier_end;
        }
        if (identifier_end == 0) {
            return false;
        }

        const std::string_view base = expression.substr(0, identifier_end);
        if (!IsLuaIdentifier(base)) {
            return false;
        }
        const std::string_view suffix = expression.substr(identifier_end);

        constexpr std::size_t kAliasSearchLimit = 8;
        std::size_t searched = 0;
        for (std::size_t probe = header_line; probe-- > 0 && searched < kAliasSearchLimit;) {
            if (TrimWhitespace(StripLineComment(lines[probe])).empty()) {
                continue;
            }
            ++searched;

            std::string indent;
            std::string lhs;
            std::string rhs;
            if (!ParseSimpleRenderedAssignment(lines[probe], &indent, &lhs, &rhs)) {
                continue;
            }
            if (lhs != base) {
                continue;
            }

            *resolved_out = rhs + std::string(suffix);
            return true;
        }
        return false;
    };

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string indent;
        std::string assignment_lhs;
        std::string assignment_rhs;
        if (!ParseSimpleRenderedAssignment(lines[line_index],
                                           &indent,
                                           &assignment_lhs,
                                           &assignment_rhs)) {
            continue;
        }
        if (!IsLuaIdentifier(assignment_rhs)) {
            continue;
        }

        for (std::size_t header_line = line_index; header_line-- > 0;) {
            std::string header_indent;
            std::string header_lhs;
            std::string header_rhs;
            if (!ParseSimpleRenderedAssignment(lines[header_line],
                                               &header_indent,
                                               &header_lhs,
                                               &header_rhs)) {
                continue;
            }
            if (header_lhs != assignment_rhs) {
                continue;
            }
            if (!StartsWith(TrimWhitespace(header_rhs), "function")) {
                continue;
            }

            std::string resolved_lhs = assignment_lhs;
            if (resolved_lhs != assigned_prefix &&
                !resolve_temp_based_lhs_near_header(header_line,
                                                    assignment_lhs,
                                                    &resolved_lhs)) {
                continue;
            }
            if (resolved_lhs != assigned_prefix) {
                continue;
            }

            for (const DecompiledFunctionSpan& span : spans) {
                if (span.start_line == header_line) {
                    *span_out = span;
                    return true;
                }
            }

            if (TryExtractFunctionSpanFromStartLine(lines, header_line, span_out)) {
                return true;
            }
            return false;
        }
    }

    return false;
}

std::vector<DecompiledFunctionSpan> CollectDecompiledFunctionSpans(
    const std::vector<std::string>& lines) {
    std::vector<DecompiledFunctionSpan> spans;
    std::vector<LuaTextBlock> block_stack;
    std::size_t root_indent_size = std::numeric_limits<std::size_t>::max();
    for (const std::string& line : lines) {
        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        if (trimmed.empty()) {
            continue;
        }
        root_indent_size = std::min(root_indent_size, GetLuaLeadingIndent(line).size());
    }
    if (root_indent_size == std::numeric_limits<std::size_t>::max()) {
        root_indent_size = 0;
    }
    const auto advance_brace_depth = [](std::string_view line,
                                        std::size_t limit,
                                        int depth) -> int {
        limit = std::min(limit, line.size());
        bool in_single_quote = false;
        bool in_double_quote = false;
        bool escaped = false;
        for (std::size_t i = 0; i < limit; ++i) {
            const char ch = line[i];
            if (!in_single_quote && !in_double_quote && !escaped &&
                ch == '-' && i + 1 < limit && line[i + 1] == '-') {
                break;
            }

            if (escaped) {
                escaped = false;
                continue;
            }

            if (in_single_quote) {
                if (ch == '\\') {
                    escaped = true;
                } else if (ch == '\'') {
                    in_single_quote = false;
                }
                continue;
            }

            if (in_double_quote) {
                if (ch == '\\') {
                    escaped = true;
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

            if (ch == '{') {
                ++depth;
            } else if (ch == '}') {
                depth = std::max(0, depth - 1);
            }
        }
        return depth;
    };
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
    const auto find_previous_significant_line = [&](std::size_t start_line) {
        for (std::size_t probe = start_line; probe-- > 0;) {
            if (!TrimWhitespace(StripLineComment(lines[probe])).empty()) {
                return probe;
            }
        }
        return lines.size();
    };

    int table_brace_depth = 0;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        const std::string& line = lines[line_index];
        const int brace_depth_before_line = table_brace_depth;
        ForEachLuaToken(line, [&](std::string_view token, std::size_t begin, std::size_t end) {
            if (token == "function") {
                const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
                const std::size_t current_indent_size = GetLuaLeadingIndent(line).size();
                const bool assigned_function_header =
                    trimmed.find("= function(") != std::string_view::npos ||
                    trimmed.find("= function ") != std::string_view::npos ||
                    StartsWith(trimmed, "function(") ||
                    StartsWith(trimmed, "function ") ||
                    StartsWith(trimmed, "local function ");
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
                    first_non_space != std::string::npos &&
                    GetLuaLeadingIndent(line).size() == root_indent_size;
                const bool nested_in_table_literal =
                    advance_brace_depth(line, begin, brace_depth_before_line) > 0;
                bool probable_top_level_sibling = false;
                if (!no_enclosing_function && !nested_in_table_literal &&
                    assigned_function_header &&
                    current_indent_size <= root_indent_size + 2) {
                    const std::size_t previous = find_previous_significant_line(line_index);
                    if (previous < lines.size()) {
                        const std::string_view previous_trimmed =
                            TrimWhitespace(StripLineComment(lines[previous]));
                        probable_top_level_sibling =
                            previous_trimmed == "end" || previous_trimmed == "}" ||
                            LooksLikeTopLevelBoundary(previous_trimmed);
                    }
                }
                if (probable_top_level_sibling) {
                    const std::size_t previous = find_previous_significant_line(line_index);
                    while (!block_stack.empty()) {
                        const LuaTextBlock block = block_stack.back();
                        block_stack.pop_back();
                        if (block.kind != LuaTextBlockKind::kFunction ||
                            block.span_index >= spans.size()) {
                            continue;
                        }
                        DecompiledFunctionSpan& enclosing_span = spans[block.span_index];
                        enclosing_span.looks_suspicious = true;
                        enclosing_span.has_error = true;
                        if (previous < lines.size() && previous >= enclosing_span.start_line) {
                            enclosing_span.end_line = previous;
                            enclosing_span.suffix.clear();
                        }
                        break;
                    }
                }
                span.top_level =
                    (no_enclosing_function || starts_at_root_indent ||
                     probable_top_level_sibling) &&
                    !nested_in_table_literal;
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
        table_brace_depth = advance_brace_depth(line, line.size(), table_brace_depth);

        const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
        const bool line_looks_broken =
            line.find("DECOMPILER ERROR:") != std::string::npos ||
            line.find("decompiler aborted repeated control-flow") != std::string::npos ||
            line.find("Warning: missing end") != std::string::npos ||
            line.find("(for generator)") != std::string::npos ||
            line.find("nil[") != std::string::npos ||
            line.find("nil(") != std::string::npos ||
            line.find("nil.") != std::string::npos ||
            ContainsNilArithmeticPattern(trimmed) ||
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
        for (std::size_t line_index = span.start_line;
             line_index <= span.end_line && line_index < lines.size();
             ++line_index) {
            const std::string_view stripped = StripLineComment(lines[line_index]);
            if (stripped.find("nil[") != std::string_view::npos ||
                stripped.find("nil(") != std::string::npos ||
                stripped.find("nil.") != std::string::npos ||
                stripped.find("nil:") != std::string::npos ||
                stripped.find("math.max(nil") != std::string::npos ||
                stripped.find("math.min(nil") != std::string::npos ||
                ContainsNilArithmeticPattern(stripped)) {
                span.looks_suspicious = true;
                span.has_error = true;
                break;
            }
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

void AppendLuaFunctionChildrenPreorderWithRootChildIndex(
    const refii::lua::Lua51Function& function,
    std::size_t inherited_root_child_index,
    std::vector<const refii::lua::Lua51Function*>* functions_out,
    std::vector<std::size_t>* root_child_indices_out) {
    if (functions_out == nullptr || root_child_indices_out == nullptr) {
        return;
    }

    for (std::size_t child_index = 0; child_index < function.child_functions.size(); ++child_index) {
        const refii::lua::Lua51Function& child = function.child_functions[child_index];
        const std::size_t root_child_index =
            inherited_root_child_index != 0 ? inherited_root_child_index : (child_index + 1);
        functions_out->push_back(&child);
        root_child_indices_out->push_back(root_child_index);
        AppendLuaFunctionChildrenPreorderWithRootChildIndex(child,
                                                            root_child_index,
                                                            functions_out,
                                                            root_child_indices_out);
    }
}

}  // namespace refii::lua::internal
