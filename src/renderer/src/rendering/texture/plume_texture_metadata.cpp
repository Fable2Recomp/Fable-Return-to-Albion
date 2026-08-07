#include "runtime/plume/resource/plume_resource_manager_internal.h"
#include "runtime/cvars.h"
// CurrentPlumeOutputExtent — the live swapchain size that drives render-surface
// promotion; see FablePromotedRenderSurfaceExtent.
#include "runtime/plume/general/plume_backend.h"
#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"

#include <rex/logging/macros.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <mutex>
#include <set>
#include <memory>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
bool SameAspectRatio(uint32_t lhs_width, uint32_t lhs_height,
                     uint32_t rhs_width, uint32_t rhs_height) {
  return lhs_width != 0 && lhs_height != 0 && rhs_width != 0 &&
         rhs_height != 0 &&
         uint64_t(lhs_width) * uint64_t(rhs_height) ==
             uint64_t(rhs_width) * uint64_t(lhs_height);
}

bool FablePromotedRenderSurfaceExtent(uint32_t guest_width,
                                      uint32_t guest_height,
                                      uint32_t& promoted_width,
                                      uint32_t& promoted_height) {
  // OUTPUT EXTENT AUTHORITY (2026-07-29): the live swapchain first, the
  // video_mode cvars only as a fallback.
  //
  // RendererOutputWidth/Height resolve to video_mode_width/height, which DEFAULT
  // to 1280x720 (rexglue window.cpp REXCVAR_DEFINE_INT32). On any machine that
  // never set them, output therefore equalled the guest's own 1280x720, this
  // function returned false on the very next line, and NOTHING was ever
  // promoted -- the title stayed 720p regardless. Meanwhile the window and
  // swapchain were 1920x1080, because ResolveWindowWidth falls through to the
  // display size when the cvar is untouched.
  //
  // So the cvars describe an intent that is usually unset, while the swapchain
  // describes what we are actually presenting to. Prefer the latter, and keep
  // the cvars as the override for anyone who does set them.
  uint32_t base_output_width = 0;
  uint32_t base_output_height = 0;
  if (!CurrentPlumeOutputExtent(base_output_width, base_output_height)) {
    base_output_width = static_cast<uint32_t>(std::max(1, RendererOutputWidth()));
    base_output_height =
        static_cast<uint32_t>(std::max(1, RendererOutputHeight()));
  }
  const uint32_t internal_scale =
      static_cast<uint32_t>(std::max(1, RendererInternalScale()));
  const uint32_t output_width = base_output_width * internal_scale;
  const uint32_t output_height = base_output_height * internal_scale;
  if (output_width == guest_width && output_height == guest_height) {
    return false;
  }
  if (output_width < guest_width || output_height < guest_height) {
    return false;
  }

  // *** PROMOTION IS UNIFORM. IT NEVER CHANGES THE GUEST'S ASPECT. ***
  //
  // Rendering natively means rasterizing MORE PIXELS of the same geometry —
  // never restretching it. Assigning the output extent directly did change
  // the aspect, because not every guest target is display-shaped:
  // sub_823893C0 (0x823894BC-0x823894CC) sets a view canvas whose HEIGHT is
  // always 720 while its WIDTH is 1120 or 960 depending on an aspect-mode
  // flag. 960x720 is 4:3 at 720 height — a deliberate PILLARBOX inside a
  // 1280-wide frame — and 1120x720 is the same thing, slightly wider.
  // Promoting either straight to 1920x1080 applied 1.7143 horizontally
  // against 1.5000 vertically and stretched a pillarboxed canvas to fill the
  // screen.
  //
  // Taking the MINIMUM of the two axis ratios preserves the guest's aspect
  // and keeps the pillarbox. This is exactly a no-op for the scene chain,
  // where the guest extent is already display-shaped: 1280x720 -> 1920x1080,
  // 640x360 -> 960x540, 320x180 -> 480x270, all at a uniform 1.5.
  auto set_scaled_output = [&](uint32_t divisor) {
    const uint32_t d = std::max(1u, divisor);
    const uint32_t target_width = std::max(1u, output_width / d);
    const uint32_t target_height = std::max(1u, output_height / d);
    const double scale =
        std::min(static_cast<double>(target_width) /
                     static_cast<double>(std::max(1u, guest_width)),
                 static_cast<double>(target_height) /
                     static_cast<double>(std::max(1u, guest_height)));
    promoted_width = std::max(
        1u, static_cast<uint32_t>(std::lround(guest_width * scale)));
    promoted_height = std::max(
        1u, static_cast<uint32_t>(std::lround(guest_height * scale)));
    return promoted_width != guest_width || promoted_height != guest_height;
  };

  // *** THE LITERAL-EXTENT WHITELIST IS GONE (2026-08-04). ***
  //
  // This used to match a fixed list — display WxH, display_width x 512,
  // 1120x720, 1120x576, 560x360, 280x180 — and promote nothing else. IDA says
  // the guest never had a list; it has one number.
  //
  // `Render_AllocateSceneRenderTargetSet` (0x82A4E6D0) takes a POINTER to a
  // {width, height} pair — call it E — and builds the entire scene set from
  // it: full-res colour, full-res depth (g_ResolvedDepthTexture), then half
  // res (`*a1 / 2` @0x82A4E850) and quarter res (`*a1 / 4` @0x82A4E89C).
  // `sub_8227A440` extends the same chain and also uses no literals — it calls
  // D3D_Get2DSurfaceDesc on the target just made and divides by 2 and 4.
  // So the guest's scene surfaces are exactly {E, E/2, E/4}, always.
  //
  // E is a RUNTIME value from the render view descriptor (view+0x5D0/+0x5D4,
  // read at 0x821C3C98/9C, falling back to qword_834A5FE8 only when both are
  // zero). **1120 does not appear as an immediate anywhere in the render
  // code.** That is why the literal list kept losing ground: at the stock
  // 1280x720 the chain is 1280/640/320, and neither 640x360 nor 320x180 was
  // ever in the list — the half- and quarter-res post chain simply never got
  // promoted while the full-res target did.
  //
  // Deleted as PROVEN DEAD, not merely replaced:
  //   * `display_width x 512` — no guest code creates such a surface. Every
  //     512 in the render path is the 512x1 tone-curve LUT built by
  //     XGSetLineTextureHeader (sub_82A7B9A8). CORRECTED 2026-08-04: an
  //     earlier revision of this comment claimed the 512-entry tonemap curve
  //     "is not a texture". It IS one — a real D3DLineTexture, 512x1
  //     GPUTEXTUREFORMAT_32_FLOAT (fmt 36), bound through D3DDevice_SetTexture
  //     as shader record 262. See [[refii-tone-curve-has-no-upload-event]].
  //     The conclusion below is unaffected: no surface is ever WxH=512.
  //     Across 2465 log files, 1280x512 /
  //     1120x512 / 1920x512 occur ZERO times. The "EDRAM-slice alias" comment
  //     was a guess with no support in the binary or in any run.
  //   * `1120x576` — likewise ZERO occurrences in 2465 log files, while
  //     1120x720 appears in 694, 560x360 in 1092 and 280x180 in 1137.
  //
  // Falling back to the display extent when E is unknown is safe: an
  // unpromoted surface has guest == host, so the ONE SCALER model yields a
  // factor of exactly 1 and content is correctly placed — just not
  // supersampled. The `scene render extent E=` log line says which case a run
  // is in.
  // E IS PER-VIEW — test the chain of EVERY observed scene extent, not just the
  // most recent one. Run 571 measured E alternating 1920x1080 <-> 1120x720, and
  // with a single remembered value promotion switched off for the whole scene
  // chain whenever the display-sized view happened to allocate last.
  //
  // Require output >= E for the WHOLE chain, not just per-surface. Without it,
  // an output smaller than the scene extent would refuse the full-res target
  // (the `output < guest` early-out above catches that) while still "promoting"
  // the half- and quarter-res members DOWNWARD — the chain would stop being
  // self-consistent, which is the one property this rule exists to guarantee.
  const size_t scene_extent_capacity = FableSceneRenderTargetExtentCapacity();
  for (size_t i = 0; i < scene_extent_capacity; ++i) {
    uint32_t scene_width = 0;
    uint32_t scene_height = 0;
    if (!FableSceneRenderTargetExtentAt(i, scene_width, scene_height)) {
      break;  // entries are append-only, so the first empty slot ends the set
    }
    if (output_width < scene_width || output_height < scene_height) {
      continue;
    }
    // Integer division, matching the guest's own arithmetic exactly.
    for (const uint32_t divisor : {1u, 2u, 4u}) {
      if (guest_width == std::max(1u, scene_width / divisor) &&
          guest_height == std::max(1u, scene_height / divisor)) {
        // May legitimately return false (no change) when this view's chain
        // already equals the output chain — that is a no-op, not a rejection,
        // so only return on a real promotion and keep testing otherwise.
        if (set_scaled_output(divisor)) {
          return true;
        }
      }
    }
  }

  // Display-sized surfaces are a separate stated family: the front buffer and
  // the display colour/depth pair built by
  // Fable_D3D_CreateDisplayOrRenderTargetSurfaces (0x82B6F040) straight from
  // qword_834A5FE8. Keep the aspect-ratio guard — a display-extent match that
  // changes aspect is a coincidence, not a scene target.
  //
  // THE DISPLAY EXTENT HAS A CHAIN TOO (2026-08-04). This used to test only
  // divisor 1, which left every display-derived HALF-res surface unpromoted
  // whenever the display extent was not also a registered E. Two allocators
  // build straight from the display global rather than from E:
  //   sub_82A6A1D0  `/2` /*0x82a6a214*/   base = qword_834A5FE8
  //   sub_82A699B8  `/2` /*0x82a69b18*/   base = qword_834A5FE8
  // and the guest itself expects E and the display extent to differ — the
  // format select in Render_AllocateSceneRenderTargetSet tests
  // `*a1 != HIDWORD(qword_834A5FE8)` /*0x82a4e708*/ to pick format 58 vs 97.
  // So when E is a per-view override (1120x720) and the display is 1280x720,
  // a 640x360 surface from these allocators matched NOTHING and stayed at
  // guest extent while its siblings grew — the same half-promoted chain this
  // rule exists to prevent, arriving from the display side instead of E.
  //
  // Walk the same {1, 2, 4} ladder, with the aspect guard applied to the
  // undivided display extent so a coincidental match at E/2 cannot slip past.
  const uint32_t display_width = FableGuestDisplayWidth();
  const uint32_t display_height = FableGuestDisplayHeight();
  if (display_width != 0 && display_height != 0 &&
      output_width >= display_width && output_height >= display_height &&
      SameAspectRatio(display_width, display_height, output_width,
                      output_height)) {
    for (const uint32_t divisor : {1u, 2u, 4u}) {
      if (guest_width == std::max(1u, display_width / divisor) &&
          guest_height == std::max(1u, display_height / divisor)) {
        if (set_scaled_output(divisor)) {
          return true;
        }
      }
    }
  }

  return false;
}

