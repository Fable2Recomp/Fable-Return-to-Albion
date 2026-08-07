#include "runtime/refii_renderer/plume/general/plume_debug_labels.h"

#include <cctype>
#include <cstdio>
#include <string_view>

namespace refii::renderer {

std::string BuildRenderGraphExecuteLabel(uint32_t frame_index,
                                         uint32_t width,
                                         uint32_t height) {
  char label[128] = {};
  std::snprintf(label, sizeof(label),
                "RefiiRenderGraph::Execute Frame %u Output_%ux%u",
                frame_index, width, height);
  return label;
}

std::string BuildRenderGraphBridgeLabel(uint32_t frame_index) {
  char label[128] = {};
  std::snprintf(label, sizeof(label),
                "RefiiRenderGraph::Phase::FableDirectBridge Frame %u",
                frame_index);
  return label;
}

std::string BuildRenderGraphSchedulerLabel(uint32_t frame_index) {
  char label[128] = {};
  std::snprintf(label, sizeof(label),
                "RefiiRenderGraph::Scheduler Frame %u", frame_index);
  return label;
}

std::string BuildRenderGraphSchedulerBridgeStepLabel(uint32_t frame_index) {
  char label[128] = {};
  std::snprintf(label, sizeof(label),
                "RefiiRenderGraph::Scheduler::Step::FableDirectBridge Frame %u",
                frame_index);
  return label;
}

std::string BuildRenderGraphSchedulerPlanStepLabel(uint32_t frame_index) {
  char label[128] = {};
  std::snprintf(
      label, sizeof(label),
      "RefiiRenderGraph::Scheduler::Step::FrameGraphPlanPublish Frame %u",
      frame_index);
  return label;
}


std::string SanitizeRenderDocTagComponent(std::string_view value) {
  std::string sanitized;
  sanitized.reserve(value.size());
  bool pending_separator = false;
  for (const unsigned char ch : value) {
    if (std::isalnum(ch) != 0) {
      if (pending_separator && !sanitized.empty()) {
        sanitized.push_back('_');
      }
      sanitized.push_back(static_cast<char>(std::tolower(ch)));
      pending_separator = false;
    } else {
      pending_separator = true;
    }
  }
  return sanitized;
}

}  // namespace refii::renderer
