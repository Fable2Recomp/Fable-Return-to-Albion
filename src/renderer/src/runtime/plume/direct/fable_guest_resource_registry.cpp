#include "runtime/plume/direct/fable_guest_resource_registry.h"

#include <algorithm>
#include <mutex>

namespace refii::renderer {
namespace {

FableGuestResourceRegistry g_fable_guest_resources;
std::recursive_mutex g_fable_guest_resource_registry_mutex;

bool SurfaceTextureLinkRequiresExactWrapperLookup(
    const FableSurfaceTextureLink& link) {
  switch (link.copy_symbol_address) {
    case 0x8228ACB8u:
    case 0x822C4290u:
    case 0x822C4950u:
      return link.copy_call_serial != 0 || link.copy_context_key != 0;
    default:
      return false;
  }
}

bool ShaderObjectsEquivalentByIdentity(uint32_t lhs, uint32_t rhs,
                                       FableGuestResourceKind kind) {
  if (lhs == rhs) {
    return true;
  }
  if (lhs == 0 || rhs == 0) {
    return false;
  }

  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto lhs_it = registry.shader_identities_by_object.find(lhs);
  const auto rhs_it = registry.shader_identities_by_object.find(rhs);
  if (lhs_it == registry.shader_identities_by_object.end() ||
      rhs_it == registry.shader_identities_by_object.end()) {
    return false;
  }

  const FableShaderIdentity& lhs_identity = lhs_it->second;
  const FableShaderIdentity& rhs_identity = rhs_it->second;
  return lhs_identity.kind == kind && rhs_identity.kind == kind &&
         lhs_identity.bytecode_hash != 0 &&
         lhs_identity.bytecode_hash == rhs_identity.bytecode_hash;
}

bool SamePrivateTextureCopyActionIdentity(
    const FablePrivateTextureCopyAction& lhs,
    const FablePrivateTextureCopyAction& rhs) {
  return lhs.source_slot == rhs.source_slot &&
         lhs.source_texture == rhs.source_texture &&
         lhs.source_base_address == rhs.source_base_address &&
         lhs.source_width == rhs.source_width &&
         lhs.source_height == rhs.source_height &&
         lhs.source_format == rhs.source_format &&
         FableDirectTextureFetchKey(lhs.source_fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.source_fetch_dwords) &&
         lhs.backing_texture == rhs.backing_texture &&
         lhs.backing_base_address == rhs.backing_base_address &&
         lhs.backing_width == rhs.backing_width &&
         lhs.backing_height == rhs.backing_height &&
         lhs.backing_format == rhs.backing_format &&
         FableDirectTextureFetchKey(lhs.backing_fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.backing_fetch_dwords) &&
         lhs.target_surface == rhs.target_surface &&
         lhs.target_width == rhs.target_width &&
         lhs.target_height == rhs.target_height &&
         lhs.target_format == rhs.target_format &&
         lhs.render_target == rhs.render_target &&
         lhs.depth_stencil == rhs.depth_stencil &&
         ShaderObjectsEquivalentByIdentity(
             lhs.vertex_shader, rhs.vertex_shader,
             FableGuestResourceKind::kVertexShader) &&
         ShaderObjectsEquivalentByIdentity(
             lhs.pixel_shader, rhs.pixel_shader,
             FableGuestResourceKind::kPixelShader) &&
         lhs.lr == rhs.lr &&
         lhs.wrapper_symbol_address == rhs.wrapper_symbol_address &&
         lhs.wrapper_context_key == rhs.wrapper_context_key &&
         lhs.wrapper_source_texture == rhs.wrapper_source_texture &&
         lhs.kind == rhs.kind &&
         lhs.rectangle_valid == rhs.rectangle_valid &&
         lhs.uv_override == rhs.uv_override &&
         lhs.rect_width == rhs.rect_width &&
         lhs.rect_height == rhs.rect_height &&
         lhs.source_x == rhs.source_x &&
         lhs.source_y == rhs.source_y &&
         lhs.u0 == rhs.u0 &&
         lhs.v0 == rhs.v0 &&
	         lhs.u1 == rhs.u1 &&
	         lhs.v1 == rhs.v1;
}

uint64_t TextureFetchBindingSlotKey(
    uint32_t texture_slot, const std::array<uint32_t, 6>& fetch_dwords) {
  const uint64_t fetch_key = FableDirectTextureFetchKey(fetch_dwords);
  if (texture_slot >= 32u || fetch_key == 0) {
    return 0;
  }
  return fetch_key ^
         ((static_cast<uint64_t>(texture_slot) + 0x9E3779B97F4A7C15ull)
          << 1u);
}

uint64_t TextureFetchResourceSlotKey(uint32_t texture_slot, uint32_t resource) {
  if (texture_slot >= 32u || resource == 0) {
    return 0;
  }
  return static_cast<uint64_t>(resource) ^
         ((static_cast<uint64_t>(texture_slot) + 0x9E3779B97F4A7C15ull)
          << 1u);
}

bool SameTextureFetchBindingAction(
    const FableTextureFetchBindingAction& lhs,
    const FableTextureFetchBindingAction& rhs) {
  return lhs.call_serial == rhs.call_serial &&
         lhs.context_key == rhs.context_key &&
         lhs.header_action_serial == rhs.header_action_serial &&
         lhs.header_context_key == rhs.header_context_key &&
         lhs.texture_slot == rhs.texture_slot &&
         lhs.texture_object == rhs.texture_object &&
         lhs.parent_texture == rhs.parent_texture &&
         lhs.source_address == rhs.source_address &&
         lhs.source_header == rhs.source_header &&
         lhs.base_address == rhs.base_address &&
         lhs.width == rhs.width &&
         lhs.height == rhs.height &&
         lhs.format == rhs.format &&
         lhs.symbol_address == rhs.symbol_address &&
         lhs.header_symbol_address == rhs.header_symbol_address &&
         lhs.lr == rhs.lr &&
         lhs.header_stack_scratch == rhs.header_stack_scratch &&
         lhs.header_fetch_payload_match == rhs.header_fetch_payload_match &&
         FableDirectTextureFetchKey(lhs.fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.fetch_dwords);
}

bool SameTextureFetchResourceLink(
    const FableTextureFetchResourceLink& lhs,
    const FableTextureFetchResourceLink& rhs) {
  return lhs.binding_serial == rhs.binding_serial &&
         lhs.binding_call_serial == rhs.binding_call_serial &&
         lhs.binding_context_key == rhs.binding_context_key &&
         lhs.header_action_serial == rhs.header_action_serial &&
         lhs.header_context_key == rhs.header_context_key &&
         lhs.texture_slot == rhs.texture_slot &&
         lhs.texture_resource == rhs.texture_resource &&
         lhs.parent_texture == rhs.parent_texture &&
         lhs.base_address == rhs.base_address && lhs.width == rhs.width &&
         lhs.height == rhs.height && lhs.format == rhs.format &&
         lhs.render_target == rhs.render_target &&
         lhs.depth_stencil == rhs.depth_stencil &&
         ShaderObjectsEquivalentByIdentity(
             lhs.vertex_shader, rhs.vertex_shader,
             FableGuestResourceKind::kVertexShader) &&
         ShaderObjectsEquivalentByIdentity(
             lhs.pixel_shader, rhs.pixel_shader,
             FableGuestResourceKind::kPixelShader) &&
         lhs.header_fetch_payload_match == rhs.header_fetch_payload_match &&
         FableDirectTextureFetchKey(lhs.fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.fetch_dwords);
}

bool TextureFetchResourceLinkReferencesResource(
    const FableTextureFetchResourceLink& link, uint32_t resource) {
  return resource != 0 &&
         (link.texture_resource == resource || link.parent_texture == resource ||
          link.base_address == resource || link.render_target == resource ||
          link.depth_stencil == resource);
}

const FableTextureFetchResourceLink* SelectVisibleTextureFetchResourceLink(
    const std::vector<FableTextureFetchResourceLink>* links,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot) {
  if (!links) {
    return nullptr;
  }
  const FableTextureFetchResourceLink* best = nullptr;
  for (const FableTextureFetchResourceLink& link : *links) {
    if (source_slot != 0xFFFFFFFFu && link.texture_slot != 0xFFFFFFFFu &&
        link.texture_slot != source_slot) {
      continue;
    }
    if (link.frame_index != 0 && frame_index != 0 &&
        link.frame_index > frame_index) {
      continue;
    }
    if (link.frame_index != 0 && frame_index != 0 &&
        link.frame_index == frame_index && link.draw_serial != 0 &&
        draw_serial != 0 && link.draw_serial > draw_serial) {
      continue;
    }
    if (!best || link.frame_index > best->frame_index ||
        (link.frame_index == best->frame_index &&
         link.draw_serial > best->draw_serial) ||
        (link.frame_index == best->frame_index &&
         link.draw_serial == best->draw_serial &&
         link.serial > best->serial)) {
      best = &link;
    }
  }
  return best;
}

bool SameTextureHeaderAction(const FableTextureHeaderAction& lhs,
                             const FableTextureHeaderAction& rhs) {
  return lhs.call_serial == rhs.call_serial &&
         lhs.context_key == rhs.context_key &&
         lhs.symbol_address == rhs.symbol_address && lhs.lr == rhs.lr &&
         lhs.texture_header == rhs.texture_header &&
         lhs.fetch_address == rhs.fetch_address &&
         lhs.texture_object == rhs.texture_object &&
         lhs.parent_texture == rhs.parent_texture &&
         lhs.base_address == rhs.base_address &&
         lhs.byte_size == rhs.byte_size &&
         lhs.width == rhs.width &&
         lhs.height == rhs.height &&
         lhs.format == rhs.format &&
         lhs.stack_scratch == rhs.stack_scratch &&
         FableDirectTextureFetchKey(lhs.fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.fetch_dwords);
}

bool HasTextureIdentityFetchDwords(
    const std::array<uint32_t, 6>& fetch_dwords) {
  for (uint32_t dword : fetch_dwords) {
    if (dword != 0) {
      return true;
    }
  }
  return false;
}

uint32_t NormalizeTextureFetchAddressDword(uint32_t value) {
  return ((((value >> 20u) + 512u) & 0x1000u) +
          (value & 0x1FFFF000u)) |
         (value & 0xFFFu);
}

std::array<uint32_t, 6> NormalizeTextureFetchKeyDwords(
    std::array<uint32_t, 6> dwords) {
  dwords[1] = NormalizeTextureFetchAddressDword(dwords[1]);
  dwords[5] = NormalizeTextureFetchAddressDword(dwords[5]);
  return dwords;
}

bool SameTextureIdentity(const FableTextureIdentity& lhs,
                         const FableTextureIdentity& rhs) {
  return lhs.object_texture == rhs.object_texture &&
         lhs.parent_texture == rhs.parent_texture &&
         lhs.base_address == rhs.base_address &&
         lhs.byte_size == rhs.byte_size && lhs.width == rhs.width &&
         lhs.height == rhs.height && lhs.format == rhs.format &&
         lhs.upload_generation == rhs.upload_generation &&
         lhs.meaningful_upload == rhs.meaningful_upload &&
         FableDirectTextureFetchKey(lhs.fetch_dwords) ==
		             FableDirectTextureFetchKey(rhs.fetch_dwords);
}

bool SameRegisteredSampleSourceIdentity(
    const FableRegisteredSampleSource& lhs,
    const FableRegisteredSampleSource& rhs) {
  return lhs.kind == rhs.kind && lhs.resource == rhs.resource &&
         lhs.base_address == rhs.base_address &&
         FableDirectTextureFetchKey(lhs.fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.fetch_dwords);
}

bool SurfaceTextureLinkShouldPrefer(const FableSurfaceTextureLink& candidate,
                                    const FableSurfaceTextureLink* current);
bool HasRegisteredSampleSource(
    const FableRegisteredSampleSource& sampled_source);
bool RepairSurfaceTextureLinkSampleIdentityFromAction(
    FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action);
bool RepairSurfaceTextureLinkSampleIdentityFromLink(
    FableSurfaceTextureLink& link,
    const FableSurfaceTextureLink& sample_link);

bool SameSurfaceTextureLinkIdentity(const FableSurfaceTextureLink& lhs,
                                    const FableSurfaceTextureLink& rhs) {
  return lhs.copy_call_serial == rhs.copy_call_serial &&
         lhs.copy_context_key == rhs.copy_context_key &&
         lhs.copy_symbol_address == rhs.copy_symbol_address &&
         lhs.source_slot == rhs.source_slot &&
         lhs.trusted_destination_edge == rhs.trusted_destination_edge &&
         lhs.texture == rhs.texture &&
         lhs.texture_base_address == rhs.texture_base_address &&
         lhs.texture_width == rhs.texture_width &&
         lhs.texture_height == rhs.texture_height &&
         lhs.texture_format == rhs.texture_format &&
         FableDirectTextureFetchKey(lhs.texture_fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.texture_fetch_dwords) &&
         lhs.source_surface == rhs.source_surface &&
         lhs.backing_texture == rhs.backing_texture &&
         lhs.backing_base_address == rhs.backing_base_address &&
         FableDirectTextureFetchKey(lhs.backing_fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.backing_fetch_dwords) &&
         lhs.render_target == rhs.render_target &&
         lhs.depth_stencil == rhs.depth_stencil &&
         ShaderObjectsEquivalentByIdentity(
             lhs.vertex_shader, rhs.vertex_shader,
             FableGuestResourceKind::kVertexShader) &&
         ShaderObjectsEquivalentByIdentity(
             lhs.pixel_shader, rhs.pixel_shader,
             FableGuestResourceKind::kPixelShader) &&
         lhs.wrapper_source_texture == rhs.wrapper_source_texture &&
         lhs.wrapper_source_exact == rhs.wrapper_source_exact &&
         lhs.wrapper_setup_globals == rhs.wrapper_setup_globals &&
         SameRegisteredSampleSourceIdentity(lhs.sampled_source,
                                            rhs.sampled_source) &&
         lhs.kind == rhs.kind;
}

bool SurfaceTextureLinkHasVertexShaderRoot(
    const FableSurfaceTextureLink& link) {
  return link.vertex_shader_root_call_serial != 0 ||
         link.vertex_shader_root_context_key != 0 ||
         link.vertex_shader_root_symbol_address != 0 ||
         link.vertex_shader_root_lr != 0;
}

bool SurfaceTextureLinkHasPixelShaderRoot(
    const FableSurfaceTextureLink& link) {
  return link.pixel_shader_root_call_serial != 0 ||
         link.pixel_shader_root_context_key != 0 ||
         link.pixel_shader_root_symbol_address != 0 ||
         link.pixel_shader_root_lr != 0;
}

bool SurfaceTextureLinkHasShaderRoots(const FableSurfaceTextureLink& link) {
  return SurfaceTextureLinkHasVertexShaderRoot(link) ||
         SurfaceTextureLinkHasPixelShaderRoot(link);
}

void CopyMissingSurfaceTextureLinkShaderRoots(
    FableSurfaceTextureLink& destination,
    const FableSurfaceTextureLink& source) {
  if (!SurfaceTextureLinkHasVertexShaderRoot(destination) &&
      SurfaceTextureLinkHasVertexShaderRoot(source)) {
    destination.vertex_shader_root_call_serial =
        source.vertex_shader_root_call_serial;
    destination.vertex_shader_root_context_key =
        source.vertex_shader_root_context_key;
    destination.vertex_shader_root_symbol_address =
        source.vertex_shader_root_symbol_address;
    destination.vertex_shader_root_lr = source.vertex_shader_root_lr;
  }
  if (!SurfaceTextureLinkHasPixelShaderRoot(destination) &&
      SurfaceTextureLinkHasPixelShaderRoot(source)) {
    destination.pixel_shader_root_call_serial =
        source.pixel_shader_root_call_serial;
    destination.pixel_shader_root_context_key =
        source.pixel_shader_root_context_key;
    destination.pixel_shader_root_symbol_address =
        source.pixel_shader_root_symbol_address;
    destination.pixel_shader_root_lr = source.pixel_shader_root_lr;
  }
}

bool PrivateTextureCopyActionHasVertexShaderRoot(
    const FablePrivateTextureCopyAction& action) {
  return action.vertex_shader_root_call_serial != 0 ||
         action.vertex_shader_root_context_key != 0 ||
         action.vertex_shader_root_symbol_address != 0 ||
         action.vertex_shader_root_lr != 0;
}

bool PrivateTextureCopyActionHasPixelShaderRoot(
    const FablePrivateTextureCopyAction& action) {
  return action.pixel_shader_root_call_serial != 0 ||
         action.pixel_shader_root_context_key != 0 ||
         action.pixel_shader_root_symbol_address != 0 ||
         action.pixel_shader_root_lr != 0;
}

void CopyMissingSurfaceTextureLinkShaderRootsFromAction(
    FableSurfaceTextureLink& destination,
    const FablePrivateTextureCopyAction& source) {
  if (destination.vertex_shader == 0 && source.vertex_shader != 0) {
    destination.vertex_shader = source.vertex_shader;
  }
  if (destination.pixel_shader == 0 && source.pixel_shader != 0) {
    destination.pixel_shader = source.pixel_shader;
  }
  if (!SurfaceTextureLinkHasVertexShaderRoot(destination) &&
      PrivateTextureCopyActionHasVertexShaderRoot(source)) {
    destination.vertex_shader_root_call_serial =
        source.vertex_shader_root_call_serial;
    destination.vertex_shader_root_context_key =
        source.vertex_shader_root_context_key;
    destination.vertex_shader_root_symbol_address =
        source.vertex_shader_root_symbol_address;
    destination.vertex_shader_root_lr = source.vertex_shader_root_lr;
  }
  if (!SurfaceTextureLinkHasPixelShaderRoot(destination) &&
      PrivateTextureCopyActionHasPixelShaderRoot(source)) {
    destination.pixel_shader_root_call_serial =
        source.pixel_shader_root_call_serial;
    destination.pixel_shader_root_context_key =
        source.pixel_shader_root_context_key;
    destination.pixel_shader_root_symbol_address =
        source.pixel_shader_root_symbol_address;
    destination.pixel_shader_root_lr = source.pixel_shader_root_lr;
  }
}

void EnrichSurfaceTextureLinkShaderRootsFromRegisteredActions(
    const FableGuestResourceRegistry& registry,
    FableSurfaceTextureLink& link) {
  if (link.copy_call_serial != 0) {
    const auto call_it =
        registry.private_texture_copy_action_by_wrapper_call.find(
            link.copy_call_serial);
	    if (call_it !=
	        registry.private_texture_copy_action_by_wrapper_call.end()) {
	      CopyMissingSurfaceTextureLinkShaderRootsFromAction(link,
	                                                         call_it->second);
	      RepairSurfaceTextureLinkSampleIdentityFromAction(link,
	                                                       call_it->second);
	    }
	  }
	  if (link.copy_context_key != 0) {
    const auto context_it =
        registry.private_texture_copy_action_by_wrapper_context.find(
            link.copy_context_key);
	    if (context_it !=
	        registry.private_texture_copy_action_by_wrapper_context.end()) {
	      CopyMissingSurfaceTextureLinkShaderRootsFromAction(link,
	                                                         context_it->second);
	      RepairSurfaceTextureLinkSampleIdentityFromAction(link,
	                                                       context_it->second);
	    }
	  }
	}

bool SurfaceTextureLinkMatchesPrivateCopyActionWrapper(
    const FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action) {
  if (link.copy_call_serial != 0 && action.wrapper_call_serial != 0 &&
      link.copy_call_serial == action.wrapper_call_serial) {
    return true;
  }
  return link.copy_context_key != 0 && action.wrapper_context_key != 0 &&
         link.copy_context_key == action.wrapper_context_key;
}

bool SurfaceTextureLinkMatchesPrivateCopyActionSource(
    const FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action) {
  if (link.source_slot != 0xFFFFFFFFu && action.source_slot != 0xFFFFFFFFu &&
      link.source_slot != action.source_slot) {
    return false;
  }
  if (link.texture != 0 && action.source_texture != 0 &&
      link.texture == action.source_texture) {
    return true;
  }
  if (link.texture_base_address != 0 && action.source_base_address != 0 &&
      link.texture_base_address == action.source_base_address) {
    return true;
  }
  const uint64_t link_fetch_key =
      FableDirectTextureFetchKey(link.texture_fetch_dwords);
  const uint64_t action_fetch_key =
      FableDirectTextureFetchKey(action.source_fetch_dwords);
  return link_fetch_key != 0 && action_fetch_key != 0 &&
         link_fetch_key == action_fetch_key;
}

bool SurfaceTextureLinkSampleSourceCanAdoptActionSample(
    const FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action) {
  if (!HasRegisteredSampleSource(action.sampled_source)) {
    return false;
  }
  if (!HasRegisteredSampleSource(link.sampled_source) ||
      link.sampled_source.content_serial == 0) {
    return true;
  }
  if (link.sampled_source.kind != action.sampled_source.kind) {
    return false;
  }
  if (link.sampled_source.resource != 0 &&
      action.sampled_source.resource != 0 &&
      link.sampled_source.resource != action.sampled_source.resource) {
    return false;
  }
  if (link.sampled_source.base_address != 0 &&
      action.sampled_source.base_address != 0 &&
      link.sampled_source.base_address != action.sampled_source.base_address) {
    return false;
  }
  const uint64_t link_fetch_key =
      FableDirectTextureFetchKey(link.sampled_source.fetch_dwords);
  const uint64_t action_fetch_key =
      FableDirectTextureFetchKey(action.sampled_source.fetch_dwords);
  if (link_fetch_key != 0 && action_fetch_key != 0 &&
      link_fetch_key != action_fetch_key) {
    return false;
  }
  return action.sampled_source.content_serial >
         link.sampled_source.content_serial;
}

bool SurfaceTextureLinkMatchesSurfaceTextureLinkSample(
    const FableSurfaceTextureLink& link,
    const FableSurfaceTextureLink& sample_link) {
  if (!HasRegisteredSampleSource(sample_link.sampled_source) ||
      link.source_surface == 0 || sample_link.source_surface == 0 ||
      link.source_surface != sample_link.source_surface) {
    return false;
  }
  if (link.copy_symbol_address != 0 && sample_link.copy_symbol_address != 0 &&
      link.copy_symbol_address != sample_link.copy_symbol_address) {
    return false;
  }
  const bool same_wrapper_call =
      link.copy_call_serial != 0 && sample_link.copy_call_serial != 0 &&
      link.copy_call_serial == sample_link.copy_call_serial;
  const bool same_wrapper_context =
      link.copy_context_key != 0 && sample_link.copy_context_key != 0 &&
      link.copy_context_key == sample_link.copy_context_key;
  if (!same_wrapper_call && !same_wrapper_context) {
    return false;
  }
  if (link.source_slot != 0xFFFFFFFFu &&
      sample_link.source_slot != 0xFFFFFFFFu &&
      link.source_slot != sample_link.source_slot) {
    return false;
  }
  const bool same_source_texture =
      link.texture != 0 && sample_link.texture != 0 &&
      link.texture == sample_link.texture;
  const bool same_source_base =
      link.texture_base_address != 0 &&
      sample_link.texture_base_address != 0 &&
      link.texture_base_address == sample_link.texture_base_address;
  const uint64_t link_fetch_key =
      FableDirectTextureFetchKey(link.texture_fetch_dwords);
  const uint64_t sample_fetch_key =
      FableDirectTextureFetchKey(sample_link.texture_fetch_dwords);
  const bool same_source_fetch =
      link_fetch_key != 0 && sample_fetch_key != 0 &&
      link_fetch_key == sample_fetch_key;
  if (!same_source_texture && !same_source_base && !same_source_fetch) {
    return false;
  }
  const bool same_kind =
      link.kind == sample_link.kind ||
      (link.kind != FablePrivateTextureCopyKind::kDepth &&
       sample_link.kind != FablePrivateTextureCopyKind::kDepth);
  if (!same_kind) {
    return false;
  }
  if (link.wrapper_source_texture != 0 &&
      sample_link.wrapper_source_texture != 0 &&
      link.wrapper_source_texture != sample_link.wrapper_source_texture) {
    return false;
  }
  return true;
}

bool SurfaceTextureLinkSampleSourceCanAdoptLinkSample(
    const FableSurfaceTextureLink& link,
    const FableSurfaceTextureLink& sample_link) {
  if (!HasRegisteredSampleSource(sample_link.sampled_source)) {
    return false;
  }
  if (!HasRegisteredSampleSource(link.sampled_source) ||
      link.sampled_source.content_serial == 0) {
    return true;
  }
  if (link.sampled_source.kind != sample_link.sampled_source.kind) {
    return false;
  }
  if (link.sampled_source.resource != 0 &&
      sample_link.sampled_source.resource != 0 &&
      link.sampled_source.resource != sample_link.sampled_source.resource) {
    return false;
  }
  if (link.sampled_source.base_address != 0 &&
      sample_link.sampled_source.base_address != 0 &&
      link.sampled_source.base_address != sample_link.sampled_source.base_address) {
    return false;
  }
  const uint64_t link_fetch_key =
      FableDirectTextureFetchKey(link.sampled_source.fetch_dwords);
  const uint64_t sample_fetch_key =
      FableDirectTextureFetchKey(sample_link.sampled_source.fetch_dwords);
  if (link_fetch_key != 0 && sample_fetch_key != 0 &&
      link_fetch_key != sample_fetch_key) {
    return false;
  }
  return sample_link.sampled_source.content_serial >
         link.sampled_source.content_serial;
}

bool RepairSurfaceTextureLinkSampleIdentityFromAction(
    FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action) {
  if (!SurfaceTextureLinkMatchesPrivateCopyActionWrapper(link, action) ||
      !SurfaceTextureLinkMatchesPrivateCopyActionSource(link, action) ||
      action.target_surface == 0 || link.source_surface != action.target_surface) {
    return false;
  }

  bool repaired = false;
  auto copy_if_zero = [&](uint32_t& destination, uint32_t source) {
    if (destination == 0 && source != 0) {
      destination = source;
      repaired = true;
    }
  };
  auto copy_fetch_if_zero =
      [&](std::array<uint32_t, 6>& destination,
          const std::array<uint32_t, 6>& source) {
    if (FableDirectTextureFetchKey(destination) == 0 &&
        FableDirectTextureFetchKey(source) != 0) {
      destination = source;
      repaired = true;
    }
  };

  copy_if_zero(link.backing_texture, action.backing_texture);
  copy_if_zero(link.backing_base_address, action.backing_base_address);
  copy_fetch_if_zero(link.backing_fetch_dwords, action.backing_fetch_dwords);
  if (action.backing_content_serial != 0 &&
      link.backing_content_serial < action.backing_content_serial) {
    link.backing_content_serial = action.backing_content_serial;
    repaired = true;
  }
  if (action.target_content_serial != 0 &&
      link.target_content_serial < action.target_content_serial) {
    link.target_content_serial = action.target_content_serial;
    repaired = true;
  }
  if (SurfaceTextureLinkSampleSourceCanAdoptActionSample(link, action)) {
    link.sampled_source = action.sampled_source;
    repaired = true;
  }
  if (link.target_content_serial != 0 &&
      (!HasRegisteredSampleSource(link.sampled_source) ||
       (link.sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface &&
        link.sampled_source.resource == link.source_surface &&
        link.sampled_source.content_serial < link.target_content_serial))) {
    link.sampled_source = {};
    link.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
    link.sampled_source.resource = link.source_surface;
    link.sampled_source.content_serial = link.target_content_serial;
    repaired = true;
  }
  return repaired;
}

bool RepairSurfaceTextureLinkSampleIdentityFromLink(
    FableSurfaceTextureLink& link,
    const FableSurfaceTextureLink& sample_link) {
  if (!SurfaceTextureLinkMatchesSurfaceTextureLinkSample(link, sample_link)) {
    return false;
  }

  bool repaired = false;
  if (sample_link.backing_content_serial != 0 &&
      link.backing_content_serial < sample_link.backing_content_serial) {
    link.backing_content_serial = sample_link.backing_content_serial;
    repaired = true;
  }
  if (sample_link.target_content_serial != 0 &&
      link.target_content_serial < sample_link.target_content_serial) {
    link.target_content_serial = sample_link.target_content_serial;
    repaired = true;
  }
  if (SurfaceTextureLinkSampleSourceCanAdoptLinkSample(link, sample_link)) {
    link.sampled_source = sample_link.sampled_source;
    repaired = true;
  }
  return repaired;
}

bool RepairSurfaceTextureLinkShaderRootsFromAction(
    FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action) {
  if (!SurfaceTextureLinkMatchesPrivateCopyActionWrapper(link, action)) {
    return false;
  }
  const uint32_t previous_vertex_shader = link.vertex_shader;
  const uint32_t previous_pixel_shader = link.pixel_shader;
  const uint64_t previous_vertex_shader_root_call_serial =
      link.vertex_shader_root_call_serial;
  const uint64_t previous_vertex_shader_root_context_key =
      link.vertex_shader_root_context_key;
  const uint32_t previous_vertex_shader_root_symbol_address =
      link.vertex_shader_root_symbol_address;
  const uint32_t previous_vertex_shader_root_lr =
      link.vertex_shader_root_lr;
  const uint64_t previous_pixel_shader_root_call_serial =
      link.pixel_shader_root_call_serial;
  const uint64_t previous_pixel_shader_root_context_key =
      link.pixel_shader_root_context_key;
  const uint32_t previous_pixel_shader_root_symbol_address =
      link.pixel_shader_root_symbol_address;
  const uint32_t previous_pixel_shader_root_lr = link.pixel_shader_root_lr;

  CopyMissingSurfaceTextureLinkShaderRootsFromAction(link, action);
  return link.vertex_shader != previous_vertex_shader ||
         link.pixel_shader != previous_pixel_shader ||
         link.vertex_shader_root_call_serial !=
             previous_vertex_shader_root_call_serial ||
         link.vertex_shader_root_context_key !=
             previous_vertex_shader_root_context_key ||
         link.vertex_shader_root_symbol_address !=
             previous_vertex_shader_root_symbol_address ||
         link.vertex_shader_root_lr != previous_vertex_shader_root_lr ||
         link.pixel_shader_root_call_serial !=
             previous_pixel_shader_root_call_serial ||
         link.pixel_shader_root_context_key !=
             previous_pixel_shader_root_context_key ||
         link.pixel_shader_root_symbol_address !=
             previous_pixel_shader_root_symbol_address ||
         link.pixel_shader_root_lr != previous_pixel_shader_root_lr;
}

void RepairSurfaceTextureLinksForPrivateCopyAction(
    FableGuestResourceRegistry& registry,
    const FablePrivateTextureCopyAction& action) {
  const bool has_sample_identity =
      HasRegisteredSampleSource(action.sampled_source) ||
      action.target_content_serial != 0 || action.backing_content_serial != 0;
  if (!PrivateTextureCopyActionHasVertexShaderRoot(action) &&
      !PrivateTextureCopyActionHasPixelShaderRoot(action) &&
      action.vertex_shader == 0 && action.pixel_shader == 0 &&
      !has_sample_identity) {
    return;
  }

	  bool repaired = false;
	  auto repair_link = [&](FableSurfaceTextureLink& link) {
	    repaired |= RepairSurfaceTextureLinkShaderRootsFromAction(link, action);
	    repaired |= RepairSurfaceTextureLinkSampleIdentityFromAction(link, action);
	  };
  auto repair_link_map = [&](auto& links) {
    for (auto& [key, link] : links) {
      (void)key;
      repair_link(link);
    }
  };
  auto repair_link_vector_map = [&](auto& buckets) {
    for (auto& [key, links] : buckets) {
      (void)key;
      for (FableSurfaceTextureLink& link : links) {
        repair_link(link);
      }
    }
  };
  auto repair_relation_vector_map = [&](auto& buckets) {
    for (auto& [key, relations] : buckets) {
      (void)key;
      for (FableSurfaceDestinationTextureRelation& relation : relations) {
        repair_link(relation.link);
      }
    }
  };
  auto repair_relation_map = [&](auto& relations) {
    for (auto& [key, relation] : relations) {
      (void)key;
      repair_link(relation.link);
    }
  };

  repair_link_vector_map(registry.surface_texture_links_by_texture);
  repair_link_vector_map(registry.surface_texture_links_by_surface);
  repair_link_vector_map(registry.surface_texture_links_by_base);
  repair_link_vector_map(registry.surface_texture_links_by_fetch_key);
  repair_link_map(registry.surface_texture_link_by_destination_texture);
  repair_link_map(registry.surface_texture_link_by_destination_texture_slot);
  repair_link_map(registry.surface_texture_exact_link_by_destination_texture);
  repair_link_map(
      registry.surface_texture_exact_link_by_destination_texture_slot);
  repair_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture);
  repair_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture_slot);
  repair_link_map(registry.surface_texture_link_by_source_surface);
  repair_link_map(registry.surface_texture_link_by_source_surface_slot);
  repair_link_map(registry.surface_texture_link_by_base);
  repair_link_map(registry.surface_texture_link_by_fetch_key);
  repair_link_map(registry.surface_texture_link_by_copy_call);
  repair_link_map(registry.surface_texture_link_by_copy_context);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_source_surface);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture);
  repair_relation_map(
      registry.surface_destination_relation_by_destination_texture);
  repair_relation_map(
      registry.surface_destination_relation_by_destination_texture_slot);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture_slot);

  if (repaired) {
    ++registry.surface_texture_link_serial;
    ++registry.surface_destination_relation_serial;
  }
}

