#include "luadec.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <memory>
#include <optional>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>
#include <cstdlib>
#include <initializer_list>

namespace refii::luadec {
namespace {

bool TraceEnabled() {
    static const bool enabled = std::getenv("REFII_LUADEC_TRACE") != nullptr;
    return enabled;
}
#define LUADEC_TRACE(...) do { if (TraceEnabled()) { std::fprintf(stderr, __VA_ARGS__); std::fprintf(stderr, "\n"); } } while (0)

// ---------------------------------------------------------------------------
// Instructions
// ---------------------------------------------------------------------------

enum Op {
    OP_MOVE, OP_LOADK, OP_LOADBOOL, OP_LOADNIL, OP_GETUPVAL, OP_GETGLOBAL, OP_GETTABLE,
    OP_SETGLOBAL, OP_SETUPVAL, OP_SETTABLE, OP_NEWTABLE, OP_SELF, OP_ADD, OP_SUB, OP_MUL,
    OP_DIV, OP_MOD, OP_POW, OP_UNM, OP_NOT, OP_LEN, OP_CONCAT, OP_JMP, OP_EQ, OP_LT, OP_LE,
    OP_TEST, OP_TESTSET, OP_CALL, OP_TAILCALL, OP_RETURN, OP_FORLOOP, OP_FORPREP,
    OP_TFORLOOP, OP_SETLIST, OP_CLOSE, OP_CLOSURE, OP_VARARG, OP_COUNT
};

const char* const kOpNames[] = {
    "MOVE", "LOADK", "LOADBOOL", "LOADNIL", "GETUPVAL", "GETGLOBAL", "GETTABLE", "SETGLOBAL",
    "SETUPVAL", "SETTABLE", "NEWTABLE", "SELF", "ADD", "SUB", "MUL", "DIV", "MOD", "POW",
    "UNM", "NOT", "LEN", "CONCAT", "JMP", "EQ", "LT", "LE", "TEST", "TESTSET", "CALL",
    "TAILCALL", "RETURN", "FORLOOP", "FORPREP", "TFORLOOP", "SETLIST", "CLOSE", "CLOSURE",
    "VARARG"};

constexpr int kRkBit = 256;
constexpr int kFieldsPerFlush = 50;

struct Instr {
    Op op = OP_MOVE;
    int a = 0;
    int b = 0;
    int c = 0;
    int bx = 0;
    int sbx = 0;
    int line = 0;
};

Instr Decode(std::uint32_t word, int line) {
    Instr instr;
    instr.op = static_cast<Op>(word & 0x3F);
    instr.a = static_cast<int>((word >> 6) & 0xFF);
    instr.c = static_cast<int>((word >> 14) & 0x1FF);
    instr.b = static_cast<int>((word >> 23) & 0x1FF);
    instr.bx = static_cast<int>((word >> 14) & 0x3FFFF);
    instr.sbx = instr.bx - 131071;
    instr.line = line;
    return instr;
}

// luaO_fb2int: the "floating byte" size hints of NEWTABLE.
int FloatingByteToInt(int x) {
    const int e = (x >> 3) & 31;
    if (e == 0) return x;
    return ((x & 7) + 8) << (e - 1);
}

bool IsTestOp(Op op) {
    return op == OP_EQ || op == OP_LT || op == OP_LE || op == OP_TEST || op == OP_TESTSET;
}

// ---------------------------------------------------------------------------
// AST
// ---------------------------------------------------------------------------

struct Expr;
struct Stmt;
struct FunctionAst;
using ExprP = std::shared_ptr<Expr>;
using StmtP = std::shared_ptr<Stmt>;

struct TableField {
    ExprP key;    // null for positional items
    ExprP value;
    int line = 0; // line of the instruction that stored the field
    int pc = 0;   // source order: the store for hash fields, the load for items
};

struct Expr {
    enum Kind {
        kNil, kTrue, kFalse, kNumber, kString, kVararg, kLocal, kUpvalue, kGlobal, kIndex,
        kCall, kMethodCall, kFunction, kTable, kBinary, kUnary, kAnd, kOr, kNot, kCompare,
        kParen, kMulti
    };
    Kind kind = kNil;
    std::string name;   // local/upvalue/global name, method name, string value
    double number = 0.0;
    ExprP a;            // binary lhs / index object / call function / unary operand / method object
    ExprP b;            // binary rhs / index key
    std::vector<ExprP> args;   // call arguments
    std::vector<TableField> fields;
    bool table_open = false;   // constructor still collecting fields
    int table_array_count = 0; // positional items already committed
    int array_budget = 0;      // NEWTABLE B (decoded): positional items in the constructor
    int hash_budget = 0;       // NEWTABLE C (decoded): hash fields in the constructor
    Op op = OP_ADD;
    bool negate = false;       // compare: EQ → ~=, LT/LE → not (...)
    bool swapped = false;      // compare: source wrote `b > a` for LT a b
    int nresults = 1;          // call/vararg: -1 = multiple results
    int line = 0;              // line of the producing instruction
    int pc = -1;               // producing instruction, -1 for locals and inline constants
    int kindex = -1;           // constant index of a literal or of a global's name
    int end_line = 0;          // call: line of ')'; table: line of '}'
    bool paren_call = false;   // call/vararg truncated to one value by parentheses
    bool explicit_not = false; // kNot that came from an OP_NOT instruction
    int op_line = 0;           // and/or: line of the operator token
    int name_line = 0;         // method call: line of the SELF (the `:name` token)
    int test_line = 0;         // chain leaf: line of the TEST/TESTSET that consumed it
    std::shared_ptr<FunctionAst> func;
};

struct Stmt {
    enum Kind {
        kLocal, kAssign, kCall, kReturn, kBreak, kIf, kWhile, kRepeat, kNumFor, kGenFor, kDo,
        kLocalFunction, kFunctionDecl, kContinue
    };
    Kind kind = kCall;
    int line = 0;                       // line of the first token, 0 = unconstrained
    int keyword_line = 0;               // for/while/if: line of the keyword's instruction hint
    std::vector<std::string> names;     // local names / for variables
    std::vector<ExprP> targets;         // assignment targets
    std::vector<ExprP> values;          // rhs / return values / call / for init
    ExprP cond;
    std::vector<StmtP> body;
    std::vector<StmtP> else_body;
    bool has_else = false;
    std::shared_ptr<FunctionAst> func; // local function / function decl
    ExprP func_name;                    // function decl target
    bool method = false;                // function decl uses ':'
    int start_pc = -1;                  // instructions this statement covers
    int end_pc = -1;
    int scope_end = -1;                 // kLocal: endpc of the declared locals
    int close_line = 0;                 // line of the closing `end`/`until` when known
    int escape_line = 0;                // if with else: line of the then-block's escape jump
};

struct FunctionAst {
    std::vector<std::string> params;
    bool vararg = false;
    int linedefined = 0;
    int lastlinedefined = 0;
    std::vector<StmtP> body;
};

ExprP MakeExpr(Expr::Kind kind, int line) {
    auto expr = std::make_shared<Expr>();
    expr->kind = kind;
    expr->line = line;
    return expr;
}

ExprP MakeNot(ExprP inner, int line) {
    if (inner->kind == Expr::kNot && !inner->explicit_not) {
        return inner->a;
    }
    if (inner->kind == Expr::kCompare) {
        auto copy = std::make_shared<Expr>(*inner);
        copy->negate = !copy->negate;
        return copy;
    }
    auto expr = MakeExpr(Expr::kNot, line);
    expr->a = std::move(inner);
    return expr;
}

// `op_line` is the line of the operator token: a TEST is emitted when the
// parser consumes `and`/`or`, so that instruction's line is the operator's.
ExprP MakeLogic(Expr::Kind kind, ExprP lhs, ExprP rhs, int op_line = 0) {
    if (!rhs) return lhs;
    if (!lhs) return rhs;
    // `a and (b and c)` and `(a and b) and c` compile identically; keep the
    // left-associative spelling, which needs no parentheses.
    if (rhs->kind == kind) {
        ExprP left = MakeLogic(kind, std::move(lhs), rhs->a, op_line);
        auto expr = MakeExpr(kind, rhs->line);
        expr->a = std::move(left);
        expr->b = rhs->b;
        expr->op_line = rhs->op_line;
        return expr;
    }
    auto expr = MakeExpr(kind, rhs->line);
    expr->a = std::move(lhs);
    expr->b = std::move(rhs);
    expr->op_line = op_line;
    return expr;
}

// ---------------------------------------------------------------------------
// Literals
// ---------------------------------------------------------------------------

// Shortest decimal that round-trips through a 4-byte float (the guest's
// lua_Number). Integral values print without a fraction.
std::string FormatNumber(double value) {
    if (std::isnan(value)) return "(0/0)";
    if (std::isinf(value)) return value > 0 ? "math.huge" : "-math.huge";
    const float as_float = static_cast<float>(value);
    if (static_cast<double>(as_float) == value && std::floor(value) == value &&
        std::fabs(value) < 1e15) {
        char buffer[64];
        std::snprintf(buffer, sizeof(buffer), "%.0f", value);
        return buffer;
    }
    for (int precision = 1; precision <= 17; ++precision) {
        char buffer[64];
        std::snprintf(buffer, sizeof(buffer), "%.*g", precision, value);
        const double parsed = std::strtod(buffer, nullptr);
        if (static_cast<float>(parsed) == as_float && static_cast<double>(as_float) == value) {
            return buffer;
        }
        if (parsed == value) {
            return buffer;
        }
    }
    char buffer[64];
    std::snprintf(buffer, sizeof(buffer), "%.17g", value);
    return buffer;
}

std::string QuoteString(const std::string& value) {
    std::string out = "\"";
    for (std::size_t i = 0; i < value.size(); ++i) {
        const unsigned char ch = static_cast<unsigned char>(value[i]);
        switch (ch) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            case '\a': out += "\\a"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\v': out += "\\v"; break;
            case 0: out += "\\0"; break;
            default:
                if (ch < 0x20 || ch == 0x7F) {
                    char buffer[8];
                    const bool digit_follows =
                        i + 1 < value.size() && value[i + 1] >= '0' && value[i + 1] <= '9';
                    std::snprintf(buffer, sizeof(buffer), digit_follows ? "\\%03u" : "\\%u", ch);
                    out += buffer;
                } else {
                    out += static_cast<char>(ch);
                }
        }
    }
    out += "\"";
    return out;
}

bool IsIdentifier(const std::string& name) {
    static const char* const kKeywords[] = {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in",
        "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while"};
    if (name.empty()) return false;
    if (!(std::isalpha(static_cast<unsigned char>(name[0])) || name[0] == '_')) return false;
    for (char ch : name) {
        if (!(std::isalnum(static_cast<unsigned char>(ch)) || ch == '_')) return false;
    }
    for (const char* keyword : kKeywords) {
        if (name == keyword) return false;
    }
    return true;
}

// ---------------------------------------------------------------------------
// Function decompiler
// ---------------------------------------------------------------------------

struct DecompileError : std::runtime_error {
    using std::runtime_error::runtime_error;
};

// Thrown when inference adds a local: the function is decompiled again with it.
struct RestartInference {};

struct Loop {
    enum Kind { kWhile, kRepeat, kNumFor, kGenFor };
    Kind kind = kWhile;
    int start = 0;      // first pc of the loop (condition or FORPREP/JMP)
    int body_start = 0;
    int body_end = 0;   // exclusive
    int end = 0;        // pc of the closing instruction (back JMP / FORLOOP / TFORLOOP's JMP)
    int exit = 0;       // pc after the loop
    bool used = false;
};

struct Slot {
    ExprP expr;
    int pc = -1;
    bool multi = false;   // extra result of a multi-result call/vararg held in `expr`
};

struct Leaf {
    int pc = 0;        // test instruction (or the bare jump of a constant leaf)
    int width = 2;     // instructions the leaf occupies
    int target = 0;    // jump target
    bool remapped = false;  // target came from an enclosing closing jump
    bool jump_when_true = false;
    Op op = OP_TEST;
    int reg = -1;      // TESTSET destination (or TEST register)
    ExprP cond;        // condition in positive form
    bool value_keeping = false;  // TESTSET, or TEST on the destination register
};

class FunctionDecompiler {
public:
    FunctionDecompiler(const Proto& proto, const std::vector<std::string>& upvalue_names,
                       const std::string& path, const std::vector<LocVar>* inferred = nullptr,
                       const std::set<std::string>* reserved_names = nullptr)
        : f_(proto), upvalue_names_(upvalue_names), path_(path) {
        for (std::size_t i = 0; i < f_.code.size(); ++i) {
            const int line = i < f_.lineinfo.size() ? f_.lineinfo[i] : 0;
            ins_.push_back(Decode(f_.code[i], line));
        }
        locvars_ = f_.locvars;
        stripped_ = f_.lineinfo.empty();  // debug info stripped: locals must be inferred
        if (inferred != nullptr) {
            locvars_ = *inferred;
            stripped_ = true;
        }
        if (reserved_names != nullptr) reserved_names_ = *reserved_names;
        BuildScopes();
        if (stripped_ && SanitiseScopes()) BuildScopes();
        BuildLoops();
        regs_.assign(static_cast<std::size_t>(f_.maxstacksize) + 4, Slot{});
        written_.assign(regs_.size(), false);
        last_value_.assign(regs_.size(), nullptr);
    }

    // Scope end for a local declared by the write at `write_pc`: the innermost
    // enclosing block that contains that write.
    int ScopeEndFor(int write_pc) const {
        int end = static_cast<int>(ins_.size());
        for (auto it = block_stack_.rbegin(); it != block_stack_.rend(); ++it) {
            if (it->first <= write_pc && write_pc < it->second) {
                end = it->second;
                break;
            }
        }
        // The blocks built so far may not include the one this write sits in
        // (a chain still being gathered): every loop and every forward jump
        // over the write bounds a block, and no local crosses one.
        for (const Loop& loop : loops_) {
            if (loop.start <= write_pc && write_pc <= loop.end) {
                const int body_end = loop.kind == Loop::kRepeat ? loop.end + 1 : loop.end;
                if (body_end > write_pc && body_end < end) end = body_end;
            }
        }
        for (int j = 0; j < write_pc; ++j) {
            const Instr& in = ins_[j];
            if (in.op != OP_JMP && in.op != OP_FORPREP) continue;
            const int target = j + 1 + in.sbx;
            if (target > write_pc && target < end) end = target;
        }
        return end;
    }

    int LastWriteBefore(int reg, int pc) const {
        for (int p = pc - 1; p >= 0; --p) {
            if (InstructionWrites(p, reg)) return p;
        }
        return -1;
    }

    // Runs inference until the locals are stable, then decompiles.
    static std::shared_ptr<FunctionAst> RunInferring(const Proto& proto,
                                                     const std::vector<std::string>& upvalue_names,
                                                     const std::string& path,
                                                     const std::set<std::string>& reserved) {
        if (!proto.lineinfo.empty()) {
            FunctionDecompiler plain(proto, upvalue_names, path, nullptr, &reserved);
            return plain.Run();
        }
        std::vector<LocVar> inferred = SeedLocals(proto, reserved);
        for (int round = 0; round < 400; ++round) {
            FunctionDecompiler pass(proto, upvalue_names, path, &inferred, &reserved);
            try {
                auto ast = pass.Run();
                if (std::getenv("REFII_LUADEC_LOCALS") != nullptr) {
                    for (const LocVar& var : pass.locvars_) {
                        std::fprintf(stderr, "%s local %s [%d,%d) r%d\n", path.c_str(), var.name.c_str(), var.startpc, var.endpc, var.reg);
                    }
                }
                return ast;
            } catch (const RestartInference&) {
                inferred = pass.locvars_;
            }
        }
        throw DecompileError(path + ": local inference did not converge");
    }

    std::shared_ptr<FunctionAst> Run() {
        auto ast = std::make_shared<FunctionAst>();
        ast->linedefined = f_.linedefined;
        ast->lastlinedefined = f_.lastlinedefined;
        ast->vararg = (f_.is_vararg & 2) != 0;
        for (int i = 0; i < f_.numparams; ++i) {
            if (i < static_cast<int>(locvars_.size())) {
                ast->params.push_back(locvars_[static_cast<std::size_t>(i)].name);
            } else {
                ast->params.push_back("arg" + std::to_string(i + 1));
            }
        }
        // Parameters, and the compat `arg` table after them, are declared by
        // the header rather than by a statement.
        const int header_locals = f_.numparams + ((f_.is_vararg & 1) != 0 ? 1 : 0);
        for (int i = 0; i < header_locals && i < static_cast<int>(locvars_.size()); ++i) {
            claimed_locvars_.insert(i);
        }
        const int last = static_cast<int>(ins_.size()) - 1;
        int end = static_cast<int>(ins_.size());
        if (last >= 0 && ins_[last].op == OP_RETURN && ins_[last].a == 0 && ins_[last].b == 1) {
            end = last;  // implicit return
        }
        ast->body = BuildBlock(0, end, -1, end);
        InsertGhostConstants(ast->body);
        return ast;
    }

    // A constant no instruction references was still mentioned in the source
    // (`if "x" then end` interns "x" and emits nothing). Mention it again just
    // before the first statement that references a later constant, so the
    // constant table comes out in the same order.
    void InsertGhostConstants(std::vector<StmtP>& body) {
        std::vector<bool> used(f_.constants.size(), false);
        for (const Instr& in : ins_) {
            const auto mark = [&](int v) {
                if (v >= kRkBit && v - kRkBit < static_cast<int>(used.size())) used[static_cast<std::size_t>(v - kRkBit)] = true;
            };
            switch (in.op) {
                case OP_LOADK: case OP_GETGLOBAL: case OP_SETGLOBAL:
                    if (in.bx < static_cast<int>(used.size())) used[static_cast<std::size_t>(in.bx)] = true;
                    break;
                case OP_GETTABLE: case OP_SELF: mark(in.c); break;
                case OP_SETTABLE: case OP_ADD: case OP_SUB: case OP_MUL: case OP_DIV: case OP_MOD:
                case OP_POW: case OP_EQ: case OP_LT: case OP_LE:
                    mark(in.b); mark(in.c); break;
                default: break;
            }
        }
        for (std::size_t g = 0; g < used.size(); ++g) {
            if (used[g]) continue;
            auto mention = std::make_shared<Stmt>();
            mention->kind = Stmt::kIf;
            mention->cond = ConstExpr(static_cast<int>(g), 0);
            if (!InsertBefore(body, static_cast<int>(g), mention)) body.push_back(mention);
        }
    }

    static int MaxConstant(const ExprP& e) {
        if (!e) return -1;
        int best = e->kindex;
        for (const ExprP& child : {e->a, e->b}) best = std::max(best, MaxConstant(child));
        for (const ExprP& arg : e->args) best = std::max(best, MaxConstant(arg));
        for (const TableField& field : e->fields) {
            best = std::max(best, MaxConstant(field.key));
            best = std::max(best, MaxConstant(field.value));
        }
        return best;
    }

    static int MaxConstantInHead(const Stmt& s) {
        int best = -1;
        for (const ExprP& e : s.targets) best = std::max(best, MaxConstant(e));
        for (const ExprP& e : s.values) best = std::max(best, MaxConstant(e));
        best = std::max(best, MaxConstant(s.cond));
        best = std::max(best, MaxConstant(s.func_name));
        return best;
    }

    static bool InsertBefore(std::vector<StmtP>& body, int ghost, const StmtP& mention) {
        for (std::size_t i = 0; i < body.size(); ++i) {
            StmtP& s = body[i];
            if (MaxConstantInHead(*s) > ghost) {
                body.insert(body.begin() + static_cast<std::ptrdiff_t>(i), mention);
                return true;
            }
            if (InsertBefore(s->body, ghost, mention)) return true;
            if (InsertBefore(s->else_body, ghost, mention)) return true;
        }
        return false;
    }

private:
    [[noreturn]] void Fail(int pc, const std::string& what) const {
        throw DecompileError(path_ + " pc=" + std::to_string(pc) + " " +
                             (pc >= 0 && pc < static_cast<int>(ins_.size())
                                  ? std::string(kOpNames[ins_[pc].op])
                                  : std::string("-")) +
                             ": " + what);
    }

    // ---- scopes -----------------------------------------------------------

    // active_[pc][reg] = index of the local living in `reg` at `pc`, or -1.
    void BuildScopes() {
        const int n = static_cast<int>(ins_.size());
        const std::size_t width = static_cast<std::size_t>(f_.maxstacksize) + 4;
        active_.assign(static_cast<std::size_t>(n) + 1, std::vector<int>(width, -1));
        for (int pc = 0; pc <= n; ++pc) {
            std::vector<int>& row = active_[static_cast<std::size_t>(pc)];
            int positional = 0;
            for (std::size_t i = 0; i < locvars_.size(); ++i) {
                const LocVar& var = locvars_[i];
                if (!(var.startpc <= pc && pc < var.endpc)) continue;
                int reg = var.reg;
                if (reg < 0) reg = positional++;
                if (reg >= 0 && static_cast<std::size_t>(reg) < width && row[static_cast<std::size_t>(reg)] < 0) {
                    row[static_cast<std::size_t>(reg)] = static_cast<int>(i);
                }
            }
        }
    }

    // Locals every stripped function has: parameters and loop controls.
    // Locals in declaration order: by start pc, then by register when known
    // (two locals declared by one statement share a start pc).
    static void SortLocvars(std::vector<LocVar>& vars) {
        std::stable_sort(vars.begin(), vars.end(), [](const LocVar& a, const LocVar& b) {
            if (a.startpc != b.startpc) return a.startpc < b.startpc;
            if (a.reg < 0 || b.reg < 0) return false;
            return a.reg < b.reg;
        });
    }

    static std::vector<LocVar> SeedLocals(const Proto& proto, const std::set<std::string>& reserved) {
        std::vector<LocVar> vars;
        const int n = static_cast<int>(proto.code.size());
        std::set<std::string> taken = reserved;
        const auto fresh = [&](const std::string& base) {
            std::string name = base;
            for (int k = 2; taken.contains(name) || !IsIdentifier(name); ++k) name = base + "_" + std::to_string(k);
            taken.insert(name);
            return name;
        };
        for (int i = 0; i < proto.numparams; ++i) {
            vars.push_back({fresh(i == 0 ? "self" : "arg" + std::to_string(i)), 0, n, i});
        }
        if ((proto.is_vararg & 1) != 0) {
            // VARARG_HASARG (LUA_COMPAT_VARARG): the function holds the `arg`
            // table in the register after its parameters.
            vars.push_back({"arg", 0, n, proto.numparams});
        }
        for (int pc = 0; pc < n; ++pc) {
            const Instr in = Decode(proto.code[pc], 0);
            if (in.op == OP_FORPREP) {
                const int end = pc + 1 + in.sbx;
                vars.push_back({"(for index)", pc, end + 1, in.a});
                vars.push_back({"(for limit)", pc, end + 1, in.a + 1});
                vars.push_back({"(for step)", pc, end + 1, in.a + 2});
                vars.push_back({fresh("i"), pc + 1, end, in.a + 3});
            } else if (in.op == OP_JMP) {
                const int target = pc + 1 + in.sbx;
                const bool paired = pc > 0 && IsTestOp(Decode(proto.code[pc - 1], 0).op);
                if (!paired && target > pc && target + 1 < n) {
                    const Instr t = Decode(proto.code[target], 0);
                    const Instr back = Decode(proto.code[target + 1], 0);
                    if (t.op == OP_TFORLOOP && back.op == OP_JMP && target + 2 + back.sbx == pc + 1) {
                        vars.push_back({"(for generator)", pc, target + 2, t.a});
                        vars.push_back({"(for state)", pc, target + 2, t.a + 1});
                        vars.push_back({"(for control)", pc, target + 2, t.a + 2});
                        for (int k = 0; k < t.c; ++k) {
                            vars.push_back({fresh(k == 0 ? "k" : k == 1 ? "v" : "v" + std::to_string(k)), pc + 1, target, t.a + 3 + k});
                        }
                    }
                }
            }
        }
        for (LocVar& var : vars) var.seeded = true;
        SortLocvars(vars);
        return vars;
    }

