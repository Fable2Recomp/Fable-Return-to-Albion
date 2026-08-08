// C ABI over the asset browser's .mdl -> glTF converter.
//
// The parser is a mature, working implementation; this exposes it to C# by
// P/Invoke, the same way refii_bnk_extractor's bank ABI is consumed. Nothing is
// reimplemented here — the shim only marshals.

#include "MDL/mdl_converter.h"

#include <cstring>
#include <exception>
#include <string>
#include <vector>

namespace {

void WriteError(const std::string& message, char* error, size_t capacity) {
    if (error == nullptr || capacity == 0) {
        return;
    }
    const size_t copied = message.size() < capacity - 1 ? message.size() : capacity - 1;
    std::memcpy(error, message.data(), copied);
    error[copied] = '\0';
}

}  // namespace

extern "C" {

/// Converts one .mdl (header bytes followed by its geometry payload) into a GLB
/// written to `out_path`. Returns 1 on success, 0 on failure with the reason in
/// `error`.
int RefiiMdlToGlb(const unsigned char* data,
                  size_t length,
                  const char* out_path,
                  const char* source_path,
                  char* error,
                  size_t error_capacity) {
    if (data == nullptr || out_path == nullptr) {
        WriteError("null argument", error, error_capacity);
        return 0;
    }

    try {
        std::vector<unsigned char> bytes(data, data + length);
        std::string message;
        const bool ok = mdl_to_glb_full(bytes, out_path,
                                        source_path == nullptr ? "" : source_path,
                                        message);
        if (!ok) {
            WriteError(message.empty() ? "conversion failed" : message, error, error_capacity);
            return 0;
        }
        return 1;
    } catch (const std::exception& ex) {
        WriteError(ex.what(), error, error_capacity);
        return 0;
    } catch (...) {
        WriteError("unknown failure converting model", error, error_capacity);
        return 0;
    }
}

}  // extern "C"
