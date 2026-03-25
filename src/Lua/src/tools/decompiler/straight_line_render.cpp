#include "tools/decompiler/straight_line_render.h"

#include "tools/decompiler/rendered_lua_text.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cctype>
#include <utility>

namespace refii::lua {
namespace {

std::string_view TrimLuaJoinOuterParens(std::string_view expression) {
    std::string_view trimmed = TrimLuaWhitespace(expression);
    while (trimmed.size() >= 2 && trimmed.front() == '(' && trimmed.back() == ')') {
        int depth = 0;
        bool wraps_whole_expression = true;
        for (std::size_t i = 0; i < trimmed.size(); ++i) {
            const char ch = trimmed[i];
            if (ch == '(') {
                ++depth;
            } else if (ch == ')') {
                --depth;
                if (depth < 0) {
                    wraps_whole_expression = false;
                    break;
                }
                if (depth == 0 && i + 1 != trimmed.size()) {
                    wraps_whole_expression = false;
                    break;
                }
            }
        }
        if (!wraps_whole_expression || depth != 0) {
            break;
        }
        trimmed = TrimLuaWhitespace(trimmed.substr(1, trimmed.size() - 2));
    }
    return trimmed;
}

bool IsLuaRenderedNumericLiteral(std::string_view expression) {
    const std::string_view trimmed = TrimLuaWhitespace(expression);
    if (trimmed.empty()) {
        return false;
    }

    std::string candidate(trimmed);
    char* end = nullptr;
    const double parsed = std::strtod(candidate.c_str(), &end);
    (void)parsed;
    return end != candidate.c_str() && end == candidate.c_str() + candidate.size();
}

bool ParseLuaRenderedStringLiteral(std::string_view expression, std::string* value) {
    if (value == nullptr || expression.size() < 2) {
        return false;
    }

    const char quote = expression.front();
    if ((quote != '"' && quote != '\'') || expression.back() != quote) {
        return false;
    }

    value->clear();
    value->reserve(expression.size() - 2);
    for (std::size_t i = 1; i + 1 < expression.size(); ++i) {
        const char ch = expression[i];
        if (ch == '\\') {
            if (i + 2 > expression.size()) {
                return false;
            }
            const char escaped = expression[++i];
            switch (escaped) {
                case '\\':
                    value->push_back('\\');
                    break;
                case '"':
                    value->push_back('"');
                    break;
                case '\'':
                    value->push_back('\'');
                    break;
                case 'n':
                    value->push_back('\n');
                    break;
                case 'r':
                    value->push_back('\r');
                    break;
                case 't':
                    value->push_back('\t');
                    break;
                default:
                    return false;
            }
            continue;
        }
        if (ch == quote || ch == '\n' || ch == '\r') {
            return false;
        }
        value->push_back(ch);
    }

    return true;
}

std::size_t FindLuaTopLevelDot(std::string_view expression) {
    int paren_depth = 0;
    int bracket_depth = 0;
    for (std::size_t i = expression.size(); i > 0; --i) {
        const char ch = expression[i - 1];
        if (ch == ')') {
            ++paren_depth;
            continue;
        }
        if (ch == '(') {
            --paren_depth;
            continue;
        }
        if (ch == ']') {
            ++bracket_depth;
            continue;
        }
        if (ch == '[') {
            --bracket_depth;
            continue;
        }
        if (ch == '.' && paren_depth == 0 && bracket_depth == 0) {
            return i - 1;
        }
    }
    return std::string_view::npos;
}

bool FindLuaTrailingBracketAccess(std::string_view expression,
                                  std::string_view* base_expression,
                                  std::string_view* key_expression) {
    if (base_expression == nullptr || key_expression == nullptr) {
        return false;
    }

    const std::string_view trimmed = TrimLuaJoinOuterParens(expression);
    if (trimmed.size() < 3 || trimmed.back() != ']') {
        return false;
    }

    int bracket_depth = 0;
    int paren_depth = 0;
    for (std::size_t i = trimmed.size(); i > 0; --i) {
        const char ch = trimmed[i - 1];
        if (ch == ']') {
            ++bracket_depth;
            continue;
        }
        if (ch == '[') {
            --bracket_depth;
            if (bracket_depth == 0 && paren_depth == 0) {
                *base_expression = TrimLuaWhitespace(trimmed.substr(0, i - 1));
                *key_expression =
                    TrimLuaWhitespace(trimmed.substr(i, trimmed.size() - i - 1));
                return !base_expression->empty() && !key_expression->empty();
            }
            continue;
        }
        if (ch == ')') {
            ++paren_depth;
            continue;
        }
        if (ch == '(') {
            --paren_depth;
            continue;
        }
    }

    return false;
}

bool TrySplitRenderedMethodCallee(std::string_view callee,
                                  std::string* receiver,
                                  std::string* method) {
    if (receiver == nullptr || method == nullptr) {
        return false;
    }

    const std::string_view trimmed = TrimLuaWhitespace(callee);
    if (trimmed.empty()) {
        return false;
    }

    if (trimmed.size() >= 4) {
        const std::size_t bracket = trimmed.rfind("[\"");
        if (bracket != std::string_view::npos && trimmed.ends_with("\"]")) {
            const std::string_view key(trimmed.data() + bracket + 2,
                                       trimmed.size() - (bracket + 4));
            if (IsValidLuaIdentifier(key)) {
                *receiver = std::string(TrimLuaWhitespace(trimmed.substr(0, bracket)));
                *method = std::string(key);
                return !receiver->empty();
            }
        }
    }

    const std::size_t dot = trimmed.rfind('.');
    if (dot == std::string_view::npos || dot == 0 || dot + 1 >= trimmed.size()) {
        return false;
    }

    const std::string_view candidate_method = trimmed.substr(dot + 1);
    if (!IsValidLuaIdentifier(candidate_method)) {
        return false;
    }

    *receiver = std::string(TrimLuaWhitespace(trimmed.substr(0, dot)));
    *method = std::string(candidate_method);
    return !receiver->empty();
}

}  // namespace

RegisterStates::RegisterStates(int registers, int lines)
    : registers_(registers),
      lines_(lines),
      states_(static_cast<std::size_t>(registers) * static_cast<std::size_t>(lines)) {}

RegisterState& RegisterStates::Get(int reg, int line) {
    return states_[Index(reg, line)];
}

void RegisterStates::SetLocal(int reg, int line) {
    for (int r = 0; r <= reg && r < registers_; ++r) {
        Get(r, line).local = true;
    }
}

void RegisterStates::SetTemporary(int reg, int line) {
    for (int r = std::max(0, reg); r < registers_; ++r) {
        Get(r, line).temporary = true;
    }
}

std::size_t RegisterStates::Index(int reg, int line) const {
    return static_cast<std::size_t>(line - 1) * static_cast<std::size_t>(registers_) +
           static_cast<std::size_t>(reg);
}

RegisterDeclarations::RegisterDeclarations(int registers,
                                           int lines,
                                           std::vector<Lua51Declaration>* declarations)
    : registers_(registers),
      lines_(lines),
      declarations_(declarations),
      slots_(static_cast<std::size_t>(registers) * static_cast<std::size_t>(lines + 1), nullptr) {
    if (declarations_ == nullptr || registers_ <= 0) {
        return;
    }

    for (Lua51Declaration& declaration : *declarations_) {
        const int start = ClampLine(static_cast<int>(declaration.begin));
        const int end = std::max(start, ClampLine(static_cast<int>(declaration.end)));
        int reg = 0;
        while (reg < registers_ && Get(reg, start) != nullptr) {
            ++reg;
        }
        if (reg >= registers_) {
            reg = registers_ - 1;
        }

        declaration.reg = static_cast<uint32_t>(reg);
        for (int line = start; line <= end; ++line) {
            slots_[Index(reg, line)] = &declaration;
        }
    }
}

const Lua51Declaration* RegisterDeclarations::Get(int reg, int line) const {
    if (reg < 0 || reg >= registers_) {
        return nullptr;
    }
    return slots_[Index(reg, ClampLine(line))];
}

bool RegisterDeclarations::IsLocal(int reg, int line) const {
    return Get(reg, line) != nullptr;
}

bool RegisterDeclarations::IsNewLocal(int reg, int line) const {
    const Lua51Declaration* declaration = Get(reg, line);
    return declaration != nullptr && static_cast<int>(declaration->begin) == ClampLine(line);
}

int RegisterDeclarations::ClampLine(int line) const {
    return std::clamp(line, 0, lines_);
}

std::size_t RegisterDeclarations::Index(int reg, int line) const {
    return static_cast<std::size_t>(line) * static_cast<std::size_t>(registers_) +
           static_cast<std::size_t>(reg);
}

RegisterValues::RegisterValues(int registers,
                               int lines,
                               const RegisterDeclarations* declarations)
    : registers_(registers),
      lines_(lines),
      declarations_(declarations),
      values_(static_cast<std::size_t>(registers) * static_cast<std::size_t>(lines + 1)),
      updated_(static_cast<std::size_t>(registers) * static_cast<std::size_t>(lines + 1), 0) {
    RegisterValue nil;
    nil.kind = Lua51RegisterValueKind::kNil;
    for (int reg = 0; reg < registers_; ++reg) {
        values_[Index(reg, 0)] = nil;
    }
}

void RegisterValues::StartLine(int line) {
    const int clamped = ClampLine(line);
    const int previous = std::max(0, clamped - 1);
    for (int reg = 0; reg < registers_; ++reg) {
        values_[Index(reg, clamped)] = values_[Index(reg, previous)];
        updated_[Index(reg, clamped)] = updated_[Index(reg, previous)];
    }
}

RegisterValue RegisterValues::GetExpression(int reg, int line) const {
    if (reg < 0 || reg >= registers_) {
        return {};
    }

    const int previous = std::max(0, ClampLine(line) - 1);
    if (declarations_ != nullptr && declarations_->IsLocal(reg, previous)) {
        RegisterValue value;
        value.kind = Lua51RegisterValueKind::kLocalReference;
        value.source_register = reg;
        return value;
    }

    return values_[Index(reg, previous)];
}

RegisterValue RegisterValues::GetKExpression(int reg, int line) const {
    if (IsConstantReference(reg)) {
        RegisterValue value;
        value.kind = Lua51RegisterValueKind::kConstant;
        return value;
    }
    return GetExpression(reg, line);
}

RegisterValue RegisterValues::GetValue(int reg, int line) const {
    if (reg < 0 || reg >= registers_) {
        return {};
    }
    return values_[Index(reg, std::max(0, ClampLine(line) - 1))];
}

int RegisterValues::GetUpdated(int reg, int line) const {
    if (reg < 0 || reg >= registers_) {
        return 0;
    }
    return updated_[Index(reg, std::max(0, ClampLine(line) - 1))];
}

void RegisterValues::SetValue(int reg, int line, RegisterValue value) {
    if (reg < 0 || reg >= registers_) {
        return;
    }
    const int clamped = ClampLine(line);
    values_[Index(reg, clamped)] = value;
    updated_[Index(reg, clamped)] = clamped;
}

int RegisterValues::ClampLine(int line) const {
    return std::clamp(line, 0, lines_);
}

std::size_t RegisterValues::Index(int reg, int line) const {
    return static_cast<std::size_t>(line) * static_cast<std::size_t>(registers_) +
           static_cast<std::size_t>(reg);
}

bool IsConstantReference(int value) {
    return (value & 0x100) != 0;
}

bool IsLoadBoolPair(const std::vector<Lua51Instruction>& instructions, int current_line) {
    if (current_line < 1 || static_cast<std::size_t>(current_line + 1) > instructions.size()) {
        return false;
    }

    const Lua51Instruction& first =
        instructions[static_cast<std::size_t>(current_line - 1)];
    const Lua51Instruction& second =
        instructions[static_cast<std::size_t>(current_line)];
    return first.opcode == Lua51Opcode::kLoadBool && first.c != 0 &&
           second.opcode == Lua51Opcode::kLoadBool && second.c == 0 &&
           second.a == first.a;
}

std::string MakeDeclarationName(char prefix, uint32_t reg, uint32_t index) {
    return std::string(1, prefix) + std::to_string(reg) + "_" + std::to_string(index);
}

RegisterValue MakeValue(Lua51RegisterValueKind kind) {
    RegisterValue value;
    value.kind = kind;
    return value;
}

void CountRegisterAssignment(const RegisterValue& value, Lua51RegisterSummary* summary) {
    switch (value.kind) {
        case Lua51RegisterValueKind::kNil:
            ++summary->nil_assignment_count;
            break;
        case Lua51RegisterValueKind::kBoolean:
            ++summary->boolean_assignment_count;
            break;
        case Lua51RegisterValueKind::kConstant:
            ++summary->constant_assignment_count;
            break;
        case Lua51RegisterValueKind::kGlobal:
            ++summary->global_assignment_count;
            break;
        case Lua51RegisterValueKind::kUpvalue:
            ++summary->upvalue_assignment_count;
            break;
        case Lua51RegisterValueKind::kTableReference:
            ++summary->table_reference_assignment_count;
            break;
        case Lua51RegisterValueKind::kTableLiteral:
            ++summary->table_literal_assignment_count;
            break;
        case Lua51RegisterValueKind::kClosure:
            ++summary->closure_assignment_count;
            break;
        case Lua51RegisterValueKind::kVarArg:
            ++summary->vararg_assignment_count;
            break;
        case Lua51RegisterValueKind::kFunctionCall:
            ++summary->function_call_assignment_count;
            break;
        case Lua51RegisterValueKind::kLocalReference:
            ++summary->local_reference_assignment_count;
            break;
        case Lua51RegisterValueKind::kExpression:
            ++summary->expression_assignment_count;
            break;
        case Lua51RegisterValueKind::kUnknown:
            break;
    }
}

bool IsUnsupportedStraightLineOpcode(Lua51Opcode opcode) {
    switch (opcode) {
        default:
            return false;
    }
}

std::string GetStraightLineRegisterName(const RegisterDeclarations& declarations,
                                        const Lua51Function& function,
                                        int reg,
                                        int line) {
    if (const Lua51Declaration* declaration = declarations.Get(reg, std::max(1, line));
        declaration != nullptr && IsValidLuaIdentifier(declaration->name)) {
        return declaration->name;
    }
    if (reg < function.parameter_count) {
        return "arg" + std::to_string(reg);
    }
    return "r" + std::to_string(reg);
}

StraightLineValue GetRegisterValue(const std::vector<StraightLineValue>& values,
                                   const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   int reg,
                                   int line) {
    if (reg < 0 || static_cast<std::size_t>(reg) >= values.size()) {
        return {};
    }
    if (values[static_cast<std::size_t>(reg)].valid) {
        return values[static_cast<std::size_t>(reg)];
    }
    StraightLineValue value;
    if (reg >= function.parameter_count && declarations.Get(reg, line) == nullptr) {
        value.text = "nil";
    } else {
        value.text = GetStraightLineRegisterName(declarations, function, reg, line);
    }
    value.valid = true;
    return value;
}

StraightLineValue GetRKValue(const std::vector<StraightLineValue>& values,
                             const RegisterDeclarations& declarations,
                             const Lua51Function& function,
                             int rk,
                             int line) {
    if (IsConstantReference(rk)) {
        const int constant_index = rk & 0xFF;
        if (constant_index >= 0 &&
            static_cast<std::size_t>(constant_index) < function.constants.size()) {
            return StraightLineValue{
                RenderLuaConstant(function.constants[static_cast<std::size_t>(constant_index)]),
                true,
                false};
        }
        return {};
    }
    return GetRegisterValue(values, declarations, function, rk, line);
}

std::string JoinExpressions(const std::vector<StraightLineValue>& expressions,
                            std::size_t start_index) {
    std::string joined;
    for (std::size_t i = start_index; i < expressions.size(); ++i) {
        if (i != start_index) {
            joined += ", ";
        }
        joined += expressions[i].text;
    }
    return joined;
}

bool TryInlineTrailingSetListInitializer(std::vector<std::string>* lines,
                                         std::string_view table_name,
                                         const std::vector<StraightLineValue>& entries) {
    if (lines == nullptr || lines->empty() || entries.empty()) {
        return false;
    }

    for (std::size_t i = 0; i + 1 < entries.size(); ++i) {
        if (entries[i].multiple_results) {
            return false;
        }
    }

    std::string indent;
    std::string lhs_prefix;
    std::string parsed_name;
    if (!ParseRenderedTableInitializerLine(lines->back(), &indent, &lhs_prefix, &parsed_name) ||
        parsed_name != table_name) {
        return false;
    }

    lines->back() = lhs_prefix + "{" + JoinExpressions(entries) + "}";
    return true;
}

std::string RenderConstantExpression(const Lua51Constant& constant) {
    return RenderLuaConstant(constant);
}

std::string RenderGlobalExpression(std::string_view name) {
    return RenderLuaGlobal(name);
}

std::string RenderUpvalueExpression(const Lua51Function& function, int index) {
    return RenderLuaUpvalue(function, index);
}

std::string RenderLuaCallExpression(std::string_view callee,
                                    const std::vector<StraightLineValue>& arguments) {
    std::string receiver;
    std::string method;
    if (!arguments.empty() && TrySplitRenderedMethodCallee(callee, &receiver, &method)) {
        const std::string first_argument =
            std::string(TrimLuaWhitespace(arguments.front().text));
        if (first_argument == receiver) {
            return receiver + ":" + method + "(" + JoinExpressions(arguments, 1) + ")";
        }
    }

    return RenderCallableExpression(callee) + "(" + JoinExpressions(arguments) + ")";
}

std::string RenderCallableExpression(std::string_view callee) {
    return WrapInlineExpression(callee);
}

std::string RenderLuaTableAccessExpression(std::string_view table_expression,
                                           std::string_view key_expression) {
    const std::string_view trimmed_table = TrimLuaWhitespace(table_expression);
    const std::string_view trimmed_key = TrimLuaWhitespace(key_expression);

    std::string rendered_table(trimmed_table);
    if (!trimmed_table.empty() &&
        (trimmed_table.front() == '{' || StartsWithLuaKeyword(trimmed_table, "function"))) {
        rendered_table = "(" + std::string(trimmed_table) + ")";
    }

    if (trimmed_key.size() >= 2 &&
        ((trimmed_key.front() == '"' && trimmed_key.back() == '"') ||
         (trimmed_key.front() == '\'' && trimmed_key.back() == '\''))) {
        const std::string_view inner = trimmed_key.substr(1, trimmed_key.size() - 2);
        if (IsValidLuaIdentifier(inner)) {
            return rendered_table + "." + std::string(inner);
        }
    }

    return rendered_table + "[" + std::string(trimmed_key) + "]";
}

std::string RenderCallExpression(const StraightLineValue& callee,
                                 const std::vector<StraightLineValue>& arguments) {
    return RenderLuaCallExpression(callee.text, arguments);
}

std::string RenderTableAccessValueExpression(const StraightLineValue& table,
                                             const StraightLineValue& key) {
    return RenderLuaTableAccessExpression(table.text, key.text);
}

bool IsSimpleAssignableLuaExpression(std::string_view expression) {
    const std::string_view trimmed = TrimLuaWhitespace(expression);
    if (trimmed.empty()) {
        return false;
    }

    if (trimmed == "nil" || trimmed == "true" || trimmed == "false") {
        return false;
    }

    const unsigned char first = static_cast<unsigned char>(trimmed.front());
    if (trimmed.front() == '{' || trimmed.front() == '(' || trimmed.front() == '"' ||
        trimmed.front() == '\'' || std::isdigit(first) != 0) {
        return false;
    }

    for (char ch : trimmed) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        if (std::isalnum(uch) != 0 || ch == '_' || ch == '.' || ch == '[' || ch == ']' ||
            ch == '"' || ch == '\'' || ch == ':' || ch == '(' || ch == ')') {
            continue;
        }
        return false;
    }

