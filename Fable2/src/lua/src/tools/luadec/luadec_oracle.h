// Byte-exact oracle for the luadec decompiler: compiles Lua text the way the
// game's compiler did (Lua 5.1.2 + `continue`, 4-byte float numbers, guest
// chunk format) and compares the result with the original chunk.
#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace refii::luadec {

enum class OracleStatus {
    kExact,            // the candidate chunk is byte-identical to the original
    kCodeExact,        // code, constants and prototypes match; only debug info differs
    kMismatch,         // the compiled code differs (detail names the first divergence)
    kCompileError,     // the source does not compile (detail carries the message)
    kInvalidOriginal,  // the original is not a readable guest chunk
};

struct OracleResult {
    OracleStatus status = OracleStatus::kInvalidOriginal;
    std::string detail;
    std::vector<unsigned char> candidate;  // the compiled candidate chunk when it compiled
};

// Compiles `source` as chunk `chunkname` and dumps it in the guest format,
// stripped of debug info when `strip` is set.
bool CompileSourceToGuestChunk(std::string_view source,
                               const std::string& chunkname,
                               bool strip,
                               bool compat_vararg,
                               std::vector<unsigned char>* out,
                               std::string* error);

// Compiles `source` under the original chunk's own conventions (chunk name,
// strip, vararg compatibility) and diffs the result against it.
OracleResult VerifySourceAgainstChunk(const std::vector<unsigned char>& original,
                                      std::string_view source);

const char* DescribeOracleStatus(OracleStatus status);

}  // namespace refii::luadec
