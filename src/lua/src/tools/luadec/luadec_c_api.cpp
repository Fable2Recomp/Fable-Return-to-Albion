// C ABI of the luadec decompiler for the Godot editor (P/Invoke through
// Engine_LuaDecompiler.cs). One shared library, no state: every call takes
// the chunk bytes it works on and hands back text the caller frees.
//
// Bump kAbiVersion and the C# ExpectedAbiVersion together when a signature
// changes.
#include "luadec.h"
#include "luadec_chunk.h"
#include "luadec_oracle.h"

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

#if defined(_WIN32)
#define REFII_LUADEC_EXPORT extern "C" __declspec(dllexport)
#else
#define REFII_LUADEC_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

constexpr int32_t kAbiVersion = 1;

// Verification outcome codes shared with the C# side.
constexpr int32_t kVerifyExact = 0;
constexpr int32_t kVerifyCodeExact = 1;
constexpr int32_t kVerifyMismatch = 2;
constexpr int32_t kVerifyCompileError = 3;
constexpr int32_t kVerifyInvalid = 4;

void CopyMessage(std::string_view text, char* buffer, uint32_t capacity) {
    if (buffer == nullptr || capacity == 0) return;
    const size_t count = text.size() < capacity - 1 ? text.size() : capacity - 1;
    std::memcpy(buffer, text.data(), count);
    buffer[count] = '\0';
}

char* AllocateText(const std::string& text, uint32_t* size) {
    char* out = static_cast<char*>(std::malloc(text.size() + 1));
    if (out == nullptr) return nullptr;
    std::memcpy(out, text.data(), text.size());
    out[text.size()] = '\0';
    if (size != nullptr) *size = static_cast<uint32_t>(text.size());
    return out;
}

int32_t StatusCode(refii::luadec::OracleStatus status) {
    switch (status) {
        case refii::luadec::OracleStatus::kExact: return kVerifyExact;
        case refii::luadec::OracleStatus::kCodeExact: return kVerifyCodeExact;
        case refii::luadec::OracleStatus::kMismatch: return kVerifyMismatch;
        case refii::luadec::OracleStatus::kCompileError: return kVerifyCompileError;
        case refii::luadec::OracleStatus::kInvalidOriginal: return kVerifyInvalid;
    }
    return kVerifyInvalid;
}

}  // namespace

REFII_LUADEC_EXPORT int32_t RefiiLuadecAbiVersion(void) { return kAbiVersion; }

/// Whether `bytes` is a compiled Lua 5.1 chunk (as opposed to source text).
REFII_LUADEC_EXPORT int32_t RefiiLuadecIsChunk(const uint8_t* bytes, uint32_t size) {
    return bytes != nullptr && size >= 4 && std::memcmp(bytes, "\x1bLua", 4) == 0 ? 1 : 0;
}

/// Decompiles a chunk to Lua text and verifies the text against it. Returns
/// 1 with `*out_text` (free with RefiiLuadecFree) and the verification code in
/// `*out_verify`; `message` carries the oracle's detail (empty when exact).
/// Returns 0 with the failure in `message` when the chunk cannot be read or
/// its structure cannot be recovered.
REFII_LUADEC_EXPORT int32_t RefiiLuadecDecompile(const uint8_t* bytes,
                                                 uint32_t size,
                                                 char** out_text,
                                                 uint32_t* out_size,
                                                 int32_t* out_verify,
                                                 char* message,
                                                 uint32_t message_capacity) {
    if (out_text != nullptr) *out_text = nullptr;
    if (out_size != nullptr) *out_size = 0;
    if (out_verify != nullptr) *out_verify = kVerifyInvalid;
    CopyMessage("", message, message_capacity);
    if (bytes == nullptr || out_text == nullptr) {
        CopyMessage("no chunk", message, message_capacity);
        return 0;
    }
    const std::string input(reinterpret_cast<const char*>(bytes), size);
    refii::luadec::Chunk chunk;
    std::string error;
    if (!refii::luadec::LoadChunk(input, &chunk, &error)) {
        CopyMessage(error, message, message_capacity);
        return 0;
    }
    std::string text;
    if (!refii::luadec::Decompile(chunk, refii::luadec::DecompileOptions{}, &text, &error)) {
        CopyMessage(error, message, message_capacity);
        return 0;
    }
    const std::vector<unsigned char> original(input.begin(), input.end());
    const refii::luadec::OracleResult verify = refii::luadec::VerifySourceAgainstChunk(original, text);
    if (out_verify != nullptr) *out_verify = StatusCode(verify.status);
    CopyMessage(verify.detail, message, message_capacity);
    *out_text = AllocateText(text, out_size);
    if (*out_text == nullptr) {
        CopyMessage("out of memory", message, message_capacity);
        return 0;
    }
    return 1;
}