    return true;
}

std::string BuildLuaTableSetStatement(std::string_view table_expression,
                                      std::string_view key_expression,
                                      std::string_view rhs_expression,
                                      std::string_view fallback_name,
                                      bool declare_local,
                                      std::string* materialized_table_name) {
    const std::string_view trimmed_table = TrimLuaWhitespace(table_expression);
    if (IsSimpleAssignableLuaExpression(trimmed_table)) {
        if (materialized_table_name != nullptr) {
            materialized_table_name->clear();
        }
        return RenderAssignmentStatement(
            RenderLuaTableAccessExpression(trimmed_table, key_expression), rhs_expression);
    }

    const std::string table_name =
        fallback_name.empty() ? "table_tmp" : std::string(fallback_name);
    const std::string initializer =
        trimmed_table.empty() || trimmed_table == "nil" ? "{}" : std::string(trimmed_table);
    if (materialized_table_name != nullptr) {
        *materialized_table_name = table_name;
    }

    std::string statement = RenderAssignmentStatement(table_name, initializer, declare_local);
    statement += "; ";
    statement += RenderAssignmentStatement(
        RenderLuaTableAccessExpression(table_name, key_expression), rhs_expression);
    return statement;
}

bool IsSafeStraightLinePropagation(std::string_view expression) {
    const std::string_view trimmed = TrimLuaWhitespace(expression);
    if (trimmed.empty()) {
        return false;
    }

    if ((trimmed.front() == '"' && trimmed.back() == '"') ||
        trimmed == "nil" || trimmed == "true" || trimmed == "false") {
        return true;
    }

    bool saw_identifier = false;
    for (char ch : trimmed) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        if (std::isalnum(uch) != 0 || ch == '_' || ch == '.' || ch == '[' || ch == ']' ||
            ch == '"' || ch == '\'') {
            if (std::isalpha(uch) != 0 || ch == '_') {
                saw_identifier = true;
            }
            continue;
        }
        return false;
    }

    return saw_identifier;
}

