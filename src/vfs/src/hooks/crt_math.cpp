// The guest's CRT math, answered by the host's libm.
//
// // The recompiler keeps the Xbox CRT's pow/sin/cos/... as PPC code (rex_pow at 0x821FE4A8 and
// // the others registered in generated/refii_init.cpp; each verified in the disassembly), so
// // every call runs a software double-precision routine. They are the C99 functions the CRT
// // implements; the host's libm answers the same contract to within an ulp.
//
// PowerPC ABI: double arguments in f1, f2; the result in f1. Nothing else is touched.
#include <cmath>
#include "vfs_hooks_private.h"

#define REFII_CRT_MATH_1(name, fn)      \
  REX_EXTERN(name) {                    \
    REX_FUNC_PROLOGUE();                \
    ctx.f1.f64 = fn(ctx.f1.f64);        \
  }

#define REFII_CRT_MATH_2(name, fn)               \
  REX_EXTERN(name) {                             \
    REX_FUNC_PROLOGUE();                         \
    ctx.f1.f64 = fn(ctx.f1.f64, ctx.f2.f64);     \
  }

REFII_CRT_MATH_2(rex_pow, std::pow)
REFII_CRT_MATH_2(rex_atan2, std::atan2)
REFII_CRT_MATH_2(rex_fmod, std::fmod)
REFII_CRT_MATH_1(rex_sin, std::sin)
REFII_CRT_MATH_1(rex_cos, std::cos)
REFII_CRT_MATH_1(rex_tan, std::tan)
REFII_CRT_MATH_1(rex_exp, std::exp)
REFII_CRT_MATH_1(rex_log, std::log)
REFII_CRT_MATH_1(rex_acos, std::acos)
REFII_CRT_MATH_1(rex_asin, std::asin)
REFII_CRT_MATH_1(rex_atan, std::atan)
REFII_CRT_MATH_1(rex_floor, std::floor)
REFII_CRT_MATH_1(rex_ceil, std::ceil)

#undef REFII_CRT_MATH_1
#undef REFII_CRT_MATH_2
