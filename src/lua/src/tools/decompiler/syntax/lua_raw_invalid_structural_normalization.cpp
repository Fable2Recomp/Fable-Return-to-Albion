#include "tools/decompiler/syntax/lua_raw_invalid_structural_normalization.h"

#include "tools/decompiler/lua_decompiler_internal.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"

#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {

bool IsTargetedRawInvalidStructuralFamily(std::string_view validation_error) {
    if (validation_error.empty()) {
        return false;
    }
    const bool eof_boundary_error =
        validation_error.find("<eof> expected near") != std::string_view::npos ||
        validation_error.find("'<eof>' expected near") != std::string_view::npos;
    return validation_error.find("unexpected symbol near 'while'") !=
               std::string_view::npos ||
           validation_error.find("unexpected symbol near 'if'") !=
               std::string_view::npos ||
           validation_error.find("unexpected symbol near 'end'") !=
               std::string_view::npos ||
           validation_error.find("unexpected symbol near '='") !=
               std::string_view::npos ||
           validation_error.find("unexpected symbol near '{'") !=
               std::string_view::npos ||
           validation_error.find("unexpected symbol near ','") !=
               std::string_view::npos ||
           validation_error.find("unexpected symbol near 'nil'") !=
               std::string_view::npos ||
           validation_error.find("unexpected symbol near ')'") !=
               std::string_view::npos ||
           validation_error.find("')' expected near ':'") !=
               std::string_view::npos ||
           validation_error.find("')' expected near '('") !=
               std::string_view::npos ||
           validation_error.find("')' expected near ','") !=
               std::string_view::npos ||
           validation_error.find("')' expected (to close '(' at line") !=
               std::string_view::npos ||
           validation_error.find("']' expected near '.'") !=
               std::string_view::npos ||
           validation_error.find("'}' expected (to close '{'") !=
               std::string_view::npos ||
           validation_error.find("'=' expected near") !=
               std::string_view::npos ||
           eof_boundary_error;
}

bool TryApplyMethodLabelStructuralNormalization(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();

    bool changed = false;
    changed |= refii::lua::SimplifyRenderedLuaRedundantColonSelfArguments(lines);
    changed |= refii::lua::SimplifyRenderedLuaInvalidEmptyStringMethodReceivers(lines);
    if (!changed) {
        return false;
    }

    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_methodlabel_normalized");
    }
    return true;
}

bool TryRewriteInlineTableLiteralFieldAssignments(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    const std::vector<std::string>& lines = text_context.EnsureLines();
    std::vector<std::string> rewritten_lines;
    rewritten_lines.reserve(lines.size() + 8);
    bool changed = false;
    int rewrite_index = 0;

    for (const std::string& line : lines) {
        const std::string_view trimmed =
            TrimWhitespace(StripLineComment(line));
        const std::size_t assignment_pos = trimmed.rfind(" = ");
        if (assignment_pos == std::string_view::npos || assignment_pos == 0 ||
            assignment_pos + 3 >= trimmed.size()) {
            rewritten_lines.push_back(line);
            continue;
        }

        const std::string_view lhs =
            TrimWhitespace(trimmed.substr(0, assignment_pos));
        const std::string_view rhs =
            TrimWhitespace(trimmed.substr(assignment_pos + 3));
        if (lhs.empty() || rhs.empty()) {
            rewritten_lines.push_back(line);
            continue;
        }

        const std::size_t dot_suffix_pos = lhs.find("}.");
        const std::size_t bracket_suffix_pos = lhs.find("}[");
        const std::size_t suffix_pos =
            dot_suffix_pos == std::string_view::npos
                ? bracket_suffix_pos
                : (bracket_suffix_pos == std::string_view::npos
                       ? dot_suffix_pos
                       : std::min(dot_suffix_pos, bracket_suffix_pos));
        if (suffix_pos == std::string_view::npos) {
            rewritten_lines.push_back(line);
            continue;
        }

        const std::size_t table_expr_end = suffix_pos + 1;
        const std::string_view table_expr = lhs.substr(0, table_expr_end);
        const std::string_view field_suffix = lhs.substr(table_expr_end);
        if (table_expr.size() < 2 || table_expr.front() != '{' ||
            table_expr.back() != '}') {
            rewritten_lines.push_back(line);
            continue;
        }

        const std::size_t indent_end = line.find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos ? std::string() : line.substr(0, indent_end);
        const std::string temp_name =
            "__refii_inline_table_" + std::to_string(++rewrite_index);
        rewritten_lines.push_back(indent + "local " + temp_name + " = " +
                                  std::string(table_expr));
        rewritten_lines.push_back(indent + temp_name + std::string(field_suffix) + " = " +
                                  std::string(rhs));
        changed = true;
    }

    if (!changed) {
        return false;
    }

    text_context.SetLines(std::move(rewritten_lines), text_context.HadTrailingNewline());
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_inline_table_assignment_normalized");
    }
    return true;
}