void RepairSurfaceTextureLinksFromSurfaceTextureLinkSample(
    FableGuestResourceRegistry& registry,
    const FableSurfaceTextureLink& sample_link) {
  if (!HasRegisteredSampleSource(sample_link.sampled_source) ||
      (sample_link.target_content_serial == 0 &&
       sample_link.backing_content_serial == 0 &&
       sample_link.sampled_source.content_serial == 0) ||
      sample_link.copy_symbol_address == 0 ||
      (sample_link.copy_call_serial == 0 &&
       sample_link.copy_context_key == 0)) {
    return;
  }

  bool repaired = false;
  auto repair_link = [&](FableSurfaceTextureLink& link) {
    repaired |= RepairSurfaceTextureLinkSampleIdentityFromLink(link, sample_link);
  };
  auto repair_link_map = [&](auto& links) {
    for (auto& [key, link] : links) {
      (void)key;
      repair_link(link);
    }
  };
  auto repair_link_vector_map = [&](auto& buckets) {
    for (auto& [key, links] : buckets) {
      (void)key;
      for (FableSurfaceTextureLink& link : links) {
        repair_link(link);
      }
    }
  };
  auto repair_relation_vector_map = [&](auto& buckets) {
    for (auto& [key, relations] : buckets) {
      (void)key;
      for (FableSurfaceDestinationTextureRelation& relation : relations) {
        repair_link(relation.link);
      }
    }
  };
  auto repair_relation_map = [&](auto& relations) {
    for (auto& [key, relation] : relations) {
      (void)key;
      repair_link(relation.link);
    }
  };

  repair_link_vector_map(registry.surface_texture_links_by_texture);
  repair_link_vector_map(registry.surface_texture_links_by_surface);
  repair_link_vector_map(registry.surface_texture_links_by_base);
  repair_link_vector_map(registry.surface_texture_links_by_fetch_key);
  repair_link_map(registry.surface_texture_link_by_destination_texture);
  repair_link_map(registry.surface_texture_link_by_destination_texture_slot);
  repair_link_map(registry.surface_texture_exact_link_by_destination_texture);
  repair_link_map(
      registry.surface_texture_exact_link_by_destination_texture_slot);
  repair_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture);
  repair_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture_slot);
  repair_link_map(registry.surface_texture_link_by_source_surface);
  repair_link_map(registry.surface_texture_link_by_source_surface_slot);
  repair_link_map(registry.surface_texture_link_by_base);
  repair_link_map(registry.surface_texture_link_by_fetch_key);
  repair_link_map(registry.surface_texture_link_by_copy_call);
  repair_link_map(registry.surface_texture_link_by_copy_context);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_source_surface);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture);
  repair_relation_map(
      registry.surface_destination_relation_by_destination_texture);
  repair_relation_map(
      registry.surface_destination_relation_by_destination_texture_slot);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture_slot);

  if (repaired) {
    ++registry.surface_texture_link_serial;
    ++registry.surface_destination_relation_serial;
  }
}

FableSurfaceTextureLink MergeSurfaceTextureLinkMetadata(
    const FableSurfaceTextureLink& candidate,
    const FableSurfaceTextureLink& current) {
  const bool candidate_preferred =
      SurfaceTextureLinkShouldPrefer(candidate, &current);
  FableSurfaceTextureLink merged = candidate_preferred ? candidate : current;
  const FableSurfaceTextureLink& fallback =
      candidate_preferred ? current : candidate;
  if (merged.serial == 0) {
    merged.serial = current.serial != 0 ? current.serial : candidate.serial;
  }
  if (merged.vertex_shader == 0) {
    merged.vertex_shader =
        current.vertex_shader != 0 ? current.vertex_shader
                                   : candidate.vertex_shader;
  }
  if (merged.pixel_shader == 0) {
    merged.pixel_shader = current.pixel_shader != 0 ? current.pixel_shader
                                                    : candidate.pixel_shader;
  }
  CopyMissingSurfaceTextureLinkShaderRoots(merged, fallback);
  return merged;
}

bool SurfaceTextureLinkIsTrivialSelfEdge(
    const FableSurfaceTextureLink& link) {
  if (!link.trusted_destination_edge || link.texture == 0 ||
      link.source_surface == 0 || link.texture != link.source_surface) {
    return false;
  }

  const uint64_t texture_fetch_key =
      FableDirectTextureFetchKey(link.texture_fetch_dwords);
  const uint64_t backing_fetch_key =
      FableDirectTextureFetchKey(link.backing_fetch_dwords);
  const uint64_t sampled_fetch_key =
      FableDirectTextureFetchKey(link.sampled_source.fetch_dwords);

  const bool has_distinct_backing_texture =
      link.backing_texture != 0 && link.backing_texture != link.texture &&
      link.backing_texture != link.source_surface;
  const bool has_distinct_backing_base =
      link.backing_base_address != 0 &&
      (link.texture_base_address == 0 ||
       link.backing_base_address != link.texture_base_address);
  const bool has_distinct_backing_fetch =
      backing_fetch_key != 0 &&
      (texture_fetch_key == 0 || backing_fetch_key != texture_fetch_key);
  if (has_distinct_backing_texture || has_distinct_backing_base ||
      has_distinct_backing_fetch) {
    return false;
  }

  const bool has_distinct_sample_resource =
      link.sampled_source.resource != 0 &&
      link.sampled_source.resource != link.texture &&
      link.sampled_source.resource != link.source_surface;
  const bool has_distinct_sample_base =
      link.sampled_source.base_address != 0 &&
      (link.texture_base_address == 0 ||
       link.sampled_source.base_address != link.texture_base_address);
  const bool has_distinct_sample_fetch =
      sampled_fetch_key != 0 &&
      (texture_fetch_key == 0 || sampled_fetch_key != texture_fetch_key);
  return !has_distinct_sample_resource && !has_distinct_sample_base &&
         !has_distinct_sample_fetch;
}