/// The disassembly listing of a chunk, for a chunk that will not decompile.
REFII_LUADEC_EXPORT int32_t RefiiLuadecDisassemble(const uint8_t* bytes,
                                                   uint32_t size,
                                                   char** out_text,
                                                   uint32_t* out_size,
                                                   char* message,
                                                   uint32_t message_capacity) {
    if (out_text != nullptr) *out_text = nullptr;
    if (out_size != nullptr) *out_size = 0;
    CopyMessage("", message, message_capacity);
    if (bytes == nullptr || out_text == nullptr) {
        CopyMessage("no chunk", message, message_capacity);
        return 0;
    }
    refii::luadec::Chunk chunk;
    std::string error;
    if (!refii::luadec::LoadChunk(std::string(reinterpret_cast<const char*>(bytes), size), &chunk, &error)) {
        CopyMessage(error, message, message_capacity);
        return 0;
    }
    std::string text;
    refii::luadec::Disassemble(chunk, &text);
    *out_text = AllocateText(text, out_size);
    return *out_text != nullptr ? 1 : 0;
}

/// Compiles `text` as chunk `chunk_name` with the game's compiler. Returns 1
/// when it compiles; 0 with the compiler's message otherwise.
REFII_LUADEC_EXPORT int32_t RefiiLuadecCheckSource(const char* text,
                                                   uint32_t size,
                                                   const char* chunk_name,
                                                   char* message,
                                                   uint32_t message_capacity) {
    CopyMessage("", message, message_capacity);
    if (text == nullptr) {
        CopyMessage("no source", message, message_capacity);
        return 0;
    }
    std::vector<unsigned char> chunk;
    std::string error;
    const bool ok = refii::luadec::CompileSourceToGuestChunk(
        std::string_view(text, size), chunk_name != nullptr ? chunk_name : "=source", /*strip=*/true,
        /*compat_vararg=*/false, &chunk, &error);
    if (!ok) CopyMessage(error, message, message_capacity);
    return ok ? 1 : 0;
}

/// Verifies `text` against the chunk it was decompiled from. Returns the
/// verification code; `message` carries the first divergence or the compiler
/// error.
REFII_LUADEC_EXPORT int32_t RefiiLuadecVerify(const uint8_t* bytes,
                                              uint32_t size,
                                              const char* text,
                                              uint32_t text_size,
                                              char* message,
                                              uint32_t message_capacity) {
    CopyMessage("", message, message_capacity);
    if (bytes == nullptr || text == nullptr) {
        CopyMessage("no chunk or no source", message, message_capacity);
        return kVerifyInvalid;
    }
    const std::vector<unsigned char> original(bytes, bytes + size);
    const refii::luadec::OracleResult verify =
        refii::luadec::VerifySourceAgainstChunk(original, std::string_view(text, text_size));
    CopyMessage(verify.detail, message, message_capacity);
    return StatusCode(verify.status);
}

REFII_LUADEC_EXPORT void RefiiLuadecFree(char* text) { std::free(text); }