// PROMOTION FOLLOWS THE ROLE, NOT THE RESOURCE KIND (2026-08-04).
//
// This used to also require `IsSurfaceResource(tracked.kind)`, which silently
// excluded every scene target the guest allocates as a TEXTURE. Those are the
// bulk of the chain: `Render_AllocateSceneRenderTargetSet` (0x82A4E6D0) builds
// the full-res colour target through `Texture_InitHeaderPair` /*0x82a4e764*/
// and the half/quarter legs through `sub_82295380` /*0x82a4e850, 0x82a4e89c*/.
// Our own retire diagnostics show them as `kind=texture ... rt=1`.
//
// The result was a HALF-PROMOTED CHAIN: kSurface members grew to output extent
// while kTexture members stayed at the guest extent — and since two resources
// can back the same logical target, both a promoted and an unpromoted image
// existed at once. The viewport scaler runs off the tracked metadata either
// way, so a draw landing on an unpromoted member got a 1.5x viewport on a
// 1x image, magnifying content 1.5x and clipping it to the top-left.
//
// MEASURED as a matched A/B, same 20 guest draws, run 597:
//   720p  loading: every pass 1:1  -> correct
//   1080p loading: fmt 97 full-res target still 1280x720 while the guiquad
//                  target was 1920x1080 and the E/4 legs were 480x270; the
//                  1920x1080 viewport into the 1280x720 image cropped the
//                  scroll banner off the bottom and the map off the right.
// Same signature in gameplay: pshader_sunglare drew 1680x1080 into a 1120x720
// fmt 97 attachment.
//
// The extent gate below is unchanged and still bounds this: only shapes on an
// observed {E, E/2, E/4} chain or the display chain promote at all, so
// widening the role test cannot pull in unrelated textures.
bool ShouldPromoteFableRenderSurfaceToOutputExtent(
    const PlumeTrackedResource& tracked) {
  if (!tracked.texture_render_target && !tracked.texture_depth_target) {
    return false;
  }

  uint32_t promoted_width = 0;
  uint32_t promoted_height = 0;
  return FablePromotedRenderSurfaceExtent(
      tracked.texture_width, tracked.texture_height, promoted_width,
      promoted_height);
}