bool IsFableRegisteredPrivateTextureAddress(uint32_t address) {
  switch (address) {
    case 0x40130C90u:
    case 0x40130CD0u:
    case 0x40131610u:
    case 0x40131650u:
    case 0x40131C90u:
    case 0x40131CD0u:
    case 0x40131D10u:
    case 0x40137C90u:
    case 0x40137CD0u:
    case 0x40137D10u:
    case 0x40133CD0u:
    case 0x40133D10u:
    case 0x404DE450u:
    case 0x404DE4D0u:
    case 0x404DE510u:
    case 0x404DE550u:
    case 0x404DE590u:
    case 0x404DE5D0u:
    case 0x404DE610u:
    case 0x404DE650u:
    case 0x404DE690u:
    case 0x404DE6D0u:
    case 0x404DE710u:
    case 0x40513AD0u:
    case 0x40513B10u:
    case 0x40513B50u:
    case 0x40513B90u:
    case 0x40513BD0u:
    case 0x40513C10u:
    case 0x40513C50u:
    case 0x40513C90u:
    case 0x40513CD0u:
    case 0x40513D10u:
    case 0x40513D90u:
    case 0x40513DD0u:
    case 0x12656000u:
    case 0x12681000u:
    case 0x12759000u:
    case 0x12771000u:
    case 0x12867000u:
    case 0x1289D000u:
    case 0x128D3000u:
    case 0x128D9000u:
    case 0x1293F000u:
      return true;
    default:
      return false;
  }
}

bool IsFableRegisteredPrivateTextureExtent(uint32_t width, uint32_t height) {
  return (width == 256u && height == 256u) ||
         (width == 280u && height == 180u) ||
         (width == 320u && height == 180u) ||
         (width == 480u && height == 270u) ||
         (width == 560u && height == 360u) ||
         (width == 960u && height == 540u);
}

bool SurfaceTextureLinkTouchesFablePrivateChain(
    const FableSurfaceTextureLink& link) {
  return IsFableRegisteredPrivateTextureAddress(link.texture) ||
         IsFableRegisteredPrivateTextureAddress(link.texture_base_address) ||
         IsFableRegisteredPrivateTextureAddress(link.source_surface) ||
         IsFableRegisteredPrivateTextureAddress(link.backing_texture) ||
         IsFableRegisteredPrivateTextureAddress(link.backing_base_address);
}

bool PrivateTextureCopyActionTouchesFablePrivateChain(
    const FablePrivateTextureCopyAction& action) {
  return action.kind != FablePrivateTextureCopyKind::kDepth &&
         (IsFableRegisteredPrivateTextureAddress(action.source_texture) ||
          IsFableRegisteredPrivateTextureAddress(action.source_base_address) ||
          IsFableRegisteredPrivateTextureAddress(action.backing_texture) ||
          IsFableRegisteredPrivateTextureAddress(action.backing_base_address) ||
          IsFableRegisteredPrivateTextureAddress(action.target_surface) ||
          IsFableRegisteredPrivateTextureAddress(action.render_target));
}

bool SameShaderIdentity(const FableShaderIdentity& lhs,
                        const FableShaderIdentity& rhs) {
  return lhs.kind == rhs.kind && lhs.object_shader == rhs.object_shader &&
         lhs.parent_shader == rhs.parent_shader &&
         lhs.bytecode_address == rhs.bytecode_address &&
         lhs.bytecode_size == rhs.bytecode_size &&
         lhs.bytecode_offset == rhs.bytecode_offset &&
         lhs.bytecode_hash == rhs.bytecode_hash;
}

bool SameVertexDeclarationIdentity(
    const FableVertexDeclarationIdentity& lhs,
    const FableVertexDeclarationIdentity& rhs) {
  return lhs.object_declaration == rhs.object_declaration &&
         lhs.parent_declaration == rhs.parent_declaration &&
         lhs.elements_address == rhs.elements_address &&
         lhs.elements_size == rhs.elements_size &&
         lhs.element_count == rhs.element_count &&
         lhs.max_stream == rhs.max_stream &&
         lhs.stream_mask == rhs.stream_mask &&
         lhs.declaration_flags == rhs.declaration_flags &&
         lhs.elements_hash == rhs.elements_hash;
}

bool ShaderIdentityIndexShouldPrefer(const FableShaderIdentity& candidate,
                                     const FableShaderIdentity* current) {
  if (!current) {
    return true;
  }
  return candidate.serial > current->serial;
}

bool VertexDeclarationIdentityIndexShouldPrefer(
    const FableVertexDeclarationIdentity& candidate,
    const FableVertexDeclarationIdentity* current) {
  if (!current) {
    return true;
  }
  return candidate.serial > current->serial;
}

const FableTextureIdentity* SelectUniqueTextureIdentity(
    const std::vector<uint32_t>* objects) {
  if (!objects) {
    return nullptr;
  }

  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const FableTextureIdentity* unique = nullptr;
  for (uint32_t object : *objects) {
    const auto it = registry.texture_identities_by_object.find(object);
    if (it == registry.texture_identities_by_object.end()) {
      continue;
    }
    if (unique) {
      return nullptr;
    }
    unique = &it->second;
  }
  return unique;
}

template <typename T>
void AddUnique(std::vector<T>& values, T value) {
  if (value == T{}) {
    return;
  }
  if (std::find(values.begin(), values.end(), value) == values.end()) {
    values.push_back(value);
  }
}

void CollectTextureIdentityIndexKeys(
    const FableTextureIdentity& identity, std::vector<uint32_t>& object_keys,
    std::vector<uint32_t>& base_keys, std::vector<uint64_t>& fetch_keys) {
  AddUnique(object_keys, identity.object_texture);
  AddUnique(object_keys, identity.parent_texture);
  AddUnique(base_keys, identity.base_address);
  AddUnique(fetch_keys, FableDirectTextureFetchKey(identity.fetch_dwords));
}

void CollectTextureIdentityIndexKeysForObject(
    const FableGuestResourceRegistry& registry, uint32_t object,
    std::vector<uint32_t>& object_keys, std::vector<uint32_t>& base_keys,
    std::vector<uint64_t>& fetch_keys) {
  AddUnique(object_keys, object);
  const auto it = registry.texture_identities_by_object.find(object);
  if (it != registry.texture_identities_by_object.end()) {
    CollectTextureIdentityIndexKeys(it->second, object_keys, base_keys,
                                    fetch_keys);
  }
}

void CollectTextureIdentityIndexKeysForBase(
    const FableGuestResourceRegistry& registry, uint32_t base_address,
    std::vector<uint32_t>& object_keys, std::vector<uint32_t>& base_keys,
    std::vector<uint64_t>& fetch_keys) {
  AddUnique(base_keys, base_address);
  const auto objects_it =
      registry.texture_identity_objects_by_base.find(base_address);
  if (objects_it == registry.texture_identity_objects_by_base.end()) {
    return;
  }
  for (uint32_t object : objects_it->second) {
    CollectTextureIdentityIndexKeysForObject(registry, object, object_keys,
                                             base_keys, fetch_keys);
  }
}

void CollectTextureIdentityIndexKeysForFetch(
    const FableGuestResourceRegistry& registry, uint64_t fetch_key,
    std::vector<uint32_t>& object_keys, std::vector<uint32_t>& base_keys,
    std::vector<uint64_t>& fetch_keys) {
  AddUnique(fetch_keys, fetch_key);
  const auto objects_it =
      registry.texture_identity_objects_by_fetch_key.find(fetch_key);
  if (objects_it == registry.texture_identity_objects_by_fetch_key.end()) {
    return;
  }
  for (uint32_t object : objects_it->second) {
    CollectTextureIdentityIndexKeysForObject(registry, object, object_keys,
                                             base_keys, fetch_keys);
  }
}

bool HasRegisteredSampleSource(
    const FableRegisteredSampleSource& sampled_source) {
  return sampled_source.kind != FableRegisteredSampleSourceKind::kUnknown &&
         sampled_source.resource != 0;
}

void NormalizePrivateTextureCopySampleSource(
    FablePrivateTextureCopyAction& action) {
  if (HasRegisteredSampleSource(action.sampled_source)) {
    if (action.sampled_source.kind ==
        FableRegisteredSampleSourceKind::kBackingTexture) {
      if (action.backing_texture == 0) {
        action.backing_texture = action.sampled_source.resource;
      }
      if (action.backing_base_address == 0) {
        action.backing_base_address = action.sampled_source.base_address;
      }
      if (FableDirectTextureFetchKey(action.backing_fetch_dwords) == 0) {
        action.backing_fetch_dwords = action.sampled_source.fetch_dwords;
      }
      if (action.backing_content_serial == 0) {
        action.backing_content_serial = action.sampled_source.content_serial;
      }
    } else if (action.sampled_source.kind ==
                   FableRegisteredSampleSourceKind::kGpuSurface &&
               action.sampled_source.resource == action.target_surface &&
               action.target_content_serial == 0) {
      action.target_content_serial = action.sampled_source.content_serial;
    }
    return;
  }
  if (action.backing_texture != 0 || action.backing_base_address != 0 ||
      FableDirectTextureFetchKey(action.backing_fetch_dwords) != 0) {
    action.sampled_source.kind =
        FableRegisteredSampleSourceKind::kBackingTexture;
    action.sampled_source.resource = action.backing_texture;
    action.sampled_source.base_address = action.backing_base_address;
    action.sampled_source.fetch_dwords = action.backing_fetch_dwords;
    action.sampled_source.content_serial = action.backing_content_serial;
    return;
  }
  action.sampled_source.kind = FableRegisteredSampleSourceKind::kGuestTexture;
  action.sampled_source.resource = action.source_texture;
  action.sampled_source.base_address = action.source_base_address;
  action.sampled_source.fetch_dwords = action.source_fetch_dwords;
}

void NormalizeSurfaceTextureLinkSampleSource(FableSurfaceTextureLink& link) {
  if (HasRegisteredSampleSource(link.sampled_source)) {
    if (link.sampled_source.kind ==
        FableRegisteredSampleSourceKind::kBackingTexture) {
      if (link.backing_texture == 0) {
        link.backing_texture = link.sampled_source.resource;
      }
      if (link.backing_base_address == 0) {
        link.backing_base_address = link.sampled_source.base_address;
      }
      if (FableDirectTextureFetchKey(link.backing_fetch_dwords) == 0) {
        link.backing_fetch_dwords = link.sampled_source.fetch_dwords;
      }
      if (link.backing_content_serial == 0) {
        link.backing_content_serial = link.sampled_source.content_serial;
      }
    } else if (link.sampled_source.kind ==
                   FableRegisteredSampleSourceKind::kGpuSurface &&
               link.sampled_source.resource == link.source_surface &&
               link.target_content_serial == 0) {
      link.target_content_serial = link.sampled_source.content_serial;
    }
    return;
  }
  if (link.backing_texture != 0 || link.backing_base_address != 0 ||
      FableDirectTextureFetchKey(link.backing_fetch_dwords) != 0) {
    link.sampled_source.kind = FableRegisteredSampleSourceKind::kBackingTexture;
    link.sampled_source.resource = link.backing_texture;
    link.sampled_source.base_address = link.backing_base_address;
    link.sampled_source.fetch_dwords = link.backing_fetch_dwords;
    link.sampled_source.content_serial = link.backing_content_serial;
    return;
  }
  if (link.source_surface != 0) {
    link.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
    link.sampled_source.resource = link.source_surface;
    link.sampled_source.content_serial = link.target_content_serial;
    return;
  }
}

const FableShaderIdentity* SelectBestShaderIdentity(
    const std::vector<uint32_t>* objects) {
  if (!objects) {
    return nullptr;
  }

  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const FableShaderIdentity* best = nullptr;
  for (uint32_t object : *objects) {
    const auto it = registry.shader_identities_by_object.find(object);
    if (it == registry.shader_identities_by_object.end()) {
      continue;
    }
    if (ShaderIdentityIndexShouldPrefer(it->second, best)) {
      best = &it->second;
    }
  }
  return best;
}

const FableVertexDeclarationIdentity* SelectBestVertexDeclarationIdentity(
    const std::vector<uint32_t>* objects) {
  if (!objects) {
    return nullptr;
  }

  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const FableVertexDeclarationIdentity* best = nullptr;
  for (uint32_t object : *objects) {
    const auto it =
        registry.vertex_declaration_identities_by_object.find(object);
    if (it == registry.vertex_declaration_identities_by_object.end()) {
      continue;
    }
    if (VertexDeclarationIdentityIndexShouldPrefer(it->second, best)) {
      best = &it->second;
    }
  }
  return best;
}

bool SurfaceTextureLinkShouldPrefer(const FableSurfaceTextureLink& candidate,
                                    const FableSurfaceTextureLink* current) {
  if (!current) {
    return true;
  }
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  const bool candidate_has_slot = candidate.source_slot != kUnknownSlot;
  const bool current_has_slot = current->source_slot != kUnknownSlot;
  if (candidate_has_slot != current_has_slot) {
    return candidate_has_slot;
  }
  if (candidate.trusted_destination_edge != current->trusted_destination_edge) {
    return candidate.trusted_destination_edge;
  }
  const bool candidate_has_copy_identity =
      candidate.copy_call_serial != 0 || candidate.copy_context_key != 0 ||
      candidate.copy_symbol_address != 0;
  const bool current_has_copy_identity =
      current->copy_call_serial != 0 || current->copy_context_key != 0 ||
      current->copy_symbol_address != 0;
  if (candidate_has_copy_identity != current_has_copy_identity) {
    return candidate_has_copy_identity;
  }
  if (candidate.copy_call_serial != current->copy_call_serial) {
    return candidate.copy_call_serial > current->copy_call_serial;
  }
  if (candidate.copy_context_key != current->copy_context_key) {
    return candidate.copy_context_key > current->copy_context_key;
  }
  if (candidate.wrapper_source_exact != current->wrapper_source_exact) {
    return candidate.wrapper_source_exact;
  }
  if (candidate.wrapper_source_texture != current->wrapper_source_texture) {
    return candidate.wrapper_source_texture != 0;
  }
  const bool candidate_has_proven_backing =
      candidate.backing_texture != 0 &&
      (candidate.backing_content_serial != 0 ||
       candidate.target_content_serial != 0);
  const bool current_has_proven_backing =
      current->backing_texture != 0 &&
      (current->backing_content_serial != 0 ||
       current->target_content_serial != 0);
  if (candidate_has_proven_backing != current_has_proven_backing) {
    return candidate_has_proven_backing;
  }
  const bool candidate_has_sample_content =
      candidate.sampled_source.resource != 0 &&
      candidate.sampled_source.content_serial != 0;
  const bool current_has_sample_content =
      current->sampled_source.resource != 0 &&
      current->sampled_source.content_serial != 0;
  if (candidate_has_sample_content != current_has_sample_content) {
    return candidate_has_sample_content;
  }
  const bool candidate_has_shader_roots =
      SurfaceTextureLinkHasShaderRoots(candidate);
  const bool current_has_shader_roots =
      SurfaceTextureLinkHasShaderRoots(*current);
  if (candidate_has_shader_roots != current_has_shader_roots) {
    return candidate_has_shader_roots;
  }
  const bool candidate_has_shader_pair =
      candidate.vertex_shader != 0 && candidate.pixel_shader != 0;
  const bool current_has_shader_pair =
      current->vertex_shader != 0 && current->pixel_shader != 0;
  if (candidate_has_shader_pair != current_has_shader_pair) {
    return candidate_has_shader_pair;
  }
  if (candidate.frame_index != current->frame_index) {
    return candidate.frame_index > current->frame_index;
  }
  if (candidate.draw_serial != current->draw_serial) {
    return candidate.draw_serial > current->draw_serial;
  }
  if (candidate.target_content_serial != current->target_content_serial) {
    return candidate.target_content_serial > current->target_content_serial;
  }
  if (candidate.backing_content_serial != current->backing_content_serial) {
    return candidate.backing_content_serial > current->backing_content_serial;
  }
  return candidate.serial > current->serial;
}

const FableSurfaceTextureLink* SelectBestSurfaceTextureLink(
    const std::vector<FableSurfaceTextureLink>* links) {
  if (!links || links->empty()) {
    return nullptr;
  }
  const FableSurfaceTextureLink* best = &links->front();
  for (const FableSurfaceTextureLink& link : *links) {
    if (SurfaceTextureLinkShouldPrefer(link, best)) {
      best = &link;
    }
  }
  return best;
}

bool SurfaceTextureLinkIsVisibleToDraw(const FableSurfaceTextureLink& link,
                                       uint32_t frame_index,
                                       uint64_t draw_serial,
                                       uint32_t source_slot) {
  if (!link.trusted_destination_edge) {
    return false;
  }
  if (link.frame_index != 0 && link.frame_index > frame_index) {
    return false;
  }
  if (link.frame_index != 0 && link.frame_index == frame_index &&
      link.draw_serial != 0 && draw_serial != 0 &&
      link.draw_serial > draw_serial) {
    return false;
  }
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  return link.source_slot == kUnknownSlot || link.source_slot == source_slot ||
         (source_slot != kUnknownSlot &&
          SurfaceTextureLinkTouchesFablePrivateChain(link));
}

const FableSurfaceTextureLink* SelectVisibleSurfaceTextureLink(
    const std::vector<FableSurfaceTextureLink>* links, uint32_t frame_index,
    uint64_t draw_serial, uint32_t source_slot) {
  if (!links || links->empty()) {
    return nullptr;
  }
  const FableSurfaceTextureLink* best = nullptr;
  for (const FableSurfaceTextureLink& link : *links) {
    if (!SurfaceTextureLinkIsVisibleToDraw(link, frame_index, draw_serial,
                                           source_slot)) {
      continue;
    }
    if (SurfaceTextureLinkShouldPrefer(link, best)) {
      best = &link;
    }
  }
  return best;
}

bool SameSurfaceDestinationRelationIdentity(
    const FableSurfaceDestinationTextureRelation& lhs,
    const FableSurfaceDestinationTextureRelation& rhs) {
  return lhs.source_surface == rhs.source_surface &&
         lhs.destination_texture == rhs.destination_texture &&
         lhs.source_slot == rhs.source_slot &&
         SameSurfaceTextureLinkIdentity(lhs.link, rhs.link);
}

bool SurfaceDestinationRelationShouldPrefer(
    const FableSurfaceDestinationTextureRelation& candidate,
    const FableSurfaceDestinationTextureRelation* current) {
  if (!current) {
    return true;
  }
  return SurfaceTextureLinkShouldPrefer(candidate.link, &current->link);
}

bool SurfaceDestinationRelationIsVisibleToDraw(
    const FableSurfaceDestinationTextureRelation& relation,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot) {
  return SurfaceTextureLinkIsVisibleToDraw(relation.link, frame_index,
                                           draw_serial, source_slot);
}

const FableSurfaceDestinationTextureRelation*
SelectVisibleSurfaceDestinationRelation(
    const std::vector<FableSurfaceDestinationTextureRelation>* relations,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot) {
  if (!relations || relations->empty()) {
    return nullptr;
  }
  const FableSurfaceDestinationTextureRelation* best = nullptr;
  for (const FableSurfaceDestinationTextureRelation& relation : *relations) {
    if (!SurfaceDestinationRelationIsVisibleToDraw(
            relation, frame_index, draw_serial, source_slot)) {
      continue;
    }
    if (SurfaceDestinationRelationShouldPrefer(relation, best)) {
      best = &relation;
    }
  }
  return best;
}

