#include "tools/decompiler/rendered/shared_table_cleanup_repairs.h"

#include "tools/decompiler/rendered/rendered_call_parser.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <algorithm>
#include <cctype>
#include <string_view>
#include <unordered_map>

namespace refii::lua::internal {
namespace {

bool IsSimpleTempIdentifier(std::string_view value) {
    value = TrimWhitespace(value);
    if (value.empty() || value.front() != 'r' || value.size() <= 1) {
        return false;
    }
    return std::all_of(value.begin() + 1, value.end(), [](char ch) {
        return std::isdigit(static_cast<unsigned char>(ch)) != 0;
    });
}

bool ExtractBaseTempIdentifierFromLhs(std::string_view lhs, std::string* temp_name_out) {
    if (temp_name_out == nullptr) {
        return false;
    }

    lhs = TrimWhitespace(lhs);
    if (lhs.empty()) {
        return false;
    }

    const std::size_t dot = lhs.find('.');
    const std::size_t bracket = lhs.find('[');
    std::size_t split = std::string_view::npos;
    if (dot != std::string_view::npos) {
        split = dot;
    }
    if (bracket != std::string_view::npos &&
        (split == std::string_view::npos || bracket < split)) {
        split = bracket;
    }
    if (split == std::string_view::npos) {
        return false;
    }

    const std::string_view base = TrimWhitespace(lhs.substr(0, split));
    if (!IsSimpleTempIdentifier(base)) {
        return false;
    }

    *temp_name_out = std::string(base);
    return true;
}

bool LooksLikeEmptyTableLiteral(std::string_view rhs) {
    rhs = TrimWhitespace(rhs);
    if (rhs.size() < 2 || rhs.front() != '{' || rhs.back() != '}') {
        return false;
    }
    rhs = TrimWhitespace(rhs.substr(1, rhs.size() - 2));
    return rhs.empty();
}

std::string ExtractSingleElementTableLiteral(std::string_view rhs) {
    rhs = TrimWhitespace(rhs);
    if (rhs.size() < 2 || rhs.front() != '{' || rhs.back() != '}') {
        return {};
    }

    std::string_view inner = TrimWhitespace(rhs.substr(1, rhs.size() - 2));
    if (inner.empty()) {
        return {};
    }

    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    bool saw_content = false;
    for (std::size_t index = 0; index < inner.size(); ++index) {
        const char ch = inner[index];
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
        } else if (ch == ',' && paren_depth == 0 && brace_depth == 0 &&
                   bracket_depth == 0) {
            const std::string_view tail = TrimWhitespace(inner.substr(index + 1));
            if (tail.empty()) {
                return std::string(TrimWhitespace(inner.substr(0, index)));
            }
            return {};
        }

        if (std::isspace(static_cast<unsigned char>(ch)) == 0) {
            saw_content = true;
        }
    }

    return saw_content ? std::string(inner) : std::string();
}

std::string BuildTableBuilderSignature(std::string_view rhs) {
    const std::string single_element = ExtractSingleElementTableLiteral(rhs);
    if (single_element.empty()) {
        rhs = TrimWhitespace(rhs);
        if (!rhs.starts_with("{")) {
            return {};
        }
        std::string_view inner = TrimWhitespace(rhs.substr(1));
        const std::size_t open = inner.find('(');
        if (open == std::string_view::npos) {
            return {};
        }
        const std::string callee(TrimWhitespace(inner.substr(0, open)));
        if (callee.empty()) {
            return {};
        }

        std::string_view args = inner.substr(open + 1);
        int paren_depth = 0;
        int brace_depth = 0;
        int bracket_depth = 0;
        for (std::size_t index = 0; index < args.size(); ++index) {
            const char ch = args[index];
            if (ch == '(') {
                ++paren_depth;
            } else if (ch == ')') {
                if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0) {
                    const std::string first_arg(TrimWhitespace(args.substr(0, index)));
                    return first_arg.empty() ? std::string() : callee + "|" + first_arg;
                }
                --paren_depth;
            } else if (ch == '{') {
                ++brace_depth;
            } else if (ch == '}') {
                --brace_depth;
            } else if (ch == '[') {
                ++bracket_depth;
            } else if (ch == ']') {
                --bracket_depth;
            } else if (ch == ',' && paren_depth == 0 && brace_depth == 0 &&
                       bracket_depth == 0) {
                const std::string first_arg(TrimWhitespace(args.substr(0, index)));
                return first_arg.empty() ? std::string() : callee + "|" + first_arg;
            }
        }
        return {};
    }

    std::string callee;
    std::vector<std::string> arguments;
    if (!ParseSimpleRenderedCall(single_element, &callee, &arguments) || arguments.empty()) {
        return {};
    }

    return callee + "|" + arguments.front();
}

}  // namespace

