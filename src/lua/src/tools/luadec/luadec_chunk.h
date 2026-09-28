// Lua 5.1 chunk model and loader for the luadec decompiler.
//
// The guest format is fixed by the header of every shipped chunk: little-endian,
// int 4, size_t 4, Instruction 4, lua_Number 4 (float), not integral. The loader
// honours the header widths rather than assuming them.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace refii::luadec {

struct Constant {
    enum Type { kNil, kBoolean, kNumber, kString };
    Type type = kNil;
    bool boolean = false;
    double number = 0.0;  // holds the float value exactly when the chunk uses 4-byte numbers
    std::string string;
};

struct LocVar {
    std::string name;
    int startpc = 0;
    int endpc = 0;
    int reg = -1;  // known register (inferred locals); -1 = positional, as in the debug info
    bool seeded = false;  // parameter or loop control seeded before inference: never retracted
};

struct Proto {
    std::string source;
    int linedefined = 0;
    int lastlinedefined = 0;
    int nups = 0;
    int numparams = 0;
    int is_vararg = 0;
    int maxstacksize = 0;
    std::vector<std::uint32_t> code;
    std::vector<Constant> constants;
    std::vector<Proto> protos;
    std::vector<int> lineinfo;
    std::vector<LocVar> locvars;
    std::vector<std::string> upvalues;

    bool HasLineInfo() const { return !lineinfo.empty(); }
    bool HasLocVars() const { return !locvars.empty(); }
};

struct ChunkHeader {
    bool little_endian = true;
    int int_size = 4;
    int size_t_size = 4;
    int instruction_size = 4;
    int number_size = 4;
    bool integral = false;
};

struct Chunk {
    ChunkHeader header;
    Proto main;
};

// Parses a binary chunk. Returns false and sets `error` on malformed input.
bool LoadChunk(const std::string& bytes, Chunk* chunk, std::string* error);

}  // namespace refii::luadec
