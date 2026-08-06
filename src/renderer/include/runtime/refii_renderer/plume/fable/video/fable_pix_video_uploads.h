#pragma once

#include "runtime/refii_renderer/plume/fable/video/fable_video_constants.h"
#include "runtime/refii_renderer/hooks/resource/fable_observer_resources.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

constexpr uint32_t kFablePixVideoPlaneCount = 3;
constexpr uint32_t kFablePixVideoFetchConstantDwords =
    kFableVideoFetchConstantDwords;

bool FablePixVideoInlineUploadSourceReadable(uint8_t* base,
                                             uint32_t base_address,
                                             uint32_t byte_size);

RendererInlineTextureUploadSnapshot FablePixVideoBuildInlineUploadSnapshot(
    uint32_t texture_handle,
    const std::array<uint32_t, 6>& fetch_dwords,
    uint32_t base_address,
    uint32_t byte_size,
    uint8_t* base,
    uint64_t upload_serial);

void FablePixVideoBindInlineUploadPlanes(
    RendererDrawEventSnapshot& event,
    uint32_t texture_handle,
    const std::array<uint32_t, kFablePixVideoFetchConstantDwords>& fetch_dwords,
    const RendererInlineTextureUploadSnapshot& upload);

FableGuestResourceSnapshot FablePixVideoBuildInlineUploadResourceSnapshot(
    const RendererInlineTextureUploadSnapshot& upload);

bool FablePixVideoApplyInlineUploadToResource(
    RendererResourceRecord& record,
    const RendererInlineTextureUploadSnapshot& upload);

}  // namespace refii::renderer