    // Declares register `reg` as a local from the instruction that last wrote
    // it before `pc`, then restarts the pass.
    [[noreturn]] void DeclareInferredLocal(const char* why, int reg, int pc, int scope_end, const ExprP& hint,
                                           int known_write = -1) {
        if (probing_) {
            // Speculative operand evaluation must not decide scopes: end the
            // probe here and let the real block build infer with its stack.
            throw DecompileError("probe needs inference");
        }
        int write_pc = -1;
        int inner_write = -1;
        // The latest write may complete an and/or value expression whose
        // leaves each wrote the register: then the local starts after it.
        int latest = known_write >= 0 ? known_write : LastWriteBefore(reg, pc);
        int chain_start = -1;
        for (;;) {
            bool elided = false;
            chain_start = latest >= 0 ? ValueChainStart(latest, reg, &elided) : -1;
            if (chain_start < 0 || !elided) break;
            // An assignment chain to an existing local: its declaration lies
            // before it, and the chain is at most the statement just built.
            if (last_stmt_start_ <= latest && last_stmt_start_ > chain_start) chain_start = last_stmt_start_;
            latest = LastWriteBefore(reg, chain_start);
            known_write = -1;
        }
        const bool chained = chain_start >= 0 && !IsInsideClosedStructure(chain_start, pc);
        LUADEC_TRACE("%s   declare r%d use=%d known=%d latest=%d chain_start=%d chained=%d last_stmt=%d", path_.c_str(), reg, pc, known_write, latest, chain_start, chained ? 1 : 0, last_stmt_start_);
        // A value pending at a block's end may have been declared inside that
        // block: that reading is taken only when no enclosing write exists.
        bool inner_ok = known_write >= 0;
        if (chained) {
            write_pc = latest;
        } else {
            for (int p = pc - 1; p >= 0; --p) {
                if (!InstructionWrites(p, reg) || !CanDeclare(p) || FrameBelowBetween(p, pc, reg)) continue;
                // A write inside a block that closed before this use was an
                // assignment; the declaration is an earlier write outside, or
                // absent (then the local is hoisted to the function start).
                if (IsInsideClosedStructure(p, pc)) {
                    if (inner_write < 0) inner_write = p;
                    continue;
                }
                write_pc = p;
                break;
            }
            if (write_pc >= 0) inner_ok = false;
            if (write_pc < 0 && inner_ok) write_pc = known_write;
            if (write_pc < 0 && inner_write < 0) Fail(pc, "cannot find the declaration of register " + std::to_string(reg));
            if (write_pc < 0) write_pc = inner_write;
        }
        scope_end = std::max(scope_end, ScopeEndFor(chained ? chain_start : write_pc));
        int startpc = write_pc + 1;
        if (!chained && !inner_ok && IsInsideClosedStructure(write_pc, pc)) {
            // Declared inside a block that closed before this use: the source
            // declared it earlier without an initialiser. Only a declaration
            // at the function start emits nothing (LOADNIL is elided at pc 0).
            for (const LocVar& existing : locvars_) {
                if (existing.startpc == 0 && existing.reg == reg) {
                    Fail(pc, "register " + std::to_string(reg) + " already hoisted");
                }
            }
            // Retract the block-scoped declarations inference made for this
            // register: they were assignments to the hoisted local.
            std::string name;
            for (const LocVar& existing : locvars_) {
                if (existing.reg == reg && existing.startpc > 0 && name.empty()) name = existing.name;
            }
            locvars_.erase(std::remove_if(locvars_.begin(), locvars_.end(), [&](const LocVar& v) {
                                return v.reg == reg && v.startpc > 0 && v.name[0] != '(';
                            }), locvars_.end());
            LocVar var;
            var.name = name.empty() ? NameForLocal(hint, reg) : name;
            var.startpc = 0;
            var.endpc = static_cast<int>(ins_.size());
            var.reg = reg;
            locvars_.push_back(var);
            SortLocvars(locvars_);
            LUADEC_TRACE("%s hoist local %s r%d to the function start", path_.c_str(), var.name.c_str(), reg);
            throw RestartInference{};
        }
        if (ins_[write_pc].op == OP_CLOSURE) startpc += f_.protos[static_cast<std::size_t>(ins_[write_pc].bx)].nups;
        if (ins_[write_pc].op == OP_NEWTABLE) {
            // A constructor's fields are stored before the local exists; the
            // NEWTABLE size hints say how many fields the constructor holds.
            const int hash_budget = FloatingByteToInt(ins_[write_pc].c);
            const int array_budget = FloatingByteToInt(ins_[write_pc].b);
            int hash_count = 0;
            int array_count = 0;
            int p = write_pc + 1;
            int last_store = write_pc;
            while (p < static_cast<int>(ins_.size())) {
                const Instr& in = ins_[p];
                if (in.op == OP_SETTABLE && in.a == reg) {
                    if (hash_count >= hash_budget) break;
                    ++hash_count;
                    last_store = p;
                } else if (in.op == OP_SETLIST && in.a == reg) {
                    if (array_count >= array_budget && array_budget > 0) break;
                    array_count += in.b == 0 ? array_budget - array_count : in.b;
                    last_store = p;
                    if (in.c == 0) ++p;
                } else if ((in.op == OP_SETTABLE || in.op == OP_SETLIST) && in.a > reg) {
                    // a nested constructor above the table: part of a field value
                } else if (IsTestOp(in.op) && in.op != OP_EQ && in.op != OP_LT && in.op != OP_LE &&
                           in.a > reg) {
                    // an and/or chain building a field value above the table
                } else if (in.op == OP_JMP && in.sbx > 0 && ConstructorResumesAt(p + 1 + in.sbx, reg)) {
                    // the chain's exit lands on the rest of the field's value or its store
                } else if (!IsPureOperand(p) || InstructionWrites(p, reg) || IsOperandOf(p, reg)) {
                    break;
                }
                ++p;
            }
            startpc = last_store + 1;
        }
        for (LocVar& var : locvars_) {
            if (var.startpc == startpc && !var.seeded && RegisterOfLocvarIn(locvars_, var) == reg) {
                // Already declared but read past its recorded scope: extend it.
                const int wanted = std::max(scope_end, EnclosingStructureEnd(pc, var.startpc - 1));
                if (var.endpc >= wanted) {
                    Fail(pc, "register " + std::to_string(reg) + " already declared at " + std::to_string(startpc));
                }
                var.endpc = wanted;
                LUADEC_TRACE("%s extend local %s r%d to %d", path_.c_str(), var.name.c_str(), reg, var.endpc);
                throw RestartInference{};
            }
        }
        LocVar var;
        var.name = NameForLocal(hint, reg);
        var.startpc = startpc;
        var.endpc = std::max(scope_end, startpc);
        var.reg = reg;
        // A register holds one local at a time: declarations inferred inside
        // this one's scope were assignments to it.
        locvars_.erase(std::remove_if(locvars_.begin(), locvars_.end(), [&](const LocVar& v) {
                            return v.reg == reg && !v.seeded && v.startpc > var.startpc && v.endpc <= var.endpc;
                        }), locvars_.end());
        locvars_.push_back(var);
        SortLocvars(locvars_);
        LUADEC_TRACE("%s infer local %s r%d [%d,%d) at %d: %s", path_.c_str(), var.name.c_str(), reg, var.startpc, var.endpc, pc, why);
        if (TraceEnabled()) {
            for (const LocVar& other : locvars_) {
                if (other.reg == reg && &other != &locvars_.back()) {
                    LUADEC_TRACE("%s   (r%d also holds %s [%d,%d))", path_.c_str(), reg, other.name.c_str(), other.startpc, other.endpc);
                }
            }
        }
        throw RestartInference{};
    }

    // Whether `write_pc` lies inside an if body or loop that ends before `use_pc`.
    bool IsInsideClosedStructure(int write_pc, int use_pc) const {
        for (const Loop& loop : loops_) {
            if (loop.start <= write_pc && write_pc <= loop.end && loop.end < use_pc) return true;
        }
        for (int q = 0; q < write_pc; ++q) {
            if (ins_[q].op != OP_JMP) continue;
            const int target = q + 1 + ins_[q].sbx;
            // a conditional jump over the write (then body) or an escape over it (else body)
            if (write_pc < target && target <= use_pc) return true;
        }
        return false;
    }

    static int RegisterOfLocvarIn(const std::vector<LocVar>& vars, const LocVar& var) {
        if (var.reg >= 0) return var.reg;
        int reg = 0;
        for (const LocVar& other : vars) {
            if (&other == &var) break;
            if (other.startpc <= var.startpc && var.startpc < other.endpc) ++reg;
        }
        return reg;
    }

    // Whether the code at `pc` is operand loads above `reg` leading to a
    // store into the table in `reg` (a constructor field continues there).
    bool ConstructorResumesAt(int pc, int reg) const {
        for (int p = pc; p < static_cast<int>(ins_.size()); ++p) {
            const Instr& in = ins_[p];
            if ((in.op == OP_SETTABLE || in.op == OP_SETLIST) && in.a == reg) return true;
            if ((in.op == OP_SETTABLE || in.op == OP_SETLIST) && in.a > reg) continue;  // nested field
            if (!IsOperandOp(p) || in.a <= reg) return false;
        }
        return false;
    }

    // Whether the instruction at `pc` stores register `reg` somewhere.
    bool IsStoreOf(int pc, int reg) const {
        const Instr& in = ins_[pc];
        switch (in.op) {
            case OP_SETTABLE: return in.c == reg;
            case OP_SETGLOBAL: case OP_SETUPVAL: return in.a == reg;
            case OP_MOVE: return in.b == reg && IsLocalAt(pc, in.a);
            default: return false;
        }
    }

    // Whether the instruction at `pc` reads register `reg`.
    bool IsOperandOf(int pc, int reg) const {
        const Instr& in = ins_[pc];
        const auto rk = [&](int v) { return v < kRkBit && v == reg; };
        switch (in.op) {
            case OP_MOVE: case OP_UNM: case OP_NOT: case OP_LEN: return in.b == reg;
            case OP_GETTABLE: return in.b == reg || rk(in.c);
            case OP_SETTABLE: return in.a == reg || rk(in.b) || rk(in.c);
            case OP_SELF: return in.b == reg || rk(in.c);
            case OP_ADD: case OP_SUB: case OP_MUL: case OP_DIV: case OP_MOD: case OP_POW:
            case OP_EQ: case OP_LT: case OP_LE:
                return rk(in.b) || rk(in.c);
            case OP_CONCAT: return in.b <= reg && reg <= in.c;
            case OP_TEST: return in.a == reg;
            case OP_TESTSET: return in.b == reg;
            case OP_CALL: case OP_TAILCALL:
                return in.b == 0 ? reg >= in.a : (in.a <= reg && reg < in.a + in.b);
            case OP_RETURN:
                return in.b == 0 ? reg >= in.a : (in.a <= reg && reg < in.a + in.b - 1);
            case OP_SETLIST:
                return in.b == 0 ? reg >= in.a : (in.a <= reg && reg <= in.a + in.b);
            case OP_SETGLOBAL: case OP_SETUPVAL: return in.a == reg;
            case OP_FORPREP: case OP_FORLOOP: return in.a <= reg && reg <= in.a + 2;
            case OP_TFORLOOP: return in.a <= reg && reg <= in.a + 2;
            case OP_CLOSURE: {
                const Proto& child = f_.protos[static_cast<std::size_t>(in.bx)];
                for (int i = 0; i < child.nups; ++i) {
                    const Instr& bind = ins_[pc + 1 + i];
                    if (bind.op == OP_MOVE && bind.b == reg) return true;
                }
                return false;
            }
            default: return false;
        }
    }

    bool InstructionWrites(int pc, int reg) const {
        const Instr& in = ins_[pc];
        switch (in.op) {
            case OP_MOVE: case OP_LOADK: case OP_LOADBOOL: case OP_GETUPVAL: case OP_GETGLOBAL:
            case OP_GETTABLE: case OP_NEWTABLE: case OP_ADD: case OP_SUB: case OP_MUL: case OP_DIV:
            case OP_MOD: case OP_POW: case OP_UNM: case OP_NOT: case OP_LEN: case OP_CONCAT:
            case OP_CLOSURE: case OP_TESTSET:
                return in.a == reg;
            case OP_LOADNIL:
                return in.a <= reg && reg <= in.b;
            case OP_SELF:
                return in.a == reg || in.a + 1 == reg;
            case OP_CALL:
                return in.c == 0 ? in.a <= reg : (in.a <= reg && reg < in.a + in.c - 1);
            case OP_VARARG:
                return in.b == 0 ? in.a <= reg : (in.a <= reg && reg < in.a + in.b - 1);
            case OP_TFORLOOP:
                return in.a + 3 <= reg && reg < in.a + 3 + in.c;
            case OP_FORLOOP: case OP_FORPREP:
                return in.a <= reg && reg <= in.a + 3;
            default:
                return false;
        }
    }

    std::string NameForLocal(const ExprP& hint, int reg) {
        std::string base;
        if (hint) {
            switch (hint->kind) {
                case Expr::kCall: case Expr::kMethodCall: {
                    std::string n = hint->kind == Expr::kMethodCall ? hint->name : "";
                    const ExprP& callee = hint->a;
                    if (hint->kind == Expr::kCall && callee) {
                        if (callee->kind == Expr::kIndex && callee->b->kind == Expr::kString) n = callee->b->name;
                        else if (callee->kind == Expr::kGlobal || callee->kind == Expr::kLocal) n = callee->name;
                    }
                    if (n.rfind("Get", 0) == 0 && n.size() > 3) n = n.substr(3);
                    else if (n.rfind("Is", 0) == 0 && n.size() > 2) n = "is_" + n.substr(2);
                    base = n;
                    break;
                }
                case Expr::kIndex:
                    if (hint->b->kind == Expr::kString) base = hint->b->name;
                    break;
                case Expr::kGlobal: case Expr::kUpvalue: case Expr::kLocal:
                    base = hint->name;
                    break;
                case Expr::kTable: base = "t"; break;
                case Expr::kFunction: base = "fn"; break;
                case Expr::kString: base = "str"; break;
                case Expr::kNumber: base = "n"; break;
                default: break;
            }
        }
        std::string snake;
        for (std::size_t i = 0; i < base.size(); ++i) {
            const char ch = base[i];
            if (std::isupper(static_cast<unsigned char>(ch))) {
                if (i > 0 && !std::isupper(static_cast<unsigned char>(base[i - 1])) && base[i - 1] != '_') snake += '_';
                snake += static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
            } else if (std::isalnum(static_cast<unsigned char>(ch)) || ch == '_') {
                snake += ch;
            }
        }
        if (snake.empty() || !IsIdentifier(snake)) snake = "v" + std::to_string(reg);
        std::set<std::string> taken = reserved_names_;
        for (const LocVar& var : locvars_) taken.insert(var.name);
        std::string name = snake;
        for (int k = 2; taken.contains(name) || !IsIdentifier(name); ++k) name = snake + "_" + std::to_string(k);
        return name;
    }

    int LocalIndexAt(int pc, int reg) const {
        if (pc < 0) pc = 0;
        if (pc > static_cast<int>(ins_.size())) pc = static_cast<int>(ins_.size());
        const auto& row = active_[static_cast<std::size_t>(pc)];
        if (reg < 0 || static_cast<std::size_t>(reg) >= row.size()) return -1;
        return row[static_cast<std::size_t>(reg)];
    }

    bool IsLocalAt(int pc, int reg) const { return LocalIndexAt(pc, reg) >= 0; }

    std::string LocalNameAt(int pc, int reg) const {
        const int index = LocalIndexAt(pc, reg);
        if (index >= 0) return locvars_[static_cast<std::size_t>(index)].name;
        return "r" + std::to_string(reg);
    }

    // Locvars that begin exactly at `pc` and are not parameters / loop internals.
    std::vector<int> LocalsStartingAt(int pc) const {
        std::vector<int> result;
        for (std::size_t i = 0; i < locvars_.size(); ++i) {
            const LocVar& var = locvars_[i];
            if (var.startpc == pc && var.endpc >= var.startpc &&
                !claimed_locvars_.contains(static_cast<int>(i)) &&
                !(var.name.size() > 1 && var.name[0] == '(')) {
                result.push_back(static_cast<int>(i));
            }
        }
        return result;
    }

    // Locals are stack-allocated: a local's register is the number of locals
    // declared before it that are still live where it starts.
    int RegisterOfLocvar(int index) const {
        const LocVar& var = locvars_[static_cast<std::size_t>(index)];
        if (var.reg >= 0) return var.reg;
        int reg = 0;
        const bool empty_scope = var.endpc <= var.startpc;
        for (int j = 0; j < index; ++j) {
            const LocVar& other = locvars_[static_cast<std::size_t>(j)];
            if (other.startpc <= var.startpc && var.startpc < other.endpc) {
                ++reg;
            } else if (empty_scope && other.startpc < var.startpc && other.endpc == var.startpc) {
                ++reg;  // ends together with this declaration at the block's end
            } else if (other.startpc == var.startpc && other.endpc == other.startpc) {
                ++reg;  // empty-scope sibling declared just before
            }
        }
        return reg;
    }

    // ---- loops ------------------------------------------------------------

    void BuildLoops() {
        const int n = static_cast<int>(ins_.size());
        for (int pc = 0; pc < n; ++pc) {
            const Instr& instr = ins_[pc];
            if (instr.op == OP_FORPREP) {
                Loop loop;
                loop.kind = Loop::kNumFor;
                loop.start = pc;
                loop.body_start = pc + 1;
                loop.end = pc + 1 + instr.sbx;
                loop.body_end = loop.end;
                loop.exit = loop.end + 1;
                loops_.push_back(loop);
            } else if (instr.op == OP_JMP) {
                const int target = pc + 1 + instr.sbx;
                if (target > pc && target < n && ins_[target].op == OP_TFORLOOP &&
                    target + 1 < n && ins_[target + 1].op == OP_JMP &&
                    target + 2 + ins_[target + 1].sbx == pc + 1) {
                    Loop loop;
                    loop.kind = Loop::kGenFor;
                    loop.start = pc;
                    loop.body_start = pc + 1;
                    loop.body_end = target;
                    loop.end = target + 1;
                    loop.exit = target + 2;
                    loops_.push_back(loop);
                } else if (target <= pc) {
                    // Skip the JMP that closes a generic for (recognised above).
                    if (pc > 0 && ins_[pc - 1].op == OP_TFORLOOP) continue;
                    bool merged = false;
                    for (Loop& loop : loops_) {
                        if ((loop.kind == Loop::kWhile || loop.kind == Loop::kRepeat) &&
                            loop.start == target) {
                            loop.end = std::max(loop.end, pc);
                            loop.exit = loop.end + 1;
                            merged = true;
                        }
                    }
                    if (!merged) {
                        Loop loop;
                        loop.start = target;
                        loop.end = pc;
                        loop.exit = pc + 1;
                        loops_.push_back(loop);
                    }
                }
            }
        }
        // Nested loops sharing a start pc (`while true do while true do`): an
        // inner back-jump is a loop end when some bare forward jump lands right
        // after it (a break out of the inner loop).
        {
            std::vector<Loop> extra;
            for (const Loop& loop : loops_) {
                if (loop.kind != Loop::kWhile) continue;
                for (int e = loop.start; e < loop.end; ++e) {
                    if (ins_[e].op != OP_JMP || e + 1 + ins_[e].sbx != loop.start || IsPairedJump(e)) continue;
                    bool exit_target = false;
                    for (int p = loop.start; p < e; ++p) {
                        if (ins_[p].op == OP_JMP && !IsPairedJump(p) && p + 1 + ins_[p].sbx == e + 1) exit_target = true;
                    }
                    if (!exit_target) continue;
                    Loop inner;
                    inner.start = loop.start;
                    inner.end = e;
                    inner.exit = e + 1;
                    extra.push_back(inner);
                }
            }
            loops_.insert(loops_.end(), extra.begin(), extra.end());
        }
        for (Loop& loop : loops_) {
            if (loop.kind == Loop::kWhile) {
                // repeat: the closing backward jump is the paired jump of a test.
                if (loop.end > 0 && IsTestOp(ins_[loop.end - 1].op)) {
                    loop.kind = Loop::kRepeat;
                }
                loop.body_start = loop.start;
                loop.body_end = loop.end;
            }
        }
    }

    bool LoopStartsAt(int pc) const {
        for (const Loop& loop : loops_) {
            if (loop.start == pc && !loop.used) return true;
        }
        return false;
    }

    Loop* FindLoopStartingAt(int pc, int block_end) {
        Loop* best = nullptr;
        for (Loop& loop : loops_) {
            if (loop.start != pc || loop.used || loop.exit > block_end + 1) continue;
            if (loop.exit > block_end) continue;
            if (best == nullptr || loop.end > best->end) best = &loop;
        }
        return best;
    }

    // ---- registers --------------------------------------------------------

    ExprP ConstExpr(int index, int line) const {
        if (index < 0 || index >= static_cast<int>(f_.constants.size())) {
            Fail(-1, "constant index out of range");
        }
        const Constant& constant = f_.constants[static_cast<std::size_t>(index)];
        ExprP expr;
        switch (constant.type) {
            case Constant::kNil: expr = MakeExpr(Expr::kNil, line); break;
            case Constant::kBoolean:
                expr = MakeExpr(constant.boolean ? Expr::kTrue : Expr::kFalse, line);
                break;
            case Constant::kNumber:
                expr = MakeExpr(Expr::kNumber, line);
                expr->number = constant.number;
                break;
            case Constant::kString:
                expr = MakeExpr(Expr::kString, line);
                expr->name = constant.string;
                break;
        }
        return expr;
    }

    ExprP RegExpr(int reg, int pc) {
        if (IsLocalAt(pc, reg)) {
            auto expr = MakeExpr(Expr::kLocal, ins_[pc].line);
            expr->name = LocalNameAt(pc, reg);
            return expr;
        }
        Slot& slot = regs_[static_cast<std::size_t>(reg)];
        if (!slot.expr) {
            if (!written_[static_cast<std::size_t>(reg)]) {
                // Never written: the compiler skipped the LOADNIL that would
                // have been the function's first instruction.
                return MakeExpr(Expr::kNil, ins_[pc].line);
            }
            if (stripped_) {
                // The value outlived its statement: it is a local.
                const int write_pc = LastWriteBefore(reg, pc);
                DeclareInferredLocal("outlived", reg, pc, ScopeEndFor(write_pc), last_value_[static_cast<std::size_t>(reg)]);
            }
            Fail(pc, "read of register " + std::to_string(reg) + " with no value");
        }
        if (stripped_ && !slot.multi && !block_stack_.empty() && slot.pc < block_stack_.back().first) {
            // A temporary does not cross a block boundary: this value was
            // written before the block that reads it began.
            DeclareInferredLocal("block-cross", reg, pc, ScopeEndFor(slot.pc), slot.expr, slot.pc);
        }
        if (stripped_ && !slot.multi && cond_floor_ >= 0 && slot.pc < cond_floor_) {
            // A loop condition runs every iteration: a value from before the
            // loop that it reads was a local.
            DeclareInferredLocal("loop-cond", reg, pc, ScopeEndFor(slot.pc), slot.expr, slot.pc);
        }
        const bool store_target = ins_[pc].op == OP_SETTABLE && (ins_[pc].a == reg || ins_[pc].b == reg);
        if (stripped_ && !slot.multi && !store_target) {
            // Temporaries are a stack: consuming `reg` while an unrelated
            // pending value sits above it means `reg` outlived a statement.
            // (A store's table and key are evaluated before the values of a
            // multiple assignment and legitimately sit below them.)
            for (int above = reg + 1; above < static_cast<int>(regs_.size()); ++above) {
                const Slot& other = regs_[static_cast<std::size_t>(above)];
                if (other.expr && !IsOperandOf(pc, above) && !IsLocalAt(pc, above)) {
                    DeclareInferredLocal("stack-above", reg, pc, ScopeEndFor(slot.pc), slot.expr, slot.pc);
                }
            }
        }
        if (slot.multi && stripped_ && !grouping_ && !IsStoreOf(pc, reg)) {
            // `local a, b = f()`: the secondary result is used as a value. A
            // store may open a multiple assignment instead; the leftover
            // rule declares the local if the primary is never stored.
            DeclareInferredLocal("multi-read", reg, slot.pc + 1, ScopeEndFor(slot.pc), nullptr, slot.pc);
        }
        if (slot.multi) {
            // A secondary result of a multi-result call: only meaningful as a
            // value in a multiple assignment, which collapses it.
            auto marker = MakeExpr(Expr::kMulti, ins_[pc].line);
            marker->a = slot.expr;
            slot = Slot{};
            return marker;
        }
        ExprP expr = slot.expr;
        if (expr->pc < 0) expr->pc = slot.pc;
        last_value_[static_cast<std::size_t>(reg)] = expr;
        slot = Slot{};
        return expr;
    }

    ExprP RkExpr(int value, int pc) {
        if (value >= kRkBit) {
            ExprP expr = ConstExpr(value - kRkBit, ins_[pc].line);
            expr->kindex = value - kRkBit;
            return expr;
        }
        return RegExpr(value, pc);
    }

    bool HasPending(int reg) const {
        return reg >= 0 && reg < static_cast<int>(regs_.size()) &&
               regs_[static_cast<std::size_t>(reg)].expr != nullptr;
    }

    // Registers holding pending temporaries from `from` upward, contiguous.
    int PendingTop(int from) const {
        int top = from;
        while (top < static_cast<int>(regs_.size()) && regs_[static_cast<std::size_t>(top)].expr) {
            ++top;
        }
        return top;
    }

