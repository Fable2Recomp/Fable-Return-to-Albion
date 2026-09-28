// The decompiler oracle: compiles Lua text with the guest's compiler
// (Lua 5.1.2 plus `continue`, 4-byte float lua_Number, LUA_COMPAT_VARARG per
// chunk) and dumps the guest chunk format, so a decompiled file can be checked
// byte-for-byte against the chunk it came from.
//
// The vendored 5.1.2 sources are compiled here as C++ inside a namespace: the
// runtime links its own Lua, and two C Lua builds cannot share one binary.
#include "luadec_oracle.h"

#include <assert.h>
#include <ctype.h>
#include <errno.h>
#include <limits.h>
#include <locale.h>
#include <math.h>
#include <setjmp.h>
#include <stdarg.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "luac32_luaconf.h"

#define LUA_CORE
namespace refii::luac32 {
#include "lapi.c"
#include "lauxlib.c"
#include "lcode.c"
#include "ldebug.c"
#include "ldo.c"
#include "ldump.c"
#include "lfunc.c"
#include "lgc.c"
#include "llex.c"
#include "lmem.c"
#include "lobject.c"
#include "lopcodes.c"
#include "lparser.c"
#include "lstate.c"
#include "lstring.c"
#include "ltable.c"
#include "ltm.c"
#include "lundump.c"
#include "lvm.c"
#include "lzio.c"
}  // namespace refii::luac32

namespace refii::luadec {
namespace {

using namespace refii::luac32;


// ---------------------------------------------------------------------------
// Guest chunk format constants.

constexpr unsigned char kGuestHeader[12] = {
    0x1b, 'L', 'u', 'a', 0x51, 0x00, 0x01, 0x04, 0x04, 0x04, 0x04, 0x00};

// ---------------------------------------------------------------------------
// IO helpers.



// ---------------------------------------------------------------------------
// Compile.

struct CompileResult {
    bool ok = false;
    std::string error;
};

// Mirrors luaL_loadfile's text handling: a leading '#' line is skipped but a
// newline is kept in its place so line numbers stay aligned with the source.
CompileResult CompileSource(lua_State* L,
                            const std::vector<unsigned char>& text,
                            const std::string& chunkname) {
    CompileResult result;
    std::string buffer;
    size_t begin = 0;
    if (!text.empty() && text[0] == '#') {
        while (begin < text.size() && text[begin] != '\n') {
            ++begin;
        }
        buffer.push_back('\n');
    }
    buffer.append(reinterpret_cast<const char*>(text.data()) + begin, text.size() - begin);

    const int status = luaL_loadbuffer(L, buffer.data(), buffer.size(), chunkname.c_str());
    if (status != 0) {
        const char* message = lua_tostring(L, -1);
        result.error = message != nullptr ? message : "unknown compile error";
        lua_pop(L, 1);
        return result;
    }
    result.ok = true;
    return result;
}

// ---------------------------------------------------------------------------
// Guest-format dumper over the parsed Proto (size_t is 4 bytes, numbers are
// 4-byte floats).

class GuestDumper {
public:
    explicit GuestDumper(bool strip) : strip_(strip) {}

    std::vector<unsigned char> Dump(const Proto* main) {
        bytes_.assign(std::begin(kGuestHeader), std::end(kGuestHeader));
        DumpFunction(main, nullptr);
        return std::move(bytes_);
    }

private:
    void PutU32(uint32_t value) {
        bytes_.push_back(static_cast<unsigned char>(value & 0xff));
        bytes_.push_back(static_cast<unsigned char>((value >> 8) & 0xff));
        bytes_.push_back(static_cast<unsigned char>((value >> 16) & 0xff));
        bytes_.push_back(static_cast<unsigned char>((value >> 24) & 0xff));
    }
    void PutInt(int value) { PutU32(static_cast<uint32_t>(value)); }
    void PutChar(int value) { bytes_.push_back(static_cast<unsigned char>(value)); }
    void PutNumber(lua_Number value) {
        static_assert(sizeof(lua_Number) == 4, "refii-luac32 must be built with float lua_Number");
        uint32_t bits = 0;
        std::memcpy(&bits, &value, sizeof(bits));
        PutU32(bits);
    }
    void PutString(const TString* s) {
        if (s == nullptr) {
            PutU32(0);
            return;
        }
        const size_t size = s->tsv.len + 1;
        PutU32(static_cast<uint32_t>(size));
        const unsigned char* data = reinterpret_cast<const unsigned char*>(getstr(s));
        bytes_.insert(bytes_.end(), data, data + size);
    }