void PromoteFableRenderSurfaceToOutputExtent(PlumeTrackedResource& tracked) {
  uint32_t promoted_width = 0;
  uint32_t promoted_height = 0;
  const bool has_role =
      tracked.texture_render_target || tracked.texture_depth_target;

  // PROMOTION MUST BE IDEMPOTENT (2026-08-04).
  //
  // It mutates texture_width/height IN PLACE, and this function runs on every
  // metadata decode — so the second call reads the value the FIRST call wrote
  // and treats a host extent as if it were the guest's. Measured, run 600:
  //   #11 guest=0x4090ED60 guest_extent=280x180  -> 420x270
  //   #12 guest=0x4090ED60 guest_extent=420x270  -> NOT-PROMOTED
  //   #1  guest=0x4011F370 guest_extent=1280x720 -> 1920x1080
  //   #2  guest=0x4011F370 guest_extent=1920x1080 -> NOT-PROMOTED
  // It is benign only because the promoted shape happens to match no chain. If
  // it ever did match, we would promote a promoted extent and overwrite
  // promoted_from_guest_* with a HOST value — corrupting the denominator of the
  // one scale factor and compounding the scale every decode. That is the
  // laundering failure this codebase has already been bitten by.
  //
  // promoted_from_guest_* is the record of what the guest actually asked for.
  // Once set it is authoritative: always promote FROM it, and never overwrite
  // it. Re-deriving is still required rather than early-returning, because the
  // output extent can change (window resize) and the target must follow.
  const uint32_t guest_width = tracked.promoted_from_guest_width != 0
                                   ? tracked.promoted_from_guest_width
                                   : tracked.texture_width;
  const uint32_t guest_height = tracked.promoted_from_guest_height != 0
                                    ? tracked.promoted_from_guest_height
                                    : tracked.texture_height;
  const bool matched =
      has_role && FablePromotedRenderSurfaceExtent(guest_width, guest_height,
                                                   promoted_width,
                                                   promoted_height);

  // PROMOTION CENSUS — this decision was previously INVISIBLE, and the code
  // comment in fable_guest_display.cpp already warned that a silently
  // un-promoted target is "the worst kind" of failure because it has no log
  // line. Two separate resolution bugs were found by hand-diffing capture
  // framebuffer extents; neither needed to be that expensive. Say the decision
  // out loud, with its inputs, so the next run answers it directly.
  //
  // Temporary by intent — delete once the promotion path is trusted.
  //
  // LOG THE NO-ROLE CASE TOO. The first cut logged only `has_role`, and the
  // run that mattered came back WITHOUT the two targets I needed to see: a
  // resource that reaches here with rt=ds=false produces no line at all, so
  // "never promoted" and "never considered" were indistinguishable — which is
  // precisely the ambiguity the census exists to remove. Dedupe by
  // (address, extent, outcome) so a per-frame decode cannot flood it and the
  // cap measures DISTINCT decisions rather than call volume.
  {
    static std::atomic<uint32_t> logged_promotion_census{0};
    static std::mutex census_mutex;
    static std::set<uint64_t> census_seen;
    const uint64_t key = (static_cast<uint64_t>(tracked.guest_address) << 32) ^
                         (static_cast<uint64_t>(tracked.texture_width) << 18) ^
                         (static_cast<uint64_t>(tracked.texture_height) << 4) ^
                         (matched ? 2ull : 0ull) ^ (has_role ? 1ull : 0ull);
    bool fresh = false;
    {
      std::lock_guard<std::mutex> lock(census_mutex);
      fresh = census_seen.size() < 4096u && census_seen.insert(key).second;
    }
    if (fresh) {
      const uint32_t n =
          logged_promotion_census.fetch_add(1, std::memory_order_relaxed);
      if (n < 400u) {
        char outcome[64] = {};
        if (!has_role) {
          std::snprintf(outcome, sizeof(outcome), "NOT-CONSIDERED-no-rt-ds-role");
        } else if (matched) {
          std::snprintf(outcome, sizeof(outcome), "%ux%u", promoted_width,
                        promoted_height);
        } else {
          std::snprintf(outcome, sizeof(outcome), "NOT-PROMOTED-no-chain-match");
        }
        REXLOG_INFO(
            "Refii plume promotion census #{} guest=0x{:08X} kind={} rt={} "
            "ds={} extent={}x{} guest_src={}x{} -> {} live_image={}x{} fmt={}",
            n + 1u, tracked.guest_address,
            TrackedResourceKindName(tracked.kind),
            tracked.texture_render_target ? 1 : 0,
            tracked.texture_depth_target ? 1 : 0, tracked.texture_width,
            tracked.texture_height, guest_width, guest_height, outcome,
            tracked.guest_texture.image_width,
            tracked.guest_texture.image_height,
            static_cast<uint32_t>(tracked.texture_format));
      }
    }
  }

  if (!matched) {
    return;
  }
  // Record what the guest asked for BEFORE overwriting it — this is the
  // denominator of the one true scale factor (see the field comment). Written
  // from guest_width/height, NOT texture_width: on a re-promotion the latter
  // already holds a HOST extent, and copying it here would silently make the
  // scale denominator a host number. Once set, this keeps its original value.
  tracked.promoted_from_guest_width = guest_width;
  tracked.promoted_from_guest_height = guest_height;
  tracked.texture_width = promoted_width;
  tracked.texture_height = promoted_height;
  RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
}

