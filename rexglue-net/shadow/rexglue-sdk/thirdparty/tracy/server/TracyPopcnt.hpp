#ifndef __TRACYPOPCNT_HPP__
#define __TRACYPOPCNT_HPP__

// REFII: the in-game Worker is built for every platform the game ships on, including Windows on
// ARM64, where upstream's `__popcnt64`/`__lzcnt64` (x86 intrinsics) do not exist. Clang and GCC
// (every build here) take the builtins, which are also right on x86 CPUs without LZCNT, where
// `__lzcnt64` silently executes as BSR. MSVC keeps its intrinsics, with ARM64's own.

#include <limits.h>
#include <stdint.h>

#if defined __GNUC__ || defined __clang__
static inline uint64_t TracyCountBits( uint64_t i )
{
    return uint64_t( __builtin_popcountll( i ) );
}
static inline uint64_t TracyLzcnt( uint64_t i )
{
    return uint64_t( __builtin_clzll( i ) );
}
#elif defined _WIN64 && ( defined _M_ARM64 || defined _M_ARM64EC )
#  include <intrin.h>
#  define TracyCountBits _CountOneBits64
#  define TracyLzcnt _CountLeadingZeros64
#elif defined _WIN64
#  include <intrin.h>
#  define TracyCountBits __popcnt64
#  define TracyLzcnt __lzcnt64
#else
static inline uint64_t TracyCountBits( uint64_t i )
{
    i = i - ( (i >> 1) & 0x5555555555555555 );
    i = ( i & 0x3333333333333333 ) + ( (i >> 2) & 0x3333333333333333 );
    i = ( (i + (i >> 4) ) & 0x0F0F0F0F0F0F0F0F );
    return ( i * (0x0101010101010101) ) >> 56;
}
static inline uint64_t TracyLzcnt( uint64_t i )
{
    i |= i >> 1;
    i |= i >> 2;
    i |= i >> 4;
    i |= i >> 8;
    i |= i >> 16;
    i |= i >> 32;
    return 64 - TracyCountBits( i );
}
#endif

#endif
