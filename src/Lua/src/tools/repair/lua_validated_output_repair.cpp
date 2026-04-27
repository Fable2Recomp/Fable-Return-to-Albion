#include "tools/lua_decompiler_internal.h"

#include <algorithm>
#include <cctype>
#include <cstddef>
#include <limits>
#include <sstream>
#include <string>
#include <string_view>
#include <tuple>
#include <vector>

namespace refii::lua::internal {
namespace {

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

bool LooksLikeTopLevelLeakageLine(std::string_view trimmed) {
    if (trimmed.empty() || StartsWith(trimmed, "--")) {
        return false;
    }

    if (trimmed == "return" || StartsWith(trimmed, "return ")) {
        return true;
    }

    const auto first_token = [&](std::string_view text) {
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

std::vector<std::string_view> CollectTopLevelLeakageBindings(
    const std::vector<std::string>& lines,
    std::size_t start_line,
    std::size_t end_line) {
    std::vector<std::string_view> bindings;
    if (start_line >= lines.size() || end_line >= lines.size() || start_line > end_line) {
        return bindings;
    }

    for (std::size_t i = start_line; i <= end_line; ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (trimmed.empty() || StartsWith(trimmed, "--")) {
            continue;
        }

        const std::string_view local_name = ExtractLocalDeclarationName(trimmed);
        if (!local_name.empty() && LooksLikeDecompilerTemporaryName(local_name)) {
            bindings.push_back(local_name);
            continue;
        }

        const std::string_view leading = ExtractLeadingIdentifier(trimmed);
        if (!leading.empty() && LooksLikeDecompilerTemporaryName(leading) &&
            trimmed.find('=') != std::string_view::npos) {
            bindings.push_back(leading);
        }
    }

    return bindings;
}

bool IsSafeToStripTopLevelLeakageGroup(const std::vector<std::string>& lines,
                                       std::size_t start_line,
                                       std::size_t end_line) {
    const std::vector<std::string_view> bindings =
        CollectTopLevelLeakageBindings(lines, start_line, end_line);
    if (bindings.empty()) {
        return true;
    }

    for (std::size_t probe = end_line + 1; probe < lines.size(); ++probe) {
        const std::string_view stripped = StripLineComment(lines[probe]);
        const std::string_view trimmed = TrimWhitespace(stripped);
        if (trimmed.empty()) {
            continue;
        }

        for (const std::string_view binding : bindings) {
            if (ContainsIdentifierReference(stripped, binding)) {
                return false;
            }
        }
    }

    return true;
}

bool IsRootIndentedLine(const std::string& line) {
    const std::size_t first = line.find_first_not_of(" \t");
    return first != std::string::npos && first == 0;
}

}  // namespace

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
    summary.suppressed_control_count =
        CountSubstring(source, "-- refii-suppressed-control:");
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

bool IsLuaOutputQualityNoWorse(const LuaOutputQualitySummary& baseline,
                               const LuaOutputQualitySummary& candidate) {
    const auto quality_tuple = [](const LuaOutputQualitySummary& quality) {
        return std::tuple(quality.marker_summary.decompiler_error_count,
                          quality.marker_summary.repeated_control_flow_count,
                          quality.marker_summary.suppressed_control_count,
                          quality.undefined_local_warning_count,
                          quality.missing_end_warning_count,
                          quality.do_return_end_count);
    };
    return quality_tuple(candidate) <= quality_tuple(baseline);
}

bool IsLuaOutputQualityBetter(const LuaOutputQualitySummary& baseline,
                              const LuaOutputQualitySummary& candidate) {
    const auto quality_tuple = [](const LuaOutputQualitySummary& quality) {
        return std::tuple(quality.marker_summary.decompiler_error_count,
                          quality.marker_summary.repeated_control_flow_count,
                          quality.marker_summary.suppressed_control_count,
                          quality.undefined_local_warning_count,
                          quality.missing_end_warning_count,
                          quality.do_return_end_count);
    };
    return quality_tuple(candidate) < quality_tuple(baseline);
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
    if (summary.marker_summary.suppressed_control_count != 0) {
        parts.push_back("suppressed_control=" +
                        std::to_string(summary.marker_summary.suppressed_control_count));
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

bool TryRepairValidatedTopLevelFunctionBoundaryBleed(std::string& decompiled_source,
                                                     std::string* note) {
    const LuaOutputQualitySummary baseline_quality =
        SummarizeLuaOutputQuality(decompiled_source);
    if (baseline_quality.marker_summary.decompiler_error_count == 0 &&
        baseline_quality.undefined_local_warning_count == 0 &&
        baseline_quality.missing_end_warning_count == 0) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return TrimWhitespace(StripLineComment(line));
    };
    const auto quality_tuple = [](const LuaOutputQualitySummary& quality) {
        return std::tuple(quality.marker_summary.decompiler_error_count,
                          quality.marker_summary.repeated_control_flow_count,
                          quality.marker_summary.suppressed_control_count,
                          quality.undefined_local_warning_count,
                          quality.missing_end_warning_count,
                          quality.do_return_end_count);
    };
    const auto suppressed_end_run = [&](std::size_t boundary_line,
                                        std::size_t* run_begin,
                                        std::size_t* run_end) -> std::size_t {
        if (run_begin != nullptr) {
            *run_begin = lines.size();
        }
        if (run_end != nullptr) {
            *run_end = lines.size();
        }
        if (boundary_line == 0 || boundary_line > lines.size()) {
            return 0;
        }

        std::size_t current = boundary_line;
        while (current > 0) {
            const std::size_t probe = FindPreviousNonEmptyLuaLine(lines, current);
            if (probe >= lines.size()) {
                break;
            }
            const std::string_view raw_trimmed = TrimWhitespace(lines[probe]);
            if (raw_trimmed != "-- refii-suppressed-control: end") {
                break;
            }
            if (run_end != nullptr && *run_end >= lines.size()) {
                *run_end = probe;
            }
            if (run_begin != nullptr) {
                *run_begin = probe;
            }
            current = probe;
            if (probe == 0) {
                break;
            }
        }

        if (run_begin == nullptr || *run_begin >= lines.size() ||
            run_end == nullptr || *run_end >= lines.size() || *run_begin > *run_end) {
            return 0;
        }
        return *run_end - *run_begin + 1;
    };

    std::size_t top_level_indent_size = std::numeric_limits<std::size_t>::max();
    for (const std::string& line : lines) {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty()) {
            continue;
        }
        top_level_indent_size =
            std::min(top_level_indent_size, GetLuaLeadingIndent(line).size());
    }
    if (top_level_indent_size == std::numeric_limits<std::size_t>::max()) {
        top_level_indent_size = 0;
    }
    const std::string top_level_indent(top_level_indent_size, ' ');

    const auto is_top_level_function_assignment = [&](std::size_t line_index) {
        if (line_index >= lines.size() ||
            GetLuaLeadingIndent(lines[line_index]).size() != top_level_indent_size) {
            return false;
        }

        std::string lhs;
        std::string rhs;
        if (!ParseSimpleRenderedAssignment(lines[line_index], nullptr, &lhs, &rhs)) {
            return false;
        }
        rhs = std::string(TrimWhitespace(rhs));
        return StartsWith(rhs, "function(") || StartsWith(rhs, "function ");
    };

    std::string best_candidate;
    LuaOutputQualitySummary best_quality = baseline_quality;
    for (std::size_t boundary_line = 1; boundary_line < lines.size(); ++boundary_line) {
        if (!is_top_level_function_assignment(boundary_line)) {
            continue;
        }

        const std::size_t previous_line = FindPreviousNonEmptyLuaLine(lines, boundary_line);
        std::size_t suppressed_run_begin = lines.size();
        std::size_t suppressed_run_end = lines.size();
        const std::size_t suppressed_run_count =
            suppressed_end_run(boundary_line, &suppressed_run_begin, &suppressed_run_end);
        if (previous_line >= lines.size() && suppressed_run_count == 0) {
            continue;
        }

        const std::string_view previous_trimmed =
            previous_line < lines.size() ? trim_line(lines[previous_line]) : std::string_view();
        const bool previous_is_suppressed_run = suppressed_run_count != 0;
        if (!previous_is_suppressed_run &&
            (previous_trimmed.empty() ||
             (previous_trimmed != "end" &&
              GetLuaLeadingIndent(lines[previous_line]).size() <= top_level_indent_size))) {
            continue;
        }

        const int max_end_count =
            previous_is_suppressed_run
                ? std::max<int>(3, std::min<int>(8, static_cast<int>(suppressed_run_count)))
                : 3;
        for (int end_count = 1; end_count <= max_end_count; ++end_count) {
            std::vector<std::string> candidate_lines = lines;
            if (previous_is_suppressed_run) {
                const std::size_t replace_count =
                    std::min<std::size_t>(static_cast<std::size_t>(end_count),
                                          suppressed_run_count);
                const std::size_t replace_begin = suppressed_run_end - replace_count + 1;
                for (std::size_t offset = 0; offset < replace_count; ++offset) {
                    candidate_lines[replace_begin + offset] = top_level_indent + "end";
                }
                if (static_cast<std::size_t>(end_count) > replace_count) {
                    candidate_lines.insert(
                        candidate_lines.begin() +
                            static_cast<std::ptrdiff_t>(boundary_line),
                        static_cast<std::size_t>(end_count) - replace_count,
                        top_level_indent + "end");
                }
            } else {
                candidate_lines.insert(candidate_lines.begin() +
                                           static_cast<std::ptrdiff_t>(boundary_line),
                                       static_cast<std::size_t>(end_count),
                                       top_level_indent + "end");
            }

            const std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            if (!ValidateLuaRepairSource(candidate).empty()) {
                continue;
            }

            const LuaOutputQualitySummary candidate_quality =
                SummarizeLuaOutputQuality(candidate);
            if (!(quality_tuple(candidate_quality) < quality_tuple(baseline_quality)) ||
                !(quality_tuple(candidate_quality) < quality_tuple(best_quality))) {
                continue;
            }

            best_candidate = candidate;
            best_quality = candidate_quality;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "validated_output_repaired_top_level_function_boundary_bleed");
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

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!IsRootIndentedLine(lines[i]) || !LooksLikeTopLevelLeakageLine(trimmed)) {
            continue;
        }

        const std::size_t previous = FindPreviousNonEmptyLuaLine(lines, i);
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
            if (!IsRootIndentedLine(lines[probe]) ||
                !LooksLikeTopLevelLeakageLine(probe_trimmed)) {
                break;
            }
            end = probe;
            ++probe;
        }

        const std::size_t next = FindNextSignificantLuaLine(lines, end + 1);
        if (next >= lines.size() || !IsRootIndentedLine(lines[next]) ||
            !LooksLikeTopLevelBoundary(TrimWhitespace(lines[next]))) {
            continue;
        }
        if (!IsSafeToStripTopLevelLeakageGroup(lines, i, end)) {
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

}  // namespace refii::lua::internal