void PromoteFableParentBackedTextureProxyToOutputExtent(
    const RendererTrackedResourceSnapshot& snapshot,
    PlumeTrackedResource& tracked) {
  if (snapshot.kind != RendererTrackedResourceKind::kTexture ||
      tracked.parent_address == 0 || tracked.texture_guest_base != 0 ||
      tracked.texture_guest_mips != 0) {
    return;
  }

  // IDEMPOTENT, for the same reason as PromoteFableRenderSurfaceToOutputExtent
  // (2026-08-04). This promoter runs back-to-back with that one on every
  // decode, so without this guard it would read a texture_width the sibling
  // just promoted and copy that HOST value into promoted_from_guest_width —
  // silently making the denominator of the one scale factor a host number and
  // compounding the scale on every subsequent decode. Always promote FROM the
  // recorded guest extent, and never overwrite it once set.
  const uint32_t guest_width = tracked.promoted_from_guest_width != 0
                                   ? tracked.promoted_from_guest_width
                                   : tracked.texture_width;
  const uint32_t guest_height = tracked.promoted_from_guest_height != 0
                                    ? tracked.promoted_from_guest_height
                                    : tracked.texture_height;

  uint32_t promoted_width = 0;
  uint32_t promoted_height = 0;
  if (!FablePromotedRenderSurfaceExtent(guest_width, guest_height,
                                        promoted_width, promoted_height)) {
    return;
  }

  tracked.promoted_from_guest_width = guest_width;
  tracked.promoted_from_guest_height = guest_height;
  tracked.texture_width = promoted_width;
  tracked.texture_height = promoted_height;
  RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
}

bool FableTextureUploadLooksLikeDecodedRgba(
    const RendererTrackedResourceSnapshot& snapshot,
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch,
    uint32_t width, uint32_t height, uint32_t depth) {
  namespace xenos = rex::graphics::xenos;
  if (snapshot.kind != RendererTrackedResourceKind::kTexture ||
      snapshot.upload_data.empty() || width == 0 || height == 0 ||
      depth == 0) {
    return false;
  }

  switch (fetch.format) {
    case xenos::TextureFormat::k_DXT2_3:
    case xenos::TextureFormat::k_DXT4_5:
    case xenos::TextureFormat::k_DXT2_3_AS_16_16_16_16:
    case xenos::TextureFormat::k_DXT4_5_AS_16_16_16_16:
      break;
    default:
      return false;
  }

  const uint64_t rgba_bytes =
      uint64_t(width) * uint64_t(height) * uint64_t(depth) * 4ull;
  return rgba_bytes != 0 && snapshot.upload_data.size() >= rgba_bytes;
}

