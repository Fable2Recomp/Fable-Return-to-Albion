#include "runtime/refii_renderer/hooks/video/fable_pix_video_decode_observer.h"

#include "renderer_runtime.h"
#include "runtime/refii_renderer/hooks/video/fable_video_observer_state.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_packets.h"

#include <rex/logging.h>

namespace refii::renderer {

void FableCapturePixDecodedVideoFrameUpload(
    const RendererHookArgs& args,
    uint32_t result,
    uint8_t* base,
    FablePixDecodedVideoFrameCaptureContext& context) {
  if (!base || result != 0 || args.r8 == 0) {
    return;
  }

  // PixDecodeVideoFrame writes a Pix metadata block, not a texture fetch:
  // dwords 0..5 are marker, flag, width, height, frame base, and alpha.
  // Match the decoded frame base against fetch constants captured from the
  // later PixRender path, then refresh the existing texture upload.
  FablePixDecodedVideoFrameInfo frame_info;
  if (!FablePixVideoReadDecodedFrameInfo(base, args.r8, frame_info)) {
    return;
  }

  if (context.log_packet_shapes && context.logged_packet_shapes < 12u) {
    const FablePixDecodePacketShape packet_shape =
        FablePixVideoReadDecodePacketShape(base, args.r5, args.r7);
    REXLOG_INFO(
        "Refii renderer PixDecode packet #{} result=0x{:08X} "
        "chunks=0x{:08X}/{} first=0x{:08X} offset=0x{:08X} "
        "chunk=0x{:08X} readable={} q0={:08X}/{:08X} flags=0x{:08X} "
        "chunk_size={}x{} scale={:08X}/{:08X} alpha={:08X} "
        "frame=0x{:08X} {}x{} out=0x{:08X}/{} "
        "sample={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
        context.logged_packet_shapes + 1u, result, args.r5,
        packet_shape.chunks_readable ? 1 : 0,
        packet_shape.first_chunk_pointer, args.r7, packet_shape.chunk,
        packet_shape.chunk_readable ? 1 : 0,
        packet_shape.q0_hi, packet_shape.q0_lo, packet_shape.flags,
        packet_shape.width, packet_shape.height, packet_shape.scale_u,
        packet_shape.scale_v, packet_shape.alpha, frame_info.frame_base,
        frame_info.width, frame_info.height, args.r8, args.r9,
        packet_shape.sample[0], packet_shape.sample[1],
        packet_shape.sample[2], packet_shape.sample[3],
        packet_shape.sample[4], packet_shape.sample[5],
        packet_shape.sample[6], packet_shape.sample[7]);
    ++context.logged_packet_shapes;
  }

  FableRecordPixDecodedVideoFrame(
      context.pending_frames, context.serial, context.decode_frames,
      context.last_decode_base, context.last_decode_width,
      context.last_decode_height, frame_info.frame_base, frame_info.width,
      frame_info.height);

  if (context.refresh_resource == nullptr) {
    return;
  }

  for (auto& [address, record] : context.resources) {
    (void)address;
    if (GuestObjectKind(record) != RendererResourceKind::kTexture) {
      continue;
    }
    context.refresh_resource(record, base, context.refresh_user);
  }
}

}  // namespace refii::renderer