bool TryRepairInlineTableFunctionFieldBoundaryBreakage(std::string& source,
                                                       std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    bool changed = false;

    for (std::size_t index = 0; index < lines->size(); ++index) {
        const std::string_view trimmed =
            TrimWhitespace(StripLineComment((*lines)[index]));
        if (!StartsWith(trimmed, "local ")) {
            continue;
        }

        const std::size_t equal = trimmed.find('=');
        if (equal == std::string_view::npos || equal <= 6) {
            continue;
        }
        const std::string table_name = std::string(
            TrimWhitespace(trimmed.substr(6, equal - 6)));
        if (!refii::lua::IsValidLuaIdentifier(table_name) ||
            refii::lua::IsLuaKeyword(table_name)) {
            continue;
        }

        const std::size_t brace_open = trimmed.find('{', equal);
        if (brace_open == std::string_view::npos) {
            continue;
        }
        const std::size_t function_assign_pos = trimmed.find("= function(", brace_open);
        if (function_assign_pos == std::string_view::npos) {
            continue;
        }

        const std::string field_name = std::string(
            TrimWhitespace(trimmed.substr(brace_open + 1,
                                          function_assign_pos - (brace_open + 1))));
        if (!refii::lua::IsValidLuaIdentifier(field_name) ||
            refii::lua::IsLuaKeyword(field_name)) {
            continue;
        }

        const std::string function_header =
            std::string(TrimWhitespace(trimmed.substr(function_assign_pos + 2)));
        if (!StartsWith(function_header, "function(")) {
            continue;
        }

        std::size_t end_line = index + 1;
        while (end_line < lines->size()) {
            const std::string_view end_trimmed =
                TrimWhitespace(StripLineComment((*lines)[end_line]));
            if (end_trimmed == "end}" || end_trimmed == "end }") {
                break;
            }
            ++end_line;
        }
        if (end_line >= lines->size() || end_line <= index + 1) {
            continue;
        }

        const std::size_t indent_end = (*lines)[index].find_first_not_of(" \t");
        const std::string indent = indent_end == std::string::npos
                                       ? std::string()
                                       : (*lines)[index].substr(0, indent_end);
        (*lines)[index] = indent + "local " + table_name + " = {}";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(index + 1),
                      indent + table_name + "." + field_name + " = " + function_header);
        (*lines)[end_line + 1] = indent + "end";
        changed = true;
        index = end_line + 1;
    }

    if (!changed) {
        return false;
    }

    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_inline_table_function_field_normalized");
    }
    return true;
}

bool TryRepairInlineTableFunctionPrematureEndBreakage(std::string& source,
                                                      std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    bool changed = false;

    for (std::size_t start = 0; start < lines->size(); ++start) {
        const std::string_view start_trimmed =
            TrimWhitespace(StripLineComment((*lines)[start]));
        if (start_trimmed.find("= {") == std::string_view::npos ||
            start_trimmed.find("function(") == std::string_view::npos) {
            continue;
        }

        std::size_t early_end_line = std::string::npos;
        std::size_t closing_end_brace_line = std::string::npos;
        bool has_statement_after_early_end = false;

        for (std::size_t probe = start + 1; probe < lines->size(); ++probe) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment((*lines)[probe]));
            if (trimmed == "end" && early_end_line == std::string::npos) {
                early_end_line = probe;
                continue;
            }
            if (trimmed == "end}" || trimmed == "end }") {
                closing_end_brace_line = probe;
                break;
            }
            if (early_end_line != std::string::npos && !trimmed.empty()) {
                has_statement_after_early_end = true;
            }
        }

        if (early_end_line == std::string::npos ||
            closing_end_brace_line == std::string::npos ||
            early_end_line >= closing_end_brace_line ||
            !has_statement_after_early_end) {
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(early_end_line));
        changed = true;
        if (closing_end_brace_line > 0) {
            start = closing_end_brace_line - 1;
        }
    }

    if (!changed) {
        return false;
    }

    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_inline_table_function_end_rebalanced");
    }
    return true;
}

bool TryStripSyntheticTopLevelFunctionWrapper(std::string& source, std::string* note) {
    refii::lua::internal::LuaMutableTextContext text_context(source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    if (lines->size() < 3) {
        return false;
    }

    const auto first_non_empty = [&]() -> std::size_t {
        for (std::size_t i = 0; i < lines->size(); ++i) {
            if (!TrimWhitespace(StripLineComment((*lines)[i])).empty()) {
                return i;
            }
        }
        return lines->size();
    }();
    if (first_non_empty >= lines->size()) {
        return false;
    }
    const auto last_non_empty = [&]() -> std::size_t {
        for (std::size_t i = lines->size(); i > 0; --i) {
            if (!TrimWhitespace(StripLineComment((*lines)[i - 1])).empty()) {
                return i - 1;
            }
        }
        return lines->size();
    }();
    if (last_non_empty >= lines->size() || first_non_empty >= last_non_empty) {
        return false;
    }

    const std::string_view head =
        TrimWhitespace(StripLineComment((*lines)[first_non_empty]));
    const std::string_view tail =
        TrimWhitespace(StripLineComment((*lines)[last_non_empty]));
    if (!(StartsWith(head, "function(") || StartsWith(head, "function (")) ||
        tail != "end") {
        return false;
    }

    for (std::size_t i = 0; i < first_non_empty; ++i) {
        if (!TrimWhitespace(StripLineComment((*lines)[i])).empty()) {
            return false;
        }
    }
    for (std::size_t i = last_non_empty + 1; i < lines->size(); ++i) {
        if (!TrimWhitespace(StripLineComment((*lines)[i])).empty()) {
            return false;
        }
    }

    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(last_non_empty));
    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(first_non_empty));
    text_context.MarkLinesMutated();
    source = std::string(text_context.EnsureSource());
    if (note != nullptr) {
        *note = JoinNotes(*note, "raw_invalid_synthetic_root_wrapper_stripped");
    }
    return true;
}

}  // namespace refii::lua::internal
