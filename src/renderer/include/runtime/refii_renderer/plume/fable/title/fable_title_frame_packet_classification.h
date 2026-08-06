#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

struct FableTitleFramePacketShaderFacts {
  bool title_indexed_declaration = false;
  bool title_billboard_mesh_declaration = false;
  bool title_gui_quad_declaration = false;
  bool title_scene_vertex_shader = false;
  bool title_gui_text_3d_pixel_shader_address = false;
  bool synthetic_private_rect_copy_shader = false;
  uint64_t vertex_hash = 0;
  uint64_t pixel_hash = 0;
};

struct FableTitleFramePacketTargetFacts {
  uint32_t target = 0;
  bool title_scene_target = false;
  bool image_ready = false;
  bool scene_target_candidate = false;
  bool present_target_candidate = false;
  uint32_t width = 0;
  uint32_t height = 0;
};

struct FableTitleFramePacketPresentCandidateFacts {
  bool source_ready = false;
  bool title_scene_target = false;
  bool source_scene_target_candidate = false;
  uint32_t width = 0;
  uint32_t height = 0;
  uint64_t write_serial = 0;
};

struct FableTitleSurfaceDestinationPresentCandidateFacts {
  bool source_ready = false;
  bool source_surface = false;
  bool title_wrapper = false;
  bool wrapper_source_exact = false;
  bool color_write_layout = false;
  bool present_target_candidate = false;
  bool full_resolution_candidate = false;
  uint32_t width = 0;
  uint32_t height = 0;
  uint64_t write_serial = 0;
};

struct FableTitlePresentSourceFacts {
  bool source_ready = false;
  bool frame_matches = false;
  uint32_t target = 0;
  uint64_t write_serial = 0;
};

using FableTitleFramePacketTargetFactsList =
    std::array<FableTitleFramePacketTargetFacts, 4>;

struct FableTitleFramePacketDerivedFacts {
  bool writes_visible_target = false;
  bool full_frame_private_copy = false;
  bool logo_billboard_overlay = false;
  bool repopulate_tail = false;
  bool full_frame_composite = false;
  bool can_produce_present_color = false;
  bool title_present_producer = false;
  bool overlay_only = false;
  bool visible_producer = false;
  bool full_scene_producer = false;
};

bool FableTitleSceneTargetCandidate(uint32_t address,
                                    uint32_t width,
                                    uint32_t height);

bool FableTitlePresentTargetCandidate(uint32_t address,
                                      uint32_t width,
                                      uint32_t height);

bool FableTitleSurfaceDestinationRootIsWrapper(uint32_t symbol_address);

bool FableTitlePresentSourceCanUseSameFrameProducer(
    const FableTitlePresentSourceFacts& facts);

bool FableTitleFramePacketExtentsCoverTarget(
    const FableDirectFrameDatabaseDrawPacket& packet,
    uint32_t target_width,
    uint32_t target_height);

uint32_t FableTitleFramePacketInputStride(
    const FableDirectFrameDatabaseDrawPacket& packet,
    uint32_t slot);

bool FableTitleFramePacketUsesBillboardAtlas(
    const FableDirectFrameDatabaseDrawPacket& packet);

bool FableTitleFramePacketCanProducePresentColor(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool full_frame_private_copy);

bool FableTitleFramePacketTargetsContain(
    const FableDirectFrameDatabaseDrawPacket& packet,
    uint32_t target);

FableTitleFramePacketDerivedFacts BuildFableTitleFramePacketDerivedFacts(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketCanProducePresentColor(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketWritesTitleVisibleTarget(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets);

bool FableTitleFramePacketIsFullFramePrivateCopy(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketIsLogoBillboardOverlay(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool writes_visible_target,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketIsLogoBillboardOverlay(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketIsRepopulateTail(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool writes_visible_target,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketIsRepopulateTail(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketIsFullFrameComposite(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool writes_visible_target,
    uint32_t target_width,
    uint32_t target_height,
    bool logo_billboard_overlay,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketIsFullFrameComposite(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketIsTitleMenuParticleComposite(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool writes_visible_target,
    uint32_t target_width,
    uint32_t target_height,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketIsPresentProducer(
    bool can_produce_present_color,
    bool full_frame_composite,
    bool full_frame_private_copy);


bool FableTitleFramePacketIsOverlayOnly(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool full_frame_private_copy,
    bool full_frame_composite,
    bool repopulate_tail,
    bool logo_billboard_overlay,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketIsOverlayOnly(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketIsVisibleProducer(
    const FableDirectFrameDatabaseDrawPacket& packet,
    bool can_produce_present_color,
    bool writes_visible_target,
    bool full_frame_composite,
    bool full_frame_private_copy,
    bool repopulate_tail,
    bool logo_billboard_overlay,
    const FableTitleFramePacketShaderFacts& facts);

bool FableTitleFramePacketIsVisibleProducer(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& facts);


uint64_t FableTitleFramePacketCurrentFramePresentScore(
    const FableDirectFrameDatabaseDrawPacket& packet,
    const FableTitleFramePacketTargetFactsList& targets,
    const FableTitleFramePacketShaderFacts& shader_facts,
    const FableTitleFramePacketPresentCandidateFacts& candidate);

uint64_t FableTitleSurfaceDestinationPresentScore(
    const FableTitleSurfaceDestinationPresentCandidateFacts& candidate);

}  // namespace refii::renderer