    void DumpFunction(const Proto* f, const TString* parent_source) {
        PutString((f->source == parent_source || strip_) ? nullptr : f->source);
        PutInt(f->linedefined);
        PutInt(f->lastlinedefined);
        PutChar(f->nups);
        PutChar(f->numparams);
        PutChar(f->is_vararg);
        PutChar(f->maxstacksize);

        PutInt(f->sizecode);
        for (int i = 0; i < f->sizecode; ++i) {
            PutU32(static_cast<uint32_t>(f->code[i]));
        }

        PutInt(f->sizek);
        for (int i = 0; i < f->sizek; ++i) {
            const TValue* o = &f->k[i];
            PutChar(ttype(o));
            switch (ttype(o)) {
            case LUA_TNIL:
                break;
            case LUA_TBOOLEAN:
                PutChar(bvalue(o));
                break;
            case LUA_TNUMBER:
                PutNumber(nvalue(o));
                break;
            case LUA_TSTRING:
                PutString(rawtsvalue(o));
                break;
            default:
                break;
            }
        }

        PutInt(f->sizep);
        for (int i = 0; i < f->sizep; ++i) {
            DumpFunction(f->p[i], f->source);
        }

        const int lineinfo_count = strip_ ? 0 : f->sizelineinfo;
        PutInt(lineinfo_count);
        for (int i = 0; i < lineinfo_count; ++i) {
            PutInt(f->lineinfo[i]);
        }

        const int locvar_count = strip_ ? 0 : f->sizelocvars;
        PutInt(locvar_count);
        for (int i = 0; i < locvar_count; ++i) {
            PutString(f->locvars[i].varname);
            PutInt(f->locvars[i].startpc);
            PutInt(f->locvars[i].endpc);
        }

        const int upvalue_count = strip_ ? 0 : f->sizeupvalues;
        PutInt(upvalue_count);
        for (int i = 0; i < upvalue_count; ++i) {
            PutString(f->upvalues[i]);
        }
    }

    bool strip_;
    std::vector<unsigned char> bytes_;
};

// ---------------------------------------------------------------------------
// Self-contained chunk loader (accepts size_t 4/8 and number 4/8 so a host
// luac dump can be diffed too, but the guest format is the one that matters).

struct LoadedConstant {
    int type = LUA_TNIL;
    bool boolean = false;
    double number = 0.0;
    uint64_t number_bits = 0;
    int number_size = 0;
    std::string string;
};

struct LoadedLocVar {
    std::string name;
    int startpc = 0;
    int endpc = 0;
};

struct LoadedFunction {
    std::optional<std::string> own_source;  // absent when inherited from the parent
    std::string source;                     // effective source
    int linedefined = 0;
    int lastlinedefined = 0;
    int nups = 0;
    int numparams = 0;
    int is_vararg = 0;
    int maxstack = 0;
    std::vector<uint32_t> code;
    std::vector<LoadedConstant> constants;
    std::vector<LoadedFunction> protos;
    std::vector<int> lineinfo;
    std::vector<LoadedLocVar> locvars;
    std::vector<std::string> upvalues;
};

struct LoadedChunk {
    unsigned char header[12] = {};
    LoadedFunction main;
};

class ChunkLoader {
public:
    explicit ChunkLoader(const std::vector<unsigned char>& bytes) : bytes_(bytes) {}

