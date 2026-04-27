bool SimplifyRenderedLuaRepeatedWhileTrueWrappers(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 1; line_index < lines->size();) {
        const std::string_view previous_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index - 1]));
        const std::string_view current_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (previous_trimmed == "while true do" && current_trimmed == "while true do" &&
            GetRenderedLuaIndent((*lines)[line_index - 1]) ==
                GetRenderedLuaIndent((*lines)[line_index])) {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            continue;
        }
        ++line_index;
    }

    return changed;
}

bool SimplifyRenderedLuaDanglingCallbackRegistrations(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_noncomment = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            return probe;
        }
        return lines->size();
    };
    const auto try_parse_callback_registration = [&](std::string_view line,
                                                     std::string* params,
                                                     std::string* rewritten) -> bool {
        if (params == nullptr || rewritten == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        const std::size_t function_pos = trimmed.rfind("function(");
        if (function_pos == std::string_view::npos || function_pos == 0 ||
            !trimmed.ends_with("))")) {
            return false;
        }

        const std::size_t params_begin = function_pos + 9;
        const std::size_t params_end = trimmed.find(')', params_begin);
        if (params_end == std::string_view::npos || params_end + 1 >= trimmed.size() ||
            trimmed[params_end + 1] != ')') {
            return false;
        }

        const std::string_view callback_params =
            TrimLuaWhitespace(trimmed.substr(params_begin, params_end - params_begin));
        if (callback_params.empty()) {
            return false;
        }

        *params = std::string(callback_params);
        *rewritten = std::string(trimmed.substr(0, function_pos)) + "callback)";
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view function_trimmed = trim_line((*lines)[line_index]);
        if (function_trimmed.find(" = function(") == std::string_view::npos &&
            !function_trimmed.starts_with("function(")) {
            continue;
        }

        const std::string function_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::size_t registration_indent_size = function_indent.size() + 2;
        const std::size_t body_start = next_nonempty_noncomment(line_index + 1);
        if (body_start >= lines->size() ||
            GetRenderedLuaIndent((*lines)[body_start]).size() <= registration_indent_size) {
            continue;
        }

        std::size_t registration_start = lines->size();
        std::string callback_params;
        std::string first_rewritten_registration;
        for (std::size_t probe = body_start; probe < lines->size(); ++probe) {
            if (GetRenderedLuaIndent((*lines)[probe]).size() != registration_indent_size) {
                continue;
            }
            if (try_parse_callback_registration((*lines)[probe],
                                                &callback_params,
                                                &first_rewritten_registration)) {
                registration_start = probe;
                break;
            }
        }
        if (registration_start >= lines->size()) {
            continue;
        }

        std::vector<std::string> rewritten_registrations;
        rewritten_registrations.push_back(function_indent + "  " +
                                          first_rewritten_registration);
        std::size_t registration_end = registration_start + 1;
        while (registration_end < lines->size() &&
               GetRenderedLuaIndent((*lines)[registration_end]).size() ==
                   registration_indent_size) {
            std::string extra_params;
            std::string rewritten;
            if (!try_parse_callback_registration(
                    (*lines)[registration_end], &extra_params, &rewritten) ||
                extra_params != callback_params) {
                break;
            }
            rewritten_registrations.push_back(function_indent + "  " + rewritten);
            ++registration_end;
        }
        if (rewritten_registrations.size() < 2) {
            continue;
        }

        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(body_start),
                      function_indent + "  local callback = function(" + callback_params + ")");
        ++line_index;
        ++registration_start;
        ++registration_end;

        const std::string callback_footer = function_indent + "  end";
        std::size_t previous_nonempty = registration_start;
        while (previous_nonempty > 0) {
            --previous_nonempty;
            if (!trim_line((*lines)[previous_nonempty]).empty()) {
                break;
            }
        }
        if (registration_start == 0 ||
            trim_line((*lines)[previous_nonempty]) != TrimLuaWhitespace(callback_footer)) {
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(registration_start),
                          callback_footer);
            ++registration_start;
            ++registration_end;
        }

        for (std::size_t rewritten_index = 0;
             rewritten_index < rewritten_registrations.size();
             ++rewritten_index) {
            (*lines)[registration_start + rewritten_index] =
                rewritten_registrations[rewritten_index];
        }

        changed = true;
        line_index = registration_end;
    }

    return changed;
}

bool SimplifyRenderedLuaDanglingEofClosures(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    int outstanding_blocks = 0;
    for (const std::string& line : *lines) {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (trimmed.empty()) {
            continue;
        }
        outstanding_blocks += GetRenderedLuaBlockDelta(trimmed);
    }

    if (outstanding_blocks <= 0 || outstanding_blocks > 6) {
        return false;
    }

    while (outstanding_blocks-- > 0) {
        lines->push_back("end");
    }
    return true;
}

bool SimplifyRenderedLuaThreadRegistrationSiblingFunctionEnds(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto looks_like_thread_registration = [&](std::string_view line) -> bool {
        const std::string_view trimmed = trim_line(line);
        return trimmed.starts_with("QuestManager.NewQuestThread(") ||
               trimmed.starts_with("QuestManager.NewEntityThread(");
    };

    bool changed = false;
    constexpr std::string_view kStepName =
        "SimplifyRenderedLuaThreadRegistrationSiblingFunctionEnds";
    RunRenderedLuaCleanupPassLoop(kStepName, 48, [&]() {
        const std::size_t eof_line = FindRenderedLastNonEmptyLine(*lines, 0, lines->size());
        if (eof_line >= lines->size() || trim_line((*lines)[eof_line]) != "end") {
            return false;
        }

        for (std::size_t line_index = 0; line_index < eof_line; ++line_index) {
            const std::string_view function_trimmed = trim_line((*lines)[line_index]);
            if (function_trimmed.find(" = function(") == std::string_view::npos &&
                !function_trimmed.starts_with("function(")) {
                continue;
            }

            const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, line_index);
            if (function_end != eof_line) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            std::size_t registration_line = lines->size();
            for (std::size_t probe = line_index + 1; probe < eof_line; ++probe) {
                if (trim_line((*lines)[probe]).empty()) {
                    continue;
                }
                if (GetRenderedLuaIndent((*lines)[probe]) != indent) {
                    continue;
                }
                if (looks_like_thread_registration((*lines)[probe])) {
                    registration_line = probe;
                    break;
                }
            }
            if (registration_line >= eof_line) {
                continue;
            }

            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(registration_line),
                          indent + "end");
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(eof_line + 1));
            changed = true;
            return true;
        }
        return false;
    });

    return changed;
}

bool SimplifyRenderedLuaEmptyLoopExitGuards(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto looks_like_top_level_function_assignment = [&](std::string_view line) -> bool {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.starts_with("local function ")) {
            return true;
        }
        return trimmed.find(" = function(") != std::string_view::npos;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        const std::string_view trimmed = trim_line((*lines)[line_index]);
        if (!trimmed.starts_with("if ") || !trimmed.ends_with(" then")) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        if (trim_line((*lines)[line_index + 1]) != "end" ||
            GetRenderedLuaIndent((*lines)[line_index + 1]) != indent) {
            continue;
        }

        std::size_t end_run_start = line_index + 2;
        std::size_t end_run_end = end_run_start;
        while (end_run_end < lines->size() &&
               trim_line((*lines)[end_run_end]) == "end" &&
               GetRenderedLuaIndent((*lines)[end_run_end]) == indent) {
            ++end_run_end;
        }
        if (end_run_end - end_run_start < 2 || end_run_end >= lines->size() ||
            !looks_like_top_level_function_assignment((*lines)[end_run_end])) {
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(end_run_start));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}
