#include "extractor/xex_decryptor.h"

#include <array>
#include <cstdint>
#include <cstring>
#include <string>
#include <utility>
#include <vector>

namespace {

// XEX2 header fields we need, all big-endian.
constexpr std::size_t kMagicOffset = 0x00;
constexpr std::size_t kHeaderSizeOffset = 0x08;
constexpr std::size_t kSecurityInfoOffset = 0x10;
constexpr std::size_t kOptionalHeaderCountOffset = 0x14;
constexpr std::size_t kOptionalHeaderTableOffset = 0x18;

// Offset of the AES key inside xex2_security_info.
constexpr std::size_t kSecurityAesKeyOffset = 0x150;

constexpr std::uint32_t kFileFormatInfoKey = 0x000003FF;
constexpr std::uint32_t kDeltaPatchDescriptorKey = 0x000005FF;

constexpr std::uint16_t kEncryptionNone = 0;
constexpr std::uint16_t kEncryptionNormal = 1;
constexpr std::uint16_t kCompressionNone = 0;
constexpr std::uint16_t kCompressionBasic = 1;

constexpr std::array<std::uint8_t, 16> kRetailKey = {0x20, 0xB1, 0x85, 0xA5, 0x9D, 0x28,
                                                     0xFD, 0xC3, 0x40, 0x58, 0x3F, 0xBB,
                                                     0x08, 0x96, 0xBF, 0x91};
constexpr std::array<std::uint8_t, 16> kDevkitKey = {};

std::uint32_t ReadBigEndian32(const std::vector<char>& bytes, std::size_t offset) {
    const auto* p = reinterpret_cast<const std::uint8_t*>(bytes.data()) + offset;
    return (static_cast<std::uint32_t>(p[0]) << 24) | (static_cast<std::uint32_t>(p[1]) << 16) |
           (static_cast<std::uint32_t>(p[2]) << 8) | static_cast<std::uint32_t>(p[3]);
}

std::uint16_t ReadBigEndian16(const std::vector<char>& bytes, std::size_t offset) {
    const auto* p = reinterpret_cast<const std::uint8_t*>(bytes.data()) + offset;
    return static_cast<std::uint16_t>((static_cast<std::uint16_t>(p[0]) << 8) | p[1]);
}

// AES-128 decryption. The tables are derived rather than transcribed so there
// is no hand-copied constant to get wrong.
std::uint8_t GfMultiply(std::uint8_t a, std::uint8_t b) {
    std::uint8_t result = 0;
    while (b != 0) {
        if ((b & 1) != 0) {
            result ^= a;
        }
        const bool high_bit = (a & 0x80) != 0;
        a = static_cast<std::uint8_t>(a << 1);
        if (high_bit) {
            a ^= 0x1B;
        }
        b = static_cast<std::uint8_t>(b >> 1);
    }
    return result;
}

struct AesTables {
    std::array<std::uint8_t, 256> sbox{};
    std::array<std::uint8_t, 256> inverse_sbox{};
    // InvMixColumns coefficients, tabulated so decryption is lookups rather
    // than a bit-at-a-time multiply over a 20+ MB payload.
    std::array<std::uint8_t, 256> mul09{};
    std::array<std::uint8_t, 256> mul11{};
    std::array<std::uint8_t, 256> mul13{};
    std::array<std::uint8_t, 256> mul14{};

