// refii - Fable 3 boots with no GPU: the device's own no-GPU bit, armed as ring setup completes.
//
// THE WAIT. Every GPU-completion wait in Fable 3's XDK D3D funnels into one spin step,
// sub_82D25890, polling the retired-fence word the GPU writes at *(device+0x2A90). Godot draws
// and the guest's command stream reaches no GPU, so nothing retires a fence: BlockOnFence
// (sub_821E7D18) spins until the hang path traps (twui at 0x82D2B0A8). The first such wait is
// on the GameThread inside device creation (BlockUntilIdle, sub_82D203A8, from the ring re-init
// at 0x82D20454), before the 3D Engine thread or any movie.
//
// THE GUEST'S OWN WAY OUT. The spin step returns 0 when device+0x2ABD has 0x02
// (0x82D258D8 lbz / rlwinm. / bne -> 0x82D2595C li r3,0), and BlockOnFence leaves on that 0
// (0x821E7DD8 cmpwi r3,0 / beq). The XDK sets it itself when HSIO training failed
// (sub_821BDD10). Fable 2's D3D has the same bit at the same offset; this is the latch
// Fable2/src/vfs/src/hooks/boot_lifecycle.cpp arms, for the same reason.
//
// THE SITE. sub_82D20428 is the ring-buffer initialiser (it calls VdInitializeRingBuffer and
// VdEnableRingBufferRPtrWriteBack). Its first call (0x82D2A388, in sub_82D2A240) is the first
// ring setup and waits on nothing; every later call re-initialises through BlockUntilIdle. Armed
// as the first call returns, the bit is up before any reader can wait on a fence. A re-run ORs
// the same bit again.

#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc.h>

REX_EXTERN(__imp__rex_D3DDevice_InitializeRingBuffer);

REX_EXTERN(rex_D3DDevice_InitializeRingBuffer) {
  const uint32_t device = ctx.r3.u32;
  __imp__rex_D3DDevice_InitializeRingBuffer(ctx, base);

  if (device != 0u) {
    const uint8_t before = base[device + 0x2ABDu];
    base[device + 0x2ABDu] = static_cast<uint8_t>(before | 0x02u);
    REXLOG_INFO("Refii boot: no-GPU latch armed after ring-buffer init "
                "(device=0x{:08X}, 0x{:02X} -> 0x{:02X})",
                device, before, before | 0x02u);
  }
}
