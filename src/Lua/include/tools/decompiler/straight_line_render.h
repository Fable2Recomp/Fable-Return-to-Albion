#pragma once

#include "tools/decompiler/lua51_analysis.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

struct RegisterState {
    bool temporary = false;
    bool local = false;
    bool read = false;
    bool written = false;
};

struct RegisterValue {
    Lua51RegisterValueKind kind = Lua51RegisterValueKind::kUnknown;
    int source_register = -1;
    int child_function = -1;
    bool multiple_results = false;
};

class RegisterStates {
  public:
    RegisterStates(int registers, int lines);

    RegisterState& Get(int reg, int line);
    void SetLocal(int reg, int line);
    void SetTemporary(int reg, int line);

  private:
    std::size_t Index(int reg, int line) const;

    int registers_ = 0;
    int lines_ = 0;
    std::vector<RegisterState> states_;
};

class RegisterDeclarations {
  public:
    RegisterDeclarations(int registers, int lines, std::vector<Lua51Declaration>* declarations);

    const Lua51Declaration* Get(int reg, int line) const;
    bool IsLocal(int reg, int line) const;
    bool IsNewLocal(int reg, int line) const;

  private:
    int ClampLine(int line) const;
    std::size_t Index(int reg, int line) const;

    int registers_ = 0;
    int lines_ = 0;
    std::vector<Lua51Declaration>* declarations_ = nullptr;
    std::vector<const Lua51Declaration*> slots_;
};

class RegisterValues {
  public:
    RegisterValues(int registers, int lines, const RegisterDeclarations* declarations);

    void StartLine(int line);
    RegisterValue GetExpression(int reg, int line) const;
    RegisterValue GetKExpression(int reg, int line) const;
    RegisterValue GetValue(int reg, int line) const;
    int GetUpdated(int reg, int line) const;
    void SetValue(int reg, int line, RegisterValue value);

  private:
    int ClampLine(int line) const;
    std::size_t Index(int reg, int line) const;

    int registers_ = 0;
    int lines_ = 0;
    const RegisterDeclarations* declarations_ = nullptr;
    std::vector<RegisterValue> values_;
    std::vector<int> updated_;
};

struct StraightLineValue {
    std::string text;
    bool valid = false;
    bool multiple_results = false;
};

bool IsConstantReference(int value);
bool IsLoadBoolPair(const std::vector<Lua51Instruction>& instructions, int current_line);
std::string MakeDeclarationName(char prefix, uint32_t reg, uint32_t index);
RegisterValue MakeValue(Lua51RegisterValueKind kind);
void CountRegisterAssignment(const RegisterValue& value, Lua51RegisterSummary* summary);

bool IsUnsupportedStraightLineOpcode(Lua51Opcode opcode);
std::string GetStraightLineRegisterName(const RegisterDeclarations& declarations,
                                        const Lua51Function& function,
                                        int reg,
                                        int line);
StraightLineValue GetRegisterValue(const std::vector<StraightLineValue>& values,
                                   const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   int reg,
                                   int line);
StraightLineValue GetRKValue(const std::vector<StraightLineValue>& values,
                             const RegisterDeclarations& declarations,
                             const Lua51Function& function,
                             int rk,
                             int line);
std::string JoinExpressions(const std::vector<StraightLineValue>& expressions,
                            std::size_t start_index = 0);
bool TryInlineTrailingSetListInitializer(std::vector<std::string>* lines,
                                         std::string_view table_name,
                                         const std::vector<StraightLineValue>& entries);
std::string RenderConstantExpression(const Lua51Constant& constant);
std::string RenderGlobalExpression(std::string_view name);
std::string RenderUpvalueExpression(const Lua51Function& function, int index);
std::string RenderLuaCallExpression(std::string_view callee,
                                    const std::vector<StraightLineValue>& arguments);
std::string RenderCallableExpression(std::string_view callee);
std::string RenderLuaTableAccessExpression(std::string_view table_expression,
                                           std::string_view key_expression);
std::string RenderCallExpression(const StraightLineValue& callee,
                                 const std::vector<StraightLineValue>& arguments);
std::string RenderTableAccessValueExpression(const StraightLineValue& table,
                                             const StraightLineValue& key);
bool IsSimpleAssignableLuaExpression(std::string_view expression);
std::string BuildLuaTableSetStatement(std::string_view table_expression,
                                      std::string_view key_expression,
                                      std::string_view rhs_expression,
                                      std::string_view fallback_name,
                                      bool declare_local,
                                      std::string* materialized_table_name);
bool IsSafeStraightLinePropagation(std::string_view expression);
bool IsSafeLuaJoinPreservation(std::string_view expression);
int ResolveLuaJoinIdentityRegister(std::string_view expression,
                                   const RegisterDeclarations& declarations,
                                   const Lua51Function& function,
                                   int line,
                                   int register_count);
std::string NormalizeLuaJoinLiteralExpression(std::string_view expression);
std::string NormalizeLuaJoinReferenceExpression(std::string_view expression);
std::string NormalizeLuaJoinIdentityExpression(std::string_view expression,
                                               const RegisterDeclarations& declarations,
                                               const Lua51Function& function,
                                               int line,
                                               int register_count);
std::string NormalizeLuaJoinSafeExpression(std::string_view expression,
                                           const RegisterDeclarations& declarations,
                                           const Lua51Function& function,
                                           int line,
                                           int register_count);
void AssignRegister(std::vector<std::string>* lines,
                    std::vector<StraightLineValue>* values,
                    std::vector<bool>* declared,
                    const RegisterDeclarations& declarations,
                    const Lua51Function& function,
                    int reg,
                    int line,
                    std::string expression,
                    bool multiple_results);
void AssignRegisters(std::vector<std::string>* lines,
                     std::vector<StraightLineValue>* values,
                     std::vector<bool>* declared,
                     const RegisterDeclarations& declarations,
                     const Lua51Function& function,
                     int first_reg,
                     int count,
                     int line,
                     std::string expression);
std::string RenderBooleanLiteralExpression(bool value);
std::string RenderNilExpression();
std::string RenderVarargExpression();
std::string RenderExpressionList(const std::vector<StraightLineValue>& values);
std::string RenderTableLiteralExpression(const std::vector<StraightLineValue>& values);
std::string RenderLocalDeclarationStatement(std::string_view name);
std::string RenderAssignmentStatement(std::string_view lhs,
                                      std::string_view rhs,
                                      bool declare_local = false);
std::string RenderMultiAssignmentStatement(const std::vector<std::string>& lhs,
                                           std::string_view rhs,
                                           bool declare_local = false);
std::string RenderReturnStatement(std::string_view expression, bool has_values);
std::string RenderIfStatementHeader(std::string condition);
std::string RenderWhileStatementHeader(std::string condition);
std::string RenderGenericForStatementHeader(const std::vector<std::string>& loop_names,
                                            std::string_view iterator_expression);
std::string RenderNumericForStatementHeader(std::string_view loop_var,
                                            std::string_view initial,
                                            std::string_view limit,
                                            std::string_view step);
std::string RenderRepeatFooter(std::string_view condition_text);
std::string RenderLogicalExpression(std::string lhs, std::string_view op, std::string rhs);
std::string RenderConcatenationExpression(const std::vector<StraightLineValue>& values);
std::string RenderUnaryExpression(std::string_view op, std::string operand);
std::string RenderBinaryExpression(std::string lhs, std::string_view op, std::string rhs);
void AssignPreviewRegister(std::vector<StraightLineValue>* values,
                           int reg,
                           std::string expression,
                           bool multiple_results);

}  // namespace refii::lua
