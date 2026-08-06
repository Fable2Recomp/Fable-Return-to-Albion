#include "runtime/refii_renderer/plume/fable/title/fable_title_resource_facts.h"

#include "runtime/refii_renderer/plume/fable/resource/fable_direct_resource_readiness.h"

namespace refii::renderer {

FableTitleResourceFacts FableTitleResourceFactsForTrackedResource(
    const PlumeTrackedResource& resource,
    const FableTitleTrackedResourceFactInputs& inputs) {
  const bool image_ready =
      IsImageResource(resource.kind) && PlumeGuestImageReady(resource);

  FableTitleResourceFacts facts;
  facts.image_ready = image_ready;
  facts.guest_address = resource.guest_address;
  facts.parent_address = resource.parent_address;
  facts.base_address = PlumeGuestTextureBaseAddress(resource);
  facts.width = PlumeGuestTextureWidth(resource);
  facts.height = PlumeGuestTextureHeight(resource);
  facts.format = PlumeGuestTextureGuestFormat(resource);
  facts.texture_render_target = resource.texture_render_target;
  facts.texture_depth_target = resource.texture_depth_target;
  facts.texture_upload_has_source = resource.texture_upload_has_source;
  facts.upload_generation = resource.upload_generation;
  facts.texture_upload_hash = resource.texture_upload_hash;
  facts.texture_upload_size = resource.texture_upload_size;
  facts.debug_first_nonzero_offset = resource.debug_first_nonzero_offset;
  facts.meaningful_upload =
      FableTrackedResourceHasMeaningfulTextureUpload(resource);
  facts.blank_uploaded_sample_proxy =
      image_ready && FableTrackedResourceIsBlankUploadedSampleProxy(resource);
  facts.sampleable_content = image_ready && inputs.sampleable_content;
  facts.gpu_produced_content =
      image_ready && FableTrackedResourceHasGpuProducedContent(resource);
  facts.visible_content_serial =
      image_ready ? inputs.visible_content_serial : 0;
  facts.draw_content_serial = image_ready ? resource.draw_content_serial : 0;
  facts.clear_content_serial = image_ready ? resource.clear_content_serial : 0;
  facts.identity = inputs.identity;
  return facts;
}

}  // namespace refii::renderer
