#include "luadec_chunk.h"

#include <cstring>

namespace refii::luadec {
namespace {

class Reader {
public:
    Reader(const std::string& bytes, const ChunkHeader& header)
        : bytes_(bytes), header_(header) {}

    bool Fail(std::string* error, const std::string& what) {
        if (error != nullptr && error->empty()) {
            *error = what + " at offset " + std::to_string(pos_);
        }
        failed_ = true;
        return false;
    }

    bool failed() const { return failed_; }
    std::size_t pos() const { return pos_; }
    void set_pos(std::size_t pos) { pos_ = pos; }

    bool Byte(std::uint8_t* out, std::string* error) {
        if (pos_ + 1 > bytes_.size()) {
            return Fail(error, "truncated byte");
        }
        *out = static_cast<std::uint8_t>(bytes_[pos_++]);
        return true;
    }

    bool Unsigned(int width, std::uint64_t* out, std::string* error) {
        if (pos_ + static_cast<std::size_t>(width) > bytes_.size()) {
            return Fail(error, "truncated integer");
        }
        std::uint64_t value = 0;
        for (int i = 0; i < width; ++i) {
            const std::uint64_t byte = static_cast<std::uint8_t>(bytes_[pos_ + static_cast<std::size_t>(i)]);
            if (header_.little_endian) {
                value |= byte << (8 * i);
            } else {
                value = (value << 8) | byte;
            }
        }
        pos_ += static_cast<std::size_t>(width);
        *out = value;
        return true;
    }

    bool Int(int* out, std::string* error) {
        std::uint64_t value = 0;
        if (!Unsigned(header_.int_size, &value, error)) {
            return false;
        }
        if (header_.int_size == 4) {
            *out = static_cast<std::int32_t>(static_cast<std::uint32_t>(value));
        } else {
            *out = static_cast<int>(static_cast<std::int64_t>(value));
        }
        return true;
    }

    bool SizeT(std::size_t* out, std::string* error) {
        std::uint64_t value = 0;
        if (!Unsigned(header_.size_t_size, &value, error)) {
            return false;
        }
        *out = static_cast<std::size_t>(value);
        return true;
    }

    bool Number(double* out, std::string* error) {
        std::uint64_t raw = 0;
        if (!Unsigned(header_.number_size, &raw, error)) {
            return false;
        }
        if (header_.integral) {
            if (header_.number_size == 4) {
                *out = static_cast<double>(static_cast<std::int32_t>(static_cast<std::uint32_t>(raw)));
            } else {
                *out = static_cast<double>(static_cast<std::int64_t>(raw));
            }
            return true;
        }
        if (header_.number_size == 4) {
            float value = 0.0f;
            const std::uint32_t bits = static_cast<std::uint32_t>(raw);
            std::memcpy(&value, &bits, sizeof(value));
            *out = static_cast<double>(value);
        } else {
            double value = 0.0;
            std::memcpy(&value, &raw, sizeof(value));
            *out = value;
        }
        return true;
    }