bool IsSafeLuaJoinPreservation(std::string_view expression) {
    const std::string_view trimmed = TrimLuaWhitespace(expression);
    if (trimmed.empty()) {
        return false;
    }

    if (IsSafeStraightLinePropagation(trimmed)) {
        return true;
    }

    std::string candidate(trimmed);
    char* end = nullptr;
    const double parsed = std::strtod(candidate.c_str(), &end);
    (void)parsed;
    return end != candidate.c_str() && end == candidate.c_str() + candidate.size();
}

int ResolveLuaJoinIdentityRegister(std::string_view expression,
                                   const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   int line,
                                   int register_count) {
    const std::string_view trimmed = TrimLuaJoinOuterParens(expression);
    if (!IsValidLuaIdentifier(trimmed)) {
        return -1;
    }

    for (int reg = 0; reg < register_count; ++reg) {
        if (GetStraightLineRegisterName(declarations, function, reg, line) == trimmed) {
            return reg;
        }
    }
    return -1;
}

std::string NormalizeLuaJoinLiteralExpression(std::string_view expression) {
    const std::string_view trimmed = TrimLuaJoinOuterParens(expression);
    if (trimmed.empty()) {
        return {};
    }

    if (trimmed == "nil" || trimmed == "true" || trimmed == "false") {
        return std::string(trimmed);
    }

    std::string parsed_string;
    if (ParseLuaRenderedStringLiteral(trimmed, &parsed_string)) {
        return EscapeLuaString(parsed_string);
    }

    if (!IsLuaRenderedNumericLiteral(trimmed)) {
        return {};
    }

    std::string candidate(trimmed);
    char* end = nullptr;
    const double parsed = std::strtod(candidate.c_str(), &end);
    if (end == candidate.c_str() || end != candidate.c_str() + candidate.size()) {
        return {};
    }

    char buffer[64];
    const int written = std::snprintf(buffer, sizeof(buffer), "%.17g", parsed);
    if (written <= 0 || static_cast<std::size_t>(written) >= sizeof(buffer)) {
        return {};
    }
    return std::string(buffer, static_cast<std::size_t>(written));
}