    AesTables() {
        // Multiplicative inverse in GF(2^8), then the AES affine transform.
        std::array<std::uint8_t, 256> inverse{};
        for (int value = 1; value < 256; ++value) {
            for (int candidate = 1; candidate < 256; ++candidate) {
                if (GfMultiply(static_cast<std::uint8_t>(value),
                               static_cast<std::uint8_t>(candidate)) == 1) {
                    inverse[static_cast<std::size_t>(value)] = static_cast<std::uint8_t>(candidate);
                    break;
                }
            }
        }
        for (int value = 0; value < 256; ++value) {
            const std::uint8_t s = inverse[static_cast<std::size_t>(value)];
            std::uint8_t transformed = static_cast<std::uint8_t>(s ^ 0x63);
            for (int rotation = 1; rotation <= 4; ++rotation) {
                transformed ^= static_cast<std::uint8_t>((s << rotation) | (s >> (8 - rotation)));
            }
            sbox[static_cast<std::size_t>(value)] = transformed;
            inverse_sbox[transformed] = static_cast<std::uint8_t>(value);
        }
        for (int value = 0; value < 256; ++value) {
            const auto byte = static_cast<std::uint8_t>(value);
            mul09[static_cast<std::size_t>(value)] = GfMultiply(byte, 0x09);
            mul11[static_cast<std::size_t>(value)] = GfMultiply(byte, 0x0B);
            mul13[static_cast<std::size_t>(value)] = GfMultiply(byte, 0x0D);
            mul14[static_cast<std::size_t>(value)] = GfMultiply(byte, 0x0E);
        }
    }
};

const AesTables& GetAesTables() {
    static const AesTables tables;
    return tables;
}

using RoundKeys = std::array<std::array<std::uint8_t, 16>, 11>;

RoundKeys ExpandKey(const std::uint8_t* key) {
    const AesTables& tables = GetAesTables();
    RoundKeys round_keys{};
    std::memcpy(round_keys[0].data(), key, 16);

    std::uint8_t rcon = 1;
    for (std::size_t round = 1; round < round_keys.size(); ++round) {
        const std::array<std::uint8_t, 16>& previous = round_keys[round - 1];
        std::array<std::uint8_t, 4> temp = {previous[13], previous[14], previous[15], previous[12]};
        for (std::uint8_t& byte : temp) {
            byte = tables.sbox[byte];
        }
        temp[0] ^= rcon;
        rcon = GfMultiply(rcon, 2);

        for (std::size_t index = 0; index < 4; ++index) {
            round_keys[round][index] = static_cast<std::uint8_t>(previous[index] ^ temp[index]);
        }
        for (std::size_t index = 4; index < 16; ++index) {
            round_keys[round][index] =
                static_cast<std::uint8_t>(previous[index] ^ round_keys[round][index - 4]);
        }
    }
    return round_keys;
}

void DecryptBlock(const RoundKeys& round_keys, const std::uint8_t* input, std::uint8_t* output) {
    const AesTables& tables = GetAesTables();
    std::array<std::uint8_t, 16> state{};
    for (std::size_t index = 0; index < 16; ++index) {
        state[index] = static_cast<std::uint8_t>(input[index] ^ round_keys[10][index]);
    }

    for (int round = 9; round >= 0; --round) {
        // InvShiftRows: row r rotates right by r.
        std::array<std::uint8_t, 16> shifted{};
        for (std::size_t column = 0; column < 4; ++column) {
            for (std::size_t row = 0; row < 4; ++row) {
                shifted[((column + row) % 4) * 4 + row] = state[column * 4 + row];
            }
        }

        for (std::size_t index = 0; index < 16; ++index) {
            state[index] = static_cast<std::uint8_t>(
                tables.inverse_sbox[shifted[index]] ^ round_keys[static_cast<std::size_t>(round)][index]);
        }

        if (round == 0) {
            break;
        }

        // InvMixColumns.
        for (std::size_t column = 0; column < 4; ++column) {
            std::uint8_t* c = state.data() + column * 4;
            const std::uint8_t c0 = c[0];
            const std::uint8_t c1 = c[1];
            const std::uint8_t c2 = c[2];
            const std::uint8_t c3 = c[3];
            c[0] = static_cast<std::uint8_t>(tables.mul14[c0] ^ tables.mul11[c1] ^
                                             tables.mul13[c2] ^ tables.mul09[c3]);
            c[1] = static_cast<std::uint8_t>(tables.mul09[c0] ^ tables.mul14[c1] ^
                                             tables.mul11[c2] ^ tables.mul13[c3]);
            c[2] = static_cast<std::uint8_t>(tables.mul13[c0] ^ tables.mul09[c1] ^
                                             tables.mul14[c2] ^ tables.mul11[c3]);
            c[3] = static_cast<std::uint8_t>(tables.mul11[c0] ^ tables.mul13[c1] ^
                                             tables.mul09[c2] ^ tables.mul14[c3]);
        }
    }

    std::memcpy(output, state.data(), 16);
}

// Decrypts in place with a zero IV. The XEX basefile is one continuous CBC
// chain, so the whole payload decrypts as a single stream.
void DecryptCbcInPlace(const std::uint8_t* key, std::uint8_t* data, std::size_t size) {
    const RoundKeys round_keys = ExpandKey(key);
    std::array<std::uint8_t, 16> chain{};
    std::array<std::uint8_t, 16> cipher_block{};
    for (std::size_t offset = 0; offset < size; offset += 16) {
        std::memcpy(cipher_block.data(), data + offset, 16);
        DecryptBlock(round_keys, data + offset, data + offset);
        for (std::size_t index = 0; index < 16; ++index) {
            data[offset + index] ^= chain[index];
        }
        chain = cipher_block;
    }
}

bool Fail(std::string* error, std::string message) {
    if (error != nullptr) {
        *error = std::move(message);
    }
    return false;
}

} // namespace

