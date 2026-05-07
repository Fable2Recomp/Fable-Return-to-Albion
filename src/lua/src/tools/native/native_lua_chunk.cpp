#include "tools/native/native_lua_chunk.h"

#include <algorithm>
#include <array>
#include <cstring>
#include <limits>
#include <sstream>

namespace refii::lua {
namespace {

constexpr std::array<char, 4> kLuaSignature = {'\x1B', 'L', 'u', 'a'};

class ByteReader {
  public:
    explicit ByteReader(const std::vector<char>& bytes) : bytes_(bytes) {}

    void set_little_endian(bool little_endian) { little_endian_ = little_endian; }

    std::size_t remaining() const { return bytes_.size() - offset_; }

    bool ReadU8(uint8_t* value) {
        if (remaining() < 1) {
            return false;
        }
        *value = static_cast<uint8_t>(bytes_[offset_++]);
        return true;
    }

    bool ReadUnsigned(uint8_t width, uint64_t* value) {
        if (width == 0 || width > 8 || remaining() < width) {
            return false;
        }

        uint64_t result = 0;
        if (little_endian_) {
            for (uint8_t i = 0; i < width; ++i) {
                result |= static_cast<uint64_t>(
                              static_cast<unsigned char>(bytes_[offset_ + i]))
                          << (i * 8);
            }
        } else {
            for (uint8_t i = 0; i < width; ++i) {
                result = (result << 8) |
                         static_cast<uint64_t>(
                             static_cast<unsigned char>(bytes_[offset_ + i]));
            }
        }
        offset_ += width;
        *value = result;
        return true;
    }

    bool ReadSigned(uint8_t width, int64_t* value) {
        uint64_t raw = 0;
        if (!ReadUnsigned(width, &raw)) {
            return false;
        }

        if (width < 8) {
            const uint64_t sign_bit = uint64_t{1} << (width * 8 - 1);
            if ((raw & sign_bit) != 0) {
                raw |= (~uint64_t{0}) << (width * 8);
            }
        }

        *value = static_cast<int64_t>(raw);
        return true;
    }

    bool ReadLuaString(uint8_t size_t_width, std::string* value) {
        uint64_t raw_size = 0;
        if (!ReadUnsigned(size_t_width, &raw_size)) {
            return false;
        }
        if (raw_size == 0) {
            value->clear();
            return true;
        }
        if (raw_size > remaining()) {
            return false;
        }

        const std::size_t size = static_cast<std::size_t>(raw_size);
        if (size == 0) {
            value->clear();
            return true;
        }

        value->assign(bytes_.data() + offset_, size - 1);
        offset_ += size;
        return true;
    }

    bool ReadLuaNumber(uint8_t width, bool integral, double* value) {
        if (integral) {
            int64_t signed_value = 0;
            if (!ReadSigned(width, &signed_value)) {
                return false;
            }
            *value = static_cast<double>(signed_value);
            return true;
        }

        uint64_t raw = 0;
        if (!ReadUnsigned(width, &raw)) {
            return false;
        }

        if (width == 4) {
            const uint32_t raw32 = static_cast<uint32_t>(raw);
            float parsed = 0.0f;
            std::memcpy(&parsed, &raw32, sizeof(parsed));
            *value = static_cast<double>(parsed);
            return true;
        }
        if (width == 8) {
            double parsed = 0.0;
            std::memcpy(&parsed, &raw, sizeof(parsed));
            *value = parsed;
            return true;
        }

        return false;
    }