bool PrivateTextureCopyActionShouldPrefer(
    const FablePrivateTextureCopyAction& candidate,
    const FablePrivateTextureCopyAction* current) {
  if (!current) {
    return true;
  }
  const auto has_exact_color_backing =
      [](const FablePrivateTextureCopyAction& action) {
        return action.kind != FablePrivateTextureCopyKind::kDepth &&
               action.backing_texture != 0;
      };
  const bool candidate_has_backing = has_exact_color_backing(candidate);
  const bool current_has_backing = has_exact_color_backing(*current);
  if (candidate_has_backing != current_has_backing) {
    return candidate_has_backing;
  }
  if (candidate.frame_index != current->frame_index) {
    return candidate.frame_index > current->frame_index;
  }
  if (candidate.draw_serial != current->draw_serial) {
    return candidate.draw_serial > current->draw_serial;
  }
  return candidate.serial > current->serial;
}

bool PrivateTextureCopyActionIsVisibleToDraw(
    const FablePrivateTextureCopyAction& action, uint32_t frame_index,
    uint64_t draw_serial, uint32_t source_slot) {
  if (action.frame_index != 0 && action.frame_index > frame_index) {
    return false;
  }
  if (action.frame_index != 0 && action.frame_index == frame_index &&
      action.draw_serial != 0 && draw_serial != 0 &&
      action.draw_serial > draw_serial) {
    return false;
  }
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  return action.source_slot == kUnknownSlot || source_slot == kUnknownSlot ||
         action.source_slot == source_slot ||
         PrivateTextureCopyActionTouchesFablePrivateChain(action);
}

const FablePrivateTextureCopyAction* SelectVisiblePrivateTextureCopyAction(
    const std::vector<FablePrivateTextureCopyAction>* actions,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot) {
  if (!actions || actions->empty()) {
    return nullptr;
  }
  const FablePrivateTextureCopyAction* best = nullptr;
  for (const FablePrivateTextureCopyAction& action : *actions) {
    if (!PrivateTextureCopyActionIsVisibleToDraw(action, frame_index,
                                                 draw_serial, source_slot)) {
      continue;
    }
    if (PrivateTextureCopyActionShouldPrefer(action, best)) {
      best = &action;
    }
  }
  return best;
}

uint64_t PrivateTextureCopyDestinationSlotKey(uint32_t destination_texture,
                                              uint32_t source_slot) {
  return (static_cast<uint64_t>(source_slot) << 32u) | destination_texture;
}

uint64_t SurfaceTextureDestinationSlotKey(uint32_t destination_texture,
                                          uint32_t source_slot) {
  return (static_cast<uint64_t>(source_slot) << 32u) | destination_texture;
}

uint64_t SurfaceTextureSourceSurfaceSlotKey(uint32_t source_surface,
                                            uint32_t source_slot) {
  return (static_cast<uint64_t>(source_slot) << 32u) | source_surface;
}

void StoreSurfaceDestinationRelation(
    FableGuestResourceRegistry& registry, const FableSurfaceTextureLink& link,
    uint32_t destination_texture) {
  if (!link.trusted_destination_edge || link.source_surface == 0 ||
      destination_texture == 0 || SurfaceTextureLinkIsTrivialSelfEdge(link)) {
    return;
  }

  FableSurfaceDestinationTextureRelation relation;
  relation.serial = ++registry.surface_destination_relation_serial;
  relation.source_surface = link.source_surface;
  relation.destination_texture = destination_texture;
  relation.source_slot = link.source_slot;
  relation.link = link;

  constexpr size_t kMaxSurfaceDestinationRelationsPerBucket = 32;
  auto upsert_relation =
      [&](std::vector<FableSurfaceDestinationTextureRelation>& relations) {
        for (FableSurfaceDestinationTextureRelation& existing : relations) {
          if (SameSurfaceDestinationRelationIdentity(existing, relation)) {
            relation.serial = existing.serial;
            existing = relation;
            return;
          }
        }
        if (relations.size() >= kMaxSurfaceDestinationRelationsPerBucket) {
          relations.erase(relations.begin());
        }
        relations.push_back(relation);
      };

  upsert_relation(
      registry.surface_destination_relations_by_source_surface
          [relation.source_surface]);
  upsert_relation(
      registry.surface_destination_relations_by_destination_texture
          [relation.destination_texture]);

  const auto direct_it =
      registry.surface_destination_relation_by_destination_texture.find(
          relation.destination_texture);
  if (direct_it ==
          registry.surface_destination_relation_by_destination_texture.end() ||
      SurfaceDestinationRelationShouldPrefer(relation, &direct_it->second)) {
    registry.surface_destination_relation_by_destination_texture
        [relation.destination_texture] = relation;
  }

  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  if (relation.source_slot == kUnknownSlot) {
    return;
  }

  const uint64_t slot_key = SurfaceTextureDestinationSlotKey(
      relation.destination_texture, relation.source_slot);
  upsert_relation(
      registry.surface_destination_relations_by_destination_texture_slot
          [slot_key]);
  const auto slot_it =
      registry.surface_destination_relation_by_destination_texture_slot.find(
          slot_key);
  if (slot_it ==
          registry.surface_destination_relation_by_destination_texture_slot
              .end() ||
      SurfaceDestinationRelationShouldPrefer(relation, &slot_it->second)) {
    registry.surface_destination_relation_by_destination_texture_slot
        [slot_key] = relation;
  }
}

void StorePrivateTextureCopyDirectAction(
    FableGuestResourceRegistry& registry,
    const FablePrivateTextureCopyAction& action) {
  if (action.source_texture == 0) {
    return;
  }

  if (action.target_surface == 0) {
    if (action.wrapper_call_serial != 0) {
      registry.private_texture_copy_action_by_wrapper_call
          [action.wrapper_call_serial] = action;
      ++registry.private_texture_copy_index_serial;
    }
    if (action.wrapper_context_key != 0) {
      registry.private_texture_copy_action_by_wrapper_context
          [action.wrapper_context_key] = action;
      ++registry.private_texture_copy_index_serial;
    }
    RepairSurfaceTextureLinksForPrivateCopyAction(registry, action);
    return;
  }

  std::vector<uint32_t> destination_keys;
  std::vector<uint32_t> base_keys;
  std::vector<uint64_t> fetch_keys;
  CollectTextureIdentityIndexKeysForObject(registry, action.source_texture,
                                           destination_keys, base_keys,
                                           fetch_keys);
  CollectTextureIdentityIndexKeysForObject(registry, action.backing_texture,
                                           destination_keys, base_keys,
                                           fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, action.source_base_address,
                                         destination_keys, base_keys,
                                         fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, action.backing_base_address,
                                         destination_keys, base_keys,
                                         fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(action.source_fetch_dwords),
      destination_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(action.backing_fetch_dwords),
      destination_keys, base_keys, fetch_keys);
  auto add_destination_key = [&](uint32_t key) {
    if (key == 0) {
      return;
    }
    if (std::find(destination_keys.begin(), destination_keys.end(), key) ==
        destination_keys.end()) {
      destination_keys.push_back(key);
    }
  };
  add_destination_key(action.target_surface);
  switch (action.kind) {
    case FablePrivateTextureCopyKind::kColor:
    case FablePrivateTextureCopyKind::kColorSibling:
      add_destination_key(action.render_target);
      break;
    case FablePrivateTextureCopyKind::kDepth:
      add_destination_key(action.depth_stencil);
      break;
  }

  constexpr size_t kMaxPrivateTextureCopyActionsPerBucket = 32;
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  auto upsert_action = [&](std::vector<FablePrivateTextureCopyAction>& actions) {
    for (FablePrivateTextureCopyAction& existing : actions) {
      if (SamePrivateTextureCopyActionIdentity(existing, action)) {
        existing = action;
        return;
      }
    }
    if (actions.size() >= kMaxPrivateTextureCopyActionsPerBucket) {
      actions.erase(actions.begin());
    }
    actions.push_back(action);
  };
  std::vector<uint32_t> output_destination_keys;
  auto add_output_destination_key = [&](uint32_t key) {
    if (key == 0) {
      return;
    }
    if (std::find(output_destination_keys.begin(),
                  output_destination_keys.end(),
                  key) == output_destination_keys.end()) {
      output_destination_keys.push_back(key);
    }
  };
  add_output_destination_key(action.target_surface);
  switch (action.kind) {
    case FablePrivateTextureCopyKind::kColor:
    case FablePrivateTextureCopyKind::kColorSibling:
      add_output_destination_key(action.render_target);
      break;
    case FablePrivateTextureCopyKind::kDepth:
      add_output_destination_key(action.depth_stencil);
      break;
  }
  for (uint32_t destination_texture : output_destination_keys) {
    upsert_action(
        registry.private_texture_copy_output_actions_by_destination_texture
            [destination_texture]);
    if (action.source_slot != kUnknownSlot) {
      const uint64_t slot_key = PrivateTextureCopyDestinationSlotKey(
          destination_texture, action.source_slot);
      upsert_action(
          registry
              .private_texture_copy_output_actions_by_destination_texture_slot
                  [slot_key]);
    }
  }

  for (uint32_t destination_texture : destination_keys) {
    upsert_action(
        registry
            .private_texture_copy_actions_by_destination_texture
                [destination_texture]);
    auto direct_it =
        registry.private_texture_copy_action_by_destination_texture.find(
            destination_texture);
    if (direct_it ==
            registry.private_texture_copy_action_by_destination_texture.end() ||
        PrivateTextureCopyActionShouldPrefer(action, &direct_it->second)) {
      registry
          .private_texture_copy_action_by_destination_texture
              [destination_texture] = action;
      ++registry.private_texture_copy_index_serial;
    }
    if (action.source_slot != kUnknownSlot) {
      const uint64_t slot_key = PrivateTextureCopyDestinationSlotKey(
          destination_texture, action.source_slot);
      upsert_action(
          registry
              .private_texture_copy_actions_by_destination_texture_slot
                  [slot_key]);
      auto slot_it =
          registry.private_texture_copy_action_by_destination_texture_slot.find(
              slot_key);
      if (slot_it ==
              registry.private_texture_copy_action_by_destination_texture_slot
                  .end() ||
          PrivateTextureCopyActionShouldPrefer(action, &slot_it->second)) {
        registry.private_texture_copy_action_by_destination_texture_slot
            [slot_key] = action;
        ++registry.private_texture_copy_index_serial;
      }
    }
  }
  if (action.wrapper_call_serial != 0) {
    registry.private_texture_copy_action_by_wrapper_call
        [action.wrapper_call_serial] = action;
    ++registry.private_texture_copy_index_serial;
  }
  if (action.wrapper_context_key != 0) {
    registry.private_texture_copy_action_by_wrapper_context
        [action.wrapper_context_key] = action;
    ++registry.private_texture_copy_index_serial;
  }
  RepairSurfaceTextureLinksForPrivateCopyAction(registry, action);
}

void RebuildPrivateTextureCopyDirectActions(FableGuestResourceRegistry& registry) {
  registry.private_texture_copy_action_by_destination_texture.clear();
  registry.private_texture_copy_action_by_destination_texture_slot.clear();
  registry.private_texture_copy_actions_by_destination_texture.clear();
  registry.private_texture_copy_actions_by_destination_texture_slot.clear();
  registry.private_texture_copy_output_actions_by_destination_texture.clear();
  registry.private_texture_copy_output_actions_by_destination_texture_slot.clear();
  registry.private_texture_copy_action_by_wrapper_call.clear();
  registry.private_texture_copy_action_by_wrapper_context.clear();
  ++registry.private_texture_copy_index_serial;
  for (const auto& [key, actions] : registry.private_texture_copies_by_source) {
    (void)key;
    for (const FablePrivateTextureCopyAction& action : actions) {
      StorePrivateTextureCopyDirectAction(registry, action);
    }
  }
}

void StoreSurfaceTextureDirectEdge(FableGuestResourceRegistry& registry,
                                   const FableSurfaceTextureLink& link) {
  if (!link.trusted_destination_edge || link.texture == 0 ||
      link.source_surface == 0 || SurfaceTextureLinkIsTrivialSelfEdge(link)) {
    return;
  }
  if (link.copy_call_serial != 0) {
    const auto call_it =
        registry.surface_texture_link_by_copy_call.find(
            link.copy_call_serial);
    if (call_it == registry.surface_texture_link_by_copy_call.end() ||
        SurfaceTextureLinkShouldPrefer(link, &call_it->second)) {
      registry.surface_texture_link_by_copy_call[link.copy_call_serial] =
          link;
    }
  }
  if (link.copy_context_key != 0) {
    const auto context_it =
        registry.surface_texture_link_by_copy_context.find(
            link.copy_context_key);
    if (context_it == registry.surface_texture_link_by_copy_context.end() ||
        SurfaceTextureLinkShouldPrefer(link, &context_it->second)) {
      registry.surface_texture_link_by_copy_context[link.copy_context_key] =
          link;
    }
  }
  if (SurfaceTextureLinkRequiresExactWrapperLookup(link)) {
    return;
  }
  const auto source_surface_it =
      registry.surface_texture_link_by_source_surface.find(link.source_surface);
  if (source_surface_it ==
          registry.surface_texture_link_by_source_surface.end() ||
      SurfaceTextureLinkShouldPrefer(link, &source_surface_it->second)) {
    registry.surface_texture_link_by_source_surface[link.source_surface] =
        link;
  }
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  if (link.source_slot != kUnknownSlot) {
    const uint64_t source_slot_key =
        SurfaceTextureSourceSurfaceSlotKey(link.source_surface,
                                           link.source_slot);
    const auto source_slot_it =
        registry.surface_texture_link_by_source_surface_slot.find(
            source_slot_key);
    if (source_slot_it ==
            registry.surface_texture_link_by_source_surface_slot.end() ||
        SurfaceTextureLinkShouldPrefer(link, &source_slot_it->second)) {
      registry.surface_texture_link_by_source_surface_slot[source_slot_key] =
          link;
    }
  }

  std::vector<uint32_t> exact_destination_keys;
  std::vector<uint32_t> exact_base_keys;
  std::vector<uint64_t> exact_fetch_keys;
  CollectTextureIdentityIndexKeysForObject(registry, link.texture,
                                           exact_destination_keys,
                                           exact_base_keys, exact_fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, link.texture_base_address,
                                         exact_destination_keys,
                                         exact_base_keys, exact_fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(link.texture_fetch_dwords),
      exact_destination_keys, exact_base_keys, exact_fetch_keys);
  auto add_exact_destination_key = [&](uint32_t key) {
    if (key == 0) {
      return;
    }
    if (std::find(exact_destination_keys.begin(),
                  exact_destination_keys.end(),
                  key) == exact_destination_keys.end()) {
      exact_destination_keys.push_back(key);
    }
  };
  add_exact_destination_key(link.texture);
  add_exact_destination_key(link.texture_base_address);
  auto upsert_exact_destination_link =
      [&](std::vector<FableSurfaceTextureLink>& links) {
        for (FableSurfaceTextureLink& existing : links) {
          if (SameSurfaceTextureLinkIdentity(existing, link)) {
            existing = MergeSurfaceTextureLinkMetadata(link, existing);
            return;
          }
        }
        constexpr size_t kMaxSurfaceTextureLinksPerBucket = 32;
        if (links.size() >= kMaxSurfaceTextureLinksPerBucket) {
          links.erase(links.begin());
        }
        links.push_back(link);
      };
  for (uint32_t destination_texture : exact_destination_keys) {
    upsert_exact_destination_link(
        registry
            .surface_texture_exact_links_by_destination_texture
                [destination_texture]);
    const auto direct_it =
        registry.surface_texture_exact_link_by_destination_texture.find(
            destination_texture);
    if (direct_it ==
            registry.surface_texture_exact_link_by_destination_texture.end() ||
        SurfaceTextureLinkShouldPrefer(link, &direct_it->second)) {
      registry.surface_texture_exact_link_by_destination_texture
          [destination_texture] = link;
    }
    if (link.source_slot != kUnknownSlot) {
      const uint64_t slot_key =
          SurfaceTextureDestinationSlotKey(destination_texture,
                                           link.source_slot);
      upsert_exact_destination_link(
          registry
              .surface_texture_exact_links_by_destination_texture_slot
                  [slot_key]);
      const auto slot_it =
          registry.surface_texture_exact_link_by_destination_texture_slot.find(
              slot_key);
      if (slot_it ==
              registry.surface_texture_exact_link_by_destination_texture_slot
                  .end() ||
          SurfaceTextureLinkShouldPrefer(link, &slot_it->second)) {
        registry.surface_texture_exact_link_by_destination_texture_slot
            [slot_key] = link;
      }
    }
    StoreSurfaceDestinationRelation(registry, link, destination_texture);
  }

  std::vector<uint32_t> destination_keys;
  std::vector<uint32_t> base_keys;
  std::vector<uint64_t> fetch_keys;
  CollectTextureIdentityIndexKeysForObject(registry, link.texture,
                                           destination_keys, base_keys,
                                           fetch_keys);
  CollectTextureIdentityIndexKeysForObject(registry, link.backing_texture,
                                           destination_keys, base_keys,
                                           fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, link.texture_base_address,
                                         destination_keys, base_keys,
                                         fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, link.backing_base_address,
                                         destination_keys, base_keys,
                                         fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(link.texture_fetch_dwords),
      destination_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(link.backing_fetch_dwords),
      destination_keys, base_keys, fetch_keys);
  auto add_destination_key = [&](uint32_t key) {
    if (key == 0) {
      return;
    }
    if (std::find(destination_keys.begin(), destination_keys.end(), key) ==
        destination_keys.end()) {
      destination_keys.push_back(key);
    }
  };
  add_destination_key(link.texture);
  add_destination_key(link.backing_texture);

  for (uint32_t destination_texture : destination_keys) {
    const auto direct_it =
        registry.surface_texture_link_by_destination_texture.find(
            destination_texture);
    if (direct_it ==
            registry.surface_texture_link_by_destination_texture.end() ||
        SurfaceTextureLinkShouldPrefer(link, &direct_it->second)) {
      registry.surface_texture_link_by_destination_texture[destination_texture] =
          link;
    }
    if (link.source_slot != kUnknownSlot) {
      const uint64_t slot_key =
          SurfaceTextureDestinationSlotKey(destination_texture,
                                           link.source_slot);
      const auto slot_it =
          registry.surface_texture_link_by_destination_texture_slot.find(
              slot_key);
      if (slot_it ==
              registry.surface_texture_link_by_destination_texture_slot.end() ||
          SurfaceTextureLinkShouldPrefer(link, &slot_it->second)) {
        registry.surface_texture_link_by_destination_texture_slot[slot_key] =
            link;
      }
    }
  }
  for (uint32_t base_key : base_keys) {
    if (base_key == 0) {
      continue;
    }
    const auto base_it = registry.surface_texture_link_by_base.find(base_key);
    if (base_it == registry.surface_texture_link_by_base.end() ||
        SurfaceTextureLinkShouldPrefer(link, &base_it->second)) {
      registry.surface_texture_link_by_base[base_key] = link;
    }
  }
  for (uint64_t fetch_key : fetch_keys) {
    if (fetch_key == 0) {
      continue;
    }
    const auto fetch_it =
        registry.surface_texture_link_by_fetch_key.find(fetch_key);
    if (fetch_it == registry.surface_texture_link_by_fetch_key.end() ||
        SurfaceTextureLinkShouldPrefer(link, &fetch_it->second)) {
      registry.surface_texture_link_by_fetch_key[fetch_key] = link;
    }
  }

}