namespace refii::extractor {

bool DecryptXexImage(std::vector<char>& bytes,
                     XexDecryptionSummary* summary,
                     std::string* error) {
    if (bytes.size() < kOptionalHeaderTableOffset) {
        return Fail(error, "XEX is too small to contain a header");
    }
    if (std::memcmp(bytes.data() + kMagicOffset, "XEX2", 4) != 0) {
        return Fail(error, "not an XEX2 image");
    }

    const std::uint32_t header_size = ReadBigEndian32(bytes, kHeaderSizeOffset);
    const std::uint32_t security_offset = ReadBigEndian32(bytes, kSecurityInfoOffset);
    const std::uint32_t optional_header_count = ReadBigEndian32(bytes, kOptionalHeaderCountOffset);

    if (header_size > bytes.size()) {
        return Fail(error, "XEX header size runs past the end of the file");
    }
    if (static_cast<std::size_t>(security_offset) + kSecurityAesKeyOffset + 16 > bytes.size()) {
        return Fail(error, "XEX security info runs past the end of the file");
    }
    if (kOptionalHeaderTableOffset +
            static_cast<std::size_t>(optional_header_count) * 8 > bytes.size()) {
        return Fail(error, "XEX optional header table runs past the end of the file");
    }

    std::uint32_t file_format_offset = 0;
    bool is_delta_patch = false;
    for (std::uint32_t index = 0; index < optional_header_count; ++index) {
        const std::size_t entry = kOptionalHeaderTableOffset + static_cast<std::size_t>(index) * 8;
        const std::uint32_t key = ReadBigEndian32(bytes, entry);
        const std::uint32_t value = ReadBigEndian32(bytes, entry + 4);
        if (key == kFileFormatInfoKey) {
            file_format_offset = value;
        } else if (key == kDeltaPatchDescriptorKey) {
            is_delta_patch = true;
        }
    }

    if (is_delta_patch) {
        return Fail(error, "XEX is a delta patch (.xexp); the runtime loader applies it at load time");
    }
    if (file_format_offset == 0 ||
        static_cast<std::size_t>(file_format_offset) + 8 > bytes.size()) {
        return Fail(error, "XEX has no usable file format info header");
    }

    const std::uint16_t encryption_type = ReadBigEndian16(bytes, file_format_offset + 4);
    const std::uint16_t compression_type = ReadBigEndian16(bytes, file_format_offset + 6);

    if (encryption_type == kEncryptionNone) {
        if (summary != nullptr) {
            summary->already_decrypted = true;
            summary->payload_offset = header_size;
            summary->payload_bytes = static_cast<std::uint32_t>(bytes.size() - header_size);
        }
        return true;
    }
    if (encryption_type != kEncryptionNormal) {
        return Fail(error, "unsupported XEX encryption type");
    }

    const std::size_t payload_size = bytes.size() - header_size;
    if (payload_size == 0) {
        return Fail(error, "XEX has no basefile payload");
    }
    if ((payload_size % 16) != 0) {
        return Fail(error, "XEX payload is not a whole number of AES blocks");
    }

    // The session key is the security info's AES key run through the console
    // key; a single CBC block with a zero IV is a plain ECB decrypt.
    const auto* security_aes_key = reinterpret_cast<const std::uint8_t*>(bytes.data()) +
                                   security_offset + kSecurityAesKeyOffset;

    // Only "none" and "basic" compression leave the PE header at the start of
    // the payload, so that is the only case where the key choice can be
    // checked. Otherwise the retail key is assumed, as it is for every disc
    // image.
    const bool can_verify_key =
        compression_type == kCompressionNone || compression_type == kCompressionBasic;

    std::vector<std::uint8_t> payload;
    bool used_devkit_key = false;
    for (const std::array<std::uint8_t, 16>* console_key : {&kRetailKey, &kDevkitKey}) {
        std::array<std::uint8_t, 16> session_key{};
        std::memcpy(session_key.data(), security_aes_key, 16);
        DecryptCbcInPlace(console_key->data(), session_key.data(), session_key.size());

        payload.assign(reinterpret_cast<const std::uint8_t*>(bytes.data()) + header_size,
                       reinterpret_cast<const std::uint8_t*>(bytes.data()) + bytes.size());
        DecryptCbcInPlace(session_key.data(), payload.data(), payload.size());

        if (!can_verify_key || (payload[0] == 'M' && payload[1] == 'Z')) {
            used_devkit_key = console_key == &kDevkitKey;
            break;
        }
        payload.clear();
    }

    if (payload.empty()) {
        return Fail(error, "XEX decryption produced no valid PE image (unknown console key)");
    }

    std::memcpy(bytes.data() + header_size, payload.data(), payload.size());
    // The basefile is plaintext now, so the header has to say so or a reader
    // would decrypt it a second time.
    bytes[file_format_offset + 4] = 0;
    bytes[file_format_offset + 5] = 0;

    if (summary != nullptr) {
        summary->already_decrypted = false;
        summary->payload_offset = header_size;
        summary->payload_bytes = static_cast<std::uint32_t>(payload.size());
        summary->used_devkit_key = used_devkit_key;
    }
    return true;
}

} // namespace refii::extractor