    void ClearPendingFrom(int from) {
        for (int r = from; r < static_cast<int>(regs_.size()); ++r) {
            regs_[static_cast<std::size_t>(r)] = Slot{};
        }
    }

    // ---- statements --------------------------------------------------------

    struct Ctx {
        int loop_exit = -1;
        int block_end = 0;
        int value_reg = -1;  // capture writes to this register instead of emitting statements
    };

    // A call frame opens on a free register: an inferred local still active
    // there ended earlier (a `do` block), so its scope shrinks to this pc.
    void CheckFrameOnLocal(int pc, int base) {
        if (!IsLocalAt(pc, base)) return;
        const int index = active_[static_cast<std::size_t>(pc)][static_cast<std::size_t>(base)];
        LocVar& var = locvars_[static_cast<std::size_t>(index)];
        if (var.reg < 0 || var.name[0] == '(' || var.startpc == 0) {
            Fail(pc, "call frame on local " + var.name);
        }
        if (probing_) throw DecompileError("probe needs inference");
        // The register was free when this frame's loads began: the scope
        // closed before them, or with the block holding the declaration.
        int endpc = std::max(var.startpc, FrameStart(pc, base));
        for (std::size_t i = block_stack_.size(); i-- > 0;) {
            if (block_stack_[i].first <= var.startpc - 1 && var.startpc - 1 < block_stack_[i].second) {
                // the statement of the declaring block that holds this frame
                endpc = std::min(endpc, std::max(stmt_starts_[i], var.startpc));
                break;
            }
        }
        const int block_end = ScopeEndFor(var.startpc - 1);
        if (block_end <= pc) endpc = std::min(endpc, std::max(block_end, var.startpc));
        var.endpc = endpc;
        LUADEC_TRACE("%s shrink local %s r%d to [%d,%d): call frame at %d", path_.c_str(), var.name.c_str(), base, var.startpc, var.endpc, pc);
        throw RestartInference{};
    }

    // Base register of an instruction that builds its operands in fresh
    // registers at and above that base, or -1. Every register at or above
    // the base is a temporary while it executes.
    int FrameOpBase(int pc) const {
        const Instr& in = ins_[pc];
        switch (in.op) {
            case OP_CALL: case OP_TAILCALL: case OP_SELF: case OP_FORPREP: case OP_NEWTABLE: return in.a;
            case OP_RETURN: return (in.b == 0 || in.b > 2) ? in.a : -1;
            case OP_SETLIST: return in.a + 1;
            case OP_CONCAT: return in.b;
            default: return -1;
        }
    }

    // Whether a frame op between `from` (exclusive) and `to` (exclusive)
    // proves `reg` was a temporary there.
    bool FrameBelowBetween(int from, int to, int reg) const {
        for (int q = from + 1; q < to; ++q) {
            const int base = FrameOpBase(q);
            if (base >= 0 && base <= reg) return true;
        }
        return false;
    }

    // First pc of the statement that contains the frame loads starting at
    // `pc`: the condition of an enclosing `if` that precedes them, walked
    // back over its operand loads.
    int StatementStartBefore(int pc, int reg) const {
        int start = pc;
        for (int q = pc - 1; q >= 0; --q) {
            const Instr& in = ins_[q];
            bool prefix = false;
            if (in.op == OP_JMP) {
                prefix = q + 1 + in.sbx > pc;  // conditional jump over this code
            } else if (IsTestOp(in.op)) {
                prefix = true;
            } else if (in.op == OP_CALL) {
                prefix = in.c != 1;
            } else if (in.a == reg && InstructionWrites(q, reg) && in.op != OP_SETTABLE) {
                // the register under question: its scope is what is being decided
                prefix = in.op != OP_LOADNIL || in.b == reg;
            } else {
                prefix = IsPureOperand(q);
            }
            if (!prefix) break;
            start = q;
        }
        return start;
    }

    // Inferred scopes are checked against the frames inside them before a
    // pass: a local cannot be live where a frame opens at or below its
    // register, so its scope ends before the statement holding that frame.
    bool SanitiseScopes() {
        bool changed = false;
        for (LocVar& var : locvars_) {
            if (var.reg < 0 || var.seeded || var.startpc == 0) continue;
            for (int q = var.startpc; q < var.endpc && q < static_cast<int>(ins_.size()); ++q) {
                const int base = FrameOpBase(q);
                int cut = -1;
                const char* why = "";
                if (base >= 0 && base <= var.reg) {
                    cut = StatementStartBefore(FrameStart(q, base), var.reg);
                    why = "frame";
                } else {
                    // A store of this register whose key constant was created
                    // before a constant first used by the value's loads: the
                    // value was written inline for the store, as a temporary.
                    const Instr& in = ins_[q];
                    int key = -1;
                    if (in.op == OP_SETTABLE && in.c == var.reg && in.b >= kRkBit) key = in.b - kRkBit;
                    if (in.op == OP_SETGLOBAL && in.a == var.reg) key = in.bx;
                    // only when the value was just computed into the register
                    if (key >= 0 && q > var.startpc && InstructionWrites(q - 1, var.reg) && FirstConstantUse(key) == q) {
                        const int region = FrameStart(q, var.reg);
                        for (int p = region; p < q && cut < 0; ++p) {
                            for (int c : ConstantsOf(p)) {
                                if (c > key && FirstConstantUse(c) == p) cut = region;
                            }
                        }
                        why = "key before value";
                    }
                }
                if (cut < 0) continue;
                cut = std::max(var.startpc, cut);
                if (cut >= var.endpc) continue;
                LUADEC_TRACE("%s sanitise local %s r%d [%d,%d) -> [%d,%d): %s at %d", path_.c_str(), var.name.c_str(), var.reg, var.startpc, var.endpc, var.startpc, cut, why, q);
                var.endpc = cut;
                changed = true;
                break;
            }
        }
        return changed;
    }

    // Constant indices referenced by the instruction at `pc`.
    std::vector<int> ConstantsOf(int pc) const {
        const Instr& in = ins_[pc];
        std::vector<int> out;
        const auto rk = [&](int v) { if (v >= kRkBit) out.push_back(v - kRkBit); };
        switch (in.op) {
            case OP_LOADK: case OP_GETGLOBAL: case OP_SETGLOBAL: out.push_back(in.bx); break;
            case OP_GETTABLE: case OP_SELF: rk(in.c); break;
            case OP_SETTABLE: case OP_EQ: case OP_LT: case OP_LE:
            case OP_ADD: case OP_SUB: case OP_MUL: case OP_DIV: case OP_MOD: case OP_POW:
                rk(in.b);
                rk(in.c);
                break;
            default: break;
        }
        return out;
    }

    // First pc that references constant `index`, or -1.
    int FirstConstantUse(int index) const {
        if (first_constant_use_.empty()) {
            first_constant_use_.assign(f_.constants.size(), -1);
            for (int p = static_cast<int>(ins_.size()) - 1; p >= 0; --p) {
                for (int c : ConstantsOf(p)) {
                    if (c >= 0 && c < static_cast<int>(first_constant_use_.size())) first_constant_use_[static_cast<std::size_t>(c)] = p;
                }
            }
        }
        if (index < 0 || index >= static_cast<int>(first_constant_use_.size())) return -1;
        return first_constant_use_[static_cast<std::size_t>(index)];
    }

    // A loop condition is an expression: an inferred local written by the
    // operand loads leading to its first test had gone out of scope.
    void ShrinkLocalsInCondition(int start) {
        int q = start;
        while (q < static_cast<int>(ins_.size()) && !IsTestOp(ins_[q].op) && ins_[q].op != OP_JMP) {
            if (!IsOperandOp(q)) return;
            ++q;
        }
        if (q >= static_cast<int>(ins_.size()) || !IsTestOp(ins_[q].op)) return;
        for (int p = start; p < q; ++p) {
            const Instr& in = ins_[p];
            if (in.op == OP_SETTABLE || in.op == OP_SETLIST || !IsLocalAt(p, in.a)) continue;
            const int index = active_[static_cast<std::size_t>(p)][static_cast<std::size_t>(in.a)];
            LocVar& var = locvars_[static_cast<std::size_t>(index)];
            if (var.reg < 0 || var.name[0] == '(' || var.startpc == 0 || var.startpc > start) continue;
            if (probing_) throw DecompileError("probe needs inference");
            var.endpc = start;
            LUADEC_TRACE("%s shrink local %s r%d to [%d,%d): loop condition at %d", path_.c_str(), var.name.c_str(), in.a, var.startpc, var.endpc, start);
            throw RestartInference{};
        }
    }

    // End (exclusive) of the outermost loop or jump region that contains
    // `read_pc` but not `decl_pc`: a local declared outside such a region
    // and read inside it lives past the region.
    int EnclosingStructureEnd(int read_pc, int decl_pc) const {
        int end = read_pc + 1;
        for (bool grown = true; grown;) {
            grown = false;
            for (const Loop& loop : loops_) {
                if (loop.start <= read_pc && read_pc <= loop.end && decl_pc < loop.start && loop.exit > end) {
                    end = loop.exit;
                    grown = true;
                }
            }
            for (int j = 0; j < read_pc; ++j) {
                if (ins_[j].op != OP_JMP && ins_[j].op != OP_FORPREP) continue;
                const int target = j + 1 + ins_[j].sbx;
                if (decl_pc <= j && target > read_pc && target > end) {
                    end = target;
                    grown = true;
                }
            }
            read_pc = end - 1;
        }
        return end;
    }

    // First pc of the straight-line loads that build the call frame based at
    // `base` and executed at `pc`: the callee and argument loads, which only
    // write registers at or above the base.
    int FrameStart(int pc, int base) const {
        int start = pc;
        for (int q = pc - 1; q >= 0; --q) {
            const Instr& in = ins_[q];
            bool frame_load = false;
            switch (in.op) {
                case OP_MOVE: case OP_LOADK: case OP_LOADBOOL: case OP_LOADNIL: case OP_GETUPVAL:
                case OP_GETGLOBAL: case OP_GETTABLE: case OP_NEWTABLE: case OP_ADD: case OP_SUB:
                case OP_MUL: case OP_DIV: case OP_MOD: case OP_POW: case OP_UNM: case OP_NOT:
                case OP_LEN: case OP_CONCAT: case OP_SELF: case OP_VARARG: case OP_CLOSURE:
                    frame_load = in.a >= base;
                    break;
                case OP_SETTABLE: case OP_SETLIST: {
                    // a field store of a constructor still being built
                    const int table_write = LastWriteBefore(in.a, q);
                    frame_load = in.a >= base && table_write >= 0 && ins_[table_write].op == OP_NEWTABLE;
                    break;
                }
                case OP_CALL:
                    frame_load = in.c != 1 && in.a >= base;
                    break;
                default:
                    break;
            }
            if (!frame_load) break;
            start = q;
        }
        return start;
    }

    // Temporaries are allocated at the top of the stack: a free register
    // below a temporary write belonged to a local.
    void CheckGapBelow(int pc, int reg) {
        if (pc + 1 < static_cast<int>(ins_.size()) && ins_[pc + 1].op == OP_SELF &&
            ins_[pc + 1].c == reg && ins_[pc + 1].a + 2 == reg) {
            // SELF reserves its two base registers before a spilled key is
            // loaded above them: the gap is the frame, not a local.
            return;
        }
        for (int r = reg - 1; r >= 0; --r) {
                if (IsLocalAt(pc, r)) continue;  // locals sit below; look further down
                const Slot& below = regs_[static_cast<std::size_t>(r)];
                if (below.expr) break;
                if (written_[static_cast<std::size_t>(r)]) {
                    DeclareInferredLocal("gap-below", r, pc, ScopeEndFor(LastWriteBefore(r, pc)), last_value_[static_cast<std::size_t>(r)]);
                }
                HoistNilLocal(r, pc, "gap-below-unwritten");
        }
    }

    // A register never written yet in use held a `local x` declared at the
    // function start, whose LOADNIL the compiler elides at pc 0.
    [[noreturn]] void HoistNilLocal(int reg, int pc, const char* why) {
        if (probing_) throw DecompileError("probe needs inference");
        LocVar var;
        var.name = NameForLocal(nullptr, reg);
        var.startpc = 0;
        var.endpc = static_cast<int>(ins_.size());
        var.reg = reg;
        locvars_.push_back(var);
        SortLocvars(locvars_);
        LUADEC_TRACE("%s infer nil local %s r%d [0,%d) at %d: %s", path_.c_str(), var.name.c_str(), reg, var.endpc, pc, why);
        throw RestartInference{};
    }

    // Result of writing a register: either the value is parked or a statement
    // was produced (assignment to an active local).
    void WriteReg(int pc, int reg, ExprP value, std::vector<StmtP>& out, const Ctx& ctx) {
        if (ctx.value_reg == reg) {
            regs_[static_cast<std::size_t>(reg)] = Slot{std::move(value), pc, false};
            return;
        }
        if (IsLocalAt(pc, reg)) {
            auto target = MakeExpr(Expr::kLocal, ins_[pc].line);
            target->name = LocalNameAt(pc, reg);
            if (grouping_) {
                EmitAssign(pc, std::move(target), std::move(value), out, ctx);
            } else {
                pending_next_pc_ = EmitSet(pc, std::move(target), std::move(value), out, ctx);
            }
            return;
        }
        if (stripped_ && ctx.value_reg < 0) CheckGapBelow(pc, reg);
        Slot& slot = regs_[static_cast<std::size_t>(reg)];
        if (slot.expr && !slot.multi && !IsLocalAt(pc + 1, reg)) {
            // A temporary overwritten before use would lose an expression.
            if (slot.expr->kind != Expr::kNil || stripped_) {
                if (stripped_) {
                    // The pending value was a local; this write assigns to it.
                    DeclareInferredLocal("overwrite", reg, slot.pc + 1, ScopeEndFor(slot.pc), slot.expr, slot.pc);
                }
                Fail(pc, "temporary in register " + std::to_string(reg) + " overwritten");
            }
        }
        written_[static_cast<std::size_t>(reg)] = true;
        slot = Slot{std::move(value), pc, false};
    }

    void EmitAssign(int pc, ExprP target, ExprP value, std::vector<StmtP>& out, const Ctx& ctx) {
        auto stmt = std::make_shared<Stmt>();
        stmt->kind = Stmt::kAssign;
        stmt->line = ins_[pc].line;
        if (value->kind == Expr::kTable) {
            value = std::make_shared<Expr>(*value);
            value->table_open = false;
        }
        if ((value->kind == Expr::kTable || value->kind == Expr::kCall ||
             value->kind == Expr::kMethodCall) && value->end_line == 0) {
            value->end_line = ins_[pc].line;
        }
        // Function statements keep their declaration form.
        const bool funcstat_line = ins_[pc].line == 0 || !value->func ||
                                   ins_[pc].line == value->func->linedefined;
        if (value->kind == Expr::kFunction && value->func && IsFunctionNameChain(target) && funcstat_line) {
            stmt->kind = Stmt::kFunctionDecl;
            stmt->func = value->func;
            stmt->func_name = target;
            stmt->line = value->func->linedefined;
            if (target->kind == Expr::kIndex && !stmt->func->params.empty() &&
                stmt->func->params[0] == "self") {
                stmt->method = true;
            }
            out.push_back(stmt);
            return;
        }
        stmt->targets.push_back(std::move(target));
        stmt->values.push_back(std::move(value));
        const ExprP& first_target = stmt->targets[0];
        if (first_target->kind == Expr::kGlobal || first_target->kind == Expr::kLocal ||
            first_target->kind == Expr::kUpvalue || first_target->kind == Expr::kIndex) {
            first_target->line = 0;  // the store's line is the statement's end, not its start
        }
        if (first_target->kind == Expr::kIndex && first_target->b &&
            (first_target->b->kind == Expr::kString || first_target->b->kind == Expr::kNumber)) {
            first_target->b->line = 0;
        }
        if (first_target->kind == Expr::kIndex && first_target->a &&
            (first_target->a->kind == Expr::kLocal || first_target->a->kind == Expr::kUpvalue)) {
            first_target->a->line = 0;  // no load instruction: the line is the store's
        }
        stmt->line = FirstLine(first_target);
        if (stmt->line == 0) stmt->line = FirstLine(stmt->values[0]);
        out.push_back(stmt);
        (void)ctx;
    }

    static bool IsFunctionNameChain(const ExprP& target) {
        if (target->kind == Expr::kGlobal || target->kind == Expr::kLocal ||
            target->kind == Expr::kUpvalue) {
            return true;
        }
        if (target->kind == Expr::kIndex && target->b->kind == Expr::kString &&
            IsIdentifier(target->b->name)) {
            return IsFunctionNameChain(target->a);
        }
        return false;
    }

    // A local or upvalue operand has no load of its own: the line it carries
    // is its consumer's. The other operand's first token bounds the start.
    static int FirstOperandLine(const ExprP& first, const ExprP& second, int own_line) {
        const int la = FirstLine(first);
        if (first && (first->kind == Expr::kLocal || first->kind == Expr::kUpvalue)) {
            const int lb = FirstLine(second);
            if (lb != 0 && (la == 0 || lb < la)) return lb;
        }
        return la != 0 ? la : own_line;
    }

    static int FirstLine(const ExprP& expr) {
        if (!expr) return 0;
        switch (expr->kind) {
            case Expr::kIndex: {
                const int line = FirstLine(expr->a);
                return line != 0 ? line : expr->line;
            }
            case Expr::kCall: {
                const int line = FirstLine(expr->a);
                return line != 0 ? line : expr->line;
            }
            case Expr::kMethodCall: {
                const int line = FirstLine(expr->a);
                return line != 0 ? line : expr->line;
            }
            case Expr::kCompare: {
                if (expr->negate && expr->op != OP_EQ) return expr->line;  // `not (` first
                const ExprP& first = expr->swapped ? expr->b : expr->a;
                const ExprP& second = expr->swapped ? expr->a : expr->b;
                return FirstOperandLine(first, second, expr->line);
            }
            case Expr::kBinary:
            case Expr::kAnd:
            case Expr::kOr:
                return FirstOperandLine(expr->a, expr->b, expr->line);
            case Expr::kUnary:
            case Expr::kNot:
            case Expr::kParen: {
                const int line = FirstLine(expr->a);
                return line != 0 ? line : expr->line;
            }
            case Expr::kFunction:
                return expr->func ? expr->func->linedefined : expr->line;
            default:
                return expr->line;
        }
    }

    // Emits `local` statements for locvars that begin at `pc`, consuming the
    // pending initialisers in their registers.
    void EmitLocalsAt(int pc, std::vector<StmtP>& out) {
        const std::vector<int> starting = LocalsStartingAt(pc);
        if (starting.empty()) return;
        LUADEC_TRACE("%s locals at %d: %zu (first reg %d)", path_.c_str(), pc, starting.size(), RegisterOfLocvar(starting[0]));
        auto stmt = std::make_shared<Stmt>();
        stmt->kind = Stmt::kLocal;
        bool all_nil = true;
        int first_reg = -1;
        for (int index : starting) {
            claimed_locvars_.insert(index);
            stmt->scope_end = std::max(stmt->scope_end, locvars_[static_cast<std::size_t>(index)].endpc);
            const int reg = RegisterOfLocvar(index);
            if (reg < 0 || reg >= static_cast<int>(regs_.size())) {
                Fail(pc, "cannot place local " + locvars_[static_cast<std::size_t>(index)].name);
            }
            if (first_reg < 0) first_reg = reg;
            stmt->names.push_back(locvars_[static_cast<std::size_t>(index)].name);
            Slot& slot = regs_[static_cast<std::size_t>(reg)];
            if (slot.expr && !slot.multi) {
                if (slot.expr->kind != Expr::kNil || slot.pc < 0 ||
                    ins_[slot.pc].op != OP_LOADNIL) {
                    all_nil = false;
                }
                stmt->values.push_back(slot.expr);
                if (stmt->line == 0) stmt->line = FirstLine(slot.expr);
                if (stmt->line == 0) stmt->line = ins_[slot.pc].line;
            } else if (slot.expr && slot.multi) {
                // covered by the multi-result call before it
            } else {
                all_nil = all_nil && true;
            }
            slot = Slot{};
        }
        // Extra initialisers beyond the names are evaluated and discarded;
        // they sit in the registers right after the declared ones.
        if (first_reg >= 0 && !stmt->values.empty()) {
            int extra = first_reg + static_cast<int>(starting.size());
            while (extra < static_cast<int>(regs_.size()) && regs_[static_cast<std::size_t>(extra)].expr &&
                   !regs_[static_cast<std::size_t>(extra)].multi &&
                   regs_[static_cast<std::size_t>(extra)].pc == pc - 1 - (extra - first_reg - static_cast<int>(starting.size())) * 0 &&
                   regs_[static_cast<std::size_t>(extra)].pc >= 0 && regs_[static_cast<std::size_t>(extra)].pc < pc &&
                   !IsLocalAt(pc, extra)) {
                stmt->values.push_back(regs_[static_cast<std::size_t>(extra)].expr);
                regs_[static_cast<std::size_t>(extra)] = Slot{};
                all_nil = false;
                ++extra;
            }
        }
        if (all_nil) {
            // `local a, b` — LOADNIL initialisers are implicit.
            if (!stmt->values.empty() && stmt->values.back()->kind == Expr::kNil) {
                stmt->values.clear();
            }
        }
        // A local whose initialiser is a function may be `local function`.
        if (stmt->names.size() == 1 && stmt->values.size() == 1 &&
            stmt->values[0]->kind == Expr::kFunction && stmt->values[0]->func) {
            stmt->kind = Stmt::kLocalFunction;
            stmt->func = stmt->values[0]->func;
            stmt->line = stmt->func->linedefined;
            stmt->values.clear();
        }
        if (stmt->line == 0 && !stmt->values.empty()) stmt->line = FirstLine(stmt->values[0]);
        out.push_back(stmt);
    }

    // Groups the chain of assignments that a multiple assignment produces:
    // the first SET stores the last target, older temporaries follow.
    void GroupAssignments(std::vector<StmtP>& out, int first_index) {
        if (first_index + 1 >= static_cast<int>(out.size())) return;
        StmtP head = out[static_cast<std::size_t>(first_index)];
        if (head->kind != Stmt::kAssign) return;
        std::vector<ExprP> targets;
        std::vector<ExprP> values;
        for (std::size_t i = static_cast<std::size_t>(first_index); i < out.size(); ++i) {
            const StmtP& stmt = out[i];
            if (stmt->kind != Stmt::kAssign) return;
            targets.push_back(stmt->targets[0]);
            values.push_back(stmt->values[0]);
        }
        std::reverse(targets.begin(), targets.end());
        std::reverse(values.begin(), values.end());
        auto stmt = std::make_shared<Stmt>();
        stmt->kind = Stmt::kAssign;
        stmt->targets = std::move(targets);
        // Collapse secondary results of one multi-result expression.
        for (const ExprP& value : values) {
            if (value->kind == Expr::kMulti) {
                bool present = false;
                for (const ExprP& existing : stmt->values) present = present || existing == value->a;
                if (!present) stmt->values.push_back(value->a);
                continue;
            }
            if (!stmt->values.empty() && stmt->values.back() == value) continue;
            stmt->values.push_back(value);
        }
        stmt->line = 0;
        for (const ExprP& t : stmt->targets) if (stmt->line == 0) stmt->line = FirstLine(t);
        for (const ExprP& v : stmt->values) if (stmt->line == 0) stmt->line = FirstLine(v);
        out.resize(static_cast<std::size_t>(first_index));
        out.push_back(stmt);
    }

    // Builds a block whose last instruction may be a break: jumps threaded
    // through that break resolve to it.
    std::vector<StmtP> BuildBodyBlock(int start, int end, int loop_exit) {
        const bool ends_in_jump = end - 1 >= start && ins_[end - 1].op == OP_JMP &&
                                  ins_[end - 1].sbx >= 0 && !IsPairedJump(end - 1);
        if (ends_in_jump) PushClosingJump(end - 1);
        std::vector<StmtP> body = BuildBlock(start, end, loop_exit, end);
        if (ends_in_jump) PopClosingJump();
        return body;
    }