void RebuildSurfaceTextureDirectEdges(FableGuestResourceRegistry& registry) {
  registry.surface_texture_link_by_destination_texture.clear();
  registry.surface_texture_link_by_destination_texture_slot.clear();
  registry.surface_texture_exact_link_by_destination_texture.clear();
  registry.surface_texture_exact_link_by_destination_texture_slot.clear();
  registry.surface_texture_exact_links_by_destination_texture.clear();
  registry.surface_texture_exact_links_by_destination_texture_slot.clear();
  registry.surface_texture_link_by_source_surface.clear();
  registry.surface_texture_link_by_source_surface_slot.clear();
  registry.surface_texture_link_by_base.clear();
  registry.surface_texture_link_by_fetch_key.clear();
  registry.surface_texture_link_by_copy_call.clear();
  registry.surface_texture_link_by_copy_context.clear();
  registry.surface_destination_relations_by_source_surface.clear();
  registry.surface_destination_relations_by_destination_texture.clear();
  registry.surface_destination_relations_by_destination_texture_slot.clear();
  registry.surface_destination_relation_by_destination_texture.clear();
  registry.surface_destination_relation_by_destination_texture_slot.clear();
  for (const auto& [key, links] : registry.surface_texture_links_by_texture) {
    (void)key;
    for (const FableSurfaceTextureLink& link : links) {
      StoreSurfaceTextureDirectEdge(registry, link);
    }
  }
}

bool TextureIdentityMayRekeyPrivateCopyDirectActions(
    const FableGuestResourceRegistry& registry,
    const FableTextureIdentity& identity,
    uint64_t fetch_key) {
  if (identity.object_texture != 0 &&
      (registry.private_texture_copies_by_source.find(identity.object_texture) !=
           registry.private_texture_copies_by_source.end() ||
       registry.private_texture_copies_by_target.find(identity.object_texture) !=
           registry.private_texture_copies_by_target.end())) {
    return true;
  }
  if (identity.parent_texture != 0 &&
      identity.parent_texture != identity.object_texture &&
      (registry.private_texture_copies_by_source.find(identity.parent_texture) !=
           registry.private_texture_copies_by_source.end() ||
       registry.private_texture_copies_by_target.find(identity.parent_texture) !=
           registry.private_texture_copies_by_target.end())) {
    return true;
  }
  if (identity.base_address != 0 &&
      registry.private_texture_copies_by_base.find(identity.base_address) !=
          registry.private_texture_copies_by_base.end()) {
    return true;
  }
  return fetch_key != 0 &&
         registry.private_texture_copies_by_fetch_key.find(fetch_key) !=
             registry.private_texture_copies_by_fetch_key.end();
}

bool TextureIdentityMayRekeySurfaceTextureDirectEdges(
    const FableGuestResourceRegistry& registry,
    const FableTextureIdentity& identity,
    uint64_t fetch_key) {
  if (identity.object_texture != 0 &&
      (registry.surface_texture_links_by_texture.find(identity.object_texture) !=
           registry.surface_texture_links_by_texture.end() ||
       registry.surface_texture_links_by_surface.find(identity.object_texture) !=
           registry.surface_texture_links_by_surface.end())) {
    return true;
  }
  if (identity.parent_texture != 0 &&
      identity.parent_texture != identity.object_texture &&
      (registry.surface_texture_links_by_texture.find(identity.parent_texture) !=
           registry.surface_texture_links_by_texture.end() ||
       registry.surface_texture_links_by_surface.find(identity.parent_texture) !=
           registry.surface_texture_links_by_surface.end())) {
    return true;
  }
  if (identity.base_address != 0 &&
      registry.surface_texture_links_by_base.find(identity.base_address) !=
          registry.surface_texture_links_by_base.end()) {
    return true;
  }
  return fetch_key != 0 &&
         registry.surface_texture_links_by_fetch_key.find(fetch_key) !=
             registry.surface_texture_links_by_fetch_key.end();
}

bool PrivateTextureCopyActionReferencesResource(
    const FablePrivateTextureCopyAction& action, uint32_t resource) {
  return action.source_texture == resource ||
         action.source_base_address == resource ||
         action.backing_texture == resource ||
         action.backing_base_address == resource ||
         action.target_surface == resource ||
         action.render_target == resource ||
         action.depth_stencil == resource;
}

bool SurfaceTextureLinkReferencesResource(const FableSurfaceTextureLink& link,
                                          uint32_t resource) {
  return link.texture == resource || link.texture_base_address == resource ||
         link.source_surface == resource || link.backing_texture == resource ||
         link.backing_base_address == resource;
}

bool SurfaceDestinationRelationReferencesResource(
    const FableSurfaceDestinationTextureRelation& relation, uint32_t resource) {
  return relation.source_surface == resource ||
         relation.destination_texture == resource ||
         SurfaceTextureLinkReferencesResource(relation.link, resource);
}

void PrunePrivateTextureCopyDirectActionsForResource(
    FableGuestResourceRegistry& registry, uint32_t resource) {
  for (auto it =
           registry.private_texture_copy_action_by_destination_texture.begin();
       it != registry.private_texture_copy_action_by_destination_texture.end();) {
    if (it->first == resource ||
        PrivateTextureCopyActionReferencesResource(it->second, resource)) {
      it = registry.private_texture_copy_action_by_destination_texture.erase(it);
      ++registry.private_texture_copy_index_serial;
    } else {
      ++it;
    }
  }
  for (auto it =
           registry.private_texture_copy_action_by_destination_texture_slot
               .begin();
       it != registry.private_texture_copy_action_by_destination_texture_slot
                 .end();) {
    if (PrivateTextureCopyActionReferencesResource(it->second, resource)) {
      it = registry.private_texture_copy_action_by_destination_texture_slot
               .erase(it);
      ++registry.private_texture_copy_index_serial;
    } else {
      ++it;
    }
  }
  auto prune_action_index =
      [&](auto& index, bool key_is_resource) {
        for (auto it = index.begin(); it != index.end();) {
          auto& actions = it->second;
          actions.erase(
              std::remove_if(actions.begin(), actions.end(),
                             [&](const FablePrivateTextureCopyAction& action) {
                               return PrivateTextureCopyActionReferencesResource(
                                   action, resource);
                             }),
              actions.end());
          if (actions.empty() ||
              (key_is_resource &&
               static_cast<uint32_t>(it->first) == resource)) {
            it = index.erase(it);
            ++registry.private_texture_copy_index_serial;
          } else {
            ++it;
          }
        }
      };
  prune_action_index(
      registry.private_texture_copy_actions_by_destination_texture, true);
  prune_action_index(
      registry.private_texture_copy_actions_by_destination_texture_slot, false);
  prune_action_index(
      registry.private_texture_copy_output_actions_by_destination_texture, true);
  prune_action_index(
      registry.private_texture_copy_output_actions_by_destination_texture_slot,
      false);
  for (auto it =
           registry.private_texture_copy_action_by_wrapper_call.begin();
       it != registry.private_texture_copy_action_by_wrapper_call.end();) {
    if (PrivateTextureCopyActionReferencesResource(it->second, resource)) {
      it = registry.private_texture_copy_action_by_wrapper_call.erase(it);
      ++registry.private_texture_copy_index_serial;
    } else {
      ++it;
    }
  }
  for (auto it =
           registry.private_texture_copy_action_by_wrapper_context.begin();
       it != registry.private_texture_copy_action_by_wrapper_context.end();) {
    if (PrivateTextureCopyActionReferencesResource(it->second, resource)) {
      it = registry.private_texture_copy_action_by_wrapper_context.erase(it);
      ++registry.private_texture_copy_index_serial;
    } else {
      ++it;
    }
  }
}

void PruneSurfaceTextureDirectEdgesForResource(
    FableGuestResourceRegistry& registry, uint32_t resource) {
  auto prune_link_map = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if ((prune_resource_key &&
           static_cast<uint64_t>(it->first) ==
               static_cast<uint64_t>(resource)) ||
          SurfaceTextureLinkReferencesResource(it->second, resource)) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };
  auto prune_link_vector_map = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if (prune_resource_key &&
          static_cast<uint64_t>(it->first) ==
              static_cast<uint64_t>(resource)) {
        it = index.erase(it);
        continue;
      }
      std::vector<FableSurfaceTextureLink>& links = it->second;
      links.erase(
          std::remove_if(links.begin(), links.end(),
                         [&](const FableSurfaceTextureLink& link) {
                           return SurfaceTextureLinkReferencesResource(
                               link, resource);
                         }),
          links.end());
      if (links.empty()) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };
  for (auto it = registry.surface_texture_link_by_destination_texture.begin();
       it != registry.surface_texture_link_by_destination_texture.end();) {
    if (it->first == resource ||
        SurfaceTextureLinkReferencesResource(it->second, resource)) {
      it = registry.surface_texture_link_by_destination_texture.erase(it);
    } else {
      ++it;
    }
  }
  for (auto it =
           registry.surface_texture_link_by_destination_texture_slot.begin();
       it != registry.surface_texture_link_by_destination_texture_slot.end();) {
    if (SurfaceTextureLinkReferencesResource(it->second, resource)) {
      it = registry.surface_texture_link_by_destination_texture_slot.erase(it);
    } else {
      ++it;
    }
  }
  prune_link_map(registry.surface_texture_exact_link_by_destination_texture,
                 true);
  prune_link_map(registry.surface_texture_exact_link_by_destination_texture_slot,
                 false);
  prune_link_map(registry.surface_texture_link_by_copy_call, false);
  prune_link_map(registry.surface_texture_link_by_copy_context, false);
  prune_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture, true);
  prune_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture_slot, false);
  for (auto it = registry.surface_texture_link_by_source_surface.begin();
       it != registry.surface_texture_link_by_source_surface.end();) {
    if (it->first == resource ||
        SurfaceTextureLinkReferencesResource(it->second, resource)) {
      it = registry.surface_texture_link_by_source_surface.erase(it);
    } else {
      ++it;
    }
  }
	  for (auto it =
	           registry.surface_texture_link_by_source_surface_slot.begin();
	       it != registry.surface_texture_link_by_source_surface_slot.end();) {
	    if (SurfaceTextureLinkReferencesResource(it->second, resource)) {
	      it = registry.surface_texture_link_by_source_surface_slot.erase(it);
    } else {
	      ++it;
	    }
	  }
	  for (auto it = registry.surface_texture_link_by_base.begin();
	       it != registry.surface_texture_link_by_base.end();) {
	    if (it->first == resource ||
	        SurfaceTextureLinkReferencesResource(it->second, resource)) {
	      it = registry.surface_texture_link_by_base.erase(it);
	    } else {
	      ++it;
	    }
	  }
	  for (auto it = registry.surface_texture_link_by_fetch_key.begin();
	       it != registry.surface_texture_link_by_fetch_key.end();) {
	    if (SurfaceTextureLinkReferencesResource(it->second, resource)) {
	      it = registry.surface_texture_link_by_fetch_key.erase(it);
	    } else {
	      ++it;
	    }
	  }

  auto prune_relation_map = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if ((prune_resource_key &&
           static_cast<uint64_t>(it->first) ==
               static_cast<uint64_t>(resource)) ||
          SurfaceDestinationRelationReferencesResource(it->second, resource)) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };
  auto prune_relation_vector_map = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if (prune_resource_key &&
          static_cast<uint64_t>(it->first) ==
              static_cast<uint64_t>(resource)) {
        it = index.erase(it);
        continue;
      }
      std::vector<FableSurfaceDestinationTextureRelation>& relations =
          it->second;
      relations.erase(
          std::remove_if(
              relations.begin(), relations.end(),
              [&](const FableSurfaceDestinationTextureRelation& relation) {
                return SurfaceDestinationRelationReferencesResource(relation,
                                                                    resource);
              }),
          relations.end());
      if (relations.empty()) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };
  prune_relation_vector_map(
      registry.surface_destination_relations_by_source_surface, true);
  prune_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture, true);
  prune_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture_slot, false);
  prune_relation_map(registry.surface_destination_relation_by_destination_texture,
                     true);
  prune_relation_map(
      registry.surface_destination_relation_by_destination_texture_slot, false);
}

void PrunePrivateTextureCopyDirectActionsForTextureIdentityLocked(
    FableGuestResourceRegistry& registry, const FableTextureIdentity& identity) {
  std::vector<uint32_t> resource_keys;
  AddUnique(resource_keys, identity.object_texture);
  AddUnique(resource_keys, identity.parent_texture);
  AddUnique(resource_keys, identity.base_address);
  for (uint32_t resource : resource_keys) {
    PrunePrivateTextureCopyDirectActionsForResource(registry, resource);
  }
}

void PruneSurfaceTextureDirectEdgesForTextureIdentityLocked(
    FableGuestResourceRegistry& registry, const FableTextureIdentity& identity) {
  std::vector<uint32_t> resource_keys;
  AddUnique(resource_keys, identity.object_texture);
  AddUnique(resource_keys, identity.parent_texture);
  AddUnique(resource_keys, identity.base_address);
  for (uint32_t resource : resource_keys) {
    PruneSurfaceTextureDirectEdgesForResource(registry, resource);
  }

  const uint64_t fetch_key = FableDirectTextureFetchKey(identity.fetch_dwords);
  if (fetch_key != 0) {
    registry.surface_texture_link_by_fetch_key.erase(fetch_key);
  }
}

void BackfillTextureIdentityDependentIndexesLocked(
    FableGuestResourceRegistry& registry,
    const FableTextureIdentity& identity) {
  std::vector<uint32_t> object_keys;
  std::vector<uint32_t> base_keys;
  std::vector<uint64_t> fetch_keys;
  CollectTextureIdentityIndexKeys(identity, object_keys, base_keys, fetch_keys);

  std::vector<FablePrivateTextureCopyAction> actions;
  const auto collect_action_bucket =
      [&](const std::vector<FablePrivateTextureCopyAction>* bucket) {
        if (!bucket) {
          return;
        }
        for (const FablePrivateTextureCopyAction& action : *bucket) {
          const auto existing = std::find_if(
              actions.begin(), actions.end(),
              [&](const FablePrivateTextureCopyAction& candidate) {
                return SamePrivateTextureCopyActionIdentity(candidate, action);
              });
          if (existing == actions.end()) {
            actions.push_back(action);
          }
        }
      };
  const auto find_bucket = [](const auto& index, auto key) {
    const auto it = index.find(key);
    return it != index.end() ? &it->second : nullptr;
  };
  for (uint32_t object_key : object_keys) {
    collect_action_bucket(
        find_bucket(registry.private_texture_copies_by_source, object_key));
    collect_action_bucket(
        find_bucket(registry.private_texture_copies_by_target, object_key));
  }
  for (uint32_t base_key : base_keys) {
    collect_action_bucket(
        find_bucket(registry.private_texture_copies_by_base, base_key));
  }
  for (uint64_t fetch_key : fetch_keys) {
    collect_action_bucket(
        find_bucket(registry.private_texture_copies_by_fetch_key, fetch_key));
  }

  std::vector<FableSurfaceTextureLink> links;
  const auto collect_link_bucket =
      [&](const std::vector<FableSurfaceTextureLink>* bucket) {
        if (!bucket) {
          return;
        }
        for (const FableSurfaceTextureLink& link : *bucket) {
          const auto existing = std::find_if(
              links.begin(), links.end(),
              [&](const FableSurfaceTextureLink& candidate) {
                return SameSurfaceTextureLinkIdentity(candidate, link);
              });
          if (existing == links.end()) {
            links.push_back(link);
          }
        }
      };
  for (uint32_t object_key : object_keys) {
    collect_link_bucket(
        find_bucket(registry.surface_texture_links_by_texture, object_key));
    collect_link_bucket(
        find_bucket(registry.surface_texture_links_by_surface, object_key));
  }
  for (uint32_t base_key : base_keys) {
    collect_link_bucket(
        find_bucket(registry.surface_texture_links_by_base, base_key));
  }
  for (uint64_t fetch_key : fetch_keys) {
    collect_link_bucket(
        find_bucket(registry.surface_texture_links_by_fetch_key, fetch_key));
  }
  for (const auto& [destination, link] :
       registry.surface_texture_link_by_destination_texture) {
    (void)destination;
    const bool endpoint_match =
        std::find(object_keys.begin(), object_keys.end(), link.texture) !=
            object_keys.end() ||
        std::find(object_keys.begin(), object_keys.end(), link.backing_texture) !=
            object_keys.end() ||
        std::find(base_keys.begin(), base_keys.end(),
                  link.texture_base_address) != base_keys.end() ||
        std::find(base_keys.begin(), base_keys.end(),
                  link.backing_base_address) != base_keys.end() ||
        std::find(fetch_keys.begin(), fetch_keys.end(),
                  FableDirectTextureFetchKey(link.texture_fetch_dwords)) !=
            fetch_keys.end() ||
        std::find(fetch_keys.begin(), fetch_keys.end(),
                  FableDirectTextureFetchKey(link.backing_fetch_dwords)) !=
            fetch_keys.end();
    if (!endpoint_match) {
      continue;
    }
    const auto existing = std::find_if(
        links.begin(), links.end(), [&](const FableSurfaceTextureLink& candidate) {
          return SameSurfaceTextureLinkIdentity(candidate, link);
        });
    if (existing == links.end()) {
      links.push_back(link);
    }
  }

  for (const FablePrivateTextureCopyAction& action : actions) {
    FableDirectRegisterPrivateTextureCopyAction(action);
  }
  for (const FableSurfaceTextureLink& link : links) {
    FableDirectRegisterSurfaceTextureLink(link);
  }
}

void RemoveTextureIdentityIndexesLocked(FableGuestResourceRegistry& registry,
                                        const FableTextureIdentity& identity) {
  const uint32_t object_texture = identity.object_texture;
  const uint32_t base_address = identity.base_address;
  const uint64_t fetch_key = FableDirectTextureFetchKey(identity.fetch_dwords);

  if (base_address != 0) {
    const auto base_objects_it =
        registry.texture_identity_objects_by_base.find(base_address);
    if (base_objects_it != registry.texture_identity_objects_by_base.end()) {
      std::vector<uint32_t>& objects = base_objects_it->second;
      objects.erase(std::remove(objects.begin(), objects.end(), object_texture),
                    objects.end());
      if (objects.empty()) {
        registry.texture_identity_objects_by_base.erase(base_objects_it);
      }
    }
  }

  if (fetch_key != 0) {
    const auto fetch_it =
        registry.texture_identity_objects_by_fetch_key.find(fetch_key);
    if (fetch_it != registry.texture_identity_objects_by_fetch_key.end()) {
      std::vector<uint32_t>& objects = fetch_it->second;
      objects.erase(std::remove(objects.begin(), objects.end(), object_texture),
                    objects.end());
      if (objects.empty()) {
        registry.texture_identity_objects_by_fetch_key.erase(fetch_it);
      }
    }
  }
}

}  // namespace

FableGuestResourceRegistry& FableDirectGuestResourceRegistry() {
  return g_fable_guest_resources;
}

std::recursive_mutex& FableDirectGuestResourceRegistryMutex() {
  return g_fable_guest_resource_registry_mutex;
}

