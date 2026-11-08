// refii - Fable 3 with no GPU: the device's own no-GPU bit, armed as ring setup completes, and
// the stream callbacks the GPU would have raised, run as they are inserted.
//
// THE WAIT. Every GPU-completion wait in Fable 3's XDK D3D funnels into one spin step,
// sub_82D57BF0, polling the retired-fence word the GPU writes at *(device+0x2A90). Godot draws
// and the guest's command stream reaches no GPU, so nothing retires a fence: BlockOnFence
// (sub_821E7980) spins until the hang path traps (twui at 0x82D5D408). The first such wait is
// on the GameThread inside device creation (BlockUntilIdle, sub_82D52708, from the ring re-init
// at 0x82D527B4), before the 3D Engine thread or any movie.
//
// THE GUEST'S OWN WAY OUT. The spin step returns 0 when device+0x2ABD has 0x02
// (0x82D57C38 lbz / rlwinm. / bne -> 0x82D57CBC li r3,0), and BlockOnFence leaves on that 0
// (0x821E7A40 cmpwi r3,0 / beq). The XDK sets it itself when HSIO training failed
// (sub_821BDC58). Fable 2's D3D has the same bit at the same offset; this is the latch
// Fable2/src/vfs/src/hooks/boot_lifecycle.cpp arms, for the same reason.
//
// THE SITE. sub_82D52788 is the ring-buffer initialiser (it calls VdInitializeRingBuffer and
// VdEnableRingBufferRPtrWriteBack). Its first call (0x82D5C6E8, in sub_82D5C5A0) is the first
// ring setup and waits on nothing; every later call re-initialises through BlockUntilIdle. Armed
// as the first call returns, the bit is up before any reader can wait on a fence. A re-run ORs
// the same bit again.

#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/rex_payload_bridge.h"

REX_EXTERN(__imp__rex_D3DDevice_InitializeRingBuffer);
REX_EXTERN(__imp__rex_D3DDevice_InsertCallback);

namespace {

constexpr uint32_t kDeviceNoGpuByte = 0x2ABDu;
constexpr uint8_t kDeviceNoGpuBit = 0x02u;

}  // namespace

REX_EXTERN(rex_D3DDevice_InitializeRingBuffer) {
  const uint32_t device = ctx.r3.u32;
  __imp__rex_D3DDevice_InitializeRingBuffer(ctx, base);

  if (device != 0u) {
    const uint8_t before = base[device + kDeviceNoGpuByte];
    base[device + kDeviceNoGpuByte] = static_cast<uint8_t>(before | kDeviceNoGpuBit);
    REXLOG_INFO("Refii boot: no-GPU latch armed after ring-buffer init "
                "(device=0x{:08X}, 0x{:02X} -> 0x{:02X})",
                device, before, before | kDeviceNoGpuBit);
  }
}

// THE CALLBACKS THE GPU WOULD HAVE RUN. sub_821D8A60(device, cmd, flags, callback, data) is the
// XDK's InsertCallback: on its interrupt path (flags & 2, after `flags |= 6` when neither bit is
// set, 0x821D8AB0) it emits PM4 that writes `callback` and `data` to the write-back block at
// *(device+10900) +16 and +20 and raises a CP interrupt. The guest's interrupt handler
// sub_82D52670 (registered with VdSetGraphicsInterruptCallback at 0x82D5C638) answers type 1
// with `(*(block+16))(*(block+20))`, 0x82D5268C..0x82D526A0. No GPU reads this stream, so the
// device consumes it as it is written and runs the callback then, with the same argument.
//
// What waits on them: Present (sub_82D7C338) inserts sub_82D9D4C0, which stamps the frame-sync
// record (qword_83595520) its clock sub_82D7D048 predicts the next vblank from. Never stamped,
// that record holds 0 and the 3D Engine frame's time is (0 - base) / frequency, ~3.7e11 s: the
// stream catch-up loop in sub_822A07D8 (`while (dbl_8334EF88[slot] < time)`) never reaches it
// and no frame completes.
REX_EXTERN(rex_D3DDevice_InsertCallback) {
  const uint32_t device = ctx.r3.u32;
  uint32_t flags = ctx.r5.u32;
  const uint32_t callback = ctx.r6.u32;
  const uint32_t data = ctx.r7.u32;
  __imp__rex_D3DDevice_InsertCallback(ctx, base);

  if (device == 0u || (base[device + kDeviceNoGpuByte] & kDeviceNoGpuBit) == 0u) {
    return;
  }

  if ((flags & 6u) == 0u) {
    flags |= 6u;
  }

  if ((flags & 2u) == 0u || callback == 0u) {
    return;
  }

  if (refii_bridge_rex_has_function(callback) == 0) {
    REXLOG_WARN("Refii device: stream callback 0x{:08X} is not a known function; not run",
                callback);
    return;
  }

  // A call from the inserter's own frame: r3 (the advanced command pointer) is the caller's,
  // everything else the callback may clobber as any call would.
  PPCContext work = ctx;
  work.r3.u64 = data;
  using GuestFn = void (*)(PPCContext&, uint8_t*);
  reinterpret_cast<GuestFn>(refii_bridge_rex_resolve_indirect_function(callback))(work, base);
}
