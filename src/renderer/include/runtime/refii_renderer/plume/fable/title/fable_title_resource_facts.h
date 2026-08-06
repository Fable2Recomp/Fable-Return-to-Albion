#pragma once

#include "runtime/refii_renderer/plume/fable/title/fable_title_classification.h"
#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <cstdint>

namespace refii::renderer {

struct FableTitleTrackedResourceFactInputs {
  const FableTextureIdentity* identity = nullptr;
  bool sampleable_content = false;
  uint64_t visible_content_serial = 0;
};

FableTitleResourceFacts FableTitleResourceFactsForTrackedResource(
    const PlumeTrackedResource& resource,
    const FableTitleTrackedResourceFactInputs& inputs = {});

}  // namespace refii::renderer