uint64_t FableDirectTextureFetchKey(
    const std::array<uint32_t, 6>& fetch_dwords) {
  if (!HasTextureIdentityFetchDwords(fetch_dwords)) {
    return 0;
  }

  const std::array<uint32_t, 6> key_dwords =
      NormalizeTextureFetchKeyDwords(fetch_dwords);
  uint64_t hash = 1469598103934665603ull;
  for (uint32_t dword : key_dwords) {
    hash ^= dword;
    hash *= 1099511628211ull;
  }
  return hash != 0 ? hash : 1;
}

void FableDirectRegisterTextureIdentity(
    const FableTextureIdentity& identity) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (identity.object_texture == 0 ||
      !HasTextureIdentityFetchDwords(identity.fetch_dwords)) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto existing_it =
      registry.texture_identities_by_object.find(identity.object_texture);
  if (existing_it != registry.texture_identities_by_object.end() &&
      SameTextureIdentity(existing_it->second, identity)) {
    return;
  }
  FableTextureIdentity previous_identity;
  bool had_previous_identity = false;
  bool previous_identity_may_rekey_private = false;
  bool previous_identity_may_rekey_surface = false;
  if (existing_it != registry.texture_identities_by_object.end()) {
    previous_identity = existing_it->second;
    had_previous_identity = true;
    const uint64_t previous_fetch_key =
        FableDirectTextureFetchKey(previous_identity.fetch_dwords);
    previous_identity_may_rekey_private =
        TextureIdentityMayRekeyPrivateCopyDirectActions(
            registry, previous_identity, previous_fetch_key);
    previous_identity_may_rekey_surface =
        TextureIdentityMayRekeySurfaceTextureDirectEdges(
            registry, previous_identity, previous_fetch_key);
    RemoveTextureIdentityIndexesLocked(registry, previous_identity);
  }

  FableTextureIdentity stored = identity;
  stored.serial = ++registry.texture_identity_serial;
  registry.texture_identities_by_object[stored.object_texture] = stored;

  if (stored.base_address != 0) {
    std::vector<uint32_t>& objects =
        registry.texture_identity_objects_by_base[stored.base_address];
    if (std::find(objects.begin(), objects.end(), stored.object_texture) ==
        objects.end()) {
      objects.push_back(stored.object_texture);
    }
  }
  const uint64_t fetch_key = FableDirectTextureFetchKey(stored.fetch_dwords);
  if (fetch_key != 0) {
    std::vector<uint32_t>& objects =
        registry.texture_identity_objects_by_fetch_key[fetch_key];
    if (std::find(objects.begin(), objects.end(), stored.object_texture) ==
        objects.end()) {
      objects.push_back(stored.object_texture);
    }
  }

  const bool stored_identity_may_rekey_private =
      TextureIdentityMayRekeyPrivateCopyDirectActions(registry, stored,
                                                      fetch_key);
  const bool stored_identity_may_rekey_surface =
      TextureIdentityMayRekeySurfaceTextureDirectEdges(registry, stored,
                                                       fetch_key);
  if (had_previous_identity &&
      (previous_identity_may_rekey_private ||
       stored_identity_may_rekey_private)) {
    PrunePrivateTextureCopyDirectActionsForTextureIdentityLocked(
        registry, previous_identity);
  }
  if (had_previous_identity &&
      (previous_identity_may_rekey_surface ||
       stored_identity_may_rekey_surface)) {
    PruneSurfaceTextureDirectEdgesForTextureIdentityLocked(
        registry, previous_identity);
  }

  BackfillTextureIdentityDependentIndexesLocked(registry, stored);
}

void FableDirectForgetTextureIdentity(uint32_t object_texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_texture == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto identity_it =
      registry.texture_identities_by_object.find(object_texture);
  if (identity_it == registry.texture_identities_by_object.end()) {
    return;
  }

  const FableTextureIdentity removed_identity = identity_it->second;
  RemoveTextureIdentityIndexesLocked(registry, removed_identity);
  registry.texture_identities_by_object.erase(identity_it);
  PrunePrivateTextureCopyDirectActionsForResource(registry, object_texture);
  PruneSurfaceTextureDirectEdgesForResource(registry, object_texture);
}

void FableDirectClearTextureIdentities() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.texture_identity_serial = 0;
  registry.texture_identities_by_object.clear();
  registry.texture_identity_objects_by_base.clear();
  registry.texture_identity_objects_by_fetch_key.clear();
  RebuildPrivateTextureCopyDirectActions(registry);
  RebuildSurfaceTextureDirectEdges(registry);
}

const FableTextureIdentity* FableDirectFindTextureIdentityByObject(
    uint32_t object_texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.texture_identities_by_object.find(object_texture);
  return it != registry.texture_identities_by_object.end() ? &it->second
                                                          : nullptr;
}

const FableTextureIdentity* FableDirectFindTextureIdentityByBase(
    uint32_t base_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  return SelectUniqueTextureIdentity(
      FableDirectFindTextureIdentityObjectsByBase(base_address));
}

const std::vector<uint32_t>* FableDirectFindTextureIdentityObjectsByBase(
    uint32_t base_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (base_address == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto base_objects_it =
      registry.texture_identity_objects_by_base.find(base_address);
  if (base_objects_it == registry.texture_identity_objects_by_base.end()) {
    return nullptr;
  }
  return &base_objects_it->second;
}

const FableTextureIdentity* FableDirectFindTextureIdentityByFetch(
    const std::array<uint32_t, 6>& fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  return SelectUniqueTextureIdentity(
      FableDirectFindTextureIdentityObjectsByFetch(fetch_dwords));
}

const std::vector<uint32_t>* FableDirectFindTextureIdentityObjectsByFetch(
    const std::array<uint32_t, 6>& fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t key = FableDirectTextureFetchKey(fetch_dwords);
  if (key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto fetch_it =
      registry.texture_identity_objects_by_fetch_key.find(key);
  if (fetch_it == registry.texture_identity_objects_by_fetch_key.end()) {
    return nullptr;
  }
  return &fetch_it->second;
}

void FableDirectRegisterTextureFetchBindingAction(
    const FableTextureFetchBindingAction& action) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t slot_fetch_key =
      TextureFetchBindingSlotKey(action.texture_slot, action.fetch_dwords);
  if (slot_fetch_key == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  std::vector<FableTextureFetchBindingAction>& actions =
      registry.texture_fetch_bindings_by_slot_fetch_key[slot_fetch_key];
  if (!actions.empty() &&
      SameTextureFetchBindingAction(actions.back(), action)) {
    return;
  }

  FableTextureFetchBindingAction stored = action;
  stored.serial = ++registry.texture_fetch_binding_serial;
  actions.push_back(stored);
  constexpr size_t kMaxBindingsPerSlotFetch = 128u;
  if (actions.size() > kMaxBindingsPerSlotFetch) {
    actions.erase(actions.begin(), actions.begin() +
                                     (actions.size() - kMaxBindingsPerSlotFetch));
  }
}

void FableDirectClearTextureFetchBindingActions() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.texture_fetch_binding_serial = 0;
  registry.texture_fetch_bindings_by_slot_fetch_key.clear();
  FableDirectClearTextureFetchResourceLinks();
}

const FableTextureFetchBindingAction*
FableDirectFindTextureFetchBindingActionForSlotFetch(
    uint32_t texture_slot, const std::array<uint32_t, 6>& fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t slot_fetch_key =
      TextureFetchBindingSlotKey(texture_slot, fetch_dwords);
  if (slot_fetch_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.texture_fetch_bindings_by_slot_fetch_key.find(slot_fetch_key);
  if (it == registry.texture_fetch_bindings_by_slot_fetch_key.end() ||
      it->second.empty()) {
    return nullptr;
  }
  return &it->second.back();
}

const FableTextureFetchBindingAction*
FableDirectFindTextureFetchBindingActionForSlotFetchSymbol(
    uint32_t texture_slot, const std::array<uint32_t, 6>& fetch_dwords,
    uint32_t symbol_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t slot_fetch_key =
      TextureFetchBindingSlotKey(texture_slot, fetch_dwords);
  if (slot_fetch_key == 0 || symbol_address == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.texture_fetch_bindings_by_slot_fetch_key.find(slot_fetch_key);
  if (it == registry.texture_fetch_bindings_by_slot_fetch_key.end() ||
      it->second.empty()) {
    return nullptr;
  }
  for (auto action_it = it->second.rbegin(); action_it != it->second.rend();
       ++action_it) {
    if (action_it->symbol_address == symbol_address) {
      return &*action_it;
    }
  }
  return nullptr;
}

void FableDirectRegisterTextureFetchResourceLink(
    const FableTextureFetchResourceLink& link) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (link.texture_resource == 0 || link.texture_slot >= 32u ||
      FableDirectTextureFetchKey(link.fetch_dwords) == 0 ||
      !link.header_fetch_payload_match) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  FableTextureFetchResourceLink stored = link;
  constexpr size_t kMaxTextureFetchResourceLinksPerBucket = 64;
  auto upsert_link =
      [&](std::vector<FableTextureFetchResourceLink>& links) {
        for (FableTextureFetchResourceLink& existing : links) {
          if (SameTextureFetchResourceLink(existing, stored)) {
            if (stored.serial == 0) {
              stored.serial = existing.serial;
            }
            existing = stored;
            return;
          }
        }
        if (stored.serial == 0) {
          stored.serial = ++registry.texture_fetch_resource_link_serial;
        }
        if (links.size() >= kMaxTextureFetchResourceLinksPerBucket) {
          links.erase(links.begin());
        }
        links.push_back(stored);
      };

  upsert_link(
      registry.texture_fetch_resource_links_by_resource[stored.texture_resource]);
  const uint64_t slot_resource_key =
      TextureFetchResourceSlotKey(stored.texture_slot, stored.texture_resource);
  if (slot_resource_key != 0) {
    upsert_link(
        registry.texture_fetch_resource_links_by_slot_resource[slot_resource_key]);
  }
  if (stored.base_address != 0) {
    upsert_link(registry.texture_fetch_resource_links_by_base[stored.base_address]);
  }
  const uint64_t fetch_key = FableDirectTextureFetchKey(stored.fetch_dwords);
  if (fetch_key != 0) {
    upsert_link(registry.texture_fetch_resource_links_by_fetch_key[fetch_key]);
    const uint64_t slot_fetch_key =
        TextureFetchBindingSlotKey(stored.texture_slot, stored.fetch_dwords);
    if (slot_fetch_key != 0) {
      upsert_link(
          registry.texture_fetch_resource_links_by_slot_fetch_key[slot_fetch_key]);
    }
  }
}

void FableDirectForgetTextureFetchResourceLinksForResource(uint32_t resource) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (resource == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto link_matches =
      [resource](const FableTextureFetchResourceLink& link) {
        return TextureFetchResourceLinkReferencesResource(link, resource);
      };
  const auto prune_index = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if (prune_resource_key &&
          static_cast<uint64_t>(it->first) == static_cast<uint64_t>(resource)) {
        it = index.erase(it);
        continue;
      }
      std::vector<FableTextureFetchResourceLink>& links = it->second;
      links.erase(std::remove_if(links.begin(), links.end(), link_matches),
                  links.end());
      if (links.empty()) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };

  prune_index(registry.texture_fetch_resource_links_by_resource, true);
  prune_index(registry.texture_fetch_resource_links_by_slot_resource, false);
  prune_index(registry.texture_fetch_resource_links_by_base, true);
  prune_index(registry.texture_fetch_resource_links_by_fetch_key, false);
  prune_index(registry.texture_fetch_resource_links_by_slot_fetch_key, false);
}

void FableDirectClearTextureFetchResourceLinks() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.texture_fetch_resource_link_serial = 0;
  registry.texture_fetch_resource_links_by_resource.clear();
  registry.texture_fetch_resource_links_by_slot_resource.clear();
  registry.texture_fetch_resource_links_by_base.clear();
  registry.texture_fetch_resource_links_by_fetch_key.clear();
  registry.texture_fetch_resource_links_by_slot_fetch_key.clear();
}

const FableTextureFetchResourceLink*
FableDirectFindVisibleTextureFetchResourceLinkForResourceSlot(
    uint32_t resource, uint32_t texture_slot, uint32_t frame_index,
    uint64_t draw_serial) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (resource == 0 || texture_slot >= 32u) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_resource_key =
      TextureFetchResourceSlotKey(texture_slot, resource);
  const auto slot_it =
      registry.texture_fetch_resource_links_by_slot_resource.find(
          slot_resource_key);
  if (slot_it != registry.texture_fetch_resource_links_by_slot_resource.end()) {
    if (const FableTextureFetchResourceLink* link =
            SelectVisibleTextureFetchResourceLink(&slot_it->second, frame_index,
                                                  draw_serial, texture_slot)) {
      return link;
    }
  }
  const auto it = registry.texture_fetch_resource_links_by_resource.find(resource);
  return it != registry.texture_fetch_resource_links_by_resource.end()
             ? SelectVisibleTextureFetchResourceLink(&it->second, frame_index,
                                                     draw_serial, texture_slot)
             : nullptr;
}

const FableTextureFetchResourceLink*
FableDirectFindVisibleTextureFetchResourceLinkForSlotFetch(
    uint32_t texture_slot, const std::array<uint32_t, 6>& fetch_dwords,
    uint32_t frame_index, uint64_t draw_serial) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t slot_fetch_key =
      TextureFetchBindingSlotKey(texture_slot, fetch_dwords);
  if (slot_fetch_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto slot_it =
      registry.texture_fetch_resource_links_by_slot_fetch_key.find(
          slot_fetch_key);
  if (slot_it != registry.texture_fetch_resource_links_by_slot_fetch_key.end()) {
    if (const FableTextureFetchResourceLink* link =
            SelectVisibleTextureFetchResourceLink(&slot_it->second, frame_index,
                                                  draw_serial, texture_slot)) {
      return link;
    }
  }
  const uint64_t fetch_key = FableDirectTextureFetchKey(fetch_dwords);
  const auto it = registry.texture_fetch_resource_links_by_fetch_key.find(fetch_key);
  return it != registry.texture_fetch_resource_links_by_fetch_key.end()
             ? SelectVisibleTextureFetchResourceLink(&it->second, frame_index,
                                                     draw_serial, texture_slot)
             : nullptr;
}

const FableTextureFetchResourceLink*
FableDirectFindVisibleTextureFetchResourceLinkForBase(
    uint32_t base_address, uint32_t texture_slot, uint32_t frame_index,
    uint64_t draw_serial) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (base_address == 0 || texture_slot >= 32u) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.texture_fetch_resource_links_by_base.find(base_address);
  return it != registry.texture_fetch_resource_links_by_base.end()
             ? SelectVisibleTextureFetchResourceLink(&it->second, frame_index,
                                                     draw_serial, texture_slot)
             : nullptr;
}

void FableDirectRegisterTextureHeaderAction(
    const FableTextureHeaderAction& action) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (action.fetch_address == 0 ||
      !HasTextureIdentityFetchDwords(action.fetch_dwords)) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  FableTextureHeaderAction stored = action;
  stored.serial = ++registry.texture_header_action_serial;
  constexpr size_t kMaxTextureHeaderActionsPerBucket = 128u;
  auto upsert_action = [&](std::vector<FableTextureHeaderAction>& actions) {
    if (!actions.empty() &&
        SameTextureHeaderAction(actions.back(), stored)) {
      return;
    }
    actions.push_back(stored);
    if (actions.size() > kMaxTextureHeaderActionsPerBucket) {
      actions.erase(actions.begin(),
                    actions.begin() +
                        (actions.size() - kMaxTextureHeaderActionsPerBucket));
    }
  };

  upsert_action(
      registry.texture_header_actions_by_fetch_address[stored.fetch_address]);
}

void FableDirectClearTextureHeaderActions() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.texture_header_action_serial = 0;
  registry.texture_header_actions_by_fetch_address.clear();
}

const FableTextureHeaderAction* FableDirectFindTextureHeaderActionForFetch(
    uint32_t fetch_address, const std::array<uint32_t, 6>& fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (fetch_address == 0 || !HasTextureIdentityFetchDwords(fetch_dwords)) {
    return nullptr;
  }

  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto address_it =
      registry.texture_header_actions_by_fetch_address.find(fetch_address);
  if (address_it != registry.texture_header_actions_by_fetch_address.end()) {
    const uint64_t fetch_key = FableDirectTextureFetchKey(fetch_dwords);
    for (auto it = address_it->second.rbegin();
         it != address_it->second.rend(); ++it) {
      if (FableDirectTextureFetchKey(it->fetch_dwords) == fetch_key) {
        return &*it;
      }
    }
    for (auto it = address_it->second.rbegin();
         it != address_it->second.rend(); ++it) {
      if (it->stack_scratch) {
        return &*it;
      }
    }
  }
  return nullptr;
}

const FableTextureHeaderAction*
FableDirectFindTextureHeaderActionForExactResource(
    uint32_t texture_header, uint32_t texture_object, uint32_t parent_texture,
    uint32_t base_address, uint32_t byte_size, uint32_t width, uint32_t height,
    uint32_t format, const std::array<uint32_t, 6>& fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (base_address == 0 || byte_size == 0 || width == 0 || height == 0 ||
      !HasTextureIdentityFetchDwords(fetch_dwords)) {
    return nullptr;
  }

  const uint64_t fetch_key = FableDirectTextureFetchKey(fetch_dwords);
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const FableTextureHeaderAction* best = nullptr;
  const auto action_matches_address =
      [&](const FableTextureHeaderAction& action, uint32_t address) {
        return address != 0 &&
               (action.texture_header == address ||
                action.texture_object == address ||
                action.parent_texture == address);
      };

  for (const auto& bucket : registry.texture_header_actions_by_fetch_address) {
    const auto& actions = bucket.second;
    for (auto it = actions.rbegin(); it != actions.rend(); ++it) {
      const FableTextureHeaderAction& action = *it;
      if (action.stack_scratch ||
          FableDirectTextureFetchKey(action.fetch_dwords) != fetch_key ||
          action.base_address != base_address ||
          action.byte_size != byte_size ||
          action.width != width ||
          action.height != height ||
          action.format != format) {
        continue;
      }
      if (!action_matches_address(action, texture_header) &&
          !action_matches_address(action, texture_object) &&
          !action_matches_address(action, parent_texture)) {
        continue;
      }
      if (best == nullptr || action.serial > best->serial) {
        best = &action;
      }
    }
  }
  return best;
}

const FableTextureHeaderAction*
FableDirectFindTextureHeaderActionForExactFetchIdentity(
    uint32_t base_address, uint32_t width, uint32_t height, uint32_t format,
    const std::array<uint32_t, 6>& fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (base_address == 0 || width == 0 || height == 0 ||
      !HasTextureIdentityFetchDwords(fetch_dwords)) {
    return nullptr;
  }

  const uint64_t fetch_key = FableDirectTextureFetchKey(fetch_dwords);
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const FableTextureHeaderAction* best = nullptr;
  for (const auto& bucket : registry.texture_header_actions_by_fetch_address) {
    const auto& actions = bucket.second;
    for (auto it = actions.rbegin(); it != actions.rend(); ++it) {
      const FableTextureHeaderAction& action = *it;
      if (action.stack_scratch ||
          FableDirectTextureFetchKey(action.fetch_dwords) != fetch_key ||
          action.base_address != base_address || action.width != width ||
          action.height != height || action.format != format) {
        continue;
      }
      if (best == nullptr || action.serial > best->serial) {
        best = &action;
      }
    }
  }
  return best;
}