std::string NormalizeLuaJoinReferenceExpression(std::string_view expression) {
    const std::string_view trimmed = TrimLuaJoinOuterParens(expression);
    if (trimmed.empty()) {
        return {};
    }

    if (IsValidLuaIdentifier(trimmed)) {
        return std::string(trimmed);
    }

    const std::size_t dot = FindLuaTopLevelDot(trimmed);
    if (dot != std::string_view::npos) {
        const std::string base =
            NormalizeLuaJoinReferenceExpression(trimmed.substr(0, dot));
        const std::string_view member = TrimLuaWhitespace(trimmed.substr(dot + 1));
        if (!base.empty() && IsValidLuaIdentifier(member)) {
            if (base == "_G") {
                return RenderLuaGlobal(member);
            }
            return base + "." + std::string(member);
        }
    }

    std::string_view base_expression;
    std::string_view key_expression;
    if (FindLuaTrailingBracketAccess(trimmed, &base_expression, &key_expression)) {
        const std::string base = NormalizeLuaJoinReferenceExpression(base_expression);
        if (base.empty()) {
            return {};
        }

        if (key_expression.size() >= 2 &&
            ((key_expression.front() == '"' && key_expression.back() == '"') ||
             (key_expression.front() == '\'' && key_expression.back() == '\''))) {
            const std::string_view inner =
                key_expression.substr(1, key_expression.size() - 2);
            if (IsValidLuaIdentifier(inner)) {
                if (base == "_G") {
                    return RenderLuaGlobal(inner);
                }
                return base + "." + std::string(inner);
            }
        }

        const std::string key_literal = NormalizeLuaJoinLiteralExpression(key_expression);
        if (!key_literal.empty()) {
            if (base == "_G") {
                std::string global_name;
                if (ParseLuaRenderedStringLiteral(key_literal, &global_name)) {
                    return RenderLuaGlobal(global_name);
                }
            }
            return base + "[" + key_literal + "]";
        }

        if (IsValidLuaIdentifier(key_expression)) {
            return base + "[" + std::string(key_expression) + "]";
        }
    }

    return {};
}

