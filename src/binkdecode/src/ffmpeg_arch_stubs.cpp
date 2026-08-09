// SIMD init stubs for the DSP helpers the Bink decoder pulls in.
//
// FFmpeg's blockdsp/hpeldsp/binkdsp each install portable C implementations and then call
// an architecture hook to swap in SIMD versions. rexglue's FFmpeg is built without the
// x86 assembly (it is an audio-only subset and never needed these), so those hooks are
// undefined at link time.
//
// Stubbing them is CORRECT, not a workaround: the C fallbacks are already installed by
// the time the hook runs, and the hook's only job is to overwrite them with faster
// equivalents. The cost is decode speed on a 1280x720 logo, which is nothing; the
// alternative is building FFmpeg's x86 assembly with nasm for four functions.

// C++ rather than C so the target does not have to enable another language for three
// no-op functions; the linkage is what matters and extern "C" gives it.

#include <cstdint>

extern "C" {

struct BlockDSPContext;
struct HpelDSPContext;
struct BinkDSPContext;

void ff_blockdsp_init_x86(struct BlockDSPContext *c);
void ff_hpeldsp_init_x86(struct HpelDSPContext *c, int flags);
void ff_binkdsp_init_x86(struct BinkDSPContext *c);

void ff_blockdsp_init_x86(struct BlockDSPContext *c) { (void)c; }
void ff_hpeldsp_init_x86(struct HpelDSPContext *c, int flags) { (void)c; (void)flags; }
void ff_binkdsp_init_x86(struct BinkDSPContext *c) { (void)c; }

}  // extern "C"
