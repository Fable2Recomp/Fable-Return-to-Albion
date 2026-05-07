#include "tools/decompiler/rendered/rendered_call_parser.h"

#include "tools/decompiler/lua_decompiler_internal.h"

namespace refii::lua::internal {

bool ParseSimpleRenderedCall(std::string_view expression,
                             std::string* callee_out,
                             std::vector<std::string>* arguments_out) {
    if (callee_out == nullptr || arguments_out == nullptr) {
        return false;
    }

    expression = TrimWhitespace(expression);
    if (expression.empty() || !expression.ends_with(")")) {
        return false;
    }

    const std::size_t open = expression.find('(');
    if (open == std::string_view::npos || open == 0) {
        return false;
    }

    int depth = 0;
    std::size_t close = std::string_view::npos;
    for (std::size_t index = open; index < expression.size(); ++index) {
        const char ch = expression[index];
        if (ch == '(') {
            ++depth;
        } else if (ch == ')') {
            --depth;
            if (depth == 0) {
                close = index;
                break;
            }
        }
    }
    if (close == std::string_view::npos || close + 1 != expression.size()) {
        return false;
    }

    *callee_out = std::string(TrimWhitespace(expression.substr(0, open)));
    arguments_out->clear();
    std::string_view args = expression.substr(open + 1, close - open - 1);
    std::size_t begin = 0;
    depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    for (std::size_t index = 0; index <= args.size(); ++index) {
        const bool at_end = index == args.size();
        const char ch = at_end ? ',' : args[index];
        if (!at_end) {
            if (ch == '(') {
                ++depth;
            } else if (ch == ')') {
                --depth;
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
        if ((at_end || ch == ',') && depth == 0 && brace_depth == 0 && bracket_depth == 0) {
            arguments_out->push_back(
                std::string(TrimWhitespace(args.substr(begin, index - begin))));
            begin = index + 1;
        }
    }
    return !callee_out->empty() && !arguments_out->empty();
}

}  // namespace refii::lua::internal