  private:
    const std::vector<char>& bytes_;
    std::size_t offset_ = 0;
    bool little_endian_ = true;
};

bool ToUint32(uint64_t raw, uint32_t* value) {
    if (raw > std::numeric_limits<uint32_t>::max()) {
        return false;
    }
    *value = static_cast<uint32_t>(raw);
    return true;
}

bool ParseFunction(ByteReader& reader,
                   const Lua51ChunkHeader& header,
                   Lua51Function* function,
                   std::string* error);

bool ParseConstant(ByteReader& reader,
                   const Lua51ChunkHeader& header,
                   Lua51Constant* constant,
                   std::string* error) {
    uint8_t type = 0;
    if (!reader.ReadU8(&type)) {
        if (error != nullptr) {
            *error = "truncated_constant_type";
        }
        return false;
    }

    switch (type) {
        case 0:
            constant->type = Lua51ConstantType::kNil;
            return true;
        case 1: {
            uint8_t value = 0;
            if (!reader.ReadU8(&value)) {
                if (error != nullptr) {
                    *error = "truncated_boolean_constant";
                }
                return false;
            }
            constant->type = Lua51ConstantType::kBoolean;
            constant->boolean_value = (value != 0);
            return true;
        }
        case 3: {
            double value = 0.0;
            if (!reader.ReadLuaNumber(header.number_size, header.integral_numbers, &value)) {
                if (error != nullptr) {
                    *error = "truncated_number_constant";
                }
                return false;
            }
            constant->type = Lua51ConstantType::kNumber;
            constant->number_value = value;
            return true;
        }
        case 4:
            constant->type = Lua51ConstantType::kString;
            if (!reader.ReadLuaString(header.size_t_size, &constant->string_value)) {
                if (error != nullptr) {
                    *error = "truncated_string_constant";
                }
                return false;
            }
            return true;
        default:
            if (error != nullptr) {
                *error = "unsupported_constant_type_" + std::to_string(type);
            }
            return false;
    }
}

bool ParseFunctionList(ByteReader& reader,
                       const Lua51ChunkHeader& header,
                       std::vector<Lua51Function>* functions,
                       std::string* error) {
    uint64_t raw_count = 0;
    if (!reader.ReadUnsigned(header.int_size, &raw_count)) {
        if (error != nullptr) {
            *error = "truncated_function_list";
        }
        return false;
    }

    if (raw_count > std::numeric_limits<std::size_t>::max()) {
        if (error != nullptr) {
            *error = "function_list_too_large";
        }
        return false;
    }

    functions->clear();
    functions->reserve(static_cast<std::size_t>(raw_count));
    for (std::size_t i = 0; i < static_cast<std::size_t>(raw_count); ++i) {
        Lua51Function child;
        if (!ParseFunction(reader, header, &child, error)) {
            return false;
        }
        functions->push_back(std::move(child));
    }

    return true;
}

bool ParseFunction(ByteReader& reader,
                   const Lua51ChunkHeader& header,
                   Lua51Function* function,
                   std::string* error) {
    if (!reader.ReadLuaString(header.size_t_size, &function->source_name)) {
        if (error != nullptr) {
            *error = "truncated_function_name";
        }
        return false;
    }

    uint64_t raw_value = 0;
    if (!reader.ReadUnsigned(header.int_size, &raw_value) ||
        !ToUint32(raw_value, &function->line_defined)) {
        if (error != nullptr) {
            *error = "truncated_line_defined";
        }
        return false;
    }
    if (!reader.ReadUnsigned(header.int_size, &raw_value) ||
        !ToUint32(raw_value, &function->last_line_defined)) {
        if (error != nullptr) {
            *error = "truncated_last_line_defined";
        }
        return false;
    }

    if (!reader.ReadU8(&function->upvalue_count) ||
        !reader.ReadU8(&function->parameter_count) ||
        !reader.ReadU8(&function->vararg_flags) ||
        !reader.ReadU8(&function->max_stack_size)) {
        if (error != nullptr) {
            *error = "truncated_function_header";
        }
        return false;
    }

    uint32_t count = 0;
    if (!reader.ReadUnsigned(header.int_size, &raw_value) || !ToUint32(raw_value, &count)) {
        if (error != nullptr) {
            *error = "truncated_instruction_count";
        }
        return false;
    }
    function->code.clear();
    function->code.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        uint64_t instruction = 0;
        if (!reader.ReadUnsigned(header.instruction_size, &instruction)) {
            if (error != nullptr) {
                *error = "truncated_instruction_stream";
            }
            return false;
        }
        function->code.push_back(static_cast<uint32_t>(instruction));
    }

    if (!reader.ReadUnsigned(header.int_size, &raw_value) || !ToUint32(raw_value, &count)) {
        if (error != nullptr) {
            *error = "truncated_constant_count";
        }
        return false;
    }
    function->constants.clear();
    function->constants.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        Lua51Constant constant;
        if (!ParseConstant(reader, header, &constant, error)) {
            return false;
        }
        function->constants.push_back(std::move(constant));
    }

    if (!ParseFunctionList(reader, header, &function->child_functions, error)) {
        return false;
    }

    if (!reader.ReadUnsigned(header.int_size, &raw_value) || !ToUint32(raw_value, &count)) {
        if (error != nullptr) {
            *error = "truncated_lineinfo_count";
        }
        return false;
    }
    function->line_info.clear();
    function->line_info.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        uint32_t line = 0;
        if (!reader.ReadUnsigned(header.int_size, &raw_value) || !ToUint32(raw_value, &line)) {
            if (error != nullptr) {
                *error = "truncated_lineinfo";
            }
            return false;
        }
        function->line_info.push_back(line);
    }

    if (!reader.ReadUnsigned(header.int_size, &raw_value) || !ToUint32(raw_value, &count)) {
        if (error != nullptr) {
            *error = "truncated_local_count";
        }
        return false;
    }
    function->locals.clear();
    function->locals.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        Lua51Local local;
        if (!reader.ReadLuaString(header.size_t_size, &local.name) ||
            !reader.ReadUnsigned(header.int_size, &raw_value) ||
            !ToUint32(raw_value, &local.start_pc) ||
            !reader.ReadUnsigned(header.int_size, &raw_value) ||
            !ToUint32(raw_value, &local.end_pc)) {
            if (error != nullptr) {
                *error = "truncated_local";
            }
            return false;
        }
        function->locals.push_back(std::move(local));
    }

    if (!reader.ReadUnsigned(header.int_size, &raw_value) || !ToUint32(raw_value, &count)) {
        if (error != nullptr) {
            *error = "truncated_upvalue_name_count";
        }
        return false;
    }
    function->upvalue_names.clear();
    function->upvalue_names.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        std::string name;
        if (!reader.ReadLuaString(header.size_t_size, &name)) {
            if (error != nullptr) {
                *error = "truncated_upvalue_name";
            }
            return false;
        }
        function->upvalue_names.push_back(std::move(name));
    }

    function->stripped = function->line_info.empty() && function->locals.empty();
    return true;
}