    // The line of the jump that closes a block is the line of the block's last
    // token; give it to a trailing call or constructor so its ')' or '}' lands there.
    static void HintEndLine(std::vector<StmtP>& body, int line) {
        if (line == 0 || body.empty()) return;
        const StmtP& last = body.back();
        switch (last->kind) {
            case Stmt::kIf: case Stmt::kWhile: case Stmt::kNumFor: case Stmt::kGenFor: case Stmt::kDo:
            case Stmt::kFunctionDecl: case Stmt::kLocalFunction:
                if (last->close_line < line) last->close_line = line;
                return;
            default:
                break;
        }
        ExprP value;
        if (last->kind == Stmt::kCall) value = last->values[0];
        else if ((last->kind == Stmt::kAssign || last->kind == Stmt::kLocal) && !last->values.empty()) value = last->values.back();
        else if (last->kind == Stmt::kReturn && !last->values.empty()) value = last->values.back();
        if (!value || value->end_line >= line) return;
        if (value->kind == Expr::kTable) {
            value->end_line = line;
            return;
        }
        if (value->kind == Expr::kCall || value->kind == Expr::kMethodCall) {
            // The last argument, unless it is a call or constructor, is loaded
            // after ')' and so already carries the ')' line.
            if (!value->args.empty()) {
                const Expr::Kind k = value->args.back()->kind;
                if (k != Expr::kCall && k != Expr::kMethodCall && k != Expr::kTable &&
                    k != Expr::kVararg && k != Expr::kFunction) {
                    return;
                }
            }
            value->end_line = line;
        }
    }

    // A backward jump to an enclosing loop's start (other than the loop's own
    // closing jump) was threaded by the compiler from a jump to that closing
    // JMP; report the closing JMP as the target.
    int EffectiveJumpTarget(int pc) const {
        const int target = pc + 1 + ins_[pc].sbx;
        for (auto it = jump_stack_.rbegin(); it != jump_stack_.rend(); ++it) {
            if (it->target == target && pc != it->jmp_pc && pc < it->jmp_pc) {
                return it->jmp_pc;
            }
        }
        return target;
    }

    // Follows a chain of jumps from `pc`: the pc a pending jump would have
    // been threaded to when the instruction at `pc` is itself a JMP.
    bool IsPairedJump(int pc) const {
        return pc > 0 && pc < static_cast<int>(ins_.size()) && ins_[pc].op == OP_JMP &&
               IsTestOp(ins_[pc - 1].op);
    }

    int ResolveThreaded(int pc) const {
        int guard = 0;
        while (pc >= 0 && pc < static_cast<int>(ins_.size()) && ins_[pc].op == OP_JMP &&
               !IsPairedJump(pc) && guard++ < 64) {
            pc = pc + 1 + ins_[pc].sbx;
        }
        return pc;
    }

    bool JumpsTo(int raw_target, int expected) const {
        return raw_target == expected || raw_target == ResolveThreaded(expected);
    }

    struct ClosingJump {
        int jmp_pc = 0;
        int target = 0;
    };

    void PushClosingJump(int jmp_pc) {
        jump_stack_.push_back({jmp_pc, jmp_pc + 1 + ins_[jmp_pc].sbx});
    }
    void PopClosingJump() { jump_stack_.pop_back(); }

    // ---- block builder -------------------------------------------------

    std::vector<StmtP> BuildBlock(int start, int end, int loop_exit, int block_end) {
        LUADEC_TRACE("%s block [%d,%d) loop_exit=%d", path_.c_str(), start, end, loop_exit);
        block_stack_.emplace_back(start, end);
        struct Pop {
            std::vector<std::pair<int, int>>& stack;
            ~Pop() { stack.pop_back(); }
        } pop{block_stack_};
        std::vector<StmtP> out;
        Ctx ctx;
        ctx.loop_exit = loop_exit;
        ctx.block_end = block_end;
        int pc = start;
        std::size_t stmt_index = out.size();
        int stmt_start = start;
        stmt_starts_.push_back(start);
        struct PopStart {
            std::vector<int>& stack;
            ~PopStart() { stack.pop_back(); }
        } pop_start{stmt_starts_};
        const auto stamp = [&](int next_pc) {
            const bool grew = stmt_index < out.size();
            for (; stmt_index < out.size(); ++stmt_index) {
                if (out[stmt_index]->start_pc < 0) out[stmt_index]->start_pc = stmt_start;
                out[stmt_index]->end_pc = next_pc;
            }
            if (grew) last_stmt_start_ = stmt_start;
            if (grew && stripped_) {
                // A statement just ended: temporaries still pending outlived it.
                LUADEC_TRACE("%s stamp %d: last stmt kind=%d [%d,%d)", path_.c_str(), next_pc, static_cast<int>(out.back()->kind), out.back()->start_pc, out.back()->end_pc);
                for (int r = 0; r < static_cast<int>(regs_.size()); ++r) {
                    const Slot& slot = regs_[static_cast<std::size_t>(r)];
                    if (!slot.expr || IsLocalAt(next_pc, r)) continue;
                    if (slot.multi) DeclareInferredLocal("stmt-leftover-multi", r, slot.pc + 1, ScopeEndFor(slot.pc), nullptr, slot.pc);
                    DeclareInferredLocal("stmt-leftover", r, slot.pc + 1, ScopeEndFor(slot.pc), slot.expr, slot.pc);
                }
            }
            stmt_start = next_pc;
            stmt_starts_.back() = next_pc;
        };
        while (pc < end) {
            EmitLocalsAt(pc, out);
            stamp(pc);
            if (Loop* loop = FindLoopStartingAt(pc, end)) {
                LUADEC_TRACE("%s loop kind=%d [%d,%d] exit=%d", path_.c_str(), loop->kind, loop->start, loop->end, loop->exit);
                loop->used = true;
                BuildLoop(*loop, out, ctx);
                pc = loop->exit;
                stamp(pc);
                continue;
            }
            const Instr& instr = ins_[pc];
            if (IsTestOp(instr.op)) {
                pc = BuildConditional(pc, end, out, ctx);
                stamp(pc);
                continue;
            }
            if (instr.op == OP_JMP) {
                const int raw_target = pc + 1 + instr.sbx;
                const int target = EffectiveJumpTarget(pc);
                if (loop_exit >= 0 && (JumpsTo(raw_target, loop_exit) || JumpsTo(target, loop_exit))) {
                    auto stmt = std::make_shared<Stmt>();
                    stmt->kind = Stmt::kBreak;
                    stmt->line = instr.line;
                    out.push_back(stmt);
                    ++pc;
                    continue;
                }
                if (target == pc + 1) {
                    auto stmt = std::make_shared<Stmt>();
                    stmt->kind = Stmt::kIf;
                    stmt->cond = MakeExpr(Expr::kFalse, instr.line);
                    stmt->line = instr.line;
                    out.push_back(stmt);
                    ++pc;
                    continue;
                }
                if (target > pc && target <= end && IsPairedJump(target - 1) &&
                    !LoopStartsAt(pc + 1)) {
                    // `if true or <cond> then`: the constant leads the chain.
                    pc = BuildConditional(pc, end, out, ctx);
                    stamp(pc);
                    continue;
                }
                if (target > pc && target <= end) {
                    // Lua 5.1.2 compiles `if false then ... end` to a bare jump
                    // over the (dead) body.
                    auto stmt = std::make_shared<Stmt>();
                    stmt->kind = Stmt::kIf;
                    stmt->cond = MakeExpr(Expr::kFalse, instr.line);
                    stmt->line = instr.line;
                    stmt->body = BuildBodyBlock(pc + 1, target, ctx.loop_exit);
                    out.push_back(stmt);
                    pc = target;
                    continue;
                }
                // A jump back to an enclosing loop's head (or forward to its
                // FORLOOP) that no structure explains is the game compiler's
                // `continue`.
                for (const Loop& loop : loops_) {
                    if (!loop.used || pc < loop.body_start || pc >= loop.body_end) continue;
                    const bool to_head = raw_target == loop.start &&
                                         (loop.kind == Loop::kWhile || loop.kind == Loop::kRepeat);
                    const bool to_forloop = raw_target == loop.end &&
                                            (loop.kind == Loop::kNumFor || loop.kind == Loop::kGenFor);
                    if (to_head || to_forloop) {
                        auto stmt = std::make_shared<Stmt>();
                        stmt->kind = Stmt::kContinue;
                        stmt->line = instr.line;
                        out.push_back(stmt);
                        ++pc;
                        goto next_instruction;
                    }
                }
                Fail(pc, "unstructured jump to " + std::to_string(target));
            }
            if (instr.op == OP_FORLOOP || instr.op == OP_FORPREP || instr.op == OP_TFORLOOP) {
                Fail(pc, "loop instruction outside a recognised loop");
            }
            pc = ProcessSimple(pc, out, ctx);
            stamp(pc);
        next_instruction:;
        }
        EmitLocalsAt(pc, out);
        stamp(pc);
        if (stripped_) {
            // Values still pending at the block's end were locals of this block.
            for (int r = 0; r < static_cast<int>(regs_.size()); ++r) {
                const Slot& slot = regs_[static_cast<std::size_t>(r)];
                if (!slot.expr || IsLocalAt(end, r) || slot.pc < start) continue;
                if (slot.multi) {
                    // secondary result: declared with the same start as its call
                    DeclareInferredLocal("block-leftover-multi", r, slot.pc + 1, end, nullptr, slot.pc);
                }
                DeclareInferredLocal("block-leftover", r, slot.pc + 1, end, slot.expr, slot.pc);
            }
        }
        return WrapDoBlocks(start, end, std::move(out));
    }

    // Locals whose scope ends before the enclosing block does were declared
    // inside a `do ... end`. Wrap the statements from the declaration to the
    // scope end.
    std::vector<StmtP> WrapDoBlocks(int start, int end, std::vector<StmtP> stmts) {
        (void)start;
        for (std::size_t i = 0; i < stmts.size(); ++i) {
            const StmtP stmt = stmts[i];
            if ((stmt->kind != Stmt::kLocal && stmt->kind != Stmt::kLocalFunction) ||
                stmt->scope_end < 0 || stmt->scope_end >= end) {
                continue;
            }
            // The scope ends on the block's own CLOSE (emitted at leaveblock,
            // before any escape jump): that is the block's end, not a do-block.
            if (stmt->scope_end < static_cast<int>(ins_.size()) && ins_[stmt->scope_end].op == OP_CLOSE) {
                int after_close = stmt->scope_end + 1;
                if (after_close < static_cast<int>(ins_.size()) && ins_[after_close].op == OP_JMP) ++after_close;
                if (after_close >= end) continue;
            }
            // The scope closes before the block does: a `do ... end` from this
            // declaration through the last statement inside the scope.
            LUADEC_TRACE("%s do-block for local at stmt %zu scope_end=%d block_end=%d", path_.c_str(), i, stmt->scope_end, end);
            std::size_t j = i;
            while (j + 1 < stmts.size() && stmts[j + 1]->end_pc <= stmt->scope_end) ++j;
            if (stripped_ && j + 1 < stmts.size() && stmts[j + 1]->start_pc < stmt->scope_end) {
                // An inferred scope ending inside the next statement: a `do`
                // block closes on statement boundaries, so the scope ended
                // before that statement began.
                int boundary = stmts[j + 1]->start_pc;
                // Assignments to the local right before its scope ends are
                // dead stores: they were the next statement's own loads.
                for (std::size_t k = j; k > i; --k) {
                    const StmtP& prev = stmts[k];
                    if (prev->kind != Stmt::kAssign || prev->targets.size() != 1 ||
                        prev->targets[0]->kind != Expr::kLocal) {
                        break;
                    }
                    bool declared_here = false;
                    for (const std::string& n : stmt->names) declared_here = declared_here || n == prev->targets[0]->name;
                    if (!declared_here) break;
                    boundary = prev->start_pc;
                }
                bool trimmed = false;
                for (LocVar& var : locvars_) {
                    if (var.reg < 0 || var.seeded || var.endpc != stmt->scope_end) continue;
                    if (var.startpc < stmt->start_pc || var.startpc > stmt->end_pc) continue;
                    if (boundary <= var.startpc) continue;
                    LUADEC_TRACE("%s trim local %s r%d to [%d,%d): statement boundary", path_.c_str(), var.name.c_str(), var.reg, var.startpc, boundary);
                    var.endpc = boundary;
                    trimmed = true;
                }
                if (trimmed) throw RestartInference{};
            }
            auto block = std::make_shared<Stmt>();
            block->kind = Stmt::kDo;
            block->line = stmt->line;
            block->start_pc = stmt->start_pc;
            block->end_pc = stmts[j]->end_pc;
            block->body.assign(stmts.begin() + static_cast<std::ptrdiff_t>(i),
                               stmts.begin() + static_cast<std::ptrdiff_t>(j) + 1);
            block->body = WrapDoBlocks(block->start_pc, stmt->scope_end, std::move(block->body));
            stmts.erase(stmts.begin() + static_cast<std::ptrdiff_t>(i),
                        stmts.begin() + static_cast<std::ptrdiff_t>(j) + 1);
            stmts.insert(stmts.begin() + static_cast<std::ptrdiff_t>(i), block);
        }
        return stmts;
    }

    // Processes one non-control instruction. Returns the next pc.
    int ProcessSimple(int pc, std::vector<StmtP>& out, const Ctx& ctx) {
        const Instr& in = ins_[pc];
        const int line = in.line;
        switch (in.op) {
            case OP_MOVE: {
                if (stripped_ && in.a < in.b && !IsLocalAt(pc, in.a) && !IsLocalAt(pc, in.b) &&
                    regs_[static_cast<std::size_t>(in.b)].multi) {
                    // A secondary call result is only moved by a multiple
                    // assignment into a local: the target register is one.
                    const Slot& target = regs_[static_cast<std::size_t>(in.a)];
                    if (target.expr) {
                        DeclareInferredLocal("multi-store", in.a, pc, ScopeEndFor(target.pc), target.expr, target.pc);
                    }
                    if (written_[static_cast<std::size_t>(in.a)]) {
                        DeclareInferredLocal("multi-store", in.a, pc, ScopeEndFor(LastWriteBefore(in.a, pc)),
                                             last_value_[static_cast<std::size_t>(in.a)]);
                    }
                    HoistNilLocal(in.a, pc, "multi-store-unwritten");
                }
                pending_next_pc_ = pc + 1;
                WriteReg(pc, in.a, RegExpr(in.b, pc), out, ctx);
                return pending_next_pc_;
            }
            case OP_LOADK: {
                ExprP expr = ConstExpr(in.bx, line);
                expr->kindex = in.bx;
                WriteReg(pc, in.a, expr, out, ctx);
                return pc + 1;
            }
            case OP_LOADBOOL: {
                if (in.c != 0) Fail(pc, "LOADBOOL with skip outside a comparison");
                WriteReg(pc, in.a, MakeExpr(in.b ? Expr::kTrue : Expr::kFalse, line), out, ctx);
                return pc + 1;
            }
            case OP_LOADNIL: {
                if (stripped_) {
                    const bool consumed_next = (pc + 1 < static_cast<int>(ins_.size()) &&
                                                (ins_[pc + 1].op == OP_SETGLOBAL || ins_[pc + 1].op == OP_SETUPVAL ||
                                                 ins_[pc + 1].op == OP_SETTABLE || ins_[pc + 1].op == OP_RETURN ||
                                                 ins_[pc + 1].op == OP_CALL || ins_[pc + 1].op == OP_SETLIST)) ||
                                               IsOperandOfLater(pc, in.a);
                    if (!consumed_next) {
                        for (int r = in.a; r <= in.b; ++r) {
                            bool declared = IsLocalAt(pc + 1, r) || IsLocalAt(pc, r);
                            for (const LocVar& var : locvars_) {
                                if (var.reg == r && var.startpc == pc + 1) declared = true;
                            }
                            if (!declared) {
                                if (probing_) throw DecompileError("probe needs inference");
                                LocVar var;
                                var.name = NameForLocal(nullptr, r);
                                var.startpc = pc + 1;
                                var.endpc = ScopeEndFor(pc);
                                var.reg = r;
                                locvars_.push_back(var);
                                SortLocvars(locvars_);
                                LUADEC_TRACE("%s infer nil local %s r%d [%d,%d)", path_.c_str(), var.name.c_str(), r, var.startpc, var.endpc);
                                throw RestartInference{};
                            }
                        }
                    }
                }
                // Registers A..B. Locals get an assignment each; temporaries park nil.
                const int first_stmt = static_cast<int>(out.size());
                for (int r = in.a; r <= in.b; ++r) {
                    WriteReg(pc, r, MakeExpr(Expr::kNil, line), out, ctx);
                }
                // `a = nil` statements in a row merge into one LOADNIL, so each
                // local keeps its own statement.
                (void)first_stmt;
                return pc + 1;
            }
            case OP_GETUPVAL: {
                auto expr = MakeExpr(Expr::kUpvalue, line);
                expr->name = UpvalueName(in.b);
                WriteReg(pc, in.a, expr, out, ctx);
                return pc + 1;
            }
            case OP_GETGLOBAL: {
                auto expr = MakeExpr(Expr::kGlobal, line);
                expr->name = f_.constants[static_cast<std::size_t>(in.bx)].string;
                expr->kindex = in.bx;
                WriteReg(pc, in.a, expr, out, ctx);
                return pc + 1;
            }
            case OP_GETTABLE: {
                if (stripped_) {
                    CheckRkConstant(pc, in.c);
                    CheckOperandOrder(pc, {in.b, in.c});
                }
                auto expr = MakeExpr(Expr::kIndex, line);
                expr->a = RegExpr(in.b, pc);
                expr->b = RkExpr(in.c, pc);
                WriteReg(pc, in.a, expr, out, ctx);
                return pc + 1;
            }
            case OP_SETGLOBAL: {
                auto target = MakeExpr(Expr::kGlobal, line);
                target->name = f_.constants[static_cast<std::size_t>(in.bx)].string;
                target->kindex = in.bx;
                return EmitSet(pc, target, RegExpr(in.a, pc), out, ctx);
            }
            case OP_SETUPVAL: {
                auto target = MakeExpr(Expr::kUpvalue, line);
                target->name = UpvalueName(in.b);
                return EmitSet(pc, target, RegExpr(in.a, pc), out, ctx);
            }
            case OP_SETTABLE: {
                if (stripped_) {
                    CheckRkConstant(pc, in.b);
                    CheckRkConstant(pc, in.c);
                    CheckOperandOrder(pc, {in.a, in.b, in.c});
                }
                Slot& slot = regs_[static_cast<std::size_t>(in.a)];
                if (!IsLocalAt(pc, in.a) && slot.expr && slot.expr->kind == Expr::kTable &&
                    slot.expr->table_open) {
                    int hash_fields = 0;
                    for (const TableField& f : slot.expr->fields) hash_fields += f.key ? 1 : 0;
                    if (stripped_ && hash_fields >= slot.expr->hash_budget) {
                        // The constructor is complete: this store assigns to a local table.
                        DeclareInferredLocal("table-complete", in.a, pc, ScopeEndFor(slot.expr->pc), slot.expr, slot.expr->pc);
                    }
                    slot.expr = std::make_shared<Expr>(*slot.expr);
                    TableField field;
                    field.key = RkExpr(in.b, pc);
                    field.value = RkExpr(in.c, pc);
                    field.line = line;
                    field.pc = pc;
                    if (field.value->kind == Expr::kTable) {
                        field.value->table_open = false;
                        if (field.value->end_line == 0) field.value->end_line = line;
                    }
                    slot.expr->fields.push_back(std::move(field));
                    return pc + 1;
                }
                auto target = MakeExpr(Expr::kIndex, line);
                target->a = RegExpr(in.a, pc);
                target->b = RkExpr(in.b, pc);
                target->b->line = 0;
                return EmitSet(pc, target, RkExpr(in.c, pc), out, ctx);
            }
            case OP_NEWTABLE: {
                auto expr = MakeExpr(Expr::kTable, line);
                expr->pc = pc;
                expr->table_open = true;
                expr->array_budget = FloatingByteToInt(in.b);
                expr->hash_budget = FloatingByteToInt(in.c);
                WriteReg(pc, in.a, expr, out, ctx);
                return pc + 1;
            }
            case OP_SELF: {
                if (stripped_) {
                    CheckRkConstant(pc, in.c);
                    CheckFrameOnLocal(pc, in.a);
                    if (ctx.value_reg < 0) CheckGapBelow(pc, in.a);
                    const Slot& object = regs_[static_cast<std::size_t>(in.b)];
                    if (in.b == in.a - 1 && !IsLocalAt(pc, in.b) && object.expr && !object.multi) {
                        // A temporary object would have been freed and the
                        // frame opened in its register (luaK_self).
                        DeclareInferredLocal("self-object", in.b, pc, ScopeEndFor(object.pc), object.expr, object.pc);
                    }
                }
                ExprP object = RegExpr(in.b, pc);
                auto method = MakeExpr(Expr::kMethodCall, line);
                method->name_line = line;
                method->a = object;
                ExprP key = RkExpr(in.c, pc);
                if (key->kind != Expr::kString || !IsIdentifier(key->name)) {
                    Fail(pc, "SELF with a non-identifier method name");
                }
                method->name = key->name;
                method->kindex = key->kindex;
                regs_[static_cast<std::size_t>(in.a)] = Slot{method, pc, false};
                regs_[static_cast<std::size_t>(in.a) + 1] = Slot{object, pc, true};
                written_[static_cast<std::size_t>(in.a)] = true;
                written_[static_cast<std::size_t>(in.a) + 1] = true;
                return pc + 1;
            }
            case OP_ADD: case OP_SUB: case OP_MUL: case OP_DIV: case OP_MOD: case OP_POW: {
                if (stripped_) {
                    CheckRkConstant(pc, in.b);
                    CheckRkConstant(pc, in.c);
                    CheckOperandOrder(pc, {in.b, in.c});
                    // Two numeric constants would have been folded by the compiler.
                    const bool b_num = in.b >= kRkBit ? f_.constants[static_cast<std::size_t>(in.b - kRkBit)].type == Constant::kNumber
                                                      : PendingIsNumber(in.b, pc);
                    const bool c_num = in.c >= kRkBit ? f_.constants[static_cast<std::size_t>(in.c - kRkBit)].type == Constant::kNumber
                                                      : PendingIsNumber(in.c, pc);
                    if (b_num && c_num) {
                        if (in.b < kRkBit) DeclareInferredLocal("fold-b", in.b, pc, ScopeEndFor(regs_[static_cast<std::size_t>(in.b)].pc), regs_[static_cast<std::size_t>(in.b)].expr, regs_[static_cast<std::size_t>(in.b)].pc);
                        if (in.c < kRkBit) DeclareInferredLocal("fold-c", in.c, pc, ScopeEndFor(regs_[static_cast<std::size_t>(in.c)].pc), regs_[static_cast<std::size_t>(in.c)].expr, regs_[static_cast<std::size_t>(in.c)].pc);
                    }
                }
                auto expr = MakeExpr(Expr::kBinary, line);
                expr->op = in.op;
                expr->a = RkExpr(in.b, pc);
                expr->b = RkExpr(in.c, pc);
                WriteReg(pc, in.a, expr, out, ctx);
                return pc + 1;
            }
            case OP_UNM: case OP_NOT: case OP_LEN: {
                if (stripped_ && in.op == OP_UNM && PendingIsNumber(in.b, pc)) {
                    // `-<constant>` folds; the operand was a local.
                    DeclareInferredLocal("unm-fold", in.b, pc, ScopeEndFor(regs_[static_cast<std::size_t>(in.b)].pc), regs_[static_cast<std::size_t>(in.b)].expr, regs_[static_cast<std::size_t>(in.b)].pc);
                }
                ExprP operand = RegExpr(in.b, pc);
                ExprP expr;
                if (in.op == OP_NOT) {
                    expr = MakeExpr(Expr::kNot, line);
                    expr->a = operand;
                    expr->explicit_not = true;
                } else {
                    expr = MakeExpr(Expr::kUnary, line);
                    expr->op = in.op;
                    expr->a = operand;
                }
                WriteReg(pc, in.a, expr, out, ctx);
                return pc + 1;
            }
            case OP_CONCAT: {
                ExprP result;
                for (int r = in.c; r >= in.b; --r) {
                    ExprP piece = RegExpr(r, pc);
                    if (!result) {
                        result = piece;
                    } else {
                        auto expr = MakeExpr(Expr::kBinary, line);
                        expr->op = OP_CONCAT;
                        expr->a = piece;
                        expr->b = result;
                        result = expr;
                    }
                }
                WriteReg(pc, in.a, result, out, ctx);
                return pc + 1;
            }
            case OP_CALL:
            case OP_TAILCALL: {
                if (stripped_) {
                    CheckFrameOnLocal(pc, in.a);
                    std::vector<int> order;
                    const int last = in.b == 0 ? PendingTop(in.a + 1) : in.a + in.b;
                    for (int r = in.a; r < last; ++r) order.push_back(r);
                    CheckOperandOrder(pc, order);
                }
                ExprP call = BuildCall(pc);
                if (in.op == OP_TAILCALL) {
                    if (pc + 1 >= static_cast<int>(ins_.size()) || ins_[pc + 1].op != OP_RETURN) {
                        Fail(pc, "TAILCALL not followed by RETURN");
                    }
                    call->nresults = -1;
                    auto stmt = std::make_shared<Stmt>();
                    stmt->kind = Stmt::kReturn;
                    stmt->values.push_back(call);
                    stmt->line = FirstLine(call);
                    out.push_back(stmt);
                    return pc + 2;
                }
                if (in.c == 1) {
                    auto stmt = std::make_shared<Stmt>();
                    stmt->kind = Stmt::kCall;
                    stmt->values.push_back(call);
                    stmt->line = FirstLine(call);
                    out.push_back(stmt);
                    return pc + 1;
                }
                call->nresults = in.c == 0 ? -1 : in.c - 1;
                WriteReg(pc, in.a, call, out, ctx);
                if (in.c > 2) {
                    for (int r = in.a + 1; r <= in.a + in.c - 2; ++r) {
                        regs_[static_cast<std::size_t>(r)] = Slot{call, pc, true};
                    }
                }
                return pc + 1;
            }
            case OP_RETURN: {
                auto stmt = std::make_shared<Stmt>();
                stmt->kind = Stmt::kReturn;
                stmt->line = line;
                if (in.b == 0) {
                    const int top = PendingTop(in.a);
                    for (int r = in.a; r < top; ++r) {
                        CollectValue(r, pc, stmt->values);
                    }
                } else {
                    for (int r = in.a; r < in.a + in.b - 1; ++r) {
                        CollectValue(r, pc, stmt->values);
                    }
                }
                if (!stmt->values.empty()) stmt->line = FirstLine(stmt->values[0]);
                out.push_back(stmt);
                return pc + 1;
            }
            case OP_VARARG: {
                auto expr = MakeExpr(Expr::kVararg, line);
                expr->nresults = in.b == 0 ? -1 : in.b - 1;
                WriteReg(pc, in.a, expr, out, ctx);
                for (int r = in.a + 1; r < in.a + in.b - 1; ++r) {
                    regs_[static_cast<std::size_t>(r)] = Slot{expr, pc, true};
                }
                return pc + 1;
            }
            case OP_SETLIST: {
                Slot& slot = regs_[static_cast<std::size_t>(in.a)];
                if (!slot.expr || slot.expr->kind != Expr::kTable || !slot.expr->table_open) {
                    Fail(pc, "SETLIST on a register that is not an open constructor");
                }
                slot.expr = std::make_shared<Expr>(*slot.expr);
                int c = in.c;
                int next = pc + 1;
                if (c == 0) {
                    c = static_cast<int>(f_.code[static_cast<std::size_t>(pc + 1)]);
                    next = pc + 2;
                }
                const int count = in.b == 0 ? PendingTop(in.a + 1) - (in.a + 1) : in.b;
                for (int i = 0; i < count; ++i) {
                    const int r = in.a + 1 + i;
                    TableField field;
                    Slot& item = regs_[static_cast<std::size_t>(r)];
                    if (!item.expr) Fail(pc, "SETLIST item missing");
                    if (item.multi) {
                        item = Slot{};
                        continue;
                    }
                    field.value = item.expr;
                    field.line = item.expr->line;
                    field.pc = item.pc;
                    item = Slot{};
                    slot.expr->fields.push_back(std::move(field));
                }
                slot.expr->table_array_count += count;
                slot.expr->end_line = line;
                (void)c;
                return next;
            }
            case OP_CLOSE:
                return pc + 1;
            case OP_CLOSURE: {
                const Proto& child = f_.protos[static_cast<std::size_t>(in.bx)];
                std::vector<std::string> names;
                for (int i = 0; i < child.nups; ++i) {
                    const Instr& bind = ins_[pc + 1 + i];
                    std::string name;
                    if (i < static_cast<int>(child.upvalues.size()) && !child.upvalues[i].empty()) {
                        name = child.upvalues[i];
                    } else if (bind.op == OP_MOVE) {
                        if (stripped_ && !IsLocalAt(pc, bind.b)) {
                            if (!written_[static_cast<std::size_t>(bind.b)]) HoistNilLocal(bind.b, pc, "captured-unwritten");
                            const int write_pc = LastWriteBefore(bind.b, pc);
                            const Slot& captured = regs_[static_cast<std::size_t>(bind.b)];
                            DeclareInferredLocal("captured", bind.b, pc, ScopeEndFor(write_pc),
                                                 captured.expr ? captured.expr : last_value_[static_cast<std::size_t>(bind.b)]);
                        }
                        name = LocalNameAt(pc, bind.b);
                    } else if (bind.op == OP_GETUPVAL) {
                        name = UpvalueName(bind.b);
                    } else {
                        Fail(pc, "bad closure upvalue binding");
                    }
                    names.push_back(name);
                }
                auto expr = MakeExpr(Expr::kFunction, line);
                std::set<std::string> child_reserved = reserved_names_;
                for (const std::string& name : names) child_reserved.insert(name);
                for (const LocVar& var : locvars_) child_reserved.insert(var.name);
                expr->func = RunInferring(child, names, path_ + "." + std::to_string(in.bx), child_reserved);
                WriteReg(pc, in.a, expr, out, ctx);
                return pc + 1 + child.nups;
            }
            default:
                Fail(pc, "unexpected opcode in straight-line code");
        }
    }

