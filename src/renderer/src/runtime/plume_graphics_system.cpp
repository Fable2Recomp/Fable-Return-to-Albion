#include "runtime/plume_graphics_system.h"

#include <cstdint>
#include <filesystem>

#include <rex/graphics/command_processor.h>
#include <rex/logging.h>
#include <tracy/Tracy.hpp>

namespace refii::renderer {
namespace {

bool g_logged_issue_swap_ignored = false;

class PlumeCommandProcessor final : public rex::graphics::CommandProcessor {
 public:
  PlumeCommandProcessor(rex::graphics::GraphicsSystem* graphics_system,
                        rex::system::KernelState* kernel_state)
      : CommandProcessor(graphics_system, kernel_state) {}

  void IssueSwap(uint32_t frontbuffer_ptr, uint32_t frontbuffer_width,
                 uint32_t frontbuffer_height) override {
    ZoneScopedN("RefiiPlumeShim/IssueSwapIgnored");
    (void)frontbuffer_ptr;
    (void)frontbuffer_width;
    (void)frontbuffer_height;
    if (!g_logged_issue_swap_ignored) {
      REXLOG_INFO(
          "Refii plume graphics shim ignored ReXGlue IssueSwap; Plume output "
          "is driven by Fable display hooks");
      g_logged_issue_swap_ignored = true;
    }
  }

  void InitializeShaderStorage(const std::filesystem::path& cache_root,
                               uint32_t title_id, bool blocking) override {
    ZoneScopedN("RefiiPlumeShim/InitializeShaderStorage");
    (void)cache_root;
    (void)title_id;
    (void)blocking;
  }

  void TracePlaybackWroteMemory(uint32_t base_ptr, uint32_t length) override {
    ZoneScopedN("RefiiPlumeShim/TracePlaybackWroteMemory");
    (void)base_ptr;
    (void)length;
  }

  void RestoreEdramSnapshot(const void* snapshot) override {
    ZoneScopedN("RefiiPlumeShim/RestoreEdramSnapshot");
    (void)snapshot;
  }

 protected:
  bool SetupContext() override {
    ZoneScopedN("RefiiPlumeShim/SetupContext");
    return true;
  }
  void ShutdownContext() override {
    ZoneScopedN("RefiiPlumeShim/ShutdownContext");
  }

  rex::graphics::Shader* LoadShader(rex::graphics::xenos::ShaderType shader_type,
                                    uint32_t guest_address,
                                    const uint32_t* host_address,
                                    uint32_t dword_count) override {
    ZoneScopedN("RefiiPlumeShim/LoadShaderIgnored");
    (void)shader_type;
    (void)guest_address;
    (void)host_address;
    (void)dword_count;
    return nullptr;
  }

  bool IssueDraw(rex::graphics::xenos::PrimitiveType prim_type,
                 uint32_t index_count, IndexBufferInfo* index_buffer_info,
                 bool major_mode_explicit) override {
    ZoneScopedN("RefiiPlumeShim/IssueDrawIgnored");
    (void)prim_type;
    (void)index_count;
    (void)index_buffer_info;
    (void)major_mode_explicit;
    return true;
  }

  bool IssueCopy() override {
    ZoneScopedN("RefiiPlumeShim/IssueCopyIgnored");
    return true;
  }
};

}  // namespace

PlumeGraphicsSystem::PlumeGraphicsSystem() = default;

PlumeGraphicsSystem::~PlumeGraphicsSystem() = default;

std::string PlumeGraphicsSystem::name() const { return "plume"; }

rex::X_STATUS PlumeGraphicsSystem::SetupPresentation(
    rex::ui::WindowedAppContext* app_context) {
  app_context_ = app_context;
  presentation_ready_ = true;
  REXLOG_INFO(
      "Refii plume graphics system is using the ReXGlue app window without a "
      "native ReXGlue presenter");
  return static_cast<rex::X_STATUS>(0);
}

bool PlumeGraphicsSystem::has_presentation() const {
  return presentation_ready_;
}

void PlumeGraphicsSystem::CreateProvider(bool with_presentation) {
  (void)with_presentation;
}

std::unique_ptr<rex::graphics::CommandProcessor>
PlumeGraphicsSystem::CreateCommandProcessor() {
  return std::make_unique<PlumeCommandProcessor>(this, kernel_state_);
}

}  // namespace refii::renderer