std::string NormalizeLuaJoinIdentityExpression(std::string_view expression,
                                               const RegisterDeclarations& declarations,
                                               const Lua51Function& function,
                                               int line,
                                               int register_count) {
    const std::string_view trimmed = TrimLuaJoinOuterParens(expression);
    if (trimmed.empty()) {
        return {};
    }

    if (IsValidLuaIdentifier(trimmed)) {
        const int reg = ResolveLuaJoinIdentityRegister(
            trimmed, declarations, function, line, register_count);
        if (reg >= 0) {
            return GetStraightLineRegisterName(
                declarations, function, reg, std::max(1, line));
        }
        return std::string(trimmed);
    }

    const std::size_t dot = FindLuaTopLevelDot(trimmed);
    if (dot != std::string_view::npos) {
        const std::string base = NormalizeLuaJoinIdentityExpression(
            trimmed.substr(0, dot), declarations, function, line, register_count);
        const std::string_view member = TrimLuaWhitespace(trimmed.substr(dot + 1));
        if (!base.empty() && IsValidLuaIdentifier(member)) {
            if (base == "_G") {
                return RenderLuaGlobal(member);
            }
            return base + "." + std::string(member);
        }
    }

    std::string_view base_expression;
    std::string_view key_expression;
    if (FindLuaTrailingBracketAccess(trimmed, &base_expression, &key_expression)) {
        const std::string base = NormalizeLuaJoinIdentityExpression(
            base_expression, declarations, function, line, register_count);
        if (base.empty()) {
            return {};
        }

        if (key_expression.size() >= 2 &&
            ((key_expression.front() == '"' && key_expression.back() == '"') ||
             (key_expression.front() == '\'' && key_expression.back() == '\''))) {
            const std::string_view inner =
                key_expression.substr(1, key_expression.size() - 2);
            if (IsValidLuaIdentifier(inner)) {
                if (base == "_G") {
                    return RenderLuaGlobal(inner);
                }
                return base + "." + std::string(inner);
            }
        }

        const std::string key_literal = NormalizeLuaJoinLiteralExpression(key_expression);
        if (!key_literal.empty()) {
            if (base == "_G") {
                std::string global_name;
                if (ParseLuaRenderedStringLiteral(key_literal, &global_name)) {
                    return RenderLuaGlobal(global_name);
                }
            }
            return base + "[" + key_literal + "]";
        }

        const std::string key_identity = NormalizeLuaJoinIdentityExpression(
            key_expression, declarations, function, line, register_count);
        if (!key_identity.empty()) {
            return base + "[" + key_identity + "]";
        }
    }

    return {};
}