plume::RenderFormat DecodeFableDepthSurfaceFormat(
    const RendererTrackedSurfaceSnapshot& surface,
    uint32_t surface_d3d_format) {
  const uint32_t d3d_format =
      surface_d3d_format != 0 ? (surface_d3d_format & 0x3Fu)
                              : (surface.d3d_format & 0x3Fu);
  return ConvertFableD3DDepthFormat(d3d_format);
}

void DecodeFableTextureMetadata(const RendererTrackedResourceSnapshot& snapshot,
                                PlumeTrackedResource& tracked) {
  tracked.kind = snapshot.kind;
  tracked.guest_address = snapshot.guest_address;
  tracked.parent_address = snapshot.parent_address;
  if (snapshot.content_channel != FableGuestContentChannel::kNone) {
    tracked.content_channel = snapshot.content_channel;
  }
  tracked.size = IsSurfaceResource(snapshot.kind) ? snapshot.surface.byte_size
                                                  : snapshot.texture.byte_size;
  tracked.stride_or_format =
      IsSurfaceResource(snapshot.kind) ? snapshot.surface.pitch_msaa : 0;
  tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
  tracked.texture_width = 1;
  tracked.texture_height = 1;
  tracked.texture_depth = 1;
  tracked.texture_mip_levels = 1;
  tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
  tracked.texture_view_dimension = plume::RenderTextureViewDimension::TEXTURE_2D;

  namespace xenos = rex::graphics::xenos;

  if (IsSurfaceResource(snapshot.kind)) {
    const RendererTrackedSurfaceSnapshot& surface = snapshot.surface;
    const uint32_t packed_extent = surface.packed_extent;
    const uint32_t byte_size = surface.byte_size;
    const uint32_t render_target_info = surface.render_target_info;
    const uint32_t depth_stencil_info = surface.depth_stencil_info;
    if (packed_extent != 0) {
      uint32_t decoded_width = 0;
      uint32_t decoded_height = 0;
      uint32_t decoded_depth = 1;
      if (DecodeFableSurfaceExtentFromPacked(
              packed_extent, byte_size, decoded_width,
              decoded_height, &decoded_depth)) {
        tracked.texture_width = ClampGuestTextureExtent(decoded_width);
        tracked.texture_height = ClampGuestTextureExtent(decoded_height);
        tracked.texture_depth = ClampGuestTextureExtent(decoded_depth);
      }
    }
    // NO byte-size fallback. `packed_extent == 0` is NEVER "extent unknown" —
    // IDA proves it has three legitimate meanings, none of which is a licence
    // to invent a size:
    //   1. The resource is not a 2D image at all. XGSetVertexBufferHeader
    //      (0x8300BD38), XGSetIndexBufferHeader (0x8300BDD0) and
    //      XGSetConstantBufferHeader (0x8300BE60) all memset(0) and never
    //      write +0x24.
    //   2. The surface is a VIEW INTO A TEXTURE — GetSurfaceLevel
    //      (0x82B98860) writes only +0x00/+0x04/+0x14/+0x18/+0x1C, and the
    //      extent lives in the PARENT at +0x18. The guest dispatches on
    //      `Common & 0x40000000` (D3DSurface_GetDesc 0x821FA070) to find it.
    //   3. The surface is genuinely 1x1 — both encodings store `dim - 1`, so
    //      a zero word decodes to 1x1. The 1x1 defaults above are correct.
    //
    // What we used to substitute was `surface.byte_size`, which
    // D3D_SetSurfaceHeader (0x821F5028) writes at +0x2C as
    // `XGSurfaceSize() * 0x1400` — an EDRAM BYTE COUNT. It is not a width
    // under any reading, and clamping it to 4096 laundered the nonsense into
    // a legal-looking extent instead of rejecting it.
    if (tracked.texture_depth == 0) {
      tracked.texture_depth = 1;
    }
    tracked.texture_mip_levels = 1;
    const uint32_t surface_d3d_format =
        surface.d3d_format != 0
            ? (surface.d3d_format & 0x3Fu)
            : (depth_stencil_info != 0 ? (depth_stencil_info & 0x3Fu)
                                       : (render_target_info & 0x3Fu));
    tracked.texture_guest_format = surface_d3d_format;
    if (IsDepthSurfaceResource(snapshot.kind)) {
      tracked.texture_format =
          DecodeFableDepthSurfaceFormat(surface, surface_d3d_format);
    } else {
      const auto color_format =
          static_cast<xenos::ColorRenderTargetFormat>(
              (render_target_info >> 16u) & 0xFu);
      tracked.texture_format = ConvertXenosColorRenderTargetFormat(color_format);
    }
    if (!IsDepthSurfaceResource(snapshot.kind) &&
        !IsUsablePlumeColorTargetFormat(tracked.texture_format)) {
      tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
    }
    tracked.texture_render_target =
        snapshot.kind == RendererTrackedResourceKind::kSurface;
    tracked.texture_depth_target =
        snapshot.kind == RendererTrackedResourceKind::kDepthSurface;
    PromoteFableRenderSurfaceToOutputExtent(tracked);
    RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
    return;
  }

  const std::array<uint32_t, 6> fetch_dwords =
      PreferredTextureFetchDwords(snapshot.texture);
  tracked.texture_fetch_dwords = fetch_dwords;

  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];

  // *** THE TYPE FIELD IS STATED. HONOUR IT. ***
  //
  // Enum: kInvalidTexture=0, kInvalidVertex=1, kTexture=2, kVertex=3 — and the
  // guest writes those low 2 bits unconditionally at creation:
  //   D3D_SetTextureHeader      0x821FB650:  821fb930  ori r7,  r7,  2
  //   XGSetVertexBufferHeader   0x8300BD38:  8300bda8  ori r10, r28, 3
  // They are a type tag, never address bits: XGOffsetResourceAddress
  // (0x8225FD6C) strips them, relocates, and re-inserts them unchanged.
  //
  // We used to also accept kInvalidTexture — i.e. a slot whose dword0 is
  // simply UNWRITTEN — and then decode whatever stale dwords happened to sit
  // in it. That is the whole 10.1 GB bug:
  //
  // The 32 fetch slots are SHARED. D3DDevice_UnsetAll (0x82BA23E8) states the
  // partition — samplers 0..25 (`cmplwi cr6, r30, 0x1A`), vertex streams
  // 0..15 mapped onto constants 26..31 (`cmplwi cr6, r30, 0x10`), growing
  // DOWNWARD. So slot 31 is not a texture slot at all: it is vertex streams
  // 0-2. When stream 2 is unbound, dword0 reads 0 = kInvalidTexture and we
  // sailed past this gate, then read stream 1's ADDRESS dword as the size
  // union and stream 0's SIZE dword as the dimension field.
  //
  // Proof, from capture refii_plume_frame9173: the 10.1 GB 1540x1811x972
  // image reconstructs to a size-union dword of 0xF2F89603 — low bits 0b11,
  // the vertex marker. A 64000-byte vertex buffer packs to dword1 0x1000FA02,
  // which this decode turns into "64003x1" — exactly what the log reported.
  //
  // Four other fetch-decode sites already gate on == kTexture. This one now
  // matches them.
  if (fetch.type != xenos::FetchConstantType::kTexture) {
    return;
  }

  tracked.texture_guest_base = fetch.base_address << 12;
  tracked.texture_guest_mips = fetch.mip_address << 12;
  tracked.texture_guest_pitch = fetch.pitch << 5;
  tracked.texture_guest_format = static_cast<uint32_t>(fetch.format);
  tracked.texture_guest_dimension = static_cast<uint32_t>(fetch.dimension);
  tracked.texture_guest_tiled = fetch.tiled != 0;
  tracked.texture_format = ConvertXenosTextureFormat(fetch.format);

  const bool cube = fetch.dimension == xenos::DataDimension::kCube;
  if (fetch.dimension == xenos::DataDimension::k1D) {
    // Fable commonly binds "line" textures such as title/menu strips through
    // shader paths that are compiled as Texture2D. Keep the guest dimension for
    // identity checks, but allocate them as 2D images with height 1 so the view
    // type matches the generated shader ABI on Vulkan/D3D12.
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
  } else if (fetch.dimension == xenos::DataDimension::k3D) {
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_3D;
  } else {
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
  }
  tracked.texture_view_dimension =
      ViewDimensionForTextureDimension(tracked.texture_dimension, cube);

  if (fetch.dimension == xenos::DataDimension::k1D) {
    tracked.texture_width =
        ClampGuestTextureExtent(fetch.size_1d.width + 1u);
  } else if (fetch.dimension == xenos::DataDimension::k3D) {
    tracked.texture_width =
        ClampGuestTextureExtent(fetch.size_3d.width + 1u);
    tracked.texture_height =
        ClampGuestTextureExtent(fetch.size_3d.height + 1u);
    tracked.texture_depth =
        ClampGuestTextureExtent(fetch.size_3d.depth + 1u);
  } else {
    tracked.texture_width =
        ClampGuestTextureExtent(fetch.size_2d.width + 1u);
    tracked.texture_height =
        ClampGuestTextureExtent(fetch.size_2d.height + 1u);
    tracked.texture_depth = 1;
  }

  tracked.texture_mip_levels =
      fetch.mip_address != 0 && fetch.mip_max_level >= fetch.mip_min_level
          ? std::max<uint32_t>(
                uint32_t(fetch.mip_max_level - fetch.mip_min_level + 1), 1u)
          : 1u;
  // Split-mip fallback: the captured upload is the mip-chain image at this
  // level (base/mip0 blank in guest RAM). The replacement IS that image —
  // create it at the level's dimensions, exactly what the console samples
  // under the min-level clamp.
  if (snapshot.upload_data_mip_level > 0) {
    const uint32_t level = std::min(snapshot.upload_data_mip_level, 12u);
    tracked.texture_width =
        std::max<uint32_t>(1u, tracked.texture_width >> level);
    tracked.texture_height =
        std::max<uint32_t>(1u, tracked.texture_height >> level);
    tracked.texture_mip_levels = 1u;
  }
  if (FableTextureUploadLooksLikeDecodedRgba(
          snapshot, fetch, tracked.texture_width, tracked.texture_height,
          tracked.texture_depth)) {
    tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
    tracked.texture_mip_levels = 1u;
  }
  // HOST-SIDE NATIVE RESOLUTION (wired 2026-07-29).
  //
  // This call was MISSING. PromoteFableRenderSurfaceToOutputExtent and its
  // predicate were fully implemented but had no caller anywhere in the tree --
  // eight references, all inside this file, not even a header declaration --
  // while its sibling PromoteFableParentBackedTextureProxyToOutputExtent below
  // was wired. So render and depth surfaces were the ONLY class never promoted,
  // and every scene target stayed at the guest's 1280x720 no matter what
  // resolution was requested.
  //
  // This is the correct place to own resolution: the GUEST keeps its 1280x720
  // coordinate space and never learns otherwise, so its EDRAM tiling presets
  // (0x834BE010, hardcoded 720p with no 1080p entry), tile rects, predication
  // and resolve rects all stay exactly as the binary expects. Only OUR host
  // targets grow. A guest-side patch was written and reverted the same day for
  // precisely that reason -- see the note in d3d_pass_through.cpp.
  //
  // Guest->host coordinates are already handled: ScaleGuestViewportToCurrentDraw
  // Surface on the observer path, and vp_scale_x/y in the GTL executor, which
  // scales only when the framebuffer exceeds 1280x720 AND the draw's viewport is
  // native-or-smaller, so an already-output-space viewport is not double-scaled.
  PromoteFableRenderSurfaceToOutputExtent(tracked);
  PromoteFableParentBackedTextureProxyToOutputExtent(snapshot, tracked);
  RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
}

