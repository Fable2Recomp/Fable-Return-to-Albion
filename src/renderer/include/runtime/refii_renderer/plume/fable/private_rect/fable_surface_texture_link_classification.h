#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"

namespace refii::renderer {

bool DirectSurfaceTextureLinkShouldPublishObjectMirror(
    const FableSurfaceTextureLink& link);

bool DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(
    const FableSurfaceTextureLink& link);

bool DirectSurfaceTextureLinkIsBeginVerticesTitleStripUpload(
    const FableSurfaceTextureLink& link);

bool DirectSurfaceTextureLinkIsUnresolvedSurfaceViewIdentity(
    const FableSurfaceTextureLink& link);

bool DirectSurfaceTextureLinkRequiresExactWrapperSourceForMutableHeaderResolve(
    const FableSurfaceTextureLink& link);

bool DirectSurfaceTextureLinkExactWrapperOwnedByCommand(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link);

bool DirectSurfaceTextureLinkWrapperOwnedByCommand(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link);

}  // namespace refii::renderer