bool TryRepairTableBuilderAssignmentCarries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct TableBuilderState {
        std::string name;
        std::string signature;
        std::size_t line_index = 0;
        std::size_t scope_begin = std::string::npos;
        std::size_t scope_end = std::string::npos;
        bool populated = false;
        bool consumed = false;
        bool saw_named_field_mutation = false;
        bool saw_index_mutation = false;
    };

    std::vector<TableBuilderState> builders;
    std::unordered_map<std::string, std::size_t> latest_builder_by_name;
    bool changed = false;

    const auto find_latest_builder = [&](std::string_view name) -> TableBuilderState* {
        const auto it = latest_builder_by_name.find(std::string(name));
        if (it == latest_builder_by_name.end() || it->second >= builders.size()) {
            return nullptr;
        }
        return &builders[it->second];
    };
    const auto find_scope_for_line =
        [&](std::size_t line_index) -> std::pair<std::size_t, std::size_t> {
        for (std::size_t candidate = line_index + 1; candidate-- > 0;) {
            const std::string_view trimmed =
                TrimWhitespace(StripLineComment((*lines)[candidate]));
            if (!refii::lua::IsRenderedFunctionStartLine(trimmed)) {
                continue;
            }
            const std::size_t function_end =
                refii::lua::FindRenderedFunctionBlockEnd(*lines, candidate);
            if (function_end < lines->size() && function_end >= line_index) {
                return {candidate, function_end};
            }
        }
        return {std::string::npos, lines->size()};
    };

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string indent;
        std::string lhs;
        std::string rhs;
        if (!ParseSimpleRenderedAssignment((*lines)[line_index], &indent, &lhs, &rhs)) {
            continue;
        }

        const std::string_view trimmed_lhs = TrimWhitespace(lhs);
        const std::string_view trimmed_rhs = TrimWhitespace(rhs);

        if (IsSimpleTempIdentifier(trimmed_lhs) && trimmed_rhs.starts_with("{")) {
            TableBuilderState builder;
            builder.name = std::string(trimmed_lhs);
            builder.signature = BuildTableBuilderSignature(trimmed_rhs);
            builder.line_index = line_index;
            const auto [scope_begin, scope_end] = find_scope_for_line(line_index);
            builder.scope_begin = scope_begin;
            builder.scope_end = scope_end;
            builder.populated = !LooksLikeEmptyTableLiteral(trimmed_rhs);
            builders.push_back(std::move(builder));
            latest_builder_by_name[std::string(trimmed_lhs)] = builders.size() - 1;
            continue;
        }

        const auto [current_scope_begin, current_scope_end] = find_scope_for_line(line_index);
        std::string mutation_base;
        if (ExtractBaseTempIdentifierFromLhs(trimmed_lhs, &mutation_base)) {
            if (TableBuilderState* builder = find_latest_builder(mutation_base)) {
                if (builder->scope_begin != current_scope_begin ||
                    builder->scope_end != current_scope_end) {
                    continue;
                }
                builder->populated = true;
                builder->line_index = line_index;
                if (trimmed_lhs.find('[') != std::string_view::npos) {
                    builder->saw_index_mutation = true;
                } else {
                    builder->saw_named_field_mutation = true;
                }
            }
        }

        if (IsSimpleTempIdentifier(trimmed_rhs) && trimmed_lhs != trimmed_rhs) {
            if (TableBuilderState* builder = find_latest_builder(trimmed_rhs)) {
                if (builder->scope_begin == current_scope_begin &&
                    builder->scope_end == current_scope_end) {
                    builder->consumed = true;
                }
            }
            continue;
        }

        if (IsSimpleTempIdentifier(trimmed_lhs)) {
            continue;
        }

        const bool rhs_is_empty_table = LooksLikeEmptyTableLiteral(trimmed_rhs);
        const std::string rhs_signature = BuildTableBuilderSignature(trimmed_rhs);
        if (!rhs_is_empty_table && rhs_signature.empty()) {
            continue;
        }

        const bool prefer_nearest_builder =
            !rhs_signature.empty() || trimmed_lhs.find(".Actions") != std::string_view::npos;
        const bool target_is_actions =
            trimmed_lhs.find(".Actions") != std::string_view::npos;
        const bool target_is_indexed =
            trimmed_lhs.find('[') != std::string_view::npos;
        TableBuilderState* replacement = nullptr;
        if (prefer_nearest_builder) {
            for (std::size_t builder_index = builders.size(); builder_index > 0; --builder_index) {
                TableBuilderState& candidate = builders[builder_index - 1];
                if (candidate.consumed || !candidate.populated ||
                    candidate.line_index >= line_index ||
                    candidate.scope_begin != current_scope_begin ||
                    candidate.scope_end != current_scope_end) {
                    continue;
                }
                if (!rhs_signature.empty() && candidate.signature != rhs_signature) {
                    continue;
                }
                if (target_is_actions && candidate.saw_named_field_mutation &&
                    !candidate.saw_index_mutation) {
                    continue;
                }
                replacement = &candidate;
                break;
            }
        } else {
            for (std::size_t builder_index = 0; builder_index < builders.size(); ++builder_index) {
                TableBuilderState& candidate = builders[builder_index];
                if (candidate.consumed || !candidate.populated ||
                    candidate.line_index >= line_index ||
                    candidate.scope_begin != current_scope_begin ||
                    candidate.scope_end != current_scope_end) {
                    continue;
                }
                if (target_is_indexed && !candidate.saw_named_field_mutation) {
                    continue;
                }
                replacement = &candidate;
                break;
            }
        }
        if (replacement == nullptr) {
            continue;
        }

        (*lines)[line_index] = indent + lhs + " = " + replacement->name;
        replacement->consumed = true;
        changed = true;
    }

    return changed;
}

}  // namespace refii::lua::internal