void FableDirectRegisterSurfaceTextureLink(
    const FableSurfaceTextureLink& link) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (link.texture == 0 || link.source_surface == 0) {
    return;
  }
  if ((link.source_surface & 0xFF000000u) == 0xF1000000u) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  FableSurfaceTextureLink stored = link;
  EnrichSurfaceTextureLinkShaderRootsFromRegisteredActions(registry, stored);
  NormalizeSurfaceTextureLinkSampleSource(stored);
  RepairSurfaceTextureLinksFromSurfaceTextureLinkSample(registry, stored);
  if (SurfaceTextureLinkIsTrivialSelfEdge(stored)) {
    return;
  }
  const bool has_exact_copy_identity =
      stored.copy_call_serial != 0 || stored.copy_context_key != 0 ||
      stored.copy_symbol_address != 0;
  if (stored.trusted_destination_edge && !has_exact_copy_identity &&
      stored.backing_texture == 0 &&
      stored.backing_base_address == 0 && stored.target_content_serial == 0 &&
      stored.sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface) {
    return;
  }
  constexpr size_t kMaxSurfaceTextureLinksPerBucket = 32;
  auto upsert_link = [&](std::vector<FableSurfaceTextureLink>& links) {
    for (FableSurfaceTextureLink& existing : links) {
      if (SameSurfaceTextureLinkIdentity(existing, stored)) {
        stored = MergeSurfaceTextureLinkMetadata(stored, existing);
        existing = stored;
        return;
      }
    }
    if (stored.serial == 0) {
      stored.serial = ++registry.surface_texture_link_serial;
    }
    if (links.size() >= kMaxSurfaceTextureLinksPerBucket) {
      links.erase(links.begin());
    }
    links.push_back(stored);
  };

  const auto upsert_u32_key = [&](auto& index, uint32_t key) {
    if (key != 0) {
      upsert_link(index[key]);
    }
  };
  const auto upsert_fetch_key_value = [&](uint64_t key) {
    if (key != 0) {
      upsert_link(registry.surface_texture_links_by_fetch_key[key]);
    }
  };

  std::vector<uint32_t> texture_keys;
  std::vector<uint32_t> base_keys;
  std::vector<uint64_t> fetch_keys;
  CollectTextureIdentityIndexKeysForObject(registry, stored.texture,
                                           texture_keys, base_keys,
                                           fetch_keys);
  CollectTextureIdentityIndexKeysForObject(registry, stored.backing_texture,
                                           texture_keys, base_keys,
                                           fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, stored.texture_base_address,
                                         texture_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, stored.backing_base_address,
                                         texture_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(stored.texture_fetch_dwords),
      texture_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(stored.backing_fetch_dwords),
      texture_keys, base_keys, fetch_keys);

  if (SurfaceTextureLinkRequiresExactWrapperLookup(stored)) {
    if (stored.serial == 0) {
      stored.serial = ++registry.surface_texture_link_serial;
    }
    StoreSurfaceTextureDirectEdge(registry, stored);
    return;
  }

  // Treat a Fable surface/texture link as an edge in the object graph. Index it
  // from both the proxy/source side and the backing side so direct rendering can
  // resolve by whichever object/base/fetch the title/menu draw binds.
  for (uint32_t texture_key : texture_keys) {
    upsert_u32_key(registry.surface_texture_links_by_texture, texture_key);
  }
  upsert_u32_key(registry.surface_texture_links_by_surface,
                 stored.source_surface);
  upsert_u32_key(registry.surface_texture_links_by_surface,
                 stored.render_target);
  for (uint32_t base_key : base_keys) {
    upsert_u32_key(registry.surface_texture_links_by_base, base_key);
  }
  for (uint64_t fetch_key : fetch_keys) {
    upsert_fetch_key_value(fetch_key);
  }
  StoreSurfaceTextureDirectEdge(registry, stored);
}

void FableDirectForgetSurfaceTextureLinksForResource(uint32_t resource) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (resource == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const bool has_indexed_link =
      registry.surface_texture_links_by_texture.find(resource) !=
          registry.surface_texture_links_by_texture.end() ||
      registry.surface_texture_links_by_surface.find(resource) !=
          registry.surface_texture_links_by_surface.end() ||
      registry.surface_texture_links_by_base.find(resource) !=
          registry.surface_texture_links_by_base.end() ||
      registry.surface_texture_link_by_source_surface.find(resource) !=
          registry.surface_texture_link_by_source_surface.end() ||
      registry.surface_destination_relations_by_source_surface.find(resource) !=
          registry.surface_destination_relations_by_source_surface.end() ||
      registry.surface_destination_relations_by_destination_texture.find(
          resource) !=
          registry.surface_destination_relations_by_destination_texture.end() ||
      registry.surface_destination_relation_by_destination_texture.find(
          resource) !=
          registry.surface_destination_relation_by_destination_texture.end();
  if (!has_indexed_link) {
    return;
  }

  const auto link_matches = [resource](const FableSurfaceTextureLink& link) {
    // Shader, depth, and render-target fields describe the context where Fable
    // discovered the edge. They are not ownership endpoints for the
    // texture/surface relationship, and pruning on them can erase a valid title
    // copy edge between adjacent GUI draws.
    return SurfaceTextureLinkReferencesResource(link, resource);
  };

  bool removed_any = false;
  const auto prune_index = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if (prune_resource_key &&
          static_cast<uint64_t>(it->first) == static_cast<uint64_t>(resource)) {
        it = index.erase(it);
        removed_any = true;
        continue;
      }
      std::vector<FableSurfaceTextureLink>& links = it->second;
      const size_t before = links.size();
      links.erase(std::remove_if(links.begin(), links.end(), link_matches),
                  links.end());
      removed_any = removed_any || links.size() != before;
      if (links.empty()) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };

  prune_index(registry.surface_texture_links_by_texture, true);
  prune_index(registry.surface_texture_links_by_surface, true);
  prune_index(registry.surface_texture_links_by_base, true);
  prune_index(registry.surface_texture_links_by_fetch_key, false);
  if (removed_any) {
    PruneSurfaceTextureDirectEdgesForResource(registry, resource);
    ++registry.surface_texture_link_serial;
  }
}

void FableDirectClearSurfaceTextureLinks() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.surface_texture_link_serial = 0;
  registry.surface_texture_links_by_texture.clear();
  registry.surface_texture_links_by_surface.clear();
  registry.surface_texture_links_by_base.clear();
  registry.surface_texture_links_by_fetch_key.clear();
  registry.surface_texture_link_by_destination_texture.clear();
  registry.surface_texture_link_by_destination_texture_slot.clear();
  registry.surface_texture_exact_link_by_destination_texture.clear();
  registry.surface_texture_exact_link_by_destination_texture_slot.clear();
  registry.surface_texture_exact_links_by_destination_texture.clear();
  registry.surface_texture_exact_links_by_destination_texture_slot.clear();
  registry.surface_texture_link_by_source_surface.clear();
  registry.surface_texture_link_by_source_surface_slot.clear();
  registry.surface_texture_link_by_base.clear();
  registry.surface_texture_link_by_fetch_key.clear();
  registry.surface_texture_link_by_copy_call.clear();
  registry.surface_texture_link_by_copy_context.clear();
  registry.surface_destination_relation_serial = 0;
  registry.surface_destination_relations_by_source_surface.clear();
  registry.surface_destination_relations_by_destination_texture.clear();
  registry.surface_destination_relations_by_destination_texture_slot.clear();
  registry.surface_destination_relation_by_destination_texture.clear();
  registry.surface_destination_relation_by_destination_texture_slot.clear();
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByTexture(
    uint32_t texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  return SelectBestSurfaceTextureLink(
      FableDirectFindSurfaceTextureLinksByTexture(texture));
}