    // The temporaries of a chain's leaves are written in leaf order: a leaf
    // operand written before an earlier leaf's operands was a local.
    void CheckLeafOrder(int pc, int* last_write) {
        if (!stripped_) return;
        const Instr& in = ins_[pc];
        std::vector<int> operands;
        switch (in.op) {
            case OP_TEST: operands = {in.a}; break;
            case OP_TESTSET: operands = {in.b}; break;
            case OP_EQ: case OP_LT: case OP_LE: operands = {in.b, in.c}; break;
            default: return;
        }
        int newest = *last_write;
        for (int r : operands) {
            if (r >= kRkBit || IsLocalAt(pc, r)) continue;
            const Slot& slot = regs_[static_cast<std::size_t>(r)];
            if (!slot.expr || slot.multi) continue;
            if (slot.pc < *last_write) {
                DeclareInferredLocal("leaf-order", r, pc, ScopeEndFor(slot.pc), slot.expr, slot.pc);
            }
            newest = std::max(newest, slot.pc);
        }
        *last_write = newest;
    }

    // A LOADK'd constant consumed where an RK operand could name it directly
    // was a local (the compiler only spills constants past index 255).
    void CheckRkConstant(int pc, int operand) {
        if (operand >= kRkBit || IsLocalAt(pc, operand)) return;
        const Slot& slot = regs_[static_cast<std::size_t>(operand)];
        if (!slot.expr || slot.pc < 0) return;
        const Instr& load = ins_[slot.pc];
        if (load.op == OP_LOADK) {
            if (slot.expr->kind != Expr::kNumber && slot.expr->kind != Expr::kString) return;
            if (load.bx > 255) return;
            // A string names its constant by index; a number goes through the
            // pool size instead (luaK_exp2RK), which the consumer's own key
            // constant has already grown.
            if (slot.expr->kind == Expr::kNumber && PoolSizeWhenDischarged(slot.pc, pc, load.bx) > 255) return;
        } else if (load.op == OP_LOADBOOL && load.c == 0 && load.a == operand &&
                   (slot.expr->kind == Expr::kTrue || slot.expr->kind == Expr::kFalse)) {
            // a literal, not the pair that materialises a comparison
            if (PoolSizeWhenDischarged(slot.pc, pc, -1) > 255) return;
        } else if (load.op == OP_LOADNIL && load.a == operand && load.b == operand &&
                   slot.expr->kind == Expr::kNil) {
            if (PoolSizeWhenDischarged(slot.pc, pc, -1) > 255) return;
        } else {
            return;
        }
        DeclareInferredLocal("rk-const", operand, pc, ScopeEndFor(slot.pc), slot.expr, slot.pc);
    }

    // Number of constants in the pool when the value loaded at `load_pc`
    // (constant `own`, or -1) was discharged for the instruction at `use_pc`:
    // every constant referenced earlier, plus the consumer's constant
    // operands parsed before the value (a store's key).
    int PoolSizeWhenDischarged(int load_pc, int use_pc, int own) const {
        int size = ConstantsBefore(load_pc);
        const Instr& use = ins_[use_pc];
        if (use.op == OP_SETTABLE && use.b >= kRkBit) size = std::max(size, use.b - kRkBit + 1);
        // A store whose value was still being built (a pending register
        // below the load) had its key created before the value.
        const int loaded = ins_[load_pc].a;
        for (int r = 0; r < loaded && r < static_cast<int>(regs_.size()); ++r) {
            const Slot& below = regs_[static_cast<std::size_t>(r)];
            if (!below.expr || below.pc >= load_pc || IsLocalAt(load_pc, r)) continue;
            for (int q = use_pc; q < static_cast<int>(ins_.size()); ++q) {
                const Instr& in = ins_[q];
                if (in.op == OP_SETTABLE && in.c == r) {
                    if (in.b >= kRkBit) size = std::max(size, in.b - kRkBit + 1);
                    break;
                }
                if (in.op == OP_SETGLOBAL && in.a == r) {
                    size = std::max(size, in.bx + 1);
                    break;
                }
                if (InstructionWrites(q, r)) break;
            }
        }
        if (own >= 0 && own < size) return size;  // already in the pool
        if (own >= 0) return own;                  // created by this discharge
        return size;
    }

    // Size of the constant pool when the instruction at `pc` was emitted:
    // constants are numbered in order of first use.
    int ConstantsBefore(int pc) const {
        int max_index = -1;
        for (int p = 0; p < pc; ++p) {
            const Instr& in = ins_[p];
            const auto rk = [&](int v) { if (v >= kRkBit) max_index = std::max(max_index, v - kRkBit); };
            switch (in.op) {
                case OP_LOADK: case OP_GETGLOBAL: case OP_SETGLOBAL:
                    max_index = std::max(max_index, in.bx);
                    break;
                case OP_GETTABLE: case OP_SELF:
                    rk(in.c);
                    break;
                case OP_SETTABLE: case OP_EQ: case OP_LT: case OP_LE:
                case OP_ADD: case OP_SUB: case OP_MUL: case OP_DIV: case OP_MOD: case OP_POW:
                    rk(in.b);
                    rk(in.c);
                    break;
                default:
                    break;
            }
        }
        return max_index + 1;
    }

    // Whether register `reg` written at `pc` is read by an instruction within
    // the same straight-line stretch before being written again.
    bool IsOperandOfLater(int pc, int reg) const {
        for (int p = pc + 1; p < static_cast<int>(ins_.size()); ++p) {
            if (IsOperandOf(p, reg)) return true;
            if (InstructionWrites(p, reg) || ins_[p].op == OP_JMP || IsTestOp(ins_[p].op) ||
                ins_[p].op == OP_RETURN) {
                return false;
            }
        }
        return false;
    }

    bool PendingIsNumber(int reg, int pc) const {
        if (IsLocalAt(pc, reg)) return false;
        const Slot& slot = regs_[static_cast<std::size_t>(reg)];
        return slot.expr && slot.expr->kind == Expr::kNumber;
    }

    // Temporaries are written in evaluation order: an operand written before
    // one that the source evaluates earlier was a local.
    void CheckOperandOrder(int pc, std::initializer_list<int> operands) {
        CheckOperandOrder(pc, std::vector<int>(operands));
    }
    void CheckOperandOrder(int pc, const std::vector<int>& operands) {
        int prev_pc = -1;
        for (int r : operands) {
            if (r >= kRkBit || IsLocalAt(pc, r)) continue;
            const Slot& slot = regs_[static_cast<std::size_t>(r)];
            if (!slot.expr || slot.multi) continue;
            if (slot.pc < prev_pc) {
                DeclareInferredLocal("operand-order", r, pc, ScopeEndFor(slot.pc), slot.expr, slot.pc);
            }
            prev_pc = slot.pc;
        }
    }

    std::string UpvalueName(int index) const {
        if (index < static_cast<int>(upvalue_names_.size())) return upvalue_names_[static_cast<std::size_t>(index)];
        return "upvalue_" + std::to_string(index);
    }

    void CollectValue(int reg, int pc, std::vector<ExprP>& values) {
        Slot& slot = regs_[static_cast<std::size_t>(reg)];
        if ((IsLocalAt(pc, reg) || !written_[static_cast<std::size_t>(reg)] || stripped_) && !slot.expr) {
            values.push_back(RegExpr(reg, pc));
            return;
        }
        if (!slot.expr) Fail(pc, "missing value in register " + std::to_string(reg));
        if (slot.multi) {
            slot = Slot{};
            return;
        }
        values.push_back(slot.expr);
        slot = Slot{};
    }

    ExprP BuildCall(int pc) {
        const Instr& in = ins_[pc];
        Slot& fslot = regs_[static_cast<std::size_t>(in.a)];
        ExprP call;
        int first_arg = in.a + 1;
        if (!IsLocalAt(pc, in.a) && fslot.expr && fslot.expr->kind == Expr::kMethodCall) {
            call = std::make_shared<Expr>(*fslot.expr);  // speculative probes share nodes
            fslot = Slot{};
            regs_[static_cast<std::size_t>(in.a) + 1] = Slot{};
            first_arg = in.a + 2;
        } else {
            call = MakeExpr(Expr::kCall, in.line);
            call->a = RegExpr(in.a, pc);
        }
        call->line = in.line;
        const int last = in.b == 0 ? PendingTop(first_arg) : in.a + in.b;
        for (int r = first_arg; r < last; ++r) {
            CollectValue(r, pc, call->args);
        }
        LUADEC_TRACE("%s call at %d: first_arg=%d last=%d args=%zu", path_.c_str(), pc, first_arg, last, call->args.size());
        if (in.b != 0 && !call->args.empty()) {
            ExprP& tail = call->args.back();
            if ((tail->kind == Expr::kCall || tail->kind == Expr::kMethodCall ||
                 tail->kind == Expr::kVararg) && tail->nresults == 1) {
                tail->paren_call = true;
            }
        }
        return call;
    }

    int EmitSet(int pc, ExprP target, ExprP value, std::vector<StmtP>& out, const Ctx& ctx) {
        const int first = static_cast<int>(out.size());
        EmitAssign(pc, std::move(target), std::move(value), out, ctx);
        int next = pc + 1;
        struct GroupGuard {
            bool& flag;
            explicit GroupGuard(bool& f) : flag(f) { flag = true; }
            ~GroupGuard() { flag = false; }
        } guard(grouping_);
        // Older temporaries left pending belong to a multiple assignment whose
        // remaining stores follow immediately.
        int lowest = -1;
        for (int r = 0; r < static_cast<int>(regs_.size()); ++r) {
            if (regs_[static_cast<std::size_t>(r)].expr && !IsLocalAt(pc, r)) {
                lowest = r;
                break;
            }
        }
        while (lowest >= 0 && next < static_cast<int>(ins_.size())) {
            const Instr& in = ins_[next];
            const bool is_store = in.op == OP_SETGLOBAL || in.op == OP_SETUPVAL ||
                                  in.op == OP_SETTABLE ||
                                  (in.op == OP_MOVE && IsLocalAt(next, in.a));
            if (!is_store) break;
            const int before = static_cast<int>(out.size());
            next = ProcessSimpleNoGroup(next, out, ctx);
            if (static_cast<int>(out.size()) != before + 1) break;
            lowest = -1;
            for (int r = 0; r < static_cast<int>(regs_.size()); ++r) {
                if (regs_[static_cast<std::size_t>(r)].expr && !IsLocalAt(next, r)) {
                    lowest = r;
                    break;
                }
            }
        }
        if (static_cast<int>(out.size()) > first + 1) {
            GroupAssignments(out, first);
        }
        return next;
    }

    int ProcessSimpleNoGroup(int pc, std::vector<StmtP>& out, const Ctx& ctx) {
        const Instr& in = ins_[pc];
        switch (in.op) {
            case OP_SETGLOBAL: {
                auto target = MakeExpr(Expr::kGlobal, in.line);
                target->name = f_.constants[static_cast<std::size_t>(in.bx)].string;
                EmitAssign(pc, target, RegExpr(in.a, pc), out, ctx);
                return pc + 1;
            }
            case OP_SETUPVAL: {
                auto target = MakeExpr(Expr::kUpvalue, in.line);
                target->name = UpvalueName(in.b);
                EmitAssign(pc, target, RegExpr(in.a, pc), out, ctx);
                return pc + 1;
            }
            case OP_SETTABLE: {
                auto target = MakeExpr(Expr::kIndex, in.line);
                target->a = RegExpr(in.a, pc);
                target->b = RkExpr(in.b, pc);
                target->b->line = 0;
                EmitAssign(pc, target, RkExpr(in.c, pc), out, ctx);
                return pc + 1;
            }
            case OP_MOVE:
                WriteReg(pc, in.a, RegExpr(in.b, pc), out, ctx);
                return pc + 1;
            default:
                return ProcessSimple(pc, out, ctx);
        }
    }

    // ---- loops ---------------------------------------------------------------

    void BuildLoop(Loop& loop, std::vector<StmtP>& out, const Ctx& ctx) {
        (void)ctx;
        auto stmt = std::make_shared<Stmt>();
        switch (loop.kind) {
            case Loop::kNumFor: {
                const Instr& prep = ins_[loop.start];
                stmt->kind = Stmt::kNumFor;
                for (int r = prep.a; r < prep.a + 3; ++r) {
                    Slot& slot = regs_[static_cast<std::size_t>(r)];
                    if (!slot.expr) Fail(loop.start, "numeric for without initialisers");
                    stmt->values.push_back(slot.expr);
                    slot = Slot{};
                }
                stmt->keyword_line = ins_[loop.end].line;
                stmt->line = FirstLine(stmt->values[0]);
                ClaimLoopLocals(loop.start, prep.a, 3);
                ClaimLoopLocals(loop.body_start, prep.a, 3);
                stmt->names.push_back(LocalNameAt(loop.body_start, prep.a + 3));
                ClaimLoopLocals(loop.body_start, prep.a + 3, 1);
                stmt->body = BuildBlock(loop.body_start, loop.body_end, loop.exit, loop.body_end);
                break;
            }
            case Loop::kGenFor: {
                const Instr& tfor = ins_[loop.body_end];
                stmt->kind = Stmt::kGenFor;
                const int base = tfor.a;
                for (int r = base; r < base + 3; ++r) {
                    Slot& slot = regs_[static_cast<std::size_t>(r)];
                    if (!slot.expr) break;
                    if (!slot.multi) stmt->values.push_back(slot.expr);
                    slot = Slot{};
                }
                // Trailing nils supplied by adjustment are implicit.
                while (stmt->values.size() > 1 && stmt->values.back()->kind == Expr::kNil) {
                    stmt->values.pop_back();
                }
                stmt->keyword_line = tfor.line;
                stmt->line = stmt->values.empty() ? tfor.line : FirstLine(stmt->values[0]);
                ClaimLoopLocals(loop.start, base, 3);
                ClaimLoopLocals(loop.body_start, base, 3);
                for (int i = 0; i < tfor.c; ++i) {
                    stmt->names.push_back(LocalNameAt(loop.body_start, base + 3 + i));
                }
                ClaimLoopLocals(loop.body_start, base + 3, tfor.c);
                stmt->body = BuildBlock(loop.body_start, loop.body_end, loop.exit, loop.body_end);
                HintEndLine(stmt->body, ins_[loop.end].line);
                break;
            }
            case Loop::kWhile: {
                stmt->kind = Stmt::kWhile;
                int body_start = loop.start;
                {
                    std::vector<Leaf> leaves;
                    int true_pc = 0;
                    int false_pc = 0;
                    if (stripped_) ShrinkLocalsInCondition(loop.start);
                    cond_floor_ = loop.start;
                    const int after = GatherChain(loop.start, loop.end, leaves, &true_pc, &false_pc,
                                                  /*allow_value=*/false);
                    cond_floor_ = -1;
                    for (std::size_t i = 0; i + 1 < leaves.size(); ++i) {
                        Leaf& leaf = leaves[i];
                        if (leaf.target != true_pc && leaf.target == ResolveThreaded(true_pc)) {
                            leaf.target = true_pc;
                        }
                    }
                    if (!leaves.empty() && JumpsTo(false_pc, loop.exit)) {
                        stmt->cond = BuildCondition(leaves, 0, static_cast<int>(leaves.size()),
                                                    true_pc, false_pc);
                        body_start = after;
                    } else {
                        // Not the loop condition after all: restore and treat as `while true`.
                        RestoreSnapshot();
                    }
                }
                if (!stmt->cond) {
                    stmt->cond = MakeExpr(Expr::kTrue, ins_[loop.start].line);
                }
                stmt->line = FirstLine(stmt->cond);
                PushClosingJump(loop.end);
                stmt->body = BuildBodyBlock(body_start, loop.body_end, loop.exit);
                PopClosingJump();
                HintEndLine(stmt->body, ins_[loop.end].line);
                break;
            }
            case Loop::kRepeat: {
                stmt->kind = Stmt::kRepeat;
                stmt->line = ins_[loop.start].line;
                // The until-condition is the trailing chain whose jumps go back to
                // the loop start. Build the body up to it.
                const int cond_start = FindRepeatConditionStart(loop);
                PushClosingJump(loop.end);
                stmt->body = BuildBlock(loop.start, cond_start, loop.exit, cond_start);
                PopClosingJump();
                std::vector<Leaf> leaves;
                int true_pc = 0;
                int false_pc = 0;
                cond_floor_ = cond_start;
                const int after = GatherChain(cond_start, loop.end + 1, leaves, &true_pc, &false_pc,
                                              /*allow_value=*/false);
                cond_floor_ = -1;
                if (leaves.empty() || after != loop.end + 1) {
                    Fail(loop.end, "repeat condition not recognised");
                }
                // For repeat the back jump is the false exit and falling through exits.
                stmt->cond = BuildCondition(leaves, 0, static_cast<int>(leaves.size()),
                                            loop.exit, loop.start);
                break;
            }
        }
        out.push_back(stmt);
    }

    void ClaimLoopLocals(int pc, int first_reg, int count) {
        for (int r = first_reg; r < first_reg + count; ++r) {
            const int index = LocalIndexAt(pc, r);
            if (index >= 0) claimed_locvars_.insert(index);
        }
    }

    // The until condition of a repeat loop starts after the last statement of
    // the body. Walk back from the closing jump over the condition's leaves and
    // their operand loads.
    int FindRepeatConditionStart(const Loop& loop) {
        // Find the earliest pc from which every instruction up to loop.end is a
        // pure operand load or a test/jump pair whose targets are the loop
        // start, the loop exit, or interior points.
        int start = loop.end - 1;  // the last test
        for (;;) {
            int candidate = start;
            // extend backwards over operand loads
            while (candidate - 1 >= loop.start && IsOperandLoad(candidate - 1, loop)) {
                --candidate;
            }
            // is there another leaf of this chain before?
            if (candidate - 2 >= loop.start && IsTestOp(ins_[candidate - 2].op) &&
                ins_[candidate - 1].op == OP_JMP) {
                const int target = candidate + ins_[candidate - 1].sbx;
                if (target == loop.start || target == loop.exit ||
                    (target > candidate - 1 && target <= loop.end + 1)) {
                    start = candidate - 2;
                    continue;
                }
            }
            return candidate;
        }
    }

    bool IsOperandLoad(int pc, const Loop& loop) const {
        (void)loop;
        const Instr& in = ins_[pc];
        switch (in.op) {
            case OP_MOVE: case OP_LOADK: case OP_GETUPVAL: case OP_GETGLOBAL: case OP_GETTABLE:
            case OP_ADD: case OP_SUB: case OP_MUL: case OP_DIV: case OP_MOD: case OP_POW:
            case OP_UNM: case OP_NOT: case OP_LEN: case OP_CONCAT: case OP_SELF:
            case OP_NEWTABLE: case OP_VARARG:
                return !IsLocalAt(pc, in.a);
            case OP_LOADBOOL:
                return !IsLocalAt(pc, in.a) && in.c == 0;
            case OP_CALL:
                return in.c != 1 && !IsLocalAt(pc, in.a);
            case OP_SETTABLE:
            case OP_SETLIST:
                return !IsLocalAt(pc, in.a) && HasOpenTableBefore(pc, in.a);
            case OP_CLOSURE:
                return !IsLocalAt(pc, in.a);
            default:
                return false;
        }
    }

    // Whether an earlier NEWTABLE into `reg` is still being built at `pc`.
    bool HasOpenTableBefore(int pc, int reg) const {
        for (int p = pc - 1; p >= 0; --p) {
            const Instr& in = ins_[p];
            if (in.op == OP_NEWTABLE && in.a == reg) return true;
            if (in.op == OP_JMP || IsTestOp(in.op) || in.op == OP_CALL || in.op == OP_RETURN) {
                if (in.op == OP_CALL && in.a > reg) continue;
                return false;
            }
            if (in.a == reg && in.op != OP_SETTABLE && in.op != OP_SETLIST) return false;
        }
        return false;
    }

    // ---- conditions ----------------------------------------------------------