    bool Load(LoadedChunk& chunk, std::string& error) {
        error_ = &error;
        if (bytes_.size() < 12 || std::memcmp(bytes_.data(), "\x1bLua", 4) != 0) {
            return Fail("not a Lua chunk");
        }
        std::memcpy(chunk.header, bytes_.data(), 12);
        if (chunk.header[4] != 0x51) {
            return Fail("unsupported chunk version");
        }
        if (chunk.header[6] != 1) {
            return Fail("big-endian chunk unsupported");
        }
        if (chunk.header[7] != 4 || chunk.header[9] != 4) {
            return Fail("unsupported int/Instruction size");
        }
        size_t_size_ = chunk.header[8];
        number_size_ = chunk.header[10];
        number_integral_ = chunk.header[11] != 0;
        if (size_t_size_ != 4 && size_t_size_ != 8) {
            return Fail("unsupported size_t size");
        }
        if (number_size_ != 4 && number_size_ != 8) {
            return Fail("unsupported number size");
        }
        pos_ = 12;
        if (!LoadFunction(chunk.main, nullptr)) {
            return false;
        }
        if (pos_ != bytes_.size()) {
            return Fail("trailing bytes after main function");
        }
        return true;
    }

private:
    bool Fail(const std::string& message) {
        if (error_ != nullptr && error_->empty()) {
            *error_ = message;
        }
        return false;
    }

    bool Need(size_t count) {
        if (pos_ + count > bytes_.size()) {
            return Fail("truncated chunk");
        }
        return true;
    }

    bool GetU32(uint32_t& value) {
        if (!Need(4)) {
            return false;
        }
        value = static_cast<uint32_t>(bytes_[pos_]) |
                (static_cast<uint32_t>(bytes_[pos_ + 1]) << 8) |
                (static_cast<uint32_t>(bytes_[pos_ + 2]) << 16) |
                (static_cast<uint32_t>(bytes_[pos_ + 3]) << 24);
        pos_ += 4;
        return true;
    }

    bool GetInt(int& value) {
        uint32_t raw = 0;
        if (!GetU32(raw)) {
            return false;
        }
        value = static_cast<int>(raw);
        return true;
    }

    bool GetChar(int& value) {
        if (!Need(1)) {
            return false;
        }
        value = bytes_[pos_++];
        return true;
    }

    bool GetSizeT(uint64_t& value) {
        if (!Need(size_t_size_)) {
            return false;
        }
        value = 0;
        for (int i = 0; i < size_t_size_; ++i) {
            value |= static_cast<uint64_t>(bytes_[pos_ + i]) << (8 * i);
        }
        pos_ += size_t_size_;
        return true;
    }

    bool GetString(std::optional<std::string>& value) {
        uint64_t size = 0;
        if (!GetSizeT(size)) {
            return false;
        }
        if (size == 0) {
            value.reset();
            return true;
        }
        if (!Need(static_cast<size_t>(size))) {
            return false;
        }
        // The stored size includes the trailing NUL.
        value.emplace(reinterpret_cast<const char*>(bytes_.data()) + pos_,
                      static_cast<size_t>(size - 1));
        pos_ += static_cast<size_t>(size);
        return true;
    }

    bool GetNumber(LoadedConstant& constant) {
        if (!Need(number_size_)) {
            return false;
        }
        uint64_t bits = 0;
        for (int i = 0; i < number_size_; ++i) {
            bits |= static_cast<uint64_t>(bytes_[pos_ + i]) << (8 * i);
        }
        pos_ += number_size_;
        constant.number_bits = bits;
        constant.number_size = number_size_;
        if (number_integral_) {
            constant.number = static_cast<double>(static_cast<int64_t>(bits));
        } else if (number_size_ == 4) {
            float f = 0.0f;
            const uint32_t bits32 = static_cast<uint32_t>(bits);
            std::memcpy(&f, &bits32, sizeof(f));
            constant.number = f;
        } else {
            double d = 0.0;
            std::memcpy(&d, &bits, sizeof(d));
            constant.number = d;
        }
        return true;
    }