    // Lua strings are stored with their trailing NUL; size 0 means "no string".
    bool String(std::string* out, std::string* error) {
        std::size_t size = 0;
        if (!SizeT(&size, error)) {
            return false;
        }
        if (size == 0) {
            out->clear();
            return true;
        }
        if (pos_ + size > bytes_.size()) {
            return Fail(error, "truncated string");
        }
        out->assign(bytes_.data() + pos_, size - 1);
        pos_ += size;
        return true;
    }

private:
    const std::string& bytes_;
    const ChunkHeader& header_;
    std::size_t pos_ = 0;
    bool failed_ = false;
};

bool LoadFunction(Reader& reader, Proto* proto, std::string* error, int depth) {
    if (depth > 200) {
        return reader.Fail(error, "function nesting too deep");
    }
    std::uint8_t byte = 0;
    if (!reader.String(&proto->source, error) || !reader.Int(&proto->linedefined, error) ||
        !reader.Int(&proto->lastlinedefined, error)) {
        return false;
    }
    if (!reader.Byte(&byte, error)) return false;
    proto->nups = byte;
    if (!reader.Byte(&byte, error)) return false;
    proto->numparams = byte;
    if (!reader.Byte(&byte, error)) return false;
    proto->is_vararg = byte;
    if (!reader.Byte(&byte, error)) return false;
    proto->maxstacksize = byte;

    int count = 0;
    if (!reader.Int(&count, error) || count < 0) {
        return reader.Fail(error, "bad code size");
    }
    proto->code.resize(static_cast<std::size_t>(count));
    for (int i = 0; i < count; ++i) {
        std::uint64_t word = 0;
        if (!reader.Unsigned(4, &word, error)) {
            return false;
        }
        proto->code[static_cast<std::size_t>(i)] = static_cast<std::uint32_t>(word);
    }

    if (!reader.Int(&count, error) || count < 0) {
        return reader.Fail(error, "bad constant count");
    }
    proto->constants.resize(static_cast<std::size_t>(count));
    for (int i = 0; i < count; ++i) {
        Constant& constant = proto->constants[static_cast<std::size_t>(i)];
        if (!reader.Byte(&byte, error)) return false;
        switch (byte) {
            case 0:
                constant.type = Constant::kNil;
                break;
            case 1:
                constant.type = Constant::kBoolean;
                if (!reader.Byte(&byte, error)) return false;
                constant.boolean = byte != 0;
                break;
            case 3:
                constant.type = Constant::kNumber;
                if (!reader.Number(&constant.number, error)) return false;
                break;
            case 4:
                constant.type = Constant::kString;
                if (!reader.String(&constant.string, error)) return false;
                break;
            default:
                return reader.Fail(error, "unknown constant type " + std::to_string(byte));
        }
    }

    if (!reader.Int(&count, error) || count < 0) {
        return reader.Fail(error, "bad proto count");
    }
    proto->protos.resize(static_cast<std::size_t>(count));
    for (int i = 0; i < count; ++i) {
        if (!LoadFunction(reader, &proto->protos[static_cast<std::size_t>(i)], error, depth + 1)) {
            return false;
        }
    }

    if (!reader.Int(&count, error) || count < 0) {
        return reader.Fail(error, "bad lineinfo count");
    }
    proto->lineinfo.resize(static_cast<std::size_t>(count));
    for (int i = 0; i < count; ++i) {
        if (!reader.Int(&proto->lineinfo[static_cast<std::size_t>(i)], error)) return false;
    }

    if (!reader.Int(&count, error) || count < 0) {
        return reader.Fail(error, "bad locvar count");
    }
    proto->locvars.resize(static_cast<std::size_t>(count));
    for (int i = 0; i < count; ++i) {
        LocVar& var = proto->locvars[static_cast<std::size_t>(i)];
        if (!reader.String(&var.name, error) || !reader.Int(&var.startpc, error) ||
            !reader.Int(&var.endpc, error)) {
            return false;
        }
    }

    if (!reader.Int(&count, error) || count < 0) {
        return reader.Fail(error, "bad upvalue count");
    }
    proto->upvalues.resize(static_cast<std::size_t>(count));
    for (int i = 0; i < count; ++i) {
        if (!reader.String(&proto->upvalues[static_cast<std::size_t>(i)], error)) return false;
    }
    return true;
}

}  // namespace

bool LoadChunk(const std::string& bytes, Chunk* chunk, std::string* error) {
    if (bytes.size() < 12 || bytes.compare(0, 4, "\x1bLua", 4) != 0) {
        if (error != nullptr) *error = "not a Lua chunk";
        return false;
    }
    const auto byte_at = [&](std::size_t index) { return static_cast<std::uint8_t>(bytes[index]); };
    if (byte_at(4) != 0x51) {
        if (error != nullptr) *error = "unsupported Lua version";
        return false;
    }
    if (byte_at(5) != 0) {
        if (error != nullptr) *error = "unsupported chunk format";
        return false;
    }
    ChunkHeader& header = chunk->header;
    header.little_endian = byte_at(6) == 1;
    header.int_size = byte_at(7);
    header.size_t_size = byte_at(8);
    header.instruction_size = byte_at(9);
    header.number_size = byte_at(10);
    header.integral = byte_at(11) != 0;
    if (header.instruction_size != 4 || (header.int_size != 4 && header.int_size != 8) ||
        (header.size_t_size != 4 && header.size_t_size != 8) ||
        (header.number_size != 4 && header.number_size != 8)) {
        if (error != nullptr) *error = "unsupported chunk header widths";
        return false;
    }

    Reader reader(bytes, header);
    reader.set_pos(12);
    chunk->main = Proto{};
    return LoadFunction(reader, &chunk->main, error, 0);
}

}  // namespace refii::luadec
