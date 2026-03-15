#include "window.h"

#include "fonts.h"
#include "platform.h"
#include "utils/inimanager.h"
#include "utils/launcherpaths.h"
#include "utils/opengl_stuff.h"

#include <cassert>

#if defined(__linux__)
#include <X11/Xlib.h>
#include <mutex>
#endif

namespace {

#if defined(__linux__)
void InitializeX11Threading() {
    static std::once_flag x11ThreadInitFlag;
    std::call_once(x11ThreadInitFlag, []() {
        XInitThreads();
    });
}
#endif

}  // namespace

VinceWindow::VinceWindow(int widthIn, int heightIn, const char* titleIn)
    : width(widthIn),
      height(heightIn),
      title(titleIn),
      window(nullptr, glfwDestroyWindow) {
    init();
}

void VinceWindow::init() {
    glfwSetErrorCallback(glfw_error_callback);
#if defined(__linux__)
    InitializeX11Threading();
#endif
    if (!glfwInit()) {
        assert(false && "Failed to initialize GLFW");
    }

    glfwWindowHint(GLFW_CONTEXT_VERSION_MAJOR, 3);
    glfwWindowHint(GLFW_CONTEXT_VERSION_MINOR, 0);
    glfwWindowHint(GLFW_TRANSPARENT_FRAMEBUFFER, GLFW_TRUE);

    window = std::unique_ptr<GLFWwindow, decltype(&glfwDestroyWindow)>(
        glfwCreateWindow(width, height, title.c_str(), nullptr, nullptr),
        &glfwDestroyWindow);
    if (!window) {
        assert(false && "Failed to create GLFW window");
    }

    int bufferWidth = 0;
    int bufferHeight = 0;
    glfwGetFramebufferSize(window.get(), &bufferWidth, &bufferHeight);
    glfwMakeContextCurrent(window.get());

    if (!gladLoadGLLoader((GLADloadproc)glfwGetProcAddress)) {
        assert(false && "Failed to initialize OpenGL context");
    }

    glViewport(0, 0, bufferWidth, bufferHeight);
    Platform::EnableWindowEffects(window.get());
}

