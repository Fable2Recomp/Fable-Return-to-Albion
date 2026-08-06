#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace refii::extractor {

struct XexDecryptionSummary {
    // True when the file was already stored unencrypted, so nothing was written.
    bool already_decrypted = false;
    // Offset of the basefile payload, i.e. the XEX header size.
    std::uint32_t payload_offset = 0;
    std::uint32_t payload_bytes = 0;
    // True when the retail key failed the PE sanity check and the devkit key
    // (all zeroes) produced a valid image instead.
    bool used_devkit_key = false;
};

// Decrypts a retail XEX2 basefile inside an in-memory image.
//
// The payload of an XEX2 is a single AES-128-CBC stream (zero IV) keyed by the
// session key, which is itself the security info's AES key decrypted with the
// console key. Decrypting rewrites the same byte range and flips the file
// format header's encryption type to "none", so the image keeps its size,
// header layout and compression, and re-running is a no-op.
//
// This deliberately works on a buffer rather than on a file. The installed
// default.xex is loaded by the runtime at boot and patched live from
// default.xexp, and that patch applies an LZXDELTA delta over the base XEX's
// own header bytes -- so the on-disc file has to stay exactly as the disc
// wrote it. Decrypt a copy, read what you need, throw the copy away.
//
// Delta patches (.xexp) are rejected: applying one needs the LZXDELTA path in
// the runtime loader, which already handles them at load time.
bool DecryptXexImage(std::vector<char>& image,
                     XexDecryptionSummary* summary,
                     std::string* error);

} // namespace refii::extractor