std::string NormalizeLuaJoinSafeExpression(std::string_view expression,
                                           const RegisterDeclarations& declarations,
                                           const Lua51Function& function,
                                           int line,
                                           int register_count) {
    const std::string_view trimmed = TrimLuaJoinOuterParens(expression);
    if (trimmed.empty()) {
        return {};
    }

    const std::string literal = NormalizeLuaJoinLiteralExpression(trimmed);
    if (!literal.empty()) {
        return literal;
    }

    const std::string identity = NormalizeLuaJoinIdentityExpression(
        trimmed, declarations, function, line, register_count);
    if (!identity.empty() && IsSafeLuaJoinPreservation(identity)) {
        return identity;
    }

    const std::string reference = NormalizeLuaJoinReferenceExpression(trimmed);
    if (!reference.empty()) {
        return reference;
    }

    if (IsSafeLuaJoinPreservation(trimmed)) {
        return std::string(trimmed);
    }

    return {};
}

void AssignRegister(std::vector<std::string>* lines,
                    std::vector<StraightLineValue>* values,
                    std::vector<bool>* declared,
                    const RegisterDeclarations& declarations,
                    const Lua51Function& function,
                    int reg,
                    int line,
                    std::string expression,
                    bool multiple_results) {
    if (reg < 0 || static_cast<std::size_t>(reg) >= values->size()) {
        return;
    }
    const std::string name = GetStraightLineRegisterName(declarations, function, reg, line);
    const std::string stored_expression = expression;
    const bool has_declaration = declarations.Get(reg, line) != nullptr;
    const std::string_view trimmed_expression = TrimLuaWhitespace(stored_expression);
    const bool is_table_literal_expression =
        !trimmed_expression.empty() && trimmed_expression.front() == '{';
    if (!multiple_results && trimmed_expression == name &&
        ((*declared)[static_cast<std::size_t>(reg)] || reg < function.parameter_count ||
         has_declaration)) {
        (*values)[static_cast<std::size_t>(reg)] =
            StraightLineValue{name, true, false};
        return;
    }
    const bool prefer_expression =
        !multiple_results && !is_table_literal_expression &&
        reg >= function.parameter_count && !has_declaration &&
        IsSafeStraightLinePropagation(stored_expression);
    if (prefer_expression) {
        (*values)[static_cast<std::size_t>(reg)] =
            StraightLineValue{stored_expression, true, multiple_results};
        return;
    }
    std::string statement;
    if (!(*declared)[static_cast<std::size_t>(reg)] && reg >= function.parameter_count) {
        statement = RenderAssignmentStatement(name, expression, true);
        (*declared)[static_cast<std::size_t>(reg)] = true;
    } else {
        statement = RenderAssignmentStatement(name, expression);
    }
    AppendIndentedStatement(lines, "  ", statement);
    (*values)[static_cast<std::size_t>(reg)] =
        StraightLineValue{prefer_expression ? stored_expression : name, true, multiple_results};
}