void VinceWindow::SetupImGuiIO() {
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO(); (void)io;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableGamepad;
    io.ConfigFlags |= ImGuiConfigFlags_DockingEnable;

    ImGui::StyleColorsDark();

    static const std::string imguiIniPath = LauncherPaths::GetImGuiIniPath().string();
    const std::string basicTextFontPath = LauncherPaths::GetLauncherAssetPath("fable2.ttf").string();
    const std::string basicTitleFontPath = LauncherPaths::GetLauncherAssetPath("fable2.ttf").string();
    io.IniFilename = imguiIniPath.c_str();

    BasicTextFont = io.Fonts->AddFontFromFileTTF(basicTextFontPath.c_str(), 18.0f);
    BasicTitleFont = io.Fonts->AddFontFromFileTTF(basicTitleFontPath.c_str(), 30.0f);

    ImGui_ImplGlfw_InitForOpenGL(window.get(), true);
    ImGui_ImplOpenGL3_Init(glsl_version);

    ImVec4* colors = ImGui::GetStyle().Colors;
    colors[ImGuiCol_Text] = ImVec4(1.00f, 1.00f, 1.00f, 1.00f);
    colors[ImGuiCol_TextDisabled] = ImVec4(0.50f, 0.50f, 0.50f, 1.00f);
    colors[ImGuiCol_WindowBg] = ImVec4(0.10f, 0.35f, 0.20f, 0.0f);
    colors[ImGuiCol_ChildBg] = ImVec4(0.10f, 0.35f, 0.20f, 0.0f);
    colors[ImGuiCol_PopupBg] = ImVec4(0.00f, 0.15f, 0.08f, 0.94f);
    colors[ImGuiCol_Border] = ImVec4(0.00f, 0.60f, 0.30f, 0.50f);
    colors[ImGuiCol_BorderShadow] = ImVec4(0.00f, 0.05f, 0.02f, 0.00f);
    colors[ImGuiCol_FrameBg] = ImVec4(0.10f, 0.35f, 0.20f, 0.54f);
    colors[ImGuiCol_FrameBgHovered] = ImVec4(0.15f, 0.65f, 0.35f, 0.40f);
    colors[ImGuiCol_FrameBgActive] = ImVec4(0.20f, 0.75f, 0.40f, 0.67f);
    colors[ImGuiCol_TitleBg] = ImVec4(0.00f, 0.12f, 0.06f, 1.00f);
    colors[ImGuiCol_TitleBgActive] = ImVec4(0.05f, 0.20f, 0.12f, 1.00f);
    colors[ImGuiCol_TitleBgCollapsed] = ImVec4(0.00f, 0.00f, 0.00f, 0.51f);
    colors[ImGuiCol_MenuBarBg] = ImVec4(0.00f, 0.15f, 0.08f, 1.00f);
    colors[ImGuiCol_ScrollbarBg] = ImVec4(0.00f, 0.10f, 0.05f, 0.53f);
    colors[ImGuiCol_ScrollbarGrab] = ImVec4(0.05f, 0.30f, 0.15f, 1.00f);
    colors[ImGuiCol_ScrollbarGrabHovered] = ImVec4(0.10f, 0.60f, 0.30f, 1.00f);
    colors[ImGuiCol_ScrollbarGrabActive] = ImVec4(0.15f, 0.80f, 0.40f, 1.00f);
    colors[ImGuiCol_CheckMark] = ImVec4(0.20f, 0.85f, 0.45f, 1.00f);
    colors[ImGuiCol_SliderGrab] = ImVec4(0.18f, 0.70f, 0.38f, 1.00f);
    colors[ImGuiCol_SliderGrabActive] = ImVec4(0.22f, 0.85f, 0.48f, 1.00f);
    colors[ImGuiCol_Button] = ImVec4(0.18f, 0.70f, 0.38f, 0.40f);
    colors[ImGuiCol_ButtonHovered] = ImVec4(0.22f, 0.80f, 0.45f, 1.00f);
    colors[ImGuiCol_ButtonActive] = ImVec4(0.10f, 0.65f, 0.30f, 1.00f);
    colors[ImGuiCol_Header] = ImVec4(0.15f, 0.55f, 0.30f, 0.31f);
    colors[ImGuiCol_HeaderHovered] = ImVec4(0.18f, 0.70f, 0.38f, 0.80f);
    colors[ImGuiCol_HeaderActive] = ImVec4(0.22f, 0.80f, 0.45f, 1.00f);
    colors[ImGuiCol_Separator] = ImVec4(0.05f, 0.40f, 0.20f, 0.50f);
    colors[ImGuiCol_SeparatorHovered] = ImVec4(0.10f, 0.60f, 0.30f, 0.78f);
    colors[ImGuiCol_SeparatorActive] = ImVec4(0.15f, 0.70f, 0.35f, 1.00f);
    colors[ImGuiCol_ResizeGrip] = ImVec4(0.20f, 0.75f, 0.40f, 0.20f);
    colors[ImGuiCol_ResizeGripHovered] = ImVec4(0.25f, 0.85f, 0.48f, 0.67f);
    colors[ImGuiCol_ResizeGripActive] = ImVec4(0.28f, 0.90f, 0.52f, 0.95f);
    colors[ImGuiCol_InputTextCursor] = ImVec4(1.00f, 1.00f, 1.00f, 1.00f);
    colors[ImGuiCol_TabHovered] = ImVec4(0.22f, 0.80f, 0.45f, 0.80f);
    colors[ImGuiCol_Tab] = ImVec4(0.12f, 0.45f, 0.25f, 0.86f);
    colors[ImGuiCol_TabSelected] = ImVec4(0.16f, 0.58f, 0.32f, 1.00f);
    colors[ImGuiCol_TabSelectedOverline] = ImVec4(0.22f, 0.80f, 0.45f, 1.00f);
    colors[ImGuiCol_TabDimmed] = ImVec4(0.05f, 0.15f, 0.08f, 0.97f);
    colors[ImGuiCol_TabDimmedSelected] = ImVec4(0.10f, 0.35f, 0.18f, 1.00f);
    colors[ImGuiCol_TabDimmedSelectedOverline] = ImVec4(0.12f, 0.42f, 0.22f, 0.00f);
    colors[ImGuiCol_DockingPreview] = ImVec4(0.25f, 0.85f, 0.48f, 0.70f);
    colors[ImGuiCol_DockingEmptyBg] = ImVec4(0.02f, 0.15f, 0.08f, 0.00f);
    colors[ImGuiCol_PlotLines] = ImVec4(0.20f, 0.90f, 0.45f, 1.00f);
    colors[ImGuiCol_PlotLinesHovered] = ImVec4(0.35f, 1.00f, 0.60f, 1.00f);
    colors[ImGuiCol_PlotHistogram] = ImVec4(0.00f, 0.70f, 0.30f, 1.00f);
    colors[ImGuiCol_PlotHistogramHovered] = ImVec4(0.00f, 0.85f, 0.40f, 1.00f);
    colors[ImGuiCol_TableHeaderBg] = ImVec4(0.02f, 0.22f, 0.12f, 1.00f);
    colors[ImGuiCol_TableBorderStrong] = ImVec4(0.03f, 0.25f, 0.13f, 1.00f);
    colors[ImGuiCol_TableBorderLight] = ImVec4(0.02f, 0.20f, 0.10f, 1.00f);
    colors[ImGuiCol_TableRowBg] = ImVec4(0.00f, 0.00f, 0.00f, 0.00f);
    colors[ImGuiCol_TableRowBgAlt] = ImVec4(1.00f, 1.00f, 1.00f, 0.06f);
    colors[ImGuiCol_TextLink] = ImVec4(0.10f, 0.75f, 0.35f, 1.00f);
    colors[ImGuiCol_TextSelectedBg] = ImVec4(0.18f, 0.70f, 0.38f, 0.35f);
    colors[ImGuiCol_DragDropTarget] = ImVec4(0.30f, 1.00f, 0.50f, 0.90f);
    colors[ImGuiCol_NavCursor] = ImVec4(0.22f, 0.80f, 0.45f, 1.00f);
    colors[ImGuiCol_NavWindowingHighlight] = ImVec4(1.00f, 1.00f, 1.00f, 0.70f);
    colors[ImGuiCol_NavWindowingDimBg] = ImVec4(0.80f, 0.80f, 0.80f, 0.20f);
    colors[ImGuiCol_ModalWindowDimBg] = ImVec4(0.80f, 0.80f, 0.80f, 0.35f);
}

void VinceWindow::NewFrame() {
    ImGui_ImplOpenGL3_NewFrame();
    ImGui_ImplGlfw_NewFrame();

    glClearColor(0.0f, 0.0f, 0.0f, 0.00f);
    glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);

    ImGui::NewFrame();
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0, 0, 0, 0));
    ImGui::PushStyleColor(ImGuiCol_ChildBg, ImVec4(0, 0, 0, 0));
    ImGui::DockSpaceOverViewport(ImGui::GetMainViewport()->ID);
    ImGui::PopStyleColor(2);
}

void VinceWindow::EndFrame() {
    ImGui_ImplOpenGL3_RenderDrawData(ImGui::GetDrawData());
    glfwSwapBuffers(window.get());
}