    void TakeSnapshot() { snapshot_ = regs_; }
    void RestoreSnapshot() { regs_ = snapshot_; }

    Leaf MakeLeaf(int pc) {
        const Instr& in = ins_[pc];
        Leaf leaf;
        leaf.pc = pc;
        leaf.op = in.op;
        leaf.target = pc + 2 + ins_[pc + 1].sbx;
        const auto pin_paren = [&](const ExprP& e) {
            if (!e || (e->kind != Expr::kCall && e->kind != Expr::kMethodCall) || e->end_line != 0 ||
                e->args.empty()) {
                return;
            }
            const Expr::Kind k = e->args.back()->kind;
            if (k == Expr::kFunction || k == Expr::kTable) e->end_line = in.line;
        };
        switch (in.op) {
            case OP_EQ: case OP_LT: case OP_LE: {
                auto cmp = MakeExpr(Expr::kCompare, in.line);
                cmp->op = in.op;
                cmp->a = RkExpr(in.b, pc);
                cmp->b = RkExpr(in.c, pc);
                cmp->swapped = in.op != OP_EQ && ShouldSwapComparison(cmp->a, cmp->b);
                {
                    // A local first operand carries the comparison's own line,
                    // which is the second operand's last token: its token sat
                    // no later than the second operand's first.
                    const ExprP& first = cmp->swapped ? cmp->b : cmp->a;
                    const ExprP& second = cmp->swapped ? cmp->a : cmp->b;
                    const int second_line = FirstLine(second);
                    if ((first->kind == Expr::kLocal || first->kind == Expr::kUpvalue) && second_line != 0 &&
                        first->line > second_line) {
                        first->line = 0;
                    }
                }
                leaf.cond = cmp;
                leaf.jump_when_true = in.a != 0;
                break;
            }
            case OP_TEST:
                leaf.reg = in.a;
                leaf.cond = RegExpr(in.a, pc);
                pin_paren(leaf.cond);
                leaf.cond->test_line = in.line;
                leaf.jump_when_true = in.c != 0;
                break;
            case OP_TESTSET:
                leaf.reg = in.a;
                leaf.cond = RegExpr(in.b, pc);
                leaf.cond->test_line = in.line;
                leaf.jump_when_true = in.c != 0;
                leaf.value_keeping = true;
                break;
            default:
                Fail(pc, "not a test");
        }
        return leaf;
    }

    // `x > y` compiles to LT y x with x loaded first. The operand that was
    // loaded (or whose constant was interned) later is the one the source
    // wrote first when it sits in B.
    static int FirstConstant(const ExprP& e) {
        if (!e) return -1;
        if (e->kindex >= 0) return e->kindex;
        int best = -1;
        for (const ExprP& child : {e->a, e->b}) {
            const int k = FirstConstant(child);
            if (k >= 0 && (best < 0 || k < best)) best = k;
        }
        return best;
    }

    static bool ShouldSwapComparison(const ExprP& b, const ExprP& c) {
        const bool b_const = b->kind == Expr::kNumber || b->kind == Expr::kString ||
                             b->kind == Expr::kNil || b->kind == Expr::kTrue ||
                             b->kind == Expr::kFalse;
        const bool c_const = c->kind == Expr::kNumber || c->kind == Expr::kString ||
                             c->kind == Expr::kNil || c->kind == Expr::kTrue ||
                             c->kind == Expr::kFalse;
        if (b->pc >= 0 && c->pc >= 0) return b->pc > c->pc;  // both loaded into registers
        if (b_const && c_const) return false;
        if (!b_const && !c_const) {
            if (b->pc >= 0 && c->pc < 0) return true;   // B is a temp, C a local: C was first
            return false;
        }
        if (b_const) {
            // B constant, C register. The source wrote `C > B` unless the
            // constant order shows B was interned before C's first constant.
            const int kc = FirstConstant(c);
            if (kc < 0) return true;
            return b->kindex > kc;
        }
        return false;  // B register, C constant: `B < C`
    }

    bool ChainValid(const std::vector<Leaf>& leaves) const {
        const int n = static_cast<int>(leaves.size());
        const int false_pc = leaves.back().target;
        // Value chains keep a tested operand by jumping past the LOADBOOL pair.
        int end_pc = -1;
        if (false_pc - 1 >= 0 && false_pc < static_cast<int>(ins_.size()) &&
            ins_[false_pc].op == OP_LOADBOOL && ins_[false_pc - 1].op == OP_LOADBOOL &&
            ins_[false_pc - 1].c == 1) {
            end_pc = false_pc + 1;
        } else if (false_pc + 1 < static_cast<int>(ins_.size()) &&
                   ins_[false_pc].op == OP_LOADBOOL && ins_[false_pc].c == 1 &&
                   ins_[false_pc + 1].op == OP_LOADBOOL) {
            end_pc = false_pc + 2;
        }
        const int body = leaves.back().pc + leaves.back().width;
        const int body_threaded = ResolveThreaded(body);
        for (int i = 0; i < n - 1; ++i) {
            const int target = leaves[static_cast<std::size_t>(i)].target;
            if (target == false_pc || target == end_pc || target == body_threaded) continue;
            bool interior = false;
            for (int k = i + 1; k < n; ++k) {
                if (target == leaves[static_cast<std::size_t>(k)].pc + leaves[static_cast<std::size_t>(k)].width) {
                    interior = true;
                    break;
                }
            }
            if (!interior) return false;
        }
        return true;
    }

    // Collects the test/jump leaves of one condition starting at `pc`. Operand
    // loads between leaves are evaluated into temporaries. Returns the pc after
    // the chain and sets the body start (true) and false exit.
    int GatherChain(int pc, int limit, std::vector<Leaf>& leaves, int* true_pc, int* false_pc,
                    bool allow_value) {
        leaves.clear();
        std::vector<Leaf> tentative;
        std::vector<Slot> best_regs;
        int best_after = pc;
        int cur = pc;
        bool has_testset = false;
        int chain_last_write = -1;
        std::vector<StmtP> scratch;
        Ctx ctx;
        ctx.block_end = limit;
        TakeSnapshot();
        if (cur < limit && ins_[cur].op == OP_JMP && ins_[cur].sbx > 0 && !IsPairedJump(cur)) {
            // `true or ...`: Lua 5.1.2 emits an unconditional jump to the body.
            Leaf leaf;
            leaf.pc = cur;
            leaf.width = 1;
            leaf.target = cur + 1 + ins_[cur].sbx;
            leaf.jump_when_true = true;
            leaf.cond = MakeExpr(Expr::kTrue, ins_[cur].line);
            tentative.push_back(leaf);
            cur += 1;
        }
        struct ProbeGuard {
            bool& flag;
            explicit ProbeGuard(bool& f) : flag(f) { flag = true; }
            ~ProbeGuard() { flag = false; }
        } probe_guard(probing_);
        // Operand loads before the first test.
        while (cur < limit && !IsTestOp(ins_[cur].op) && IsPureOperand(cur) &&
               (cur == pc || (LocalsStartingAt(cur).empty() && !LoopStartsAt(cur)))) {
            try {
                cur = ProcessSimple(cur, scratch, ctx);
            } catch (const DecompileError&) {
                break;
            }
            if (!scratch.empty()) break;
        }
        for (;;) {
            // A bare forward jump right after a leaf is a constant operand
            // (Lua 5.1.2 emits `luaK_jump` for `true` in an or-test and `false`
            // in an and-test). It must land inside the enclosing block.
            if (!tentative.empty() && cur < limit && ins_[cur].op == OP_JMP && ins_[cur].sbx > 0 &&
                cur + 1 + ins_[cur].sbx <= limit) {
                Leaf leaf;
                leaf.pc = cur;
                leaf.width = 1;
                leaf.target = cur + 1 + ins_[cur].sbx;
                leaf.jump_when_true = true;
                leaf.cond = MakeExpr(Expr::kTrue, ins_[cur].line);
                tentative.push_back(leaf);
                cur += 1;
                if (ChainValid(tentative)) {
                    leaves = tentative;
                    best_regs = regs_;
                    best_after = cur;
                }
                int probe = cur;
                bool ok = true;
                while (probe < limit && !IsTestOp(ins_[probe].op)) {
                    if (!IsPureOperand(probe) || !LocalsStartingAt(probe).empty() ||
                        LoopStartsAt(probe)) {
                        ok = false;
                        break;
                    }
                    try {
                        probe = ProcessSimple(probe, scratch, ctx);
                    } catch (const DecompileError&) {
                        ok = false;
                        break;
                    }
                    if (!scratch.empty()) {
                        ok = false;
                        break;
                    }
                }
                if (!ok || probe >= limit) break;
                cur = probe;
            }
            if (cur + 1 >= static_cast<int>(ins_.size()) || !IsTestOp(ins_[cur].op) ||
                ins_[cur + 1].op != OP_JMP) {
                break;
            }
            const bool is_testset = ins_[cur].op == OP_TESTSET;
            if (is_testset && !allow_value) break;
            if (is_testset && !tentative.empty() && !has_testset && !allow_value) break;
            probing_ = false;
            CheckLeafOrder(cur, &chain_last_write);
            Leaf leaf = MakeLeaf(cur);
            probing_ = true;
            has_testset = has_testset || is_testset;
            tentative.push_back(leaf);
            cur += 2;
            if (ChainValid(tentative)) {
                leaves = tentative;
                best_regs = regs_;
                best_after = cur;
            }
            // Evaluate operand loads leading to the next test.
            if (cur < limit && ins_[cur].op == OP_JMP) continue;  // constant leaf
            int probe = cur;
            bool ok = true;
            while (probe < limit && !IsTestOp(ins_[probe].op)) {
                if (!IsPureOperand(probe) || !LocalsStartingAt(probe).empty() ||
                    LoopStartsAt(probe)) {
                    ok = false;
                    break;
                }
                try {
                    probe = ProcessSimple(probe, scratch, ctx);
                } catch (const DecompileError&) {
                    ok = false;
                    break;
                }
                if (!scratch.empty()) {
                    ok = false;
                    break;
                }
            }
            if (!ok || probe >= limit || !LocalsStartingAt(probe).empty() || LoopStartsAt(probe)) break;
            cur = probe;
        }
        if (leaves.empty()) {
            RestoreSnapshot();
            *true_pc = pc;
            *false_pc = -1;
            return pc;
        }
        regs_ = best_regs;
        if (TraceEnabled()) {
            std::string desc;
            for (const Leaf& leaf : leaves) desc += " " + std::to_string(leaf.pc) + "->" + std::to_string(leaf.target);
            LUADEC_TRACE("%s chain from %d:%s true=%d false=%d", path_.c_str(), pc, desc.c_str(), best_after, leaves.back().target);
        }
        *true_pc = best_after;
        *false_pc = leaves.back().target;
        return best_after;
    }

    // The operand-load opcodes, whatever register they target.
    bool IsOperandOp(int pc) const {
        const Instr& in = ins_[pc];
        switch (in.op) {
            case OP_MOVE: case OP_LOADK: case OP_GETUPVAL: case OP_GETGLOBAL: case OP_GETTABLE:
            case OP_ADD: case OP_SUB: case OP_MUL: case OP_DIV: case OP_MOD: case OP_POW:
            case OP_UNM: case OP_NOT: case OP_LEN: case OP_CONCAT: case OP_SELF:
            case OP_NEWTABLE: case OP_VARARG: case OP_CLOSURE: case OP_LOADNIL:
                return true;
            case OP_LOADBOOL:
                return in.c == 0;
            case OP_CALL:
                return in.c != 1;
            case OP_SETTABLE: case OP_SETLIST:
                return HasOpenTableBefore(pc, in.a);
            default:
                return false;
        }
    }

    bool IsPureOperand(int pc) const {
        const Instr& in = ins_[pc];
        switch (in.op) {
            case OP_MOVE: case OP_LOADK: case OP_GETUPVAL: case OP_GETGLOBAL: case OP_GETTABLE:
            case OP_ADD: case OP_SUB: case OP_MUL: case OP_DIV: case OP_MOD: case OP_POW:
            case OP_UNM: case OP_NOT: case OP_LEN: case OP_CONCAT: case OP_SELF:
            case OP_NEWTABLE: case OP_VARARG: case OP_CLOSURE:
                return !IsLocalAt(pc, in.a);
            case OP_LOADBOOL:
                return !IsLocalAt(pc, in.a) && in.c == 0;
            case OP_LOADNIL:
                return !IsLocalAt(pc, in.a);
            case OP_CALL:
                return in.c != 1 && !IsLocalAt(pc, in.a);
            case OP_SETTABLE:
            case OP_SETLIST:
                return !IsLocalAt(pc, in.a) && HasOpenTableBefore(pc, in.a);
            default:
                return false;
        }
    }

    // Condition of a chain in which jumping to `true_pc` means true and
    // jumping to `false_pc` means false.
    ExprP BuildCondition(const std::vector<Leaf>& leaves, int i, int j, int true_pc, int false_pc) {
        ExprP result = BuildConditionInner(leaves, i, j, true_pc, false_pc);
        FixOperatorLines(result);
        return result;
    }

    // The TEST of a leaf is coded when the token after it is consumed: the
    // and/or operator following it, or the closing token of the whole
    // expression. Every operator and group-closing line follows from the
    // last leaf of its left operand.
    static int LastLeafTestLine(const ExprP& e) {
        if (!e) return 0;
        if (e->kind == Expr::kAnd || e->kind == Expr::kOr) return LastLeafTestLine(e->b);
        if (e->kind == Expr::kNot && !e->explicit_not) return LastLeafTestLine(e->a);
        return e->test_line;
    }
    static void FixOperatorLines(const ExprP& e) {
        if (!e) return;
        if (e->kind == Expr::kAnd || e->kind == Expr::kOr) {
            if (e->op_line == 0) e->op_line = LastLeafTestLine(e->a);
            if (e->end_line == 0) e->end_line = LastLeafTestLine(e);
            FixOperatorLines(e->a);
            FixOperatorLines(e->b);
        } else if (e->kind == Expr::kNot) {
            FixOperatorLines(e->a);
        }
    }

    ExprP BuildConditionInner(const std::vector<Leaf>& leaves, int i, int j, int true_pc, int false_pc) {
        if (i >= j) return nullptr;
        const Leaf& leaf = leaves[static_cast<std::size_t>(i)];
        const int line = ins_[leaf.pc].line;
        const int op_line = (leaf.op == OP_TEST || leaf.op == OP_TESTSET) ? line : 0;
        ExprP fall = leaf.jump_when_true ? MakeNot(leaf.cond, line) : leaf.cond;
        if (i + 1 == j) {
            if ((leaf.op == OP_TEST || leaf.op == OP_TESTSET) && leaf.cond &&
                (leaf.cond->kind == Expr::kCall || leaf.cond->kind == Expr::kMethodCall) &&
                leaf.cond->end_line == 0 && leaf.cond->args.empty()) {
                leaf.cond->end_line = line;  // `f(\n)`: the test is coded after ')'
            }
            if (leaf.target == false_pc) return fall;
            if (leaf.target == true_pc) return MakeNot(fall, line);
            Fail(leaf.pc, "condition leaf jumps outside its chain");
        }
        if (leaf.target == false_pc) {
            return MakeLogic(Expr::kAnd, fall, BuildCondition(leaves, i + 1, j, true_pc, false_pc), op_line);
        }
        if (leaf.target == true_pc) {
            return MakeLogic(Expr::kOr, MakeNot(fall, line),
                             BuildCondition(leaves, i + 1, j, true_pc, false_pc), op_line);
        }
        int k = -1;
        for (int m = i + 1; m < j; ++m) {
            if (LeafStart(leaves, m) == leaf.target) {
                k = m;
                break;
            }
        }
        if (k < 0) {
            for (int m = i + 1; m < j; ++m) {
                const Leaf& lm = leaves[static_cast<std::size_t>(m)];
                if (lm.pc + lm.width == leaf.target) {
                    k = m + 1;
                    break;
                }
            }
        }
        if (k < 0 || k > j) Fail(leaf.pc, "condition leaf jumps to an unknown point");
        // The group [i, k) either ends on the false exit, making it an or-chain
        // that is and-ed with the rest, or ends on a true exit X, making it an
        // and-chain that is or-ed with the alternatives up to X and and-ed with
        // whatever follows X.
        const int group_exit = leaves[static_cast<std::size_t>(k) - 1].target;
        const auto last_test_line = [&](int upto) {
            const Leaf& last = leaves[static_cast<std::size_t>(upto) - 1];
            return (last.op == OP_TEST || last.op == OP_TESTSET) ? ins_[last.pc].line : 0;
        };
        if (group_exit == false_pc) {
            ExprP inner = BuildCondition(leaves, i + 1, k, leaf.target, false_pc);
            ExprP rest = BuildCondition(leaves, k, j, true_pc, false_pc);
            ExprP group = MakeLogic(Expr::kOr, MakeNot(fall, line), inner, op_line);
            group->end_line = last_test_line(k);
            return MakeLogic(Expr::kAnd, group, rest, last_test_line(k));
        }
        int m = j;
        if (group_exit != true_pc) {
            m = -1;
            for (int q = k; q < j; ++q) {
                if (LeafStart(leaves, q) == group_exit) {
                    m = q;
                    break;
                }
            }
            if (m < 0) Fail(leaf.pc, "condition group exits to an unknown point");
        }
        ExprP inner = BuildCondition(leaves, i + 1, k, group_exit, leaf.target);
        ExprP alternatives = BuildCondition(leaves, k, m, group_exit, false_pc);
        ExprP rest = BuildCondition(leaves, m, j, true_pc, false_pc);
        ExprP group = MakeLogic(Expr::kAnd, fall, inner, op_line);
        group->end_line = last_test_line(k);
        ExprP either = MakeLogic(Expr::kOr, group, alternatives, last_test_line(k));
        either->end_line = last_test_line(m);
        return MakeLogic(Expr::kAnd, either, rest, last_test_line(m));
    }

    int LeafStart(const std::vector<Leaf>& leaves, int m) const {
        // pc after the previous leaf's jump, or the leaf itself for the first
        if (m == 0) return leaves[0].pc;
        const Leaf& prev = leaves[static_cast<std::size_t>(m) - 1];
        return prev.pc + prev.width;
    }

    // Value form: a value-keeping leaf (TESTSET, or TEST on the result
    // register) contributes its operand; C says whether it is `and` or `or`.
    ExprP BuildValueChain(const std::vector<Leaf>& leaves, int i, int j, int true_pc, int false_pc,
                          int end_pc, ExprP rest) {
        if (i >= j) return rest;
        const Leaf& leaf = leaves[static_cast<std::size_t>(i)];
        const int line = ins_[leaf.pc].line;
        const int op_line = (leaf.op == OP_TEST || leaf.op == OP_TESTSET) ? line : 0;
        const Leaf& last_leaf = leaves[static_cast<std::size_t>(j) - 1];
        const int rest_start = last_leaf.pc + last_leaf.width;
        auto find_k = [&](int target) {
            for (int m = i + 1; m < j; ++m) {
                if (LeafStart(leaves, m) == target) return m;
            }
            if (target == rest_start) return j;
            return -1;
        };
        if (leaf.value_keeping) {
            if (leaf.target == end_pc) {
                ExprP tail = BuildValueChain(leaves, i + 1, j, true_pc, false_pc, end_pc, rest);
                return MakeLogic(leaf.jump_when_true ? Expr::kOr : Expr::kAnd, leaf.cond, tail, op_line);
            }
            const int k = find_k(leaf.target);
            if (k < 0) Fail(leaf.pc, "value chain leaf jumps to an unknown point");
            ExprP inner = BuildValueChain(leaves, i + 1, k, true_pc, false_pc, end_pc, nullptr);
            ExprP outer = BuildValueChain(leaves, k, j, true_pc, false_pc, end_pc, rest);
            if (leaf.jump_when_true) {
                return MakeLogic(Expr::kAnd, MakeLogic(Expr::kOr, leaf.cond, inner, op_line), outer);
            }
            return MakeLogic(Expr::kOr, MakeLogic(Expr::kAnd, leaf.cond, inner, op_line), outer);
        }
        ExprP fall = leaf.jump_when_true ? MakeNot(leaf.cond, line) : leaf.cond;
        if (leaf.target == false_pc) {
            ExprP tail = BuildValueChain(leaves, i + 1, j, true_pc, false_pc, end_pc, rest);
            return MakeLogic(Expr::kAnd, fall, tail, op_line);
        }
        if (leaf.target == true_pc) {
            ExprP tail = BuildValueChain(leaves, i + 1, j, true_pc, false_pc, end_pc, rest);
            return MakeLogic(Expr::kOr, MakeNot(fall, line), tail, op_line);
        }
        const int k = find_k(leaf.target);
        if (k < 0) Fail(leaf.pc, "value chain leaf jumps to an unknown point");
        const Leaf* last = k - 1 > i ? &leaves[static_cast<std::size_t>(k) - 1] : nullptr;
        const int group_exit = last ? last->target : -1;
        const bool ends_false = last && (group_exit == false_pc ||
                                         (last->value_keeping && group_exit == end_pc && !last->jump_when_true));
        if (ends_false) {
            ExprP inner = BuildValueChain(leaves, i + 1, k, leaf.target, false_pc, end_pc, nullptr);
            ExprP tail = BuildValueChain(leaves, k, j, true_pc, false_pc, end_pc, rest);
            return MakeLogic(Expr::kAnd, MakeLogic(Expr::kOr, MakeNot(fall, line), inner), tail);
        }
        if (last && group_exit == rest_start && group_exit != end_pc) {
            if (leaf.jump_when_true) {
                // `((a or b) and c) or rest`: the or-group fails into the rest.
                ExprP inner = BuildValueChain(leaves, i + 1, k, leaf.target, group_exit, end_pc, nullptr);
                ExprP continuation = BuildValueChain(leaves, k, j, group_exit, false_pc, end_pc, nullptr);
                return MakeLogic(Expr::kOr, MakeLogic(Expr::kAnd, MakeLogic(Expr::kOr, MakeNot(fall, line), inner), continuation), rest);
            }
            // `(a and b or c) and rest`: the or-group's true exit is the continuation.
            ExprP inner = BuildValueChain(leaves, i + 1, k, group_exit, leaf.target, end_pc, nullptr);
            ExprP alternatives = BuildValueChain(leaves, k, j, group_exit, false_pc, end_pc, nullptr);
            return MakeLogic(Expr::kAnd, MakeLogic(Expr::kOr, MakeLogic(Expr::kAnd, fall, inner), alternatives), rest);
        }
        int m = j;
        if (k - 1 > i && group_exit != true_pc && group_exit != end_pc && group_exit != rest_start) {
            m = -1;
            for (int q = k; q < j; ++q) {
                if (LeafStart(leaves, q) == group_exit) {
                    m = q;
                    break;
                }
            }
            if (m < 0) Fail(leaf.pc, "value chain group exits to an unknown point");
        }
        const int inner_true = k - 1 > i ? group_exit : true_pc;
        ExprP inner = BuildValueChain(leaves, i + 1, k, inner_true, leaf.target, end_pc, nullptr);
        ExprP alternatives = BuildValueChain(leaves, k, m, inner_true, false_pc, end_pc, m == j ? rest : nullptr);
        ExprP tail = m == j ? nullptr : BuildValueChain(leaves, m, j, true_pc, false_pc, end_pc, rest);
        return MakeLogic(Expr::kAnd, MakeLogic(Expr::kOr, MakeLogic(Expr::kAnd, fall, inner), alternatives), tail);
    }

    // Resolves leaf targets through the closing jumps of enclosing blocks and
    // brings threaded true-jumps back to the body start.
    void NormalizeChain(std::vector<Leaf>& leaves, int block_end, int true_pc) const {
        for (Leaf& leaf : leaves) {
            const int raw = leaf.pc + leaf.width + ins_[leaf.pc + leaf.width - 1].sbx;
            leaf.target = EffectiveJumpTarget(leaf.pc + leaf.width - 1);
            if (leaf.target > block_end && ResolveThreaded(block_end) == leaf.target) {
                leaf.target = block_end;
            }
            leaf.remapped = leaf.target != raw;
        }
        // A true-jump onto a `break` or escape at the body start was threaded
        // through it; bring it back to the body start. The last leaf always
        // jumps on false, so it is never one of these.
        for (std::size_t i = 0; i + 1 < leaves.size(); ++i) {
            Leaf& leaf = leaves[i];
            if (!leaf.remapped && leaf.target != true_pc && leaf.target == ResolveThreaded(true_pc)) {
                leaf.target = true_pc;
            }
        }
    }