void AssignRegisters(std::vector<std::string>* lines,
                     std::vector<StraightLineValue>* values,
                     std::vector<bool>* declared,
                     const RegisterDeclarations& declarations,
                     const Lua51Function& function,
                     int first_reg,
                     int count,
                     int line,
                     std::string expression) {
    if (count <= 0) {
        return;
    }

    std::vector<std::string> names;
    names.reserve(static_cast<std::size_t>(count));
    bool use_local = true;
    for (int offset = 0; offset < count; ++offset) {
        const int reg = first_reg + offset;
        if (reg < 0 || static_cast<std::size_t>(reg) >= values->size()) {
            return;
        }
        names.push_back(GetStraightLineRegisterName(declarations, function, reg, line));
        if ((*declared)[static_cast<std::size_t>(reg)] || reg < function.parameter_count) {
            use_local = false;
        }
    }

    for (int offset = 0; offset < count; ++offset) {
        const int reg = first_reg + offset;
        (*declared)[static_cast<std::size_t>(reg)] = true;
        (*values)[static_cast<std::size_t>(reg)] =
            StraightLineValue{names[static_cast<std::size_t>(offset)], true, false};
    }

    const std::string statement =
        RenderMultiAssignmentStatement(names, expression, use_local);
    AppendIndentedStatement(lines, "  ", statement);
}

