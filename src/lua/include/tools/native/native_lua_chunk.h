#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace refii::lua {

enum class Lua51ConstantType {
    kNil,
    kBoolean,
    kNumber,
    kString,
};

struct Lua51Constant {
    Lua51ConstantType type = Lua51ConstantType::kNil;
    bool boolean_value = false;
    double number_value = 0.0;
    std::string string_value;
};

struct Lua51Local {
    std::string name;
    uint32_t start_pc = 0;
    uint32_t end_pc = 0;
};

struct Lua51Function {
    std::string source_name;
    uint32_t line_defined = 0;
    uint32_t last_line_defined = 0;
    uint8_t upvalue_count = 0;
    uint8_t parameter_count = 0;
    uint8_t vararg_flags = 0;
    uint8_t max_stack_size = 0;
    std::vector<uint32_t> code;
    std::vector<Lua51Constant> constants;
    std::vector<Lua51Function> child_functions;
    std::vector<uint32_t> line_info;
    std::vector<Lua51Local> locals;
    std::vector<std::string> upvalue_names;
    bool stripped = false;
};

struct Lua51ChunkHeader {
    bool little_endian = true;
    uint8_t int_size = 0;
    uint8_t size_t_size = 0;
    uint8_t instruction_size = 0;
    uint8_t number_size = 0;
    bool integral_numbers = false;
};

struct Lua51Chunk {
    Lua51ChunkHeader header;
    Lua51Function main;
};

struct Lua51ChunkStats {
    bool stripped = false;
    std::size_t function_count = 0;
    std::size_t instruction_count = 0;
    std::size_t local_count = 0;
    std::size_t constant_count = 0;
    std::size_t string_constant_count = 0;
};

bool ParseLua51Chunk(const std::vector<char>& bytes,
                     Lua51Chunk* chunk,
                     std::string* error = nullptr);

std::string GetLua51ChunkSourceName(const std::vector<char>& bytes);

Lua51ChunkStats SummarizeLua51Chunk(const Lua51Chunk& chunk);

std::string DescribeLua51ChunkStats(const Lua51ChunkStats& stats);

}  // namespace refii::lua