std::unique_ptr<plume::RenderTexture> CreateTextureForTrackedResource(
    plume::RenderDevice* device, const RendererTrackedResourceSnapshot& snapshot,
    PlumeTrackedResource& tracked) {
  if (!device || !IsImageResource(snapshot.kind)) {
    return nullptr;
  }

  bool requested_color_target =
      snapshot.kind == RendererTrackedResourceKind::kTexture &&
      tracked.texture_render_target && !tracked.texture_depth_target;
  // ESTABLISH THE ROLE BEFORE DECODING (2026-08-04).
  //
  // DecodeFableTextureMetadata calls PromoteFableRenderSurfaceToOutputExtent,
  // whose gate reads these two flags — so they must already be correct when it
  // runs. They used to be assigned AFTER the decode, which meant a brand-new
  // resource always presented role=false and could NEVER promote at creation.
  // Promotion could only land on a later re-decode, once the flags persisted
  // from the previous sync — and by then the image existed at the guest extent,
  // which is exactly how the metadata/image divergence in
  // [[refii-live-image-vs-metadata-extent-divergence]] was manufactured. Decide
  // the role first, create at the right size the first time.
  tracked.texture_render_target =
      snapshot.kind == RendererTrackedResourceKind::kSurface ||
      requested_color_target;
  tracked.texture_depth_target =
      snapshot.kind == RendererTrackedResourceKind::kDepthSurface;
  DecodeFableTextureMetadata(snapshot, tracked);
  // Xenos can't color-render into block-compressed memory — a color-target
  // request against decoded BC metadata is a stale alias and must not survive
  // (it would force an RGBA8 image and upload the DXT payload as noise).
  // Re-evaluated after the decode because it needs the decoded format; when it
  // fires the role narrows back to whatever the KIND alone justifies.
  if (requested_color_target &&
      IsBlockCompressedFormat(tracked.texture_format)) {
    requested_color_target = false;
    tracked.texture_render_target =
        snapshot.kind == RendererTrackedResourceKind::kSurface;
  }

  if (snapshot.kind == RendererTrackedResourceKind::kSurface &&
      snapshot.surface.packed_extent != 0) {
    uint32_t surface_width = 0;
    uint32_t surface_height = 0;
    if (DecodeFableSurfaceExtentFromPacked(snapshot.surface.packed_extent,
                                           snapshot.surface.byte_size,
                                           surface_width, surface_height) &&
        !IsPlausibleFableColorRenderTargetExtent(surface_width,
                                                surface_height)) {
      return nullptr;
    }
  }

  plume::RenderTextureDesc desc;
  bool creating_color_target = false;
  if (IsDepthSurfaceResource(snapshot.kind)) {
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
    tracked.texture_view_dimension =
        plume::RenderTextureViewDimension::TEXTURE_2D;
    tracked.texture_depth = 1;
    if (!plume::RenderFormatIsDepth(tracked.texture_format)) {
      tracked.texture_format =
          DecodeFableDepthSurfaceFormat(snapshot.surface,
                                        tracked.texture_guest_format);
    }
    if (!IsUsablePlumeTextureFormat(tracked.texture_format) ||
        !plume::RenderFormatIsDepth(tracked.texture_format)) {
      tracked.texture_format = plume::RenderFormat::D32_FLOAT_S8_UINT;
    }
    PromoteFableRenderSurfaceToOutputExtent(tracked);
    RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
    desc = plume::RenderTextureDesc::DepthTarget(
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
        PlumeGuestTextureFormat(tracked));
  } else if (snapshot.kind == RendererTrackedResourceKind::kSurface) {
    if (!IsUsablePlumeColorTargetFormat(tracked.texture_format)) {
      tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
    }
    PromoteFableRenderSurfaceToOutputExtent(tracked);
    RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
    desc = plume::RenderTextureDesc::ColorTarget(
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
        PlumeGuestTextureFormat(tracked));
    creating_color_target = true;
  } else if (requested_color_target &&
             tracked.texture_dimension ==
                 plume::RenderTextureDimension::TEXTURE_2D &&
             tracked.texture_view_dimension ==
                 plume::RenderTextureViewDimension::TEXTURE_2D) {
    if (!IsUsablePlumeColorTargetFormat(tracked.texture_format)) {
      tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
    }
    tracked.texture_depth = 1;
    RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
    desc = plume::RenderTextureDesc::ColorTarget(
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
        PlumeGuestTextureFormat(tracked));
    creating_color_target = true;
  } else if (tracked.texture_view_dimension ==
             plume::RenderTextureViewDimension::TEXTURE_CUBE) {
    desc = plume::RenderTextureDesc::Texture(
        plume::RenderTextureDimension::TEXTURE_2D,
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked), 1, 1,
        6, PlumeGuestTextureFormat(tracked),
        plume::RenderTextureFlag::CUBE);
  } else if (tracked.texture_dimension ==
             plume::RenderTextureDimension::TEXTURE_3D) {
    desc = plume::RenderTextureDesc::Texture3D(
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
        PlumeGuestTextureDepth(tracked), 1, PlumeGuestTextureFormat(tracked));
  } else if (tracked.texture_dimension ==
             plume::RenderTextureDimension::TEXTURE_1D) {
    desc = plume::RenderTextureDesc::Texture1D(
        PlumeGuestTextureWidth(tracked), 1, PlumeGuestTextureFormat(tracked));
  } else {
    desc = plume::RenderTextureDesc::Texture2D(
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked), 1,
        PlumeGuestTextureFormat(tracked));
  }

  // The color-target request can be refused by the dimension gates above.
  // The tracked flags must describe the texture actually being created, not
  // the request — a framebuffer built on a stale request bool asserts in
  // VulkanFramebuffer against the sampled-only image.
  tracked.texture_host_color_target = creating_color_target;
  if (!creating_color_target && !IsDepthSurfaceResource(snapshot.kind)) {
    tracked.texture_render_target = false;
  }
  RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);

  // LAST-CHANCE ALLOCATION GATE — applies to EVERY resource kind.
  //
  // The only prior validity check (above) fires solely for kSurface with a
  // non-zero packed extent, so kTexture and kDepthSurface — the entire
  // fetch-decoded family — reached vmaCreateImage completely ungated. That is
  // how a 1540x1811x972 / 10.1 GB image was allocated from a mis-decoded
  // descriptor. Reject here rather than clamp: a resource whose extent cannot
  // be established is not a 2D image, and inventing a plausible-looking size
  // for it is what produced the garbage in the first place.
  uint64_t allocation_bytes = 0;
  if (!ValidGuestImageAllocation(desc.format, desc.width, desc.height,
                                 desc.depth, desc.arraySize,
                                 &allocation_bytes)) {
    static std::atomic<uint32_t> rejected_allocations{0};
    const uint32_t n =
        rejected_allocations.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (n <= 32u) {
      REXLOG_WARN(
          "Refii plume REJECTED implausible image #{} guest=0x{:08X} kind={} "
          "{}x{}x{} array={} mips={} fmt={} => {:.1f} MB (ceiling {} MB) — "
          "extent could not have come from a 512 MB console, so the "
          "descriptor it was decoded from is not a 2D image",
          n, snapshot.guest_address, static_cast<int>(snapshot.kind),
          desc.width, desc.height, desc.depth, desc.arraySize, desc.mipLevels,
          static_cast<int>(desc.format),
          static_cast<double>(allocation_bytes) / (1024.0 * 1024.0),
          kMaxGuestImageBytes / (1024u * 1024u));
    }
    return nullptr;
  }
  return device->createTexture(desc);
}

void NameTrackedTexture(plume::RenderTexture* texture,
                        const RendererTrackedResourceSnapshot& snapshot,
                        PlumeTrackedResource& tracked) {
  if (!texture) {
    return;
  }

  char name[160] = {};
  std::snprintf(name, sizeof(name),
                "Refii Fable %s guest=0x%08X base=0x%08X %ux%ux%u mips=%u fmt=%u gfmt=%u pitch=%u %s%s%s",
                TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
                PlumeGuestTextureBaseAddress(tracked),
                PlumeGuestTextureWidth(tracked),
                PlumeGuestTextureHeight(tracked),
                PlumeGuestTextureDepth(tracked),
                tracked.texture_mip_levels,
                static_cast<uint32_t>(PlumeGuestTextureFormat(tracked)),
                PlumeGuestTextureGuestFormat(tracked),
                PlumeGuestTexturePitch(tracked),
                tracked.texture_guest_tiled ? "tiled " : "linear ",
                tracked.texture_upload_decoded ? "decoded " : "",
                tracked.texture_upload_pending ? "pending" : "");
  tracked.debug_summary = name;
  texture->setName(name);
}
#endif

}  // namespace refii::renderer