    bool LoadFunction(LoadedFunction& f, const LoadedFunction* parent) {
        if (!GetString(f.own_source)) {
            return false;
        }
        f.source = f.own_source ? *f.own_source : (parent != nullptr ? parent->source : std::string());
        if (!GetInt(f.linedefined) || !GetInt(f.lastlinedefined) || !GetChar(f.nups) ||
            !GetChar(f.numparams) || !GetChar(f.is_vararg) || !GetChar(f.maxstack)) {
            return false;
        }

        int count = 0;
        if (!GetInt(count) || count < 0) {
            return Fail("bad code size");
        }
        f.code.resize(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i) {
            if (!GetU32(f.code[static_cast<size_t>(i)])) {
                return false;
            }
        }

        if (!GetInt(count) || count < 0) {
            return Fail("bad constant count");
        }
        f.constants.resize(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i) {
            LoadedConstant& constant = f.constants[static_cast<size_t>(i)];
            if (!GetChar(constant.type)) {
                return false;
            }
            switch (constant.type) {
            case LUA_TNIL:
                break;
            case LUA_TBOOLEAN: {
                int value = 0;
                if (!GetChar(value)) {
                    return false;
                }
                constant.boolean = value != 0;
                break;
            }
            case LUA_TNUMBER:
                if (!GetNumber(constant)) {
                    return false;
                }
                break;
            case LUA_TSTRING: {
                std::optional<std::string> value;
                if (!GetString(value)) {
                    return false;
                }
                constant.string = value.value_or(std::string());
                break;
            }
            default:
                return Fail("bad constant type");
            }
        }

        if (!GetInt(count) || count < 0) {
            return Fail("bad proto count");
        }
        f.protos.resize(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i) {
            if (!LoadFunction(f.protos[static_cast<size_t>(i)], &f)) {
                return false;
            }
        }

        if (!GetInt(count) || count < 0) {
            return Fail("bad lineinfo count");
        }
        f.lineinfo.resize(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i) {
            if (!GetInt(f.lineinfo[static_cast<size_t>(i)])) {
                return false;
            }
        }

        if (!GetInt(count) || count < 0) {
            return Fail("bad locvar count");
        }
        f.locvars.resize(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i) {
            LoadedLocVar& var = f.locvars[static_cast<size_t>(i)];
            std::optional<std::string> name;
            if (!GetString(name) || !GetInt(var.startpc) || !GetInt(var.endpc)) {
                return false;
            }
            var.name = name.value_or(std::string());
        }

        if (!GetInt(count) || count < 0) {
            return Fail("bad upvalue count");
        }
        f.upvalues.resize(static_cast<size_t>(count));
        for (int i = 0; i < count; ++i) {
            std::optional<std::string> name;
            if (!GetString(name)) {
                return false;
            }
            f.upvalues[static_cast<size_t>(i)] = name.value_or(std::string());
        }
        return true;
    }