void AccumulateStats(const Lua51Function& function, Lua51ChunkStats* stats) {
    ++stats->function_count;
    stats->instruction_count += function.code.size();
    stats->local_count += function.locals.size();
    stats->constant_count += function.constants.size();
    stats->string_constant_count += std::count_if(
        function.constants.begin(), function.constants.end(), [](const Lua51Constant& constant) {
            return constant.type == Lua51ConstantType::kString;
        });
    stats->stripped = stats->stripped && function.stripped;

    for (const Lua51Function& child : function.child_functions) {
        AccumulateStats(child, stats);
    }
}

}  // namespace

bool ParseLua51Chunk(const std::vector<char>& bytes,
                     Lua51Chunk* chunk,
                     std::string* error) {
    if (chunk == nullptr) {
        if (error != nullptr) {
            *error = "null_chunk_output";
        }
        return false;
    }
    if (bytes.size() < 12) {
        if (error != nullptr) {
            *error = "truncated_header";
        }
        return false;
    }
    if (!std::equal(kLuaSignature.begin(), kLuaSignature.end(), bytes.begin())) {
        if (error != nullptr) {
            *error = "invalid_signature";
        }
        return false;
    }

    ByteReader reader(bytes);

    uint8_t value = 0;
    reader.ReadU8(&value);  // signature consumed below via direct comparison
    reader.ReadU8(&value);
    reader.ReadU8(&value);
    reader.ReadU8(&value);

    if (!reader.ReadU8(&value) || value != 0x51) {
        if (error != nullptr) {
            *error = "unsupported_lua_version";
        }
        return false;
    }
    if (!reader.ReadU8(&value) || value != 0x00) {
        if (error != nullptr) {
            *error = "unsupported_lua_format";
        }
        return false;
    }
    if (!reader.ReadU8(&value) || (value != 0x00 && value != 0x01)) {
        if (error != nullptr) {
            *error = "invalid_endianness_flag";
        }
        return false;
    }
    chunk->header.little_endian = (value == 0x01);
    reader.set_little_endian(chunk->header.little_endian);

    if (!reader.ReadU8(&chunk->header.int_size) ||
        !reader.ReadU8(&chunk->header.size_t_size) ||
        !reader.ReadU8(&chunk->header.instruction_size) ||
        !reader.ReadU8(&chunk->header.number_size) ||
        !reader.ReadU8(&value)) {
        if (error != nullptr) {
            *error = "truncated_numeric_header";
        }
        return false;
    }
    chunk->header.integral_numbers = (value != 0);

    if (chunk->header.instruction_size != 4) {
        if (error != nullptr) {
            *error = "unsupported_instruction_size";
        }
        return false;
    }
    if (chunk->header.int_size == 0 || chunk->header.int_size > 8 ||
        chunk->header.size_t_size == 0 || chunk->header.size_t_size > 8) {
        if (error != nullptr) {
            *error = "unsupported_integer_width";
        }
        return false;
    }
    if (chunk->header.number_size != 4 && chunk->header.number_size != 8) {
        if (error != nullptr) {
            *error = "unsupported_number_width";
        }
        return false;
    }

    if (!ParseFunction(reader, chunk->header, &chunk->main, error)) {
        return false;
    }

    return true;
}

std::string GetLua51ChunkSourceName(const std::vector<char>& bytes) {
    Lua51Chunk chunk;
    if (!ParseLua51Chunk(bytes, &chunk, nullptr)) {
        return {};
    }
    return chunk.main.source_name;
}

Lua51ChunkStats SummarizeLua51Chunk(const Lua51Chunk& chunk) {
    Lua51ChunkStats stats;
    stats.stripped = true;
    AccumulateStats(chunk.main, &stats);
    return stats;
}

std::string DescribeLua51ChunkStats(const Lua51ChunkStats& stats) {
    std::ostringstream stream;
    stream << "chunk_functions=" << stats.function_count;
    stream << ";chunk_instructions=" << stats.instruction_count;
    stream << ";chunk_locals=" << stats.local_count;
    stream << ";chunk_constants=" << stats.constant_count;
    stream << ";chunk_string_constants=" << stats.string_constant_count;
    stream << ";chunk_stripped=" << (stats.stripped ? "true" : "false");
    return stream.str();
}

}  // namespace refii::lua
