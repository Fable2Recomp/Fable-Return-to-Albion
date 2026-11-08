#pragma once

// refii — a guest object's fields read in one copy instead of one guarded read per field.
//
// A span is copied a 4 KiB page at a time, each page on its own: a page that is not readable
// zeroes only its own bytes, which is what reading each naturally aligned field on it alone
// returns, so a span decodes to exactly what the per-field reads it replaces did. Guest page
// protection is never finer than 4 KiB.
//
// No host dependencies: `Memory` is any reader with
//   void     Copy(uint32_t address, void* out, std::size_t size) const   (zero-fills what it cannot read)
//   uint32_t Word(uint32_t address) const                                 (big-endian, 0 when unreadable)
//   uint8_t  Byte(uint32_t address) const
// — the guest's (GuestReader, host_core.h) or a test's.

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace refii::hostcore {

constexpr uint32_t kGuestSpanPage = 0x1000u;

/// `copy(address, out, size)` once per 4 KiB page the range touches, in order.
template <typename Copy>
void ForEachGuestPage(uint32_t address, void* out, std::size_t size, Copy&& copy) {
    auto* bytes = static_cast<uint8_t*>(out);
    while (size != 0u) {
        const std::size_t room = kGuestSpanPage - (address & (kGuestSpanPage - 1u));
        const std::size_t chunk = size < room ? size : room;
        copy(address, bytes, chunk);
        address += static_cast<uint32_t>(chunk);
        bytes += chunk;
        size -= chunk;
    }
}

/// Big-endian word `index` of a copied guest block.
inline uint32_t GuestBlockWord(const void* block, std::size_t index) {
    uint32_t value;
    std::memcpy(&value, static_cast<const uint8_t*>(block) + (index * 4u), sizeof(value));
    return __builtin_bswap32(value);
}

/// `count` big-endian words at `address`, in one copy, into host order.
template <typename Memory>
void ReadGuestWords(const Memory& memory, uint32_t address, uint32_t* out, std::size_t count) {
    memory.Copy(address, out, count * 4u);
    for (std::size_t i = 0; i < count; ++i) {
        out[i] = __builtin_bswap32(out[i]);
    }
}

/// Bytes [First, End) of a guest object, addressed by the object's own offsets.
template <uint32_t First, uint32_t End>
struct GuestSpan {
    static_assert(First < End, "an empty span");

    uint8_t bytes[End - First];

    template <typename Memory>
    GuestSpan(const Memory& memory, uint32_t object) {
        memory.Copy(object + First, bytes, sizeof(bytes));
    }

    uint8_t Byte(uint32_t offset) const { return bytes[offset - First]; }

    uint32_t Word(uint32_t offset) const {
        uint32_t value;
        std::memcpy(&value, bytes + (offset - First), sizeof(value));
        return __builtin_bswap32(value);
    }

    /// `count` words from `offset`, in host order, as the guest's per-word reads returned them.
    void Words(uint32_t offset, uint32_t* out, uint32_t count) const {
        for (uint32_t i = 0; i < count; ++i) {
            out[i] = Word(offset + (i * 4u));
        }
    }
};

}  // namespace refii::hostcore