    const std::vector<unsigned char>& bytes_;
    size_t pos_ = 0;
    int size_t_size_ = 4;
    int number_size_ = 4;
    bool number_integral_ = false;
    std::string* error_ = nullptr;
};

// ---------------------------------------------------------------------------
// Disassembly and formatting.

constexpr const char* kOpNames[] = {
    "MOVE",     "LOADK",    "LOADBOOL", "LOADNIL",  "GETUPVAL", "GETGLOBAL", "GETTABLE",
    "SETGLOBAL", "SETUPVAL", "SETTABLE", "NEWTABLE", "SELF",     "ADD",       "SUB",
    "MUL",      "DIV",      "MOD",      "POW",      "UNM",      "NOT",       "LEN",
    "CONCAT",   "JMP",      "EQ",       "LT",       "LE",       "TEST",      "TESTSET",
    "CALL",     "TAILCALL", "RETURN",   "FORLOOP",  "FORPREP",  "TFORLOOP",  "SETLIST",
    "CLOSE",    "CLOSURE",  "VARARG",
};
constexpr int kOpCount = static_cast<int>(sizeof(kOpNames) / sizeof(kOpNames[0]));

enum class OpFormat { kABC, kABx, kAsBx };

OpFormat FormatOf(int op) {
    switch (op) {
    case OP_LOADK:
    case OP_GETGLOBAL:
    case OP_SETGLOBAL:
    case OP_CLOSURE:
        return OpFormat::kABx;
    case OP_JMP:
    case OP_FORLOOP:
    case OP_FORPREP:
        return OpFormat::kAsBx;
    default:
        return OpFormat::kABC;
    }
}

std::string Disassemble(uint32_t instruction) {
    const int op = static_cast<int>(GET_OPCODE(instruction));
    const int a = GETARG_A(instruction);
    char buffer[96];
    const char* name = (op >= 0 && op < kOpCount) ? kOpNames[op] : "OP?";
    switch (FormatOf(op)) {
    case OpFormat::kABx:
        std::snprintf(buffer, sizeof(buffer), "%s A=%d Bx=%d", name, a, GETARG_Bx(instruction));
        break;
    case OpFormat::kAsBx:
        std::snprintf(buffer, sizeof(buffer), "%s A=%d sBx=%d", name, a, GETARG_sBx(instruction));
        break;
    default:
        std::snprintf(buffer, sizeof(buffer), "%s A=%d B=%d C=%d", name, a,
                      GETARG_B(instruction), GETARG_C(instruction));
        break;
    }
    if (op < 0 || op >= kOpCount) {
        char raw[24];
        std::snprintf(raw, sizeof(raw), "(0x%08x)", instruction);
        return std::string(buffer) + raw;
    }
    return buffer;
}

std::string QuoteString(const std::string& value, size_t limit = 60) {
    std::string out = "\"";
    size_t shown = 0;
    for (unsigned char ch : value) {
        if (shown >= limit) {
            out += "...";
            break;
        }
        if (ch == '\\' || ch == '"') {
            out.push_back('\\');
            out.push_back(static_cast<char>(ch));
        } else if (ch == '\n') {
            out += "\\n";
        } else if (ch == '\r') {
            out += "\\r";
        } else if (ch == '\t') {
            out += "\\t";
        } else if (ch < 0x20 || ch >= 0x7f) {
            char hex[8];
            std::snprintf(hex, sizeof(hex), "\\x%02x", ch);
            out += hex;
        } else {
            out.push_back(static_cast<char>(ch));
        }
        ++shown;
    }
    out += "\"";
    return out;
}

std::string DescribeConstant(const LoadedConstant& constant) {
    switch (constant.type) {
    case LUA_TNIL:
        return "nil";
    case LUA_TBOOLEAN:
        return constant.boolean ? "true" : "false";
    case LUA_TNUMBER: {
        char buffer[64];
        if (constant.number_size == 4) {
            std::snprintf(buffer, sizeof(buffer), "%.9g(0x%08x)", constant.number,
                          static_cast<unsigned>(constant.number_bits));
        } else {
            std::snprintf(buffer, sizeof(buffer), "%.17g(0x%016llx)", constant.number,
                          static_cast<unsigned long long>(constant.number_bits));
        }
        return buffer;
    }
    case LUA_TSTRING:
        return QuoteString(constant.string);
    default:
        return "?";
    }
}

bool SameConstant(const LoadedConstant& a, const LoadedConstant& b) {
    if (a.type != b.type) {
        return false;
    }
    switch (a.type) {
    case LUA_TNIL:
        return true;
    case LUA_TBOOLEAN:
        return a.boolean == b.boolean;
    case LUA_TNUMBER:
        return a.number_size == b.number_size && a.number_bits == b.number_bits;
    case LUA_TSTRING:
        return a.string == b.string;
    default:
        return false;
    }
}

// ---------------------------------------------------------------------------
// Structural diff. Pass 1 covers everything that survives `-s`; pass 2 the
// debug sections. The first difference found is the reported location.

std::string Fmt(const char* format, auto... args) {
    char buffer[512];
    std::snprintf(buffer, sizeof(buffer), format, args...);
    return buffer;
}

bool UsesCompatVararg(const LoadedFunction& f) {
    if ((f.is_vararg & 1) != 0) return true;
    for (const LoadedFunction& child : f.protos) {
        if (UsesCompatVararg(child)) return true;
    }
    return false;
}

std::optional<std::string> DiffCore(const LoadedFunction& orig,
                                    const LoadedFunction& cand,
                                    const std::string& path) {
    if (orig.numparams != cand.numparams || orig.is_vararg != cand.is_vararg) {
        return Fmt("%s params orig=%d/vararg=%d cand=%d/vararg=%d", path.c_str(), orig.numparams,
                   orig.is_vararg, cand.numparams, cand.is_vararg);
    }
    if (orig.nups != cand.nups) {
        return Fmt("%s nups orig=%d cand=%d", path.c_str(), orig.nups, cand.nups);
    }
    const size_t code_common = std::min(orig.code.size(), cand.code.size());
    for (size_t pc = 0; pc < code_common; ++pc) {
        if (orig.code[pc] != cand.code[pc]) {
            return Fmt("%s code pc=%zu orig=%s cand=%s", path.c_str(), pc,
                       Disassemble(orig.code[pc]).c_str(), Disassemble(cand.code[pc]).c_str());
        }
    }
    if (orig.code.size() != cand.code.size()) {
        const std::string orig_text =
            orig.code.size() > code_common ? Disassemble(orig.code[code_common]) : "<end>";
        const std::string cand_text =
            cand.code.size() > code_common ? Disassemble(cand.code[code_common]) : "<end>";
        return Fmt("%s code pc=%zu orig=%s cand=%s", path.c_str(), code_common,
                   orig_text.c_str(), cand_text.c_str());
    }

    const size_t k_common = std::min(orig.constants.size(), cand.constants.size());
    for (size_t i = 0; i < k_common; ++i) {
        if (!SameConstant(orig.constants[i], cand.constants[i])) {
            return Fmt("%s constants idx=%zu orig=%s cand=%s", path.c_str(), i,
                       DescribeConstant(orig.constants[i]).c_str(),
                       DescribeConstant(cand.constants[i]).c_str());
        }
    }
    if (orig.constants.size() != cand.constants.size()) {
        return Fmt("%s nconstants orig=%zu cand=%zu", path.c_str(), orig.constants.size(),
                   cand.constants.size());
    }
    // Checked after code and constants: a stack-size difference alone points
    // at declarations, while code differences are more specific.
    if (orig.maxstack != cand.maxstack) {
        return Fmt("%s maxstack orig=%d cand=%d", path.c_str(), orig.maxstack, cand.maxstack);
    }

    if (orig.protos.size() != cand.protos.size()) {
        return Fmt("%s nprotos orig=%zu cand=%zu", path.c_str(), orig.protos.size(),
                   cand.protos.size());
    }
    for (size_t i = 0; i < orig.protos.size(); ++i) {
        if (auto diff = DiffCore(orig.protos[i], cand.protos[i], path + "." + std::to_string(i))) {
            return diff;
        }
    }
    return std::nullopt;
}

std::optional<std::string> DiffDebug(const LoadedFunction& orig,
                                     const LoadedFunction& cand,
                                     const std::string& path) {
    if (orig.source != cand.source || orig.own_source.has_value() != cand.own_source.has_value()) {
        return Fmt("%s source orig=%s cand=%s", path.c_str(),
                   orig.own_source ? QuoteString(orig.source).c_str() : "<inherited>",
                   cand.own_source ? QuoteString(cand.source).c_str() : "<inherited>");
    }
    if (orig.linedefined != cand.linedefined) {
        return Fmt("%s linedefined orig=%d cand=%d", path.c_str(), orig.linedefined,
                   cand.linedefined);
    }
    if (orig.lastlinedefined != cand.lastlinedefined) {
        return Fmt("%s lastlinedefined orig=%d cand=%d", path.c_str(), orig.lastlinedefined,
                   cand.lastlinedefined);
    }

    const size_t line_common = std::min(orig.lineinfo.size(), cand.lineinfo.size());
    for (size_t pc = 0; pc < line_common; ++pc) {
        if (orig.lineinfo[pc] != cand.lineinfo[pc]) {
            return Fmt("%s lineinfo pc=%zu orig=%d cand=%d", path.c_str(), pc, orig.lineinfo[pc],
                       cand.lineinfo[pc]);
        }
    }
    if (orig.lineinfo.size() != cand.lineinfo.size()) {
        return Fmt("%s lineinfo pc=%zu orig=%s cand=%s", path.c_str(), line_common,
                   orig.lineinfo.size() > line_common
                       ? std::to_string(orig.lineinfo[line_common]).c_str()
                       : "<end>",
                   cand.lineinfo.size() > line_common
                       ? std::to_string(cand.lineinfo[line_common]).c_str()
                       : "<end>");
    }

    const size_t var_common = std::min(orig.locvars.size(), cand.locvars.size());
    for (size_t i = 0; i < var_common; ++i) {
        const LoadedLocVar& a = orig.locvars[i];
        const LoadedLocVar& b = cand.locvars[i];
        if (a.name != b.name || a.startpc != b.startpc || a.endpc != b.endpc) {
            return Fmt("%s locvars idx=%zu orig=%s/%d-%d cand=%s/%d-%d", path.c_str(), i,
                       QuoteString(a.name).c_str(), a.startpc, a.endpc,
                       QuoteString(b.name).c_str(), b.startpc, b.endpc);
        }
    }
    if (orig.locvars.size() != cand.locvars.size()) {
        return Fmt("%s locvars idx=%zu orig=%s cand=%s", path.c_str(), var_common,
                   orig.locvars.size() > var_common
                       ? Fmt("%s/%d-%d", QuoteString(orig.locvars[var_common].name).c_str(),
                             orig.locvars[var_common].startpc, orig.locvars[var_common].endpc)
                             .c_str()
                       : "<end>",
                   cand.locvars.size() > var_common
                       ? Fmt("%s/%d-%d", QuoteString(cand.locvars[var_common].name).c_str(),
                             cand.locvars[var_common].startpc, cand.locvars[var_common].endpc)
                             .c_str()
                       : "<end>");
    }

    const size_t up_common = std::min(orig.upvalues.size(), cand.upvalues.size());
    for (size_t i = 0; i < up_common; ++i) {
        if (orig.upvalues[i] != cand.upvalues[i]) {
            return Fmt("%s upvalues idx=%zu orig=%s cand=%s", path.c_str(), i,
                       QuoteString(orig.upvalues[i]).c_str(), QuoteString(cand.upvalues[i]).c_str());
        }
    }
    if (orig.upvalues.size() != cand.upvalues.size()) {
        return Fmt("%s upvalues idx=%zu orig=%s cand=%s", path.c_str(), up_common,
                   orig.upvalues.size() > up_common ? QuoteString(orig.upvalues[up_common]).c_str()
                                                    : "<end>",
                   cand.upvalues.size() > up_common ? QuoteString(cand.upvalues[up_common]).c_str()
                                                    : "<end>");
    }

    for (size_t i = 0; i < std::min(orig.protos.size(), cand.protos.size()); ++i) {
        if (auto diff = DiffDebug(orig.protos[i], cand.protos[i], path + "." + std::to_string(i))) {
            return diff;
        }
    }
    return std::nullopt;
}

std::string HexBytes(const unsigned char* bytes, size_t count) {
    std::string out;
    for (size_t i = 0; i < count; ++i) {
        char hex[4];
        std::snprintf(hex, sizeof(hex), "%02x", bytes[i]);
        out += hex;
    }
    return out;
}

// ---------------------------------------------------------------------------
// Command line.

struct LuaStateDeleter {
    void operator()(lua_State* L) const { lua_close(L); }
};

const Proto* MainProto(lua_State* L) {
    const TValue* top = L->top - 1;
    return clvalue(top)->l.p;
}


}  // namespace

bool CompileSourceToGuestChunk(std::string_view source,
                               const std::string& chunkname,
                               bool strip,
                               bool compat_vararg,
                               std::vector<unsigned char>* out,
                               std::string* error) {
    std::unique_ptr<lua_State, LuaStateDeleter> L(luaL_newstate());
    if (!L) {
        if (error != nullptr) *error = "cannot create Lua state";
        return false;
    }
    luaP_compat_vararg = compat_vararg ? 1 : 0;
    const std::vector<unsigned char> text(source.begin(), source.end());
    const CompileResult compiled = CompileSource(L.get(), text, chunkname);
    if (!compiled.ok) {
        if (error != nullptr) *error = compiled.error;
        return false;
    }
    *out = GuestDumper(strip).Dump(MainProto(L.get()));
    return true;
}

OracleResult VerifySourceAgainstChunk(const std::vector<unsigned char>& original_bytes,
                                      std::string_view source) {
    OracleResult result;
    LoadedChunk original;
    std::string load_error;
    if (!ChunkLoader(original_bytes).Load(original, load_error)) {
        result.status = OracleStatus::kInvalidOriginal;
        result.detail = load_error;
        return result;
    }
    // Chunk name, strip and the vararg convention follow the original: a
    // stripped chunk has no lineinfo and an empty source; a function carrying
    // VARARG_HASARG was compiled with LUA_COMPAT_VARARG.
    const bool strip = original.main.lineinfo.empty();
    std::string error;
    if (!CompileSourceToGuestChunk(source, original.main.source, strip, UsesCompatVararg(original.main),
                                   &result.candidate, &error)) {
        result.status = OracleStatus::kCompileError;
        result.detail = error;
        return result;
    }
    if (result.candidate == original_bytes) {
        result.status = OracleStatus::kExact;
        return result;
    }
    LoadedChunk candidate;
    load_error.clear();
    if (!ChunkLoader(result.candidate).Load(candidate, load_error)) {
        result.status = OracleStatus::kMismatch;
        result.detail = "candidate chunk unreadable: " + load_error;
        return result;
    }
    std::optional<std::string> where;
    if (std::memcmp(original.header, candidate.header, sizeof(original.header)) != 0) {
        where = "header orig=" + HexBytes(original.header, 12) + " cand=" + HexBytes(candidate.header, 12);
    }
    if (!where) where = DiffCore(original.main, candidate.main, "main");
    if (where) {
        result.status = OracleStatus::kMismatch;
        result.detail = *where;
        return result;
    }
    where = DiffDebug(original.main, candidate.main, "main");
    result.status = OracleStatus::kCodeExact;
    result.detail = where ? *where : "bytes differ with identical structure";
    return result;
}

const char* DescribeOracleStatus(OracleStatus status) {
    switch (status) {
        case OracleStatus::kExact: return "exact";
        case OracleStatus::kCodeExact: return "code_exact";
        case OracleStatus::kMismatch: return "mismatch";
        case OracleStatus::kCompileError: return "compile_error";
        case OracleStatus::kInvalidOriginal: return "invalid";
    }
    return "unknown";
}

}  // namespace refii::luadec
