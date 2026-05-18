#pragma once

#include <memory>
#include <string>

#include <rex/graphics/graphics_system.h>

namespace refii::renderer {

class PlumeGraphicsSystem final : public rex::graphics::GraphicsSystem {
 public:
  PlumeGraphicsSystem();
  ~PlumeGraphicsSystem() override;

  std::string name() const override;

  rex::X_STATUS SetupPresentation(rex::ui::WindowedAppContext* app_context) override;
  bool has_presentation() const override;

 protected:
  void CreateProvider(bool with_presentation) override;
  std::unique_ptr<rex::graphics::CommandProcessor> CreateCommandProcessor() override;

 private:
  bool presentation_ready_ = false;
};

}  // namespace refii::renderer