std::string RenderBinaryExpression(std::string lhs, std::string_view op, std::string rhs) {
    return "(" + WrapInlineExpression(lhs) + " " + std::string(op) + " " +
           WrapInlineExpression(rhs) + ")";
}

std::string RenderBooleanLiteralExpression(bool value) {
    return value ? "true" : "false";
}

std::string RenderNilExpression() {
    return "nil";
}

std::string RenderVarargExpression() {
    return "...";
}

std::string RenderExpressionList(const std::vector<StraightLineValue>& values) {
    return JoinExpressions(values);
}

std::string RenderTableLiteralExpression(const std::vector<StraightLineValue>& values) {
    if (values.empty()) {
        return "{}";
    }
    return "{" + RenderExpressionList(values) + "}";
}

std::string RenderLocalDeclarationStatement(std::string_view name) {
    return "local " + std::string(name);
}

std::string RenderAssignmentStatement(std::string_view lhs,
                                      std::string_view rhs,
                                      bool declare_local) {
    std::string statement;
    if (declare_local) {
        statement = "local ";
    }
    statement += std::string(lhs);
    statement += " = ";
    statement += std::string(rhs);
    return statement;
}

std::string RenderMultiAssignmentStatement(const std::vector<std::string>& lhs,
                                           std::string_view rhs,
                                           bool declare_local) {
    std::string statement;
    if (declare_local) {
        statement = "local ";
    }
    for (std::size_t i = 0; i < lhs.size(); ++i) {
        if (i != 0) {
            statement += ", ";
        }
        statement += lhs[i];
    }
    statement += " = ";
    statement += std::string(rhs);
    return statement;
}

std::string RenderReturnStatement(std::string_view expression, bool has_values) {
    if (!has_values) {
        return "return";
    }
    return "return " + std::string(expression);
}

std::string RenderIfStatementHeader(std::string condition) {
    return "if " + std::move(condition) + " then";
}

std::string RenderWhileStatementHeader(std::string condition) {
    return "while " + std::move(condition) + " do";
}

std::string RenderGenericForStatementHeader(const std::vector<std::string>& loop_names,
                                            std::string_view iterator_expression) {
    std::string statement = "for ";
    for (std::size_t i = 0; i < loop_names.size(); ++i) {
        if (i != 0) {
            statement += ", ";
        }
        statement += loop_names[i];
    }
    statement += " in ";
    statement += std::string(iterator_expression);
    statement += " do";
    return statement;
}

std::string RenderNumericForStatementHeader(std::string_view loop_var,
                                            std::string_view initial,
                                            std::string_view limit,
                                            std::string_view step) {
    std::string statement = "for ";
    statement += std::string(loop_var);
    statement += " = ";
    statement += std::string(initial);
    statement += ", ";
    statement += std::string(limit);
    if (step != "1" && step != "1.0") {
        statement += ", ";
        statement += std::string(step);
    }
    statement += " do";
    return statement;
}

std::string RenderRepeatFooter(std::string_view condition_text) {
    return "until " + std::string(condition_text);
}

std::string RenderLogicalExpression(std::string lhs, std::string_view op, std::string rhs) {
    return "(" + WrapInlineExpression(lhs) + " " + std::string(op) + " " +
           WrapInlineExpression(rhs) + ")";
}

std::string RenderConcatenationExpression(const std::vector<StraightLineValue>& values) {
    if (values.empty()) {
        return "\"\"";
    }

    std::string expression;
    for (std::size_t i = 0; i < values.size(); ++i) {
        if (i != 0) {
            expression += " .. ";
        }
        expression += WrapInlineExpression(values[i].text);
    }
    return "(" + expression + ")";
}

std::string RenderUnaryExpression(std::string_view op, std::string operand) {
    const std::string wrapped_operand = WrapInlineExpression(operand);
    if (op == "not") {
        return "not " + wrapped_operand;
    }
    return std::string(op) + wrapped_operand;
}

void AssignPreviewRegister(std::vector<StraightLineValue>* values,
                           int reg,
                           std::string expression,
                           bool multiple_results) {
    if (reg < 0 || static_cast<std::size_t>(reg) >= values->size()) {
        return;
    }
    (*values)[static_cast<std::size_t>(reg)] =
        StraightLineValue{std::move(expression), true, multiple_results};
}

}  // namespace refii::lua