    // Handles a test at `pc`: an if statement, or a boolean/and-or value.
    int BuildConditional(int pc, int block_end, std::vector<StmtP>& out, Ctx& ctx) {
        std::vector<Leaf> leaves;
        int true_pc = 0;
        int false_pc = 0;
        int after = GatherChain(pc, block_end, leaves, &true_pc, &false_pc, /*allow_value=*/true);
        NormalizeChain(leaves, block_end, true_pc);
        // Targets remapped through enclosing closing jumps can invalidate a
        // chain that looked consistent on raw targets: keep the longest valid
        // prefix by gathering again up to the first leaf that breaks it.
        for (int guard = 0; guard < 8 && leaves.size() > 1 && !ChainValid(leaves); ++guard) {
            std::vector<Leaf> prefix = leaves;
            while (prefix.size() > 1 && !ChainValid(prefix)) prefix.pop_back();
            const int limit = leaves[prefix.size()].pc;
            RestoreSnapshot();
            GatherChain(pc, limit, leaves, &true_pc, &false_pc, /*allow_value=*/true);
            NormalizeChain(leaves, block_end, true_pc);
        }
        false_pc = leaves.back().target;
        after = true_pc;

        // Value forms.
        int value_reg = -1;
        int value_end = -1;
        for (const Leaf& leaf : leaves) {
            if (leaf.op == OP_TESTSET) {
                value_reg = leaf.reg;
                value_end = std::max(value_end, leaf.target);
            }
        }
        const auto lb_pair_at = [&](int p) {
            return p >= 0 && p + 1 < static_cast<int>(ins_.size()) && ins_[p].op == OP_LOADBOOL &&
                   ins_[p].c == 1 && ins_[p + 1].op == OP_LOADBOOL;
        };
        if (lb_pair_at(true_pc)) {
            value_reg = ins_[true_pc].a;
            value_end = true_pc + 2;
        } else if (lb_pair_at(false_pc) && false_pc - 1 >= true_pc && ins_[false_pc - 1].op == OP_JMP) {
            value_reg = ins_[false_pc].a;
            value_end = false_pc + 2;
        } else if (lb_pair_at(false_pc - 1) && false_pc - 2 >= true_pc && ins_[false_pc - 2].op == OP_JMP) {
            // the last leaf jumps straight to the `true` load
            value_reg = ins_[false_pc].a;
            value_end = false_pc + 1;
        }
        if (value_reg < 0) {
            // `r = a or b` with `a` already in r compiles to TEST r; the register
            // is a temporary that the code after the chain rewrites and keeps.
            for (const Leaf& leaf : leaves) {
                if (leaf.op != OP_TEST || IsLocalAt(leaf.pc, leaf.reg)) continue;
                if (!IsPureValueRegion(true_pc, false_pc, leaf.reg)) continue;
                bool empty_scope = false;
                for (const LocVar& var : locvars_) {
                    if (var.startpc == false_pc && var.endpc == false_pc) empty_scope = true;
                }
                if (empty_scope) continue;
                value_reg = leaf.reg;
                value_end = false_pc;
                break;
            }
        }
        if (value_reg >= 0) {
            // A plain TEST on another register that exits to the end keeps no
            // value: this is an `if` whose body assigns a boolean, not a value.
            bool plain_exit = false;
            std::size_t guard_count = 0;
            for (std::size_t i = 0; i < leaves.size(); ++i) {
                const Leaf& leaf = leaves[i];
                const bool keeps = leaf.op == OP_TESTSET || (leaf.op == OP_TEST && leaf.reg == value_reg);
                if (!keeps && leaf.target == value_end) {
                    plain_exit = true;
                    if (guard_count == i) guard_count = i + 1;  // leading guard leaves
                }
            }
            if (plain_exit) {
                value_reg = -1;
                if (guard_count > 0 && guard_count < leaves.size()) {
                    // `if guard then r = <value> end`: keep only the guard leaves.
                    const int limit = leaves[guard_count].pc;
                    RestoreSnapshot();
                    after = GatherChain(pc, limit, leaves, &true_pc, &false_pc, /*allow_value=*/false);
                    NormalizeChain(leaves, block_end, true_pc);
                    false_pc = leaves.back().target;
                    after = true_pc;
                }
            }
        }
        if (value_reg >= 0) {
            for (Leaf& leaf : leaves) {
                if (leaf.op == OP_TEST && leaf.reg == value_reg && leaf.target == value_end) {
                    leaf.value_keeping = true;
                }
            }
            return BuildValue(pc, leaves, true_pc, false_pc, block_end, out, ctx);
        }
        // Not a value: leaves that jump to a LOADBOOL label belong to a value
        // expression inside the body. Re-gather the chain up to the first one.
        {
            int cut = -1;
            for (const Leaf& leaf : leaves) {
                const int t = leaf.target;
                const bool lb_label = t >= 1 && t < static_cast<int>(ins_.size()) &&
                                      ins_[t].op == OP_LOADBOOL &&
                                      ((ins_[t].c == 1) || (ins_[t - 1].op == OP_LOADBOOL && ins_[t - 1].c == 1));
                if (lb_label) {
                    cut = leaf.pc;
                    break;
                }
            }
            if (cut > pc) {
                RestoreSnapshot();
                after = GatherChain(pc, cut, leaves, &true_pc, &false_pc, /*allow_value=*/false);
                for (Leaf& leaf : leaves) leaf.target = EffectiveJumpTarget(leaf.pc + leaf.width - 1);
                false_pc = leaves.back().target;
                for (std::size_t i = 0; i + 1 < leaves.size(); ++i) {
                    Leaf& leaf = leaves[i];
                    if (leaf.target != true_pc && leaf.target == ResolveThreaded(true_pc)) {
                        leaf.target = true_pc;
                    }
                }
            }
        }

        if (false_pc > block_end) {
            // The false exit may have been threaded through a `break` that
            // opens the else body: find the escape that closes the then body.
            for (int p = after; p < block_end; ++p) {
                if (ins_[p].op == OP_JMP && !IsPairedJump(p) && ins_[p].sbx >= 0 &&
                    ResolveThreaded(p + 1) == false_pc && p + 1 < block_end) {
                    false_pc = p + 1;
                    for (Leaf& leaf : leaves) {
                        if (leaf.target == ResolveThreaded(p + 1)) leaf.target = false_pc;
                    }
                    break;
                }
            }
        }
        if (false_pc > block_end || false_pc < after) {
            Fail(pc, "if body extends past the enclosing block");
        }
        auto stmt = std::make_shared<Stmt>();
        stmt->kind = Stmt::kIf;
        stmt->cond = BuildCondition(leaves, 0, static_cast<int>(leaves.size()), true_pc, false_pc);
        stmt->line = FirstLine(stmt->cond);
        int then_end = false_pc;
        int else_end = false_pc;
        if (false_pc - 1 >= after && ins_[false_pc - 1].op == OP_JMP && !IsPairedJump(false_pc - 1)) {
            const int raw_target = false_pc + ins_[false_pc - 1].sbx;
            int target = EffectiveJumpTarget(false_pc - 1);
            if (target > block_end && ResolveThreaded(block_end) == target) target = block_end;
            const bool is_break = ctx.loop_exit >= 0 &&
                                  (JumpsTo(raw_target, ctx.loop_exit) || JumpsTo(target, ctx.loop_exit));
            // A backward jump here may be a `continue` rather than an else
            // escape: an escape inside the then body that lands on the
            // would-be else start proves the then body extends past this jump.
            bool is_continue = false;
            if (raw_target <= false_pc - 1) {
                for (int p = after; p < false_pc - 1; ++p) {
                    if (ins_[p].op == OP_JMP && !IsPairedJump(p) && p + 1 + ins_[p].sbx == false_pc) {
                        is_continue = true;
                    }
                }
            }
            const bool own_statement = target == false_pc && false_pc - 2 >= after &&
                                       ins_[false_pc - 1].line > ins_[false_pc - 2].line;
            if (!is_break && !is_continue && !own_statement && target > false_pc - 1 && target <= block_end) {
                then_end = false_pc - 1;
                else_end = target;
                stmt->has_else = true;
            }
        }
        LUADEC_TRACE("%s if at %d: then [%d,%d) else [%d,%d) has_else=%d block_end=%d", path_.c_str(), pc,
                     after, then_end, false_pc, else_end, stmt->has_else ? 1 : 0, block_end);
        if (stmt->has_else) PushClosingJump(then_end);
        stmt->body = BuildBodyBlock(after, then_end, ctx.loop_exit);
        if (stmt->has_else) {
            PopClosingJump();
            stmt->else_body = BuildBodyBlock(false_pc, else_end, ctx.loop_exit);
        }
        HintEndLine(stmt->body, stmt->has_else ? ins_[then_end].line : 0);
        stmt->escape_line = stmt->has_else ? ins_[then_end].line : 0;
        out.push_back(stmt);
        return else_end;
    }

    // Whether the value written to `reg` at `write_pc` is next used where
    // only a temporary can sit: a call frame, a return list, a constructor
    // item list or a concatenation. Locals are copied into those places.
    bool ConsumedAsFrameTemp(int write_pc, int reg) const {
        for (int q = write_pc + 1; q < static_cast<int>(ins_.size()); ++q) {
            const Instr& in = ins_[q];
            switch (in.op) {
                case OP_CALL: case OP_TAILCALL:
                    if (in.b == 0 ? reg >= in.a : (in.a <= reg && reg < in.a + in.b)) return true;
                    break;
                case OP_RETURN:
                    // `return x` returns a local from its own register; longer
                    // lists and open lists are built from temporaries.
                    return in.b == 0 ? reg >= in.a : (in.b > 2 && in.a <= reg && reg < in.a + in.b - 1);
                case OP_SETLIST:
                    if (reg > in.a && (in.b == 0 || reg <= in.a + in.b)) return true;
                    break;
                case OP_CONCAT:
                    if (in.b <= reg && reg <= in.c) return true;
                    break;
                case OP_SELF:
                    if (in.a == reg) return in.b == reg;
                    break;
                case OP_JMP: case OP_FORPREP: case OP_FORLOOP: case OP_TFORLOOP:
                case OP_TEST: case OP_TESTSET: case OP_EQ: case OP_LT: case OP_LE:
                    // frame temporaries are consumed within straight-line code
                    return false;
                default:
                    break;
            }
            if (InstructionWrites(q, reg)) return false;
        }
        return false;
    }

    // Whether the instruction at `pc` can be a local's initialiser: a call
    // feeding another call's arguments (C=0) or a SELF frame never is.
    bool CanDeclare(int pc) const {
        const Instr& in = ins_[pc];
        switch (in.op) {
            case OP_CALL: return in.c != 0;
            case OP_VARARG: return in.b != 0;
            case OP_SELF: case OP_FORPREP: case OP_FORLOOP: case OP_TFORLOOP: return false;
            default: return true;
        }
    }

    // If the write at `write_pc` completes an and/or value expression into
    // `reg` (every jump to the pc after it follows a test that keeps the
    // register's value), the pc where that expression starts; otherwise -1.
    int ValueChainStart(int write_pc, int reg, bool* elided = nullptr) const {
        int end = write_pc + 1;
        bool landed = false;
        int start = write_pc;
        // `x = ... or x` elides the final load: the chain ends with a jump to
        // its own end, and jumps there may come from tests of other
        // registers (the discarded `and` operands).
        bool elided_tail = ins_[write_pc].op == OP_JMP && write_pc + 1 + ins_[write_pc].sbx == end;
        int end_adjusted = end;
        if (!elided_tail && end < static_cast<int>(ins_.size()) && ins_[end].op == OP_JMP &&
            end + 1 + ins_[end].sbx == end + 1 && (ins_[write_pc].op == OP_TESTSET || ins_[write_pc].op == OP_TEST)) {
            // the last test keeps the value and its jump skips the elided load
            elided_tail = true;
            end_adjusted = end + 1;
        }
        if (elided) *elided = elided_tail;
        if (elided_tail && write_pc == 0) return -1;
        end = end_adjusted;
        // A comparison materialised as a value ends in LOADBOOL r 0 1 / LOADBOOL
        // r 1 0: jumps land on either of them.
        const bool bool_pair = write_pc >= 1 && ins_[write_pc].op == OP_LOADBOOL && ins_[write_pc].a == reg &&
                               ins_[write_pc].c == 0 && ins_[write_pc - 1].op == OP_LOADBOOL &&
                               ins_[write_pc - 1].a == reg && ins_[write_pc - 1].c == 1;
        if (bool_pair) {
            landed = true;
            start = write_pc - 1;
        }
        while (start > 0) {
            const int p = start - 1;
            const Instr& in = ins_[p];
            if (in.op == OP_JMP) {
                const int target = p + 1 + in.sbx;
                if (bool_pair && (target == end - 1 || target == end - 2)) {
                    // comparison true/false jumps onto the pair
                } else if (target == end) {
                    if (p == 0) return -1;
                    const Instr& test = ins_[p - 1];
                    const bool kept = (test.op == OP_TEST || test.op == OP_TESTSET) && test.a == reg;
                    // with a pair, the last operand is loaded and skips it
                    const bool loaded = bool_pair && InstructionWrites(p - 1, reg);
                    const bool discarded = elided_tail && IsTestOp(test.op);
                    if (!kept && !loaded && !discarded) return -1;
                    landed = true;
                } else if (target <= p || target > end) {
                    break;
                }
            } else if (!IsTestOp(in.op)) {
                if (!IsPureOperand(p)) break;
                if (in.op == OP_MOVE && in.a == reg && in.b > reg) return -1;
                bool below = false;
                for (int r = 0; r < reg; ++r) {
                    if (InstructionWrites(p, r)) below = true;
                }
                if ((in.op == OP_SETTABLE || in.op == OP_SETLIST) && in.a < reg) below = true;
                if (below) break;
            }
            start = p;
        }
        return landed ? start : -1;
    }

    // Whether [start, end) is operand code whose last instruction writes `reg`.
    bool IsPureValueRegion(int start, int end, int reg) const {
        if (start >= end) return false;
        int last_write = -1;
        for (int pc = start; pc < end; ++pc) {
            const Instr& in = ins_[pc];
            if (IsTestOp(in.op) || in.op == OP_JMP) {
                // nested and/or inside the value: allowed if it stays inside
                if (in.op == OP_JMP) {
                    const int target = pc + 1 + in.sbx;
                    if (target > end || target <= start) return false;
                }
                continue;
            }
            if (!IsPureOperand(pc)) return false;
            if (in.op == OP_SETTABLE || in.op == OP_SETLIST) continue;
            // A leaf is discharged straight into the target register; a
            // value built above it and moved down means the register was a
            // live local being assigned.
            if (in.op == OP_MOVE && in.a == reg && in.b > reg) return false;
            last_write = in.a;
            if (in.op == OP_LOADNIL) last_write = in.a <= reg && reg <= in.b ? reg : in.a;
        }
        // The value is complete at the region's last instruction, which must
        // be the write itself (a trailing jump means a statement structure).
        const Op last_op = ins_[end - 1].op;
        if (last_op == OP_JMP || IsTestOp(last_op)) return false;
        return last_write == reg;
    }

    // Builds `r = <and/or/comparison expression>` from a chain and the code
    // that follows it up to the point where the register is complete.
    int BuildValue(int pc, std::vector<Leaf>& leaves, int true_pc, int false_pc, int block_end,
                   std::vector<StmtP>& out, Ctx& ctx) {
        // Destination register.
        int reg = -1;
        for (const Leaf& leaf : leaves) {
            if (leaf.value_keeping) reg = leaf.reg;
        }
        int end_pc = 0;
        for (const Leaf& leaf : leaves) end_pc = std::max(end_pc, leaf.target);

        ExprP rest;
        int lb_false = -1;
        int lb_true = -1;
        // LOADBOOL pair directly after the chain.
        if (true_pc + 1 < static_cast<int>(ins_.size()) && ins_[true_pc].op == OP_LOADBOOL &&
            ins_[true_pc].c == 1 && ins_[true_pc + 1].op == OP_LOADBOOL) {
            lb_false = true_pc;
            lb_true = true_pc + 1;
            reg = ins_[true_pc].a;
            end_pc = std::max(end_pc, lb_true + 1);
            rest = nullptr;
        } else {
            // Straight code computing the rest value into `reg`, optionally
            // followed by a jump over a LOADBOOL pair.
            int cur = true_pc;
            int stop = end_pc;
            // If the chain's exit is a LOADBOOL pair, the rest ends before its jump.
            if (false_pc < static_cast<int>(ins_.size()) && ins_[false_pc].op == OP_LOADBOOL &&
                ins_[false_pc].c == 1) {
                lb_false = false_pc;
                lb_true = false_pc + 1;
                reg = ins_[false_pc].a;
                end_pc = lb_true + 1;
                stop = false_pc - 1;  // the JMP over the pair
                if (ins_[stop].op != OP_JMP) Fail(stop, "expected jump over LOADBOOL pair");
            } else if (false_pc - 1 >= 0 && ins_[false_pc - 1].op == OP_LOADBOOL &&
                       ins_[false_pc - 1].c == 1 && ins_[false_pc].op == OP_LOADBOOL) {
                lb_false = false_pc - 1;
                lb_true = false_pc;
                reg = ins_[false_pc].a;
                end_pc = lb_true + 1;
                stop = lb_false - 1;
                if (ins_[stop].op != OP_JMP) Fail(stop, "expected jump over LOADBOOL pair");
            }
            if (reg < 0) {
                // TEST-only chains: the register is whatever the rest writes last.
                Fail(pc, "value chain without a destination register");
            }
            Ctx inner = ctx;
            inner.value_reg = reg;
            std::vector<StmtP> scratch;
            while (cur < stop) {
                if (IsTestOp(ins_[cur].op)) {
                    cur = BuildConditional(cur, stop, scratch, inner);
                } else {
                    cur = ProcessSimple(cur, scratch, inner);
                }
                if (!scratch.empty()) Fail(cur, "statement inside a value expression");
            }
            Slot& slot = regs_[static_cast<std::size_t>(reg)];
            if (stripped_ && cur == true_pc && !IsLocalAt(true_pc, reg) && slot.expr && slot.pc < pc) {
                // `x = ... or x` with the final load elided: the register held
                // x from before this statement, so x is a local declared by
                // that earlier write.
                DeclareInferredLocal("elided-assign", reg, pc, ScopeEndFor(slot.pc), slot.expr, slot.pc);
            }
            if (!slot.expr && cur == true_pc && IsLocalAt(true_pc, reg)) {
                // `x = a or x`: the fallback is the register's own value.
                rest = MakeExpr(Expr::kLocal, ins_[true_pc - 1].line);
                rest->name = LocalNameAt(true_pc, reg);
            } else {
                if (!slot.expr) Fail(true_pc, "value expression leaves no result");
                rest = slot.expr;
                slot = Slot{};
            }
        }
        ExprP value = BuildValueChain(leaves, 0, static_cast<int>(leaves.size()), lb_true, lb_false,
                                      end_pc, rest);
        FixOperatorLines(value);
        (void)block_end;
        // Deliver the value: the register may be an active local (assignment)
        // or a temporary.
        const int write_pc = end_pc - 1;
        LUADEC_TRACE("%s value chain at %d -> r%d end=%d", path_.c_str(), pc, reg, end_pc);
        if (ctx.value_reg != reg && IsLocalAt(write_pc, reg) && !IsLocalAt(pc, reg)) {
            // becomes a local right after: park for the local statement
            regs_[static_cast<std::size_t>(reg)] = Slot{value, write_pc, false};
        } else {
            WriteReg(write_pc, reg, value, out, ctx);
        }
        return end_pc;
    }

    const Proto& f_;
    std::vector<std::string> upvalue_names_;
    std::string path_;
    std::vector<Instr> ins_;
    std::vector<std::vector<int>> active_;  // per pc: register -> locvar index
    std::set<int> claimed_locvars_;
    std::vector<Loop> loops_;
    std::vector<ClosingJump> jump_stack_;
    std::vector<LocVar> locvars_;
    std::set<std::string> reserved_names_;
    std::vector<std::pair<int, int>> block_stack_;   // [start, end) of enclosing blocks
    std::vector<ExprP> last_value_;                  // last expression each register held
    std::vector<Slot> regs_;
    std::vector<bool> written_;
    mutable std::vector<int> first_constant_use_;
    std::vector<Slot> snapshot_;
    bool stripped_ = false;
    bool grouping_ = false;
    bool probing_ = false;
    int pending_next_pc_ = 0;
    std::vector<int> stmt_starts_;  // per open block: first pc of the statement being built
    int last_stmt_start_ = 0;       // first pc of the statement most recently completed
    int cond_floor_ = -1;   // while building a loop condition: its first pc
};

// ---------------------------------------------------------------------------
// Printer
// ---------------------------------------------------------------------------

class Printer {
public:
    explicit Printer(const DecompileOptions& options) : options_(options) {}

    std::string Finish() {
        out_ += "\n";
        return out_;
    }

    void PrintChunk(const FunctionAst& ast) {
        PrintBlock(ast.body, 0, 0);
    }

private:
    // Emits `text` at `line` (0 = wherever we are). Line moves are monotone.
    void Tok(int line, const std::string& text, bool space = true) {
        if (options_.line_layout && line > cur_line_) {
            while (cur_line_ < line) {
                out_ += "\n";
                ++cur_line_;
            }
            at_line_start_ = true;
        }
        if (at_line_start_) {
            for (int i = 0; i < depth_; ++i) out_ += options_.indent;
            at_line_start_ = false;
        } else if (space && !out_.empty() && out_.back() != '\n' && out_.back() != '(' &&
                   out_.back() != '[' && out_.back() != '{' && out_.back() != '.' &&
                   out_.back() != ':' && out_.back() != '#' && out_.back() != '\t') {
            out_ += " ";
        }
        out_ += text;
    }

    void Newline() {
        if (!at_line_start_) {
            out_ += "\n";
            ++cur_line_;
            at_line_start_ = true;
        }
    }

    // Ensures the next token starts a fresh line unless the layout forces it
    // to stay on the current one.
    void Break(int next_line) {
        if (options_.line_layout && !line_limits_.empty() && line_limits_.back() > 0 &&
            cur_line_ >= line_limits_.back()) {
            // The enclosing function ends on this line: nothing below it may
            // move down.
            return;
        }
        if (!options_.line_layout || next_line == 0 || next_line > cur_line_) {
            Newline();
        }
    }

    // A closing keyword whose line is known stays on the current line when
    // that is its line; otherwise it opens a new one.
    void CloseKeyword(int close_line, int following_line, const char* word) {
        if (options_.line_layout && close_line > 0 && close_line <= cur_line_) {
            Tok(0, word);
            return;
        }
        Break(following_line);
        Tok(close_line, word);
    }

    static int Prec(const Expr& e) {
        switch (e.kind) {
            case Expr::kOr: return 1;
            case Expr::kAnd: return 2;
            case Expr::kCompare: return 3;
            case Expr::kBinary:
                switch (e.op) {
                    case OP_CONCAT: return 4;
                    case OP_ADD: case OP_SUB: return 5;
                    case OP_MUL: case OP_DIV: case OP_MOD: return 6;
                    case OP_POW: return 8;
                    default: return 6;
                }
            case Expr::kUnary:
            case Expr::kNot:
                return 7;
            default:
                return 9;
        }
    }

    static bool RightAssoc(const Expr& e) {
        return e.kind == Expr::kBinary && (e.op == OP_CONCAT || e.op == OP_POW);
    }

    void PrintExpr(const ExprP& e, int parent_prec = 0, bool right_side = false) {
        const int prec = Prec(*e);
        bool parens = false;
        if (prec < 9 && parent_prec != 0) {
            if (prec < parent_prec) parens = true;
            else if (prec == parent_prec) {
                // same precedence: parenthesise the side that associativity does not cover
                parens = right_side != RightAssoc(*e);
                if (e->kind == Expr::kUnary || e->kind == Expr::kNot) parens = false;
            }
        }
        if (parens) Tok(FirstLineOf(e), "(");
        PrintExprInner(e, parens ? 0 : parent_prec);
        if (parens) Tok((e->kind == Expr::kAnd || e->kind == Expr::kOr) ? e->end_line : 0, ")", false);
    }

    static int FirstLineOf(const ExprP& e) {
        switch (e->kind) {
            case Expr::kCompare:
                if (e->negate && e->op != OP_EQ) return e->line;
                return FirstLineOf(e->swapped ? e->b : e->a);
            case Expr::kIndex: case Expr::kCall: case Expr::kMethodCall: case Expr::kBinary:
            case Expr::kAnd: case Expr::kOr: case Expr::kParen:
            case Expr::kUnary: case Expr::kNot:
                return e->a ? FirstLineOf(e->a) : e->line;
            case Expr::kFunction:
                return e->func ? e->func->linedefined : e->line;
            default:
                return e->line;
        }
    }

