#pragma once

struct ImFontAtlas;

namespace rex::ui {
class ImmediateDrawer;
class ImGuiDrawer;
}

namespace refii::achievement {

void ConfigureAchievementFonts(ImFontAtlas *atlas);

void RegisterAchievementImGui(rex::ui::ImGuiDrawer *drawer,
                              rex::ui::ImmediateDrawer *immediate_drawer);

void UnregisterAchievementImGui();

} // namespace refii::achievement
