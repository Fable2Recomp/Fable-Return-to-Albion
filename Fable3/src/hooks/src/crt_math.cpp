// refii - Fable 3's CRT math, answered by the host's libm.
//
// The recompiler keeps the Xbox CRT's pow/sin/cos/... as PPC code, so every call runs a software
// double-precision routine. They are the C99 functions the CRT implements; the host's libm
// answers the same contract to within an ulp (Fable2/src/vfs/src/hooks/crt_math.cpp is the
// reference; this file names Fable 3's own entries, refiii_config.toml).
//
// EACH ENTRY IS FABLE 2'S CRT ROUTINE AT FABLE 3'S ADDRESS: found by Fable 2 TU1's entry-point
// byte signature matching exactly one function start in the Fable 3 IDB, of the same size, whose
// head reads f1 (and f2) as the CRT's does:
//   sin   0x821E1DF8 (0xE0)  `fabs f0,f1`, the Cody-Waite table at dbl_8327C308
//   cos   0x821E1F58 (0xE0)  the same table, `fadd f12,f13,f0`
//   tan   0x82255458 (0xD4)  `fcmpu cr6,f1,dbl_8200E8F0; beqlr`
//   exp   0x821EF240 (0xDC)  `fmul f5,f0,f1` with the dbl_8327C870 table
//   log   0x822461D0 (0x18C) `fcmpu cr6,f1,dbl_82000D40` (1.0 -> 0)
//   asin  0x8225B6D8 (0xC8)  Fable 2's asin/acos core with the flag folded to `li r10,1` (asin)
//   atan  0x82254920 (0xE4)  `fabs f0,f1` against flt_8327C300
//   atan2 0x82E2C110 (0x130) `stfd f1; stfd f2`, the zero tests on f2 then f1
//   fmod  0x821FC448 (0x184) `stfd f1; lhz r11; rlwinm 0,17,27; stfd f2; cmplwi 0x7FF0` (NaN/Inf)
//   floor 0x821F0B80 (0x3C)  `fctidz f12,f1 ... fsub f10,f1,f12`
//   ceil  0x821A1428 (0x3C)  the same head, `fsub f10,f12,f1`
//   pow   0x821EF7C8 (0x4E4) Fable 2's pow prologue (`mflr; stw r12; std r31; bl __savefpr_25`,
//         `stfd f1; stfd f2; fmr f28,f1`), 0x588 past exp as Fable 2's is 0x548 past its exp,
//         two instructions shorter; the signature's relative branch is what kept it from matching.
// NOT NAMED: acos. Fable 2's acos is an 8-byte `li r4,1; b core` stub into the asin/acos core;
// Fable 3 folded the flag into asin (above) and no function start carries the core's head with
// `li r10,0`, so Fable 3 has no acos entry to answer.
//
// PowerPC ABI: double arguments in f1, f2; the result in f1. Nothing else is touched.
#include <cmath>

#include <rex/ppc.h>

#define REFIII_CRT_MATH_1(name, fn) \
  REX_EXTERN(name) { ctx.f1.f64 = fn(ctx.f1.f64); }

#define REFIII_CRT_MATH_2(name, fn) \
  REX_EXTERN(name) { ctx.f1.f64 = fn(ctx.f1.f64, ctx.f2.f64); }

REFIII_CRT_MATH_2(rex_pow, std::pow)
REFIII_CRT_MATH_2(rex_atan2, std::atan2)
REFIII_CRT_MATH_2(rex_fmod, std::fmod)
REFIII_CRT_MATH_1(rex_sin, std::sin)
REFIII_CRT_MATH_1(rex_cos, std::cos)
REFIII_CRT_MATH_1(rex_tan, std::tan)
REFIII_CRT_MATH_1(rex_exp, std::exp)
REFIII_CRT_MATH_1(rex_log, std::log)
REFIII_CRT_MATH_1(rex_asin, std::asin)
REFIII_CRT_MATH_1(rex_atan, std::atan)
REFIII_CRT_MATH_1(rex_floor, std::floor)
REFIII_CRT_MATH_1(rex_ceil, std::ceil)

#undef REFIII_CRT_MATH_1
#undef REFIII_CRT_MATH_2