const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksByTexture(uint32_t texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.surface_texture_links_by_texture.find(texture);
  return it != registry.surface_texture_links_by_texture.end() ? &it->second
                                                               : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForDestinationTexture(uint32_t texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_exact_link_by_destination_texture.find(texture);
  return it != registry.surface_texture_exact_link_by_destination_texture.end()
             ? &it->second
             : nullptr;
}

const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksForDestinationTexture(uint32_t texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_exact_links_by_destination_texture.find(texture);
  return it != registry.surface_texture_exact_links_by_destination_texture.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      SurfaceTextureDestinationSlotKey(texture, source_slot);
  const auto it =
      registry.surface_texture_exact_link_by_destination_texture_slot.find(
          slot_key);
  return it !=
                 registry.surface_texture_exact_link_by_destination_texture_slot
                     .end()
             ? &it->second
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
    uint32_t texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_exact_links_by_destination_texture.find(texture);
  return it != registry.surface_texture_exact_links_by_destination_texture.end()
             ? SelectVisibleSurfaceTextureLink(&it->second, frame_index,
                                               draw_serial, source_slot)
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForSourceSurface(uint32_t source_surface) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_surface == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_link_by_source_surface.find(source_surface);
  return it != registry.surface_texture_link_by_source_surface.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForSourceSurfaceSlot(
    uint32_t source_surface, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_surface == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      SurfaceTextureSourceSurfaceSlotKey(source_surface, source_slot);
  const auto it =
      registry.surface_texture_link_by_source_surface_slot.find(slot_key);
  return it != registry.surface_texture_link_by_source_surface_slot.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForSourceSurface(
    uint32_t source_surface, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_surface == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.surface_texture_links_by_surface.find(source_surface);
  return it != registry.surface_texture_links_by_surface.end()
             ? SelectVisibleSurfaceTextureLink(&it->second, frame_index,
                                               draw_serial, source_slot)
             : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkForBase(
    uint32_t texture_base_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture_base_address == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_link_by_base.find(texture_base_address);
  return it != registry.surface_texture_link_by_base.end() ? &it->second
                                                           : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkForFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t fetch_key = FableDirectTextureFetchKey(texture_fetch_dwords);
  if (fetch_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.surface_texture_link_by_fetch_key.find(fetch_key);
  return it != registry.surface_texture_link_by_fetch_key.end() ? &it->second
                                                                : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByCopyCall(
    uint64_t copy_call_serial) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (copy_call_serial == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_link_by_copy_call.find(copy_call_serial);
  return it != registry.surface_texture_link_by_copy_call.end() ? &it->second
                                                                : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByCopyContext(
    uint64_t copy_context_key) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (copy_context_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_link_by_copy_context.find(copy_context_key);
  return it != registry.surface_texture_link_by_copy_context.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForBase(
    uint32_t texture_base_address, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture_base_address == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_links_by_base.find(texture_base_address);
  return it != registry.surface_texture_links_by_base.end()
             ? SelectVisibleSurfaceTextureLink(&it->second, frame_index,
                                               draw_serial, source_slot)
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords, uint32_t frame_index,
    uint64_t draw_serial, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t fetch_key = FableDirectTextureFetchKey(texture_fetch_dwords);
  if (fetch_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.surface_texture_links_by_fetch_key.find(fetch_key);
  return it != registry.surface_texture_links_by_fetch_key.end()
             ? SelectVisibleSurfaceTextureLink(&it->second, frame_index,
                                               draw_serial, source_slot)
             : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkBySurface(
    uint32_t source_surface) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  return SelectBestSurfaceTextureLink(
      FableDirectFindSurfaceTextureLinksBySurface(source_surface));
}

const std::vector<FableSurfaceDestinationTextureRelation>*
FableDirectFindSurfaceDestinationRelationsBySourceSurface(
    uint32_t source_surface) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_surface == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_destination_relations_by_source_surface.find(
          source_surface);
  return it != registry.surface_destination_relations_by_source_surface.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceDestinationTextureRelation*
FableDirectFindSurfaceDestinationRelationForDestinationTexture(
    uint32_t destination_texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (destination_texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_destination_relation_by_destination_texture.find(
          destination_texture);
  return it != registry.surface_destination_relation_by_destination_texture.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceDestinationTextureRelation*
FableDirectFindSurfaceDestinationRelationForDestinationTextureSlot(
    uint32_t destination_texture, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (destination_texture == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      SurfaceTextureDestinationSlotKey(destination_texture, source_slot);
  const auto it =
      registry.surface_destination_relation_by_destination_texture_slot.find(
          slot_key);
  return it !=
                 registry.surface_destination_relation_by_destination_texture_slot
                     .end()
             ? &it->second
             : nullptr;
}

const FableSurfaceDestinationTextureRelation*
FableDirectFindVisibleSurfaceDestinationRelationForDestinationTexture(
    uint32_t destination_texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (destination_texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_destination_relations_by_destination_texture.find(
          destination_texture);
  return it !=
                 registry.surface_destination_relations_by_destination_texture.end()
             ? SelectVisibleSurfaceDestinationRelation(
                   &it->second, frame_index, draw_serial, source_slot)
             : nullptr;
}

const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksBySurface(uint32_t source_surface) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_surface == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_links_by_surface.find(source_surface);
  return it != registry.surface_texture_links_by_surface.end() ? &it->second
                                                               : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByBase(
    uint32_t texture_base_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  return SelectBestSurfaceTextureLink(
      FableDirectFindSurfaceTextureLinksByBase(texture_base_address));
}

const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksByBase(uint32_t texture_base_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture_base_address == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_links_by_base.find(texture_base_address);
  return it != registry.surface_texture_links_by_base.end() ? &it->second
                                                            : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  return SelectBestSurfaceTextureLink(
      FableDirectFindSurfaceTextureLinksByFetch(texture_fetch_dwords));
}

const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksByFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t fetch_key = FableDirectTextureFetchKey(texture_fetch_dwords);
  if (fetch_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.surface_texture_links_by_fetch_key.find(fetch_key);
  return it != registry.surface_texture_links_by_fetch_key.end() ? &it->second
                                                                 : nullptr;
}

void FableDirectRegisterShaderIdentity(
    const FableShaderIdentity& identity) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (identity.object_shader == 0 || identity.bytecode_hash == 0 ||
      identity.kind == FableGuestResourceKind::kUnknown) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto existing_it =
      registry.shader_identities_by_object.find(identity.object_shader);
  if (existing_it != registry.shader_identities_by_object.end() &&
      SameShaderIdentity(existing_it->second, identity)) {
    return;
  }

  if (existing_it != registry.shader_identities_by_object.end() &&
      existing_it->second.bytecode_hash != 0 &&
      existing_it->second.bytecode_hash != identity.bytecode_hash) {
    const auto hash_it = registry.shader_identity_objects_by_hash.find(
        existing_it->second.bytecode_hash);
    if (hash_it != registry.shader_identity_objects_by_hash.end()) {
      std::vector<uint32_t>& objects = hash_it->second;
      objects.erase(std::remove(objects.begin(), objects.end(),
                                identity.object_shader),
                    objects.end());
      if (objects.empty()) {
        registry.shader_identity_objects_by_hash.erase(hash_it);
      }
    }
  }

  FableShaderIdentity stored = identity;
  stored.serial = ++registry.shader_identity_serial;
  registry.shader_identities_by_object[stored.object_shader] = stored;

  std::vector<uint32_t>& objects =
      registry.shader_identity_objects_by_hash[stored.bytecode_hash];
  if (std::find(objects.begin(), objects.end(), stored.object_shader) ==
      objects.end()) {
    objects.push_back(stored.object_shader);
  }
}

void FableDirectForgetShaderIdentity(uint32_t object_shader) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_shader == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto identity_it =
      registry.shader_identities_by_object.find(object_shader);
  if (identity_it == registry.shader_identities_by_object.end()) {
    return;
  }

  const uint64_t bytecode_hash = identity_it->second.bytecode_hash;
  registry.shader_identities_by_object.erase(identity_it);
  if (bytecode_hash == 0) {
    return;
  }

  const auto hash_it =
      registry.shader_identity_objects_by_hash.find(bytecode_hash);
  if (hash_it == registry.shader_identity_objects_by_hash.end()) {
    return;
  }
  std::vector<uint32_t>& objects = hash_it->second;
  objects.erase(std::remove(objects.begin(), objects.end(), object_shader),
                objects.end());
  if (objects.empty()) {
    registry.shader_identity_objects_by_hash.erase(hash_it);
  }
}

void FableDirectClearShaderIdentities() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.shader_identity_serial = 0;
  registry.shader_identities_by_object.clear();
  registry.shader_identity_objects_by_hash.clear();
}

const FableShaderIdentity* FableDirectFindShaderIdentityByObject(
    uint32_t object_shader) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_shader == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.shader_identities_by_object.find(object_shader);
  return it != registry.shader_identities_by_object.end() ? &it->second
                                                          : nullptr;
}

const FableShaderIdentity* FableDirectFindShaderIdentityByHash(
    uint64_t bytecode_hash) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  return SelectBestShaderIdentity(
      FableDirectFindShaderIdentityObjectsByHash(bytecode_hash));
}

const std::vector<uint32_t>* FableDirectFindShaderIdentityObjectsByHash(
    uint64_t bytecode_hash) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (bytecode_hash == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.shader_identity_objects_by_hash.find(bytecode_hash);
  return it != registry.shader_identity_objects_by_hash.end() ? &it->second
                                                              : nullptr;
}

void FableDirectRegisterVertexDeclarationIdentity(
    const FableVertexDeclarationIdentity& identity) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (identity.object_declaration == 0 || identity.elements_hash == 0 ||
      identity.elements_size == 0 || identity.element_count == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto existing_it =
      registry.vertex_declaration_identities_by_object.find(
          identity.object_declaration);
  if (existing_it !=
          registry.vertex_declaration_identities_by_object.end() &&
      SameVertexDeclarationIdentity(existing_it->second, identity)) {
    return;
  }

  if (existing_it !=
          registry.vertex_declaration_identities_by_object.end() &&
      existing_it->second.elements_hash != 0 &&
      existing_it->second.elements_hash != identity.elements_hash) {
    const auto hash_it =
        registry.vertex_declaration_identity_objects_by_hash.find(
            existing_it->second.elements_hash);
    if (hash_it !=
        registry.vertex_declaration_identity_objects_by_hash.end()) {
      std::vector<uint32_t>& objects = hash_it->second;
      objects.erase(std::remove(objects.begin(), objects.end(),
                                identity.object_declaration),
                    objects.end());
      if (objects.empty()) {
        registry.vertex_declaration_identity_objects_by_hash.erase(hash_it);
      }
    }
  }

  FableVertexDeclarationIdentity stored = identity;
  stored.serial = ++registry.vertex_declaration_identity_serial;
  registry.vertex_declaration_identities_by_object[stored.object_declaration] =
      stored;

  std::vector<uint32_t>& objects =
      registry.vertex_declaration_identity_objects_by_hash[stored.elements_hash];
  if (std::find(objects.begin(), objects.end(), stored.object_declaration) ==
      objects.end()) {
    objects.push_back(stored.object_declaration);
  }
}

void FableDirectForgetVertexDeclarationIdentity(
    uint32_t object_declaration) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_declaration == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto identity_it =
      registry.vertex_declaration_identities_by_object.find(
          object_declaration);
  if (identity_it ==
      registry.vertex_declaration_identities_by_object.end()) {
    return;
  }

  const uint64_t elements_hash = identity_it->second.elements_hash;
  registry.vertex_declaration_identities_by_object.erase(identity_it);
  if (elements_hash == 0) {
    return;
  }

  const auto hash_it =
      registry.vertex_declaration_identity_objects_by_hash.find(elements_hash);
  if (hash_it ==
      registry.vertex_declaration_identity_objects_by_hash.end()) {
    return;
  }
  std::vector<uint32_t>& objects = hash_it->second;
  objects.erase(std::remove(objects.begin(), objects.end(),
                            object_declaration),
                objects.end());
  if (objects.empty()) {
    registry.vertex_declaration_identity_objects_by_hash.erase(hash_it);
  }
}

void FableDirectClearVertexDeclarationIdentities() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.vertex_declaration_identity_serial = 0;
  registry.vertex_declaration_identities_by_object.clear();
  registry.vertex_declaration_identity_objects_by_hash.clear();
}

const FableVertexDeclarationIdentity*
FableDirectFindVertexDeclarationIdentityByObject(uint32_t object_declaration) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_declaration == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.vertex_declaration_identities_by_object.find(object_declaration);
  return it != registry.vertex_declaration_identities_by_object.end()
             ? &it->second
             : nullptr;
}

const FableVertexDeclarationIdentity*
FableDirectFindVertexDeclarationIdentityByHash(uint64_t elements_hash) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  return SelectBestVertexDeclarationIdentity(
      FableDirectFindVertexDeclarationIdentityObjectsByHash(elements_hash));
}

const std::vector<uint32_t>*
FableDirectFindVertexDeclarationIdentityObjectsByHash(uint64_t elements_hash) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (elements_hash == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.vertex_declaration_identity_objects_by_hash.find(elements_hash);
  return it != registry.vertex_declaration_identity_objects_by_hash.end()
             ? &it->second
             : nullptr;
}

const char* FablePrivateTextureCopyKindName(FablePrivateTextureCopyKind kind) {
  switch (kind) {
    case FablePrivateTextureCopyKind::kColor:
      return "color";
    case FablePrivateTextureCopyKind::kDepth:
      return "depth";
    case FablePrivateTextureCopyKind::kColorSibling:
      return "color-sibling";
  }
  return "unknown";
}

const char* FableRegisteredSampleSourceKindName(
    FableRegisteredSampleSourceKind kind) {
  switch (kind) {
    case FableRegisteredSampleSourceKind::kUnknown:
      return "unknown";
    case FableRegisteredSampleSourceKind::kGuestTexture:
      return "guest-texture";
    case FableRegisteredSampleSourceKind::kBackingTexture:
      return "backing-texture";
    case FableRegisteredSampleSourceKind::kGpuSurface:
      return "gpu-surface";
  }
  return "unknown";
}

void FableDirectRegisterPrivateTextureCopyAction(
    const FablePrivateTextureCopyAction& action) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const bool wrapper_context_action =
      action.wrapper_call_serial != 0 || action.wrapper_context_key != 0;
  if (action.source_texture == 0 ||
      (action.target_surface == 0 && !wrapper_context_action)) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();

  FablePrivateTextureCopyAction stored = action;
  NormalizePrivateTextureCopySampleSource(stored);
  auto find_existing_serial =
      [&](const std::vector<FablePrivateTextureCopyAction>* actions)
      -> uint64_t {
        if (!actions) {
          return 0;
        }
        for (auto it = actions->rbegin(); it != actions->rend(); ++it) {
          if (!SamePrivateTextureCopyActionIdentity(*it, stored)) {
            continue;
          }
          const bool same_static_action =
              stored.frame_index == 0 && stored.draw_serial == 0;
          const bool same_producer_action = stored.frame_index != 0;
          if (same_static_action ||
              (same_producer_action &&
               it->frame_index == stored.frame_index &&
               it->draw_serial == stored.draw_serial)) {
            return it->serial;
          }
        }
        return 0;
      };
  auto find_actions_by_key =
      [](const auto& index, auto key)
      -> const std::vector<FablePrivateTextureCopyAction>* {
    const auto it = index.find(key);
    return it != index.end() ? &it->second : nullptr;
  };

  const uint64_t source_fetch_key =
      FableDirectTextureFetchKey(stored.source_fetch_dwords);
  const uint64_t backing_fetch_key =
      FableDirectTextureFetchKey(stored.backing_fetch_dwords);
  std::vector<uint32_t> source_keys;
  std::vector<uint32_t> base_keys;
  std::vector<uint64_t> fetch_keys;
  CollectTextureIdentityIndexKeysForObject(registry, stored.source_texture,
                                           source_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForObject(registry, stored.backing_texture,
                                           source_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, stored.source_base_address,
                                         source_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, stored.backing_base_address,
                                         source_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(registry, source_fetch_key,
                                          source_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(registry, backing_fetch_key,
                                          source_keys, base_keys, fetch_keys);
  const bool static_alias_action =
      stored.frame_index == 0 && stored.draw_serial == 0;
  const bool repeated_producer_action = stored.frame_index != 0;
  uint64_t existing_serial = 0;
  if (static_alias_action || repeated_producer_action) {
    for (uint32_t source_key : source_keys) {
      existing_serial = find_existing_serial(find_actions_by_key(
          registry.private_texture_copies_by_source, source_key));
      if (existing_serial != 0) {
        break;
      }
    }
    if (existing_serial == 0 && stored.target_surface != 0) {
      existing_serial =
          find_existing_serial(find_actions_by_key(
              registry.private_texture_copies_by_target, stored.target_surface));
    }
    if (existing_serial == 0 && stored.render_target != 0) {
      existing_serial =
          find_existing_serial(find_actions_by_key(
              registry.private_texture_copies_by_target, stored.render_target));
    }
    for (uint32_t base_key : base_keys) {
      if (existing_serial != 0) {
        break;
      }
      existing_serial = find_existing_serial(find_actions_by_key(
          registry.private_texture_copies_by_base, base_key));
    }
    for (uint64_t fetch_key : fetch_keys) {
      if (existing_serial != 0) {
        break;
      }
      existing_serial = find_existing_serial(find_actions_by_key(
          registry.private_texture_copies_by_fetch_key, fetch_key));
    }
  }

  stored.serial =
      existing_serial != 0 ? existing_serial
                           : ++registry.private_texture_copy_serial;

  constexpr size_t kMaxPrivateTextureCopyActionsPerBucket = 32;
  auto upsert_action = [&](std::vector<FablePrivateTextureCopyAction>& actions) {
    for (FablePrivateTextureCopyAction& existing : actions) {
      if (SamePrivateTextureCopyActionIdentity(existing, stored)) {
        existing = stored;
        return;
      }
    }
    if (actions.size() >= kMaxPrivateTextureCopyActionsPerBucket) {
      actions.erase(actions.begin());
    }
    actions.push_back(stored);
  };

  for (uint32_t source_key : source_keys) {
    upsert_action(registry.private_texture_copies_by_source[source_key]);
  }
  if (stored.target_surface != 0) {
    upsert_action(
        registry.private_texture_copies_by_target[stored.target_surface]);
  }
  if (stored.render_target != 0 &&
      stored.render_target != stored.target_surface) {
    upsert_action(
        registry.private_texture_copies_by_target[stored.render_target]);
  }

  for (uint32_t base_key : base_keys) {
    registry.private_texture_copy_source_by_base[base_key] =
        stored.source_texture;
    upsert_action(registry.private_texture_copies_by_base[base_key]);
  }
  for (uint64_t fetch_key : fetch_keys) {
    upsert_action(registry.private_texture_copies_by_fetch_key[fetch_key]);
  }
  if (stored.frame_index != 0) {
    constexpr size_t kMaxPrivateTextureCopyActionsPerFrame = 128;
    std::vector<FablePrivateTextureCopyAction>& frame_actions =
        registry.private_texture_copy_actions_by_frame[stored.frame_index];
    upsert_action(frame_actions);
    if (frame_actions.size() > kMaxPrivateTextureCopyActionsPerFrame) {
      frame_actions.erase(
          frame_actions.begin(),
          frame_actions.begin() +
              static_cast<std::ptrdiff_t>(
                  frame_actions.size() -
                  kMaxPrivateTextureCopyActionsPerFrame));
    }
  }
  StorePrivateTextureCopyDirectAction(registry, stored);
  if (stored.target_surface == 0) {
    return;
  }

  FableSurfaceTextureLink link;
  link.source_slot = stored.source_slot;
  link.trusted_destination_edge = true;
  link.texture = stored.source_texture;
  link.texture_base_address = stored.source_base_address;
  link.texture_width = stored.source_width;
  link.texture_height = stored.source_height;
  link.texture_format = stored.source_format;
  link.texture_fetch_dwords = stored.source_fetch_dwords;
  link.source_surface = stored.target_surface;
  link.backing_texture = stored.backing_texture;
  link.backing_base_address = stored.backing_base_address;
  link.backing_fetch_dwords = stored.backing_fetch_dwords;
  link.backing_content_serial = stored.backing_content_serial;
  link.target_content_serial = stored.target_content_serial;
  link.render_target = stored.render_target;
  link.depth_stencil = stored.depth_stencil;
  link.vertex_shader = stored.vertex_shader;
  link.pixel_shader = stored.pixel_shader;
  link.vertex_shader_root_call_serial = stored.vertex_shader_root_call_serial;
  link.vertex_shader_root_context_key = stored.vertex_shader_root_context_key;
  link.vertex_shader_root_symbol_address =
      stored.vertex_shader_root_symbol_address;
  link.vertex_shader_root_lr = stored.vertex_shader_root_lr;
  link.pixel_shader_root_call_serial = stored.pixel_shader_root_call_serial;
  link.pixel_shader_root_context_key = stored.pixel_shader_root_context_key;
  link.pixel_shader_root_symbol_address =
      stored.pixel_shader_root_symbol_address;
  link.pixel_shader_root_lr = stored.pixel_shader_root_lr;
  link.copy_call_serial = stored.wrapper_call_serial;
  link.copy_context_key = stored.wrapper_context_key;
  link.copy_symbol_address = stored.wrapper_symbol_address;
  link.wrapper_source_texture = stored.wrapper_source_texture;
  link.wrapper_source_exact = stored.wrapper_source_exact;
  link.wrapper_setup_globals = stored.wrapper_setup_globals;
  link.frame_index = stored.frame_index;
  link.draw_serial = stored.frame_index != 0 ? stored.draw_serial : 0;
  if (stored.sampled_source.kind ==
      FableRegisteredSampleSourceKind::kBackingTexture) {
    link.sampled_source = stored.sampled_source;
  } else {
    link.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
    link.sampled_source.resource = stored.target_surface;
    link.sampled_source.content_serial = stored.target_content_serial;
  }
  link.kind = stored.kind;
  FableDirectRegisterSurfaceTextureLink(link);
}

void FableDirectForgetPrivateTextureCopyActionsForResource(uint32_t resource) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (resource == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const bool has_indexed_action =
      registry.private_texture_copies_by_source.find(resource) !=
          registry.private_texture_copies_by_source.end() ||
      registry.private_texture_copies_by_target.find(resource) !=
          registry.private_texture_copies_by_target.end() ||
      registry.private_texture_copies_by_base.find(resource) !=
          registry.private_texture_copies_by_base.end() ||
      registry.private_texture_copy_source_by_base.find(resource) !=
          registry.private_texture_copy_source_by_base.end() ||
      registry.private_texture_copy_action_by_destination_texture.find(resource) !=
          registry.private_texture_copy_action_by_destination_texture.end();
  if (!has_indexed_action) {
    FableDirectForgetSurfaceTextureLinksForResource(resource);
    return;
  }

  const auto action_matches =
      [resource](const FablePrivateTextureCopyAction& action) {
    // Keep exact copy actions keyed by the real source/backing/target endpoints.
    // Shader/depth/RT context participates in matching and scoring elsewhere,
    // but it must not make the object graph forget a still-live copy relation.
    return PrivateTextureCopyActionReferencesResource(action, resource);
  };

  bool removed_any = false;
  const auto prune_index = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if (prune_resource_key &&
          static_cast<uint64_t>(it->first) == static_cast<uint64_t>(resource)) {
        it = index.erase(it);
        removed_any = true;
        continue;
      }
      std::vector<FablePrivateTextureCopyAction>& actions = it->second;
      const size_t before = actions.size();
      actions.erase(std::remove_if(actions.begin(), actions.end(),
                                   action_matches),
                    actions.end());
      removed_any = removed_any || actions.size() != before;
      if (actions.empty()) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };

  prune_index(registry.private_texture_copies_by_source, true);
  prune_index(registry.private_texture_copies_by_target, true);
  prune_index(registry.private_texture_copies_by_base, true);
  prune_index(registry.private_texture_copies_by_fetch_key, false);
  prune_index(registry.private_texture_copy_actions_by_frame, false);
  for (auto it = registry.private_texture_copy_source_by_base.begin();
       it != registry.private_texture_copy_source_by_base.end();) {
    if (it->first == resource || it->second == resource) {
      it = registry.private_texture_copy_source_by_base.erase(it);
      removed_any = true;
    } else {
      ++it;
    }
  }

  if (removed_any) {
    PrunePrivateTextureCopyDirectActionsForResource(registry, resource);
    ++registry.private_texture_copy_serial;
  }
  FableDirectForgetSurfaceTextureLinksForResource(resource);
}

void FableDirectClearPrivateTextureCopyActions() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.private_texture_copy_serial = 0;
  registry.private_texture_copy_index_serial = 0;
  registry.private_texture_copy_logs = 0;
  registry.private_texture_copies_by_source.clear();
  registry.private_texture_copies_by_target.clear();
  registry.private_texture_copy_source_by_base.clear();
  registry.private_texture_copies_by_base.clear();
  registry.private_texture_copies_by_fetch_key.clear();
  registry.private_texture_copy_action_by_destination_texture.clear();
  registry.private_texture_copy_action_by_destination_texture_slot.clear();
  registry.private_texture_copy_actions_by_destination_texture.clear();
  registry.private_texture_copy_actions_by_destination_texture_slot.clear();
  registry.private_texture_copy_output_actions_by_destination_texture.clear();
  registry.private_texture_copy_output_actions_by_destination_texture_slot.clear();
  registry.private_texture_copy_action_by_wrapper_call.clear();
  registry.private_texture_copy_action_by_wrapper_context.clear();
  registry.private_texture_copy_actions_by_frame.clear();
  FableDirectClearSurfaceTextureLinks();
}

const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionBySource(uint32_t source_texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const std::vector<FablePrivateTextureCopyAction>* actions =
      FableDirectFindPrivateTextureCopyActionsBySource(source_texture);
  return actions && !actions->empty() ? &actions->back() : nullptr;
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsBySource(uint32_t source_texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.private_texture_copies_by_source.find(source_texture);
  return it != registry.private_texture_copies_by_source.end() ? &it->second
                                                               : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByTarget(uint32_t target_surface) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const std::vector<FablePrivateTextureCopyAction>* actions =
      FableDirectFindPrivateTextureCopyActionsByTarget(target_surface);
  return actions && !actions->empty() ? &actions->back() : nullptr;
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByTarget(uint32_t target_surface) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (target_surface == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto target_it = registry.private_texture_copies_by_target.find(
      target_surface);
  return target_it != registry.private_texture_copies_by_target.end()
             ? &target_it->second
             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByBase(uint32_t source_base_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const std::vector<FablePrivateTextureCopyAction>* actions =
      FableDirectFindPrivateTextureCopyActionsByBase(source_base_address);
  return actions && !actions->empty() ? &actions->back() : nullptr;
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByBase(uint32_t source_base_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_base_address == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto base_it =
      registry.private_texture_copies_by_base.find(source_base_address);
  return base_it != registry.private_texture_copies_by_base.end()
             ? &base_it->second
             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByFetch(
    const std::array<uint32_t, 6>& source_fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const std::vector<FablePrivateTextureCopyAction>* actions =
      FableDirectFindPrivateTextureCopyActionsByFetch(source_fetch_dwords);
  return actions && !actions->empty() ? &actions->back() : nullptr;
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByFetch(
    const std::array<uint32_t, 6>& source_fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t fetch_key = FableDirectTextureFetchKey(source_fetch_dwords);
  if (fetch_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto fetch_it =
      registry.private_texture_copies_by_fetch_key.find(fetch_key);
  if (fetch_it == registry.private_texture_copies_by_fetch_key.end()) {
    return nullptr;
  }
  return &fetch_it->second;
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByFrame(uint32_t frame_index) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (frame_index == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto frame_it =
      registry.private_texture_copy_actions_by_frame.find(frame_index);
  return frame_it != registry.private_texture_copy_actions_by_frame.end()
             ? &frame_it->second
             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByWrapperCall(
    uint64_t wrapper_call_serial) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (wrapper_call_serial == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.private_texture_copy_action_by_wrapper_call.find(
          wrapper_call_serial);
  return it != registry.private_texture_copy_action_by_wrapper_call.end()
             ? &it->second
             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByWrapperContext(
    uint64_t wrapper_context_key) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (wrapper_context_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.private_texture_copy_action_by_wrapper_context.find(
          wrapper_context_key);
  return it != registry.private_texture_copy_action_by_wrapper_context.end()
             ? &it->second
             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionForDestinationTexture(uint32_t texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.private_texture_copy_action_by_destination_texture.find(texture);
  return it != registry.private_texture_copy_action_by_destination_texture.end()
             ? &it->second
             : nullptr;
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsForDestinationTexture(uint32_t texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.private_texture_copy_actions_by_destination_texture.find(texture);
  return it !=
                 registry.private_texture_copy_actions_by_destination_texture
                     .end()
             ? &it->second
             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      PrivateTextureCopyDestinationSlotKey(texture, source_slot);
  const auto it =
      registry.private_texture_copy_action_by_destination_texture_slot.find(
          slot_key);
  return it !=
                 registry
                     .private_texture_copy_action_by_destination_texture_slot
                     .end()
             ? &it->second
             : nullptr;
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      PrivateTextureCopyDestinationSlotKey(texture, source_slot);
  const auto it =
      registry.private_texture_copy_actions_by_destination_texture_slot
          .find(slot_key);
  return it !=
                 registry
                     .private_texture_copy_actions_by_destination_texture_slot
                     .end()
             ? &it->second
             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyActionForDestinationTexture(
    uint32_t texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.private_texture_copy_actions_by_destination_texture.find(texture);
  return it !=
                 registry.private_texture_copy_actions_by_destination_texture
                     .end()
             ? SelectVisiblePrivateTextureCopyAction(&it->second, frame_index,
                                                     draw_serial, source_slot)
             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyActionForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot, uint32_t frame_index,
    uint64_t draw_serial) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      PrivateTextureCopyDestinationSlotKey(texture, source_slot);
  const auto it =
      registry.private_texture_copy_actions_by_destination_texture_slot
          .find(slot_key);
  return it !=
                 registry
                     .private_texture_copy_actions_by_destination_texture_slot
                     .end()
	             ? SelectVisiblePrivateTextureCopyAction(&it->second, frame_index,
	                                                     draw_serial, source_slot)
	             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTexture(
    uint32_t texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.private_texture_copy_output_actions_by_destination_texture.find(
          texture);
  return it !=
                 registry.private_texture_copy_output_actions_by_destination_texture
                     .end()
             ? SelectVisiblePrivateTextureCopyAction(&it->second, frame_index,
                                                     draw_serial, source_slot)
             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot, uint32_t frame_index,
    uint64_t draw_serial) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      PrivateTextureCopyDestinationSlotKey(texture, source_slot);
  const auto it =
      registry.private_texture_copy_output_actions_by_destination_texture_slot
          .find(slot_key);
  return it !=
                 registry
                     .private_texture_copy_output_actions_by_destination_texture_slot
                     .end()
             ? SelectVisiblePrivateTextureCopyAction(&it->second, frame_index,
                                                     draw_serial, source_slot)
             : nullptr;
}

}  // namespace refii::renderer
