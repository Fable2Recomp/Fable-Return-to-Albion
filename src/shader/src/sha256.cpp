#include "shader/sha256.h"

#include <array>
#include <cstring>
#include <iomanip>
#include <sstream>

namespace refii::shader::crypto {
namespace {

struct Sha256Context {
    std::array<unsigned char, 64> data{};
    unsigned int data_length = 0;
    unsigned long long bit_length = 0;
    std::array<unsigned int, 8> state{};
};

constexpr std::array<unsigned int, 64> kRoundConstants = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1,
    0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
    0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786,
    0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147,
    0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
    0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
    0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a,
    0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
    0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};

constexpr unsigned int RotateRight(unsigned int value, unsigned int amount) {
    return (value >> amount) | (value << (32 - amount));
}

constexpr unsigned int Choice(unsigned int x, unsigned int y, unsigned int z) {
    return (x & y) ^ (~x & z);
}

constexpr unsigned int Majority(unsigned int x, unsigned int y, unsigned int z) {
    return (x & y) ^ (x & z) ^ (y & z);
}

constexpr unsigned int BigSigma0(unsigned int value) {
    return RotateRight(value, 2) ^ RotateRight(value, 13) ^ RotateRight(value, 22);
}

constexpr unsigned int BigSigma1(unsigned int value) {
    return RotateRight(value, 6) ^ RotateRight(value, 11) ^ RotateRight(value, 25);
}

constexpr unsigned int SmallSigma0(unsigned int value) {
    return RotateRight(value, 7) ^ RotateRight(value, 18) ^ (value >> 3);
}

constexpr unsigned int SmallSigma1(unsigned int value) {
    return RotateRight(value, 17) ^ RotateRight(value, 19) ^ (value >> 10);
}

void Transform(Sha256Context* context, const unsigned char block[64]) {
    unsigned int message_schedule[64];
    for (unsigned int i = 0, j = 0; i < 16; ++i, j += 4) {
        message_schedule[i] = (static_cast<unsigned int>(block[j]) << 24) |
                              (static_cast<unsigned int>(block[j + 1]) << 16) |
                              (static_cast<unsigned int>(block[j + 2]) << 8) |
                              static_cast<unsigned int>(block[j + 3]);
    }
    for (unsigned int i = 16; i < 64; ++i) {
        message_schedule[i] = SmallSigma1(message_schedule[i - 2]) +
                              message_schedule[i - 7] +
                              SmallSigma0(message_schedule[i - 15]) +
                              message_schedule[i - 16];
    }

    unsigned int a = context->state[0];
    unsigned int b = context->state[1];
    unsigned int c = context->state[2];
    unsigned int d = context->state[3];
    unsigned int e = context->state[4];
    unsigned int f = context->state[5];
    unsigned int g = context->state[6];
    unsigned int h = context->state[7];

    for (unsigned int i = 0; i < 64; ++i) {
        const unsigned int t1 =
            h + BigSigma1(e) + Choice(e, f, g) + kRoundConstants[i] + message_schedule[i];
        const unsigned int t2 = BigSigma0(a) + Majority(a, b, c);
        h = g;
        g = f;
        f = e;
        e = d + t1;
        d = c;
        c = b;
        b = a;
        a = t1 + t2;
    }

    context->state[0] += a;
    context->state[1] += b;
    context->state[2] += c;
    context->state[3] += d;
    context->state[4] += e;
    context->state[5] += f;
    context->state[6] += g;
    context->state[7] += h;
}

void Initialize(Sha256Context* context) {
    context->data_length = 0;
    context->bit_length = 0;
    context->state = {0x6a09e667,
                      0xbb67ae85,
                      0x3c6ef372,
                      0xa54ff53a,
                      0x510e527f,
                      0x9b05688c,
                      0x1f83d9ab,
                      0x5be0cd19};
}

void Update(Sha256Context* context, const unsigned char* data, std::size_t size) {
    for (std::size_t i = 0; i < size; ++i) {
        context->data[context->data_length++] = data[i];
        if (context->data_length == context->data.size()) {
            Transform(context, context->data.data());
            context->bit_length += 512;
            context->data_length = 0;
        }
    }
}

std::array<unsigned char, 32> Finalize(Sha256Context* context) {
    unsigned int i = context->data_length;
    if (context->data_length < 56) {
        context->data[i++] = 0x80;
        while (i < 56) {
            context->data[i++] = 0x00;
        }
    } else {
        context->data[i++] = 0x80;
        while (i < 64) {
            context->data[i++] = 0x00;
        }
        Transform(context, context->data.data());
        std::memset(context->data.data(), 0, 56);
    }

    context->bit_length += static_cast<unsigned long long>(context->data_length) * 8;
    context->data[63] = static_cast<unsigned char>(context->bit_length);
    context->data[62] = static_cast<unsigned char>(context->bit_length >> 8);
    context->data[61] = static_cast<unsigned char>(context->bit_length >> 16);
    context->data[60] = static_cast<unsigned char>(context->bit_length >> 24);
    context->data[59] = static_cast<unsigned char>(context->bit_length >> 32);
    context->data[58] = static_cast<unsigned char>(context->bit_length >> 40);
    context->data[57] = static_cast<unsigned char>(context->bit_length >> 48);
    context->data[56] = static_cast<unsigned char>(context->bit_length >> 56);
    Transform(context, context->data.data());

    std::array<unsigned char, 32> hash{};
    for (i = 0; i < 4; ++i) {
        hash[i] = static_cast<unsigned char>((context->state[0] >> (24 - i * 8)) & 0xff);
        hash[i + 4] =
            static_cast<unsigned char>((context->state[1] >> (24 - i * 8)) & 0xff);
        hash[i + 8] =
            static_cast<unsigned char>((context->state[2] >> (24 - i * 8)) & 0xff);
        hash[i + 12] =
            static_cast<unsigned char>((context->state[3] >> (24 - i * 8)) & 0xff);
        hash[i + 16] =
            static_cast<unsigned char>((context->state[4] >> (24 - i * 8)) & 0xff);
        hash[i + 20] =
            static_cast<unsigned char>((context->state[5] >> (24 - i * 8)) & 0xff);
        hash[i + 24] =
            static_cast<unsigned char>((context->state[6] >> (24 - i * 8)) & 0xff);
        hash[i + 28] =
            static_cast<unsigned char>((context->state[7] >> (24 - i * 8)) & 0xff);
    }
    return hash;
}

}  // namespace

std::string ComputeSha256Hex(const void* data, std::size_t size) {
    Sha256Context context;
    Initialize(&context);
    Update(&context, static_cast<const unsigned char*>(data), size);
    const std::array<unsigned char, 32> hash = Finalize(&context);

    std::ostringstream stream;
    stream << std::hex << std::setfill('0');
    for (const unsigned char byte : hash) {
        stream << std::setw(2) << static_cast<unsigned int>(byte);
    }
    return stream.str();
}

}  // namespace refii::shader::crypto