    void PrintExprInner(const ExprP& e, int parent_prec) {
        switch (e->kind) {
            case Expr::kMulti: PrintExpr(e->a, parent_prec); break;  // collapsed by its assignment
            case Expr::kNil: Tok(e->line, "nil"); break;
            case Expr::kTrue: Tok(e->line, "true"); break;
            case Expr::kFalse: Tok(e->line, "false"); break;
            case Expr::kNumber: {
                std::string text = FormatNumber(e->number);
                Tok(e->line, text);
                break;
            }
            case Expr::kString: Tok(e->line, QuoteString(e->name)); break;
            case Expr::kVararg:
                if (e->paren_call) {
                    Tok(e->line, "(");
                    Tok(e->line, "...", false);
                    Tok(0, ")", false);
                } else {
                    Tok(e->line, "...");
                }
                break;
            case Expr::kLocal:
            case Expr::kUpvalue:
            case Expr::kGlobal:
                Tok(e->line, e->name);
                break;
            case Expr::kIndex: {
                const bool late = late_index_;
                PrintPrefix(e->a);
                late_index_ = false;
                const int key_line = late ? 0 : (e->b->line != 0 ? e->b->line : e->line);
                if (e->b->kind == Expr::kString && IsIdentifier(e->b->name)) {
                    // The dot stays on the object's line: a constructor item
                    // is looked ahead one token, so a dot on the next line
                    // would advance the object's own line.
                    Tok(0, ".", false);
                    Tok(key_line, e->b->name, false);
                } else {
                    Tok(key_line, "[", false);
                    PrintExpr(e->b);
                    Tok(0, "]", false);
                }
                break;
            }
            case Expr::kCall:
            case Expr::kMethodCall: {
                if (e->paren_call) Tok(FirstLineOf(e), "(");
                late_index_ = false;
                PrintPrefix(e->a);
                if (e->kind == Expr::kMethodCall) {
                    const int name_line = e->name_line != 0 ? e->name_line : e->line;
                    Tok(name_line, ":", false);
                    Tok(name_line, e->name, false);
                }
                const bool brace_call = e->args.size() == 1 && e->args[0]->kind == Expr::kTable &&
                                        e->args[0]->line > 0 && e->line > e->args[0]->line;
                if (brace_call) {
                    // `name\n{...}`: the CALL takes the line of the `{`, the
                    // NEWTABLE that of the token before it.
                    brace_line_override_ = e->line;
                    PrintExpr(e->args[0]);
                    brace_line_override_ = 0;
                    if (e->paren_call) Tok(0, ")", false);
                    break;
                }
                Tok(e->line, "(", false);
                for (std::size_t i = 0; i < e->args.size(); ++i) {
                    if (i != 0) Tok(0, ",", false);
                    late_index_ = i + 1 == e->args.size();
                    PrintExpr(e->args[i]);
                    late_index_ = false;
                }
                Tok(e->end_line, ")", false);
                if (e->paren_call) Tok(0, ")", false);
                break;
            }
            case Expr::kFunction:
                PrintFunction(*e->func, e->func->linedefined, "function", true);
                break;
            case Expr::kTable: {
                // NEWTABLE carries the line of the token before '{'; a nested
                // constructor as the first item pins where '{' itself sits.
                int brace_line = e->line;
                std::vector<TableField> fields = e->fields;
                std::stable_sort(fields.begin(), fields.end(),
                                 [](const TableField& x, const TableField& y) { return x.pc < y.pc; });
                if (!fields.empty() && !fields[0].key && fields[0].value->kind == Expr::kTable &&
                    fields[0].value->line > brace_line) {
                    brace_line = fields[0].value->line;
                }
                if (brace_line_override_ > brace_line) brace_line = brace_line_override_;
                brace_line_override_ = 0;
                MoveTo(e->line);
                Tok(brace_line, "{");
                for (std::size_t i = 0; i < fields.size(); ++i) {
                    const TableField& field = fields[i];
                    const bool last = i + 1 == fields.size();
                    // A nested constructor's NEWTABLE carries the line of the
                    // token before its '{': the separator. A positional item is
                    // discharged when the separator after it is consumed, so
                    // its instruction line is that separator's line.
                    int sep_line = (!field.key && field.value->kind == Expr::kTable) ? field.value->line : 0;
                    if (i != 0 && !fields[i - 1].key && sep_line == 0) {
                        const ExprP& prev = fields[i - 1].value;
                        const Expr::Kind pk = prev->kind;
                        if (pk == Expr::kIndex || pk == Expr::kGlobal || pk == Expr::kLocal || pk == Expr::kUpvalue ||
                            pk == Expr::kString || pk == Expr::kNumber || pk == Expr::kNil || pk == Expr::kTrue ||
                            pk == Expr::kFalse) {
                            sep_line = prev->line;
                        }
                    }
                    if (i != 0) Tok(sep_line, ",", false);
                    if (field.key) {
                        if (field.key->kind == Expr::kString && IsIdentifier(field.key->name)) {
                            Tok(FirstLineOf(field.value) ? std::min(FirstLineOf(field.value), field.line) : field.line,
                                field.key->name);
                        } else {
                            Tok(field.key->line, "[");
                            PrintExpr(field.key);
                            Tok(0, "]", false);
                        }
                        Tok(0, "=");
                        PrintExpr(field.value);
                    } else if (last && (field.value->kind == Expr::kCall ||
                                        field.value->kind == Expr::kMethodCall ||
                                        field.value->kind == Expr::kVararg) &&
                               field.value->nresults == 1) {
                        Tok(FirstLineOf(field.value), "(");
                        PrintExpr(field.value);
                        Tok(0, ")", false);
                    } else {
                        late_index_ = !field.key && !last;
                        PrintExpr(field.value);
                        late_index_ = false;
                    }
                }
                Tok(e->end_line, "}", e->fields.empty() ? false : true);
                break;
            }
            case Expr::kBinary: {
                const int prec = Prec(*e);
                const bool late = late_index_;
                late_index_ = false;
                PrintExpr(e->a, prec, false);
                Tok(0, BinaryOpText(e->op));
                late_index_ = late;
                PrintExpr(e->b, prec, true);
                break;
            }
            case Expr::kUnary: {
                const int prec = Prec(*e);
                Tok(FirstLineOf(e), e->op == OP_UNM ? "-" : "#");
                // `- -x` needs a space; `-x` does not.
                PrintExpr(e->a, prec, true);
                break;
            }
            case Expr::kNot: {
                Tok(FirstLineOf(e), "not");
                PrintExpr(e->a, Prec(*e), true);
                break;
            }
            case Expr::kAnd:
            case Expr::kOr: {
                const int prec = Prec(*e);
                const bool late = late_index_;
                late_index_ = false;
                PrintExpr(e->a, prec, false);
                Tok(e->op_line, e->kind == Expr::kAnd ? "and" : "or");
                late_index_ = late;
                PrintExpr(e->b, prec, true);
                break;
            }
            case Expr::kCompare: {
                const int prec = Prec(*e);
                const bool late_cmp = late_index_;
                late_index_ = false;
                struct RestoreLate {
                    bool& flag; bool value; ~RestoreLate() { flag = value; }
                } restore_late{late_index_, false};
                (void)late_cmp;
                if (e->negate && e->op != OP_EQ) {
                    Tok(FirstLineOf(e), "not");
                    Tok(0, "(");
                    if (e->swapped) {
                        PrintExpr(e->b, prec, false);
                        Tok(0, e->op == OP_LT ? ">" : ">=");
                        PrintExpr(e->a, prec, true);
                    } else {
                        PrintExpr(e->a, prec, false);
                        Tok(0, e->op == OP_LT ? "<" : "<=");
                        PrintExpr(e->b, prec, true);
                    }
                    Tok(0, ")", false);
                    break;
                }
                const bool swap = e->swapped;
                if (swap) {
                    PrintExpr(e->b, prec, false);
                    Tok(0, e->op == OP_LT ? ">" : ">=");
                    PrintExpr(e->a, prec, true);
                } else {
                    PrintExpr(e->a, prec, false);
                    Tok(0, e->op == OP_EQ ? (e->negate ? "~=" : "==") : (e->op == OP_LT ? "<" : "<="));
                    PrintExpr(e->b, prec, true);
                }
                break;
            }
            case Expr::kParen:
                Tok(FirstLineOf(e), "(");
                PrintExpr(e->a);
                Tok(0, ")", false);
                break;
        }
        (void)parent_prec;
    }

    // A prefix expression: parenthesised unless it is a name, index or call.
    void PrintPrefix(const ExprP& e) {
        switch (e->kind) {
            case Expr::kIndex:
                PrintExpr(e);
                break;
            case Expr::kLocal: case Expr::kUpvalue: case Expr::kGlobal:
            case Expr::kCall: case Expr::kMethodCall: case Expr::kParen:
                late_index_ = false;
                PrintExpr(e);
                break;
            default:
                Tok(FirstLineOf(e), "(");
                PrintExpr(e);
                Tok(0, ")", false);
        }
    }

    static const char* BinaryOpText(Op op) {
        switch (op) {
            case OP_ADD: return "+";
            case OP_SUB: return "-";
            case OP_MUL: return "*";
            case OP_DIV: return "/";
            case OP_MOD: return "%";
            case OP_POW: return "^";
            case OP_CONCAT: return "..";
            default: return "?";
        }
    }

    void PrintFunction(const FunctionAst& func, int line, const std::string& keyword,
                       bool anonymous, const ExprP& name = nullptr, bool method = false,
                       const std::string& simple_name = "") {
        Tok(line, keyword);
        if (!simple_name.empty()) {
            Tok(0, simple_name);
        } else if (name) {
            if (method) {
                PrintMethodName(name);
            } else {
                PrintExpr(name);
            }
        }
        Tok(0, "(", false);
        std::size_t first_param = method ? 1 : 0;
        for (std::size_t i = first_param; i < func.params.size(); ++i) {
            if (i != first_param) Tok(0, ",", false);
            Tok(0, func.params[i], i != first_param);
        }
        if (func.vararg) {
            if (func.params.size() > first_param) Tok(0, ",", false);
            Tok(0, "...", func.params.size() > first_param);
        }
        Tok(0, ")", false);
        ++depth_;
        line_limits_.push_back(func.lastlinedefined);
        deadlines_.push_back(func.lastlinedefined > 0 ? func.lastlinedefined + 1 : 0);
        PrintBlock(func.body, func.lastlinedefined, func.lastlinedefined);
        deadlines_.pop_back();
        line_limits_.pop_back();
        --depth_;
        Break(func.lastlinedefined);
        Tok(func.lastlinedefined, "end");
        (void)anonymous;
    }

    void PrintMethodName(const ExprP& name) {
        PrintPrefix(name->a);
        Tok(name->b->line, ":", false);
        Tok(name->b->line, name->b->name, false);
    }

    // `next_line` is the line of whatever follows the block (0 = unknown), so
    // closing keywords can stay on the current line when the source did.
    // The earliest line a statement must reach: the header line of the first
    // function it contains, or 0.
    static int FirstAnchorExpr(const ExprP& e) {
        if (!e) return 0;
        if (e->kind == Expr::kFunction && e->func) return e->func->linedefined;
        int best = 0;
        const auto take = [&](int line) {
            if (line > 0 && (best == 0 || line < best)) best = line;
        };
        take(FirstAnchorExpr(e->a));
        take(FirstAnchorExpr(e->b));
        for (const ExprP& arg : e->args) take(FirstAnchorExpr(arg));
        for (const TableField& f : e->fields) {
            take(FirstAnchorExpr(f.key));
            take(FirstAnchorExpr(f.value));
        }
        return best;
    }
    static int FirstAnchor(const Stmt& s) {
        int best = 0;
        const auto take = [&](int line) {
            if (line > 0 && (best == 0 || line < best)) best = line;
        };
        if (s.func) take(s.func->linedefined);
        for (const ExprP& e : s.targets) take(FirstAnchorExpr(e));
        for (const ExprP& e : s.values) take(FirstAnchorExpr(e));
        take(FirstAnchorExpr(s.cond));
        for (const StmtP& child : s.body) take(FirstAnchor(*child));
        for (const StmtP& child : s.else_body) take(FirstAnchor(*child));
        return best;
    }

    void PrintBlock(const std::vector<StmtP>& body, int next_line, int end_line) {
        // Statements without a recorded line each open a new one, unless the
        // lines left before the next function header (or the enclosing
        // deadline) are needed by the statements that follow.
        const int n = static_cast<int>(body.size());
        const int outer = deadlines_.empty() ? 0 : deadlines_.back();
        std::vector<int> anchors(static_cast<std::size_t>(n), 0);
        for (int i = 0; i < n; ++i) anchors[static_cast<std::size_t>(i)] = FirstAnchor(*body[static_cast<std::size_t>(i)]);
        for (int i = 0; i < n; ++i) {
            const StmtP& stmt = body[static_cast<std::size_t>(i)];
            const int following = i + 1 < n ? body[static_cast<std::size_t>(i) + 1]->line : next_line;
            int j = i;
            while (j < n && anchors[static_cast<std::size_t>(j)] == 0) ++j;
            int limit = 0;  // latest line on which this statement may start
            int child_deadline = 0;
            if (j < n) {
                limit = anchors[static_cast<std::size_t>(j)] - (j - i);
                child_deadline = j > i ? anchors[static_cast<std::size_t>(j)] - (j - i - 1) : 0;
            }
            if (j == n || j == i) {
                if (outer > 0) {
                    const int from_outer = outer - (n - i);
                    if (limit == 0 || from_outer < limit) limit = from_outer;
                    child_deadline = outer - (n - 1 - i);
                }
            }
            if (options_.line_layout && stmt->line == 0 && limit > 0 && cur_line_ + 1 > limit) {
                // no room: stay on this line
            } else {
                Break(stmt->line);
            }
            deadlines_.push_back(child_deadline);
            const bool early = (stmt->kind == Stmt::kReturn || stmt->kind == Stmt::kBreak ||
                                stmt->kind == Stmt::kContinue) &&
                               i + 1 < n;
            if (early) {
                Tok(stmt->line, "do");
                PrintStmt(*stmt, following);
                Tok(0, "end");
            } else {
                PrintStmt(*stmt, following);
            }
            deadlines_.pop_back();
        }
        (void)end_line;
    }

    void PrintNameList(const std::vector<std::string>& names, int line) {
        for (std::size_t i = 0; i < names.size(); ++i) {
            if (i != 0) Tok(0, ",", false);
            Tok(line, names[i], true);
        }
    }

    void PrintExprList(const std::vector<ExprP>& values, int expected = -1) {
        for (std::size_t i = 0; i < values.size(); ++i) {
            if (i != 0) Tok(0, ",", false);
            const ExprP& value = values[i];
            const bool last = i + 1 == values.size();
            const bool multret_kind = value->kind == Expr::kCall ||
                                      value->kind == Expr::kMethodCall ||
                                      value->kind == Expr::kVararg;
            if (last && multret_kind && value->nresults == 1 &&
                ((expected >= 0 && expected > static_cast<int>(values.size())) || expected == -2)) {
                Tok(FirstLineOf(value), "(");
                PrintExpr(value);
                Tok(0, ")", false);
            } else {
                late_index_ = last;
                PrintExpr(value);
                late_index_ = false;
            }
        }
    }

    void MoveTo(int line) {
        if (options_.line_layout && line > cur_line_) {
            while (cur_line_ < line) {
                out_ += "\n";
                ++cur_line_;
            }
            at_line_start_ = true;
        }
    }

    void PrintStmt(const Stmt& s, int following_line) {
        MoveTo(s.line);
        switch (s.kind) {
            case Stmt::kLocal:
                Tok(s.line, "local");
                PrintNameList(s.names, 0);
                if (!s.values.empty()) {
                    Tok(0, "=");
                    PrintExprList(s.values, static_cast<int>(s.names.size()));
                }
                break;
            case Stmt::kAssign:
                for (std::size_t i = 0; i < s.targets.size(); ++i) {
                    if (i != 0) Tok(0, ",", false);
                    PrintExpr(s.targets[i]);
                }
                Tok(0, "=");
                PrintExprList(s.values, static_cast<int>(s.targets.size()));
                break;
            case Stmt::kCall:
                PrintExpr(s.values[0]);
                break;
            case Stmt::kReturn:
                Tok(s.line, "return");
                PrintExprList(s.values, -2);
                break;
            case Stmt::kBreak:
                Tok(s.line, "break");
                break;
            case Stmt::kContinue:
                Tok(s.line, "continue");
                break;
            case Stmt::kIf: {
                const Stmt* current = &s;
                Tok(s.line, "if");
                for (;;) {
                    PrintExpr(current->cond);
                    Tok(0, "then");
                    ++depth_;
                    const int after_then = current->has_else
                                               ? (current->else_body.empty() ? following_line
                                                                             : current->else_body[0]->line)
                                               : following_line;
                    PrintBlock(current->body, after_then, 0);
                    --depth_;
                    if (!current->has_else) break;
                    if (current->else_body.size() == 1 && current->else_body[0]->kind == Stmt::kIf) {
                        Break(current->else_body[0]->line);
                        Tok(current->else_body[0]->line, "elseif");
                        current = current->else_body[0].get();
                        continue;
                    }
                    if (options_.line_layout && current->escape_line > cur_line_) {
                        // the jump before `else` was coded after a token on
                        // its own line: an empty statement
                        Tok(current->escape_line, ";");
                    }
                    Break(current->else_body.empty() ? following_line : current->else_body[0]->line);
                    Tok(0, "else");
                    ++depth_;
                    PrintBlock(current->else_body, following_line, 0);
                    --depth_;
                    break;
                }
                CloseKeyword(s.close_line, following_line, "end");
                break;
            }
            case Stmt::kWhile:
                Tok(s.line, "while");
                PrintExpr(s.cond);
                Tok(0, "do");
                ++depth_;
                PrintBlock(s.body, following_line, 0);
                --depth_;
                CloseKeyword(s.close_line, following_line, "end");
                break;
            case Stmt::kRepeat:
                Tok(s.line, "repeat");
                ++depth_;
                PrintBlock(s.body, FirstLineOf(s.cond), 0);
                --depth_;
                Break(FirstLineOf(s.cond));
                Tok(0, "until");
                PrintExpr(s.cond);
                break;
            case Stmt::kNumFor:
                Tok(s.keyword_line ? s.keyword_line : s.line, "for");
                PrintNameList(s.names, 0);
                Tok(0, "=");
                PrintExpr(s.values[0]);
                Tok(0, ",", false);
                PrintExpr(s.values[1]);
                if (!(s.values[2]->kind == Expr::kNumber && s.values[2]->number == 1.0)) {
                    Tok(0, ",", false);
                    PrintExpr(s.values[2]);
                }
                Tok(0, "do");
                ++depth_;
                PrintBlock(s.body, following_line, 0);
                --depth_;
                CloseKeyword(s.close_line, following_line, "end");
                break;
            case Stmt::kGenFor:
                Tok(s.keyword_line ? s.keyword_line : s.line, "for");
                PrintNameList(s.names, 0);
                Tok(0, "in");
                PrintExprList(s.values);
                Tok(0, "do");
                ++depth_;
                PrintBlock(s.body, following_line, 0);
                --depth_;
                CloseKeyword(s.close_line, following_line, "end");
                break;
            case Stmt::kDo:
                Tok(s.line, "do");
                ++depth_;
                PrintBlock(s.body, following_line, 0);
                --depth_;
                CloseKeyword(s.close_line, following_line, "end");
                break;
            case Stmt::kLocalFunction:
                Tok(s.line, "local");
                PrintFunction(*s.func, s.line, "function", false, nullptr, false, s.names[0]);
                break;
            case Stmt::kFunctionDecl:
                PrintFunction(*s.func, s.line, "function", false, s.func_name, s.method);
                break;
        }
    }

    const DecompileOptions& options_;
    std::string out_;
    bool late_index_ = false;
    int cur_line_ = 1;
    int depth_ = 0;
    bool at_line_start_ = true;
    std::vector<int> line_limits_;  // lastlinedefined of the open functions
    std::vector<int> deadlines_;    // line (exclusive) by which the open block must be done, 0 = none
    int brace_line_override_ = 0;   // a parenless call puts its argument's '{' on the CALL line
};

}  // namespace

namespace {

// Every identifier-like string constant in the chunk: a synthetic local must
// not shadow a global or field name the code refers to.
void CollectReservedNames(const Proto& proto, std::set<std::string>& out) {
    for (const Constant& k : proto.constants) {
        if (k.type == Constant::kString && IsIdentifier(k.string)) out.insert(k.string);
    }
    for (const Proto& child : proto.protos) CollectReservedNames(child, out);
}

void DisassembleProto(const Proto& proto, const std::string& path, std::string* out) {
    char buffer[256];
    std::snprintf(buffer, sizeof(buffer), "=== %s (%zu instructions, %d params, %d upvalues, lines %d-%d)\n",
                  path.c_str(), proto.code.size(), proto.numparams, proto.nups, proto.linedefined,
                  proto.lastlinedefined);
    *out += buffer;
    for (std::size_t i = 0; i < proto.locvars.size(); ++i) {
        std::snprintf(buffer, sizeof(buffer), "  local %s [%d,%d)\n", proto.locvars[i].name.c_str(),
                      proto.locvars[i].startpc, proto.locvars[i].endpc);
        *out += buffer;
    }
    for (std::size_t i = 0; i < proto.constants.size(); ++i) {
        const Constant& k = proto.constants[i];
        std::string text;
        switch (k.type) {
            case Constant::kNil: text = "nil"; break;
            case Constant::kBoolean: text = k.boolean ? "true" : "false"; break;
            case Constant::kNumber: text = FormatNumber(k.number); break;
            case Constant::kString: text = QuoteString(k.string); break;
        }
        std::snprintf(buffer, sizeof(buffer), "  k%zu = %s\n", i, text.c_str());
        *out += buffer;
    }
    for (std::size_t pc = 0; pc < proto.code.size(); ++pc) {
        const Instr in = Decode(proto.code[pc], pc < proto.lineinfo.size() ? proto.lineinfo[pc] : 0);
        std::string operands;
        const auto rk = [&](int v) {
            if (v >= kRkBit) {
                const Constant& k = proto.constants[static_cast<std::size_t>(v - kRkBit)];
                return "k" + std::to_string(v - kRkBit) +
                       (k.type == Constant::kString ? "(" + QuoteString(k.string) + ")" :
                        k.type == Constant::kNumber ? "(" + FormatNumber(k.number) + ")" : "");
            }
            return "r" + std::to_string(v);
        };
        switch (in.op) {
            case OP_JMP:
                operands = "-> " + std::to_string(pc + 1 + in.sbx);
                break;
            case OP_FORLOOP: case OP_FORPREP:
                operands = "r" + std::to_string(in.a) + " -> " + std::to_string(pc + 1 + in.sbx);
                break;
            case OP_LOADK: case OP_GETGLOBAL: case OP_SETGLOBAL:
                operands = "r" + std::to_string(in.a) + " " + rk(in.bx + kRkBit);
                break;
            case OP_CLOSURE:
                operands = "r" + std::to_string(in.a) + " proto " + std::to_string(in.bx);
                break;
            case OP_EQ: case OP_LT: case OP_LE:
                operands = std::to_string(in.a) + " " + rk(in.b) + " " + rk(in.c);
                break;
            case OP_GETTABLE: case OP_SELF:
                operands = "r" + std::to_string(in.a) + " r" + std::to_string(in.b) + " " + rk(in.c);
                break;
            case OP_SETTABLE:
                operands = "r" + std::to_string(in.a) + " " + rk(in.b) + " " + rk(in.c);
                break;
            case OP_ADD: case OP_SUB: case OP_MUL: case OP_DIV: case OP_MOD: case OP_POW:
                operands = "r" + std::to_string(in.a) + " " + rk(in.b) + " " + rk(in.c);
                break;
            default:
                operands = std::to_string(in.a) + " " + std::to_string(in.b) + " " + std::to_string(in.c);
        }
        std::snprintf(buffer, sizeof(buffer), "  %4zu [%4d] %-10s %s\n", pc, in.line, kOpNames[in.op],
                      operands.c_str());
        *out += buffer;
    }
    for (std::size_t i = 0; i < proto.protos.size(); ++i) {
        DisassembleProto(proto.protos[i], path + "." + std::to_string(i), out);
    }
}

}  // namespace

void Disassemble(const Chunk& chunk, std::string* out) {
    DisassembleProto(chunk.main, "main", out);
}

bool Decompile(const Chunk& chunk, const DecompileOptions& options, std::string* out,
               std::string* error) {
    try {
        std::set<std::string> reserved;
        CollectReservedNames(chunk.main, reserved);
        std::shared_ptr<FunctionAst> ast = FunctionDecompiler::RunInferring(chunk.main, {}, "main", reserved);
        Printer printer(options);
        printer.PrintChunk(*ast);
        *out = printer.Finish();
        return true;
    } catch (const DecompileError& e) {
        if (error != nullptr) *error = e.what();
        return false;
    } catch (const std::exception& e) {
        if (error != nullptr) *error = std::string("internal: ") + e.what();
        return false;
    }
}

}  // namespace refii::luadec
