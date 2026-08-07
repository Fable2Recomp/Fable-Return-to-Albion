#include "runtime/plume/resource/plume_resource_manager_internal.h"

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
const char* TrackedResourceKindName(RendererTrackedResourceKind kind) {
  switch (kind) {
    case RendererTrackedResourceKind::kUnknown:
      return "unknown";
    case RendererTrackedResourceKind::kTexture:
      return "texture";
    case RendererTrackedResourceKind::kSurface:
      return "surface";
    case RendererTrackedResourceKind::kDepthSurface:
      return "depth_surface";
    case RendererTrackedResourceKind::kVertexBuffer:
      return "vertex_buffer";
    case RendererTrackedResourceKind::kIndexBuffer:
      return "index_buffer";
    case RendererTrackedResourceKind::kVertexDeclaration:
      return "vertex_declaration";
    case RendererTrackedResourceKind::kVertexShader:
      return "vertex_shader";
    case RendererTrackedResourceKind::kPixelShader:
      return "pixel_shader";
  }

  return "unknown";
}

bool IsBufferResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kVertexBuffer ||
         kind == RendererTrackedResourceKind::kIndexBuffer;
}

bool IsShaderResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kVertexShader ||
         kind == RendererTrackedResourceKind::kPixelShader;
}

bool IsDeclarationResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kVertexDeclaration;
}

bool IsTextureResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kTexture;
}

bool IsSurfaceResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kSurface ||
         kind == RendererTrackedResourceKind::kDepthSurface;
}

bool IsDepthSurfaceResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kDepthSurface;
}

bool IsImageResource(RendererTrackedResourceKind kind) {
  return IsTextureResource(kind) || IsSurfaceResource(kind);
}
#endif

}  // namespace refii::renderer
