#include "window.h"

#include "fonts.h"
#include "platform.h"
#include "utils/inimanager.h"
#include "utils/launcherpaths.h"
#include "utils/opengl_stuff.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>

namespace {

#if defined(__linux__)
#ifndef REFII_LAUNCHER_HAS_WAYLAND
#define REFII_LAUNCHER_HAS_WAYLAND 0
#endif
#ifndef REFII_LAUNCHER_HAS_X11
#define REFII_LAUNCHER_HAS_X11 0
#endif

bool HasEnvironmentValue(const char* name) {
    const char* value = std::getenv(name);
    return value != nullptr && value[0] != '\0';
}
#endif

// These failures used to be plain asserts, which NDEBUG strips out of release
// builds: startup carried on with an uninitialized GLFW and a null window and
// the process died with nothing printed. Report and stop instead.
[[noreturn]] void FailWindowStartup(const char* message) {
    std::fprintf(stderr, "Refii launcher: %s\n", message);
    std::fflush(stderr);
    std::exit(EXIT_FAILURE);
}

#if defined(__linux__)

std::string LowercaseAscii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        if (character >= 'A' && character <= 'Z') {
            return static_cast<char>(character - 'A' + 'a');
        }
        return static_cast<char>(character);
    });
    return value;
}

const char* GLFWPlatformName(int platform) {
    switch (platform) {
        case GLFW_PLATFORM_WAYLAND:
            return "Wayland";
        case GLFW_PLATFORM_X11:
            return "X11";
        case GLFW_ANY_PLATFORM:
            return "auto";
        default:
            return "unknown";
    }
}

int FirstAvailablePlatform() {
#if REFII_LAUNCHER_HAS_WAYLAND && REFII_LAUNCHER_HAS_X11
    return GLFW_ANY_PLATFORM;
#elif REFII_LAUNCHER_HAS_WAYLAND
    return GLFW_PLATFORM_WAYLAND;
#elif REFII_LAUNCHER_HAS_X11
    return GLFW_PLATFORM_X11;
#else
    return GLFW_ANY_PLATFORM;
#endif
}

bool IsPlatformAvailable(int platform) {
    switch (platform) {
        case GLFW_PLATFORM_WAYLAND:
            return REFII_LAUNCHER_HAS_WAYLAND != 0;
        case GLFW_PLATFORM_X11:
            return REFII_LAUNCHER_HAS_X11 != 0;
        case GLFW_ANY_PLATFORM:
            return true;
        default:
            return false;
    }
}

int SelectAvailablePlatform(int platform) {
    if (IsPlatformAvailable(platform)) {
        return platform;
    }

    std::fprintf(
        stderr,
        "Requested GLFW platform %s is not built into this launcher; falling back to %s.\n",
        GLFWPlatformName(platform),
        GLFWPlatformName(FirstAvailablePlatform()));
    return FirstAvailablePlatform();
}

int ReadLauncherPlatformOverride() {
    const char* value = std::getenv("REFII_LAUNCHER_GLFW_PLATFORM");
    if (value == nullptr || value[0] == '\0') {
        return GLFW_ANY_PLATFORM;
    }

    const std::string platform = LowercaseAscii(value);
    if (platform == "wayland") {
        return GLFW_PLATFORM_WAYLAND;
    }
    if (platform == "x11") {
        return GLFW_PLATFORM_X11;
    }
    if (platform == "auto" || platform == "any") {
        return GLFW_ANY_PLATFORM;
    }

    std::fprintf(
        stderr,
        "Unknown REFII_LAUNCHER_GLFW_PLATFORM value '%s'; using automatic platform selection.\n",
        value);
    return GLFW_ANY_PLATFORM;
}

struct PlatformChoice {
    int platform = GLFW_ANY_PLATFORM;
    // An explicit request is honoured as-is; only auto-detection falls back.
    bool explicitly_requested = false;
};

PlatformChoice ChooseGLFWPlatform() {
    const int overridePlatform = ReadLauncherPlatformOverride();
    if (overridePlatform != GLFW_ANY_PLATFORM) {
        return {SelectAvailablePlatform(overridePlatform), true};
    }

#if REFII_LAUNCHER_HAS_WAYLAND
    if (HasEnvironmentValue("WAYLAND_DISPLAY")) {
        return {GLFW_PLATFORM_WAYLAND, false};
    }
#endif

#if REFII_LAUNCHER_HAS_X11
    if (HasEnvironmentValue("DISPLAY")) {
        return {GLFW_PLATFORM_X11, false};
    }
#endif

    return {FirstAvailablePlatform(), false};
}

bool TryInitGLFWPlatform(int platform) {
    glfwInitHint(GLFW_PLATFORM, platform);
#if REFII_LAUNCHER_HAS_WAYLAND
    glfwInitHint(GLFW_WAYLAND_LIBDECOR, GLFW_WAYLAND_PREFER_LIBDECOR);
#endif
    if (glfwInit()) {
        std::fprintf(stderr, "Refii launcher GLFW platform: %s\n", GLFWPlatformName(platform));
        return true;
    }
    return false;
}

// Pinning GLFW_PLATFORM means a failed init is final, so a Wayland session
// whose client libraries do not line up would otherwise take the launcher down
// with it. Fall back to X11, which on such a session means XWayland.
bool InitializeGLFW() {
    const PlatformChoice choice = ChooseGLFWPlatform();
    if (TryInitGLFWPlatform(choice.platform)) {
        return true;
    }

    if (choice.explicitly_requested) {
        std::fprintf(stderr,
                     "Refii launcher could not initialize the requested GLFW platform (%s).\n",
                     GLFWPlatformName(choice.platform));
        return false;
    }

    for (const int fallback : {GLFW_PLATFORM_WAYLAND, GLFW_PLATFORM_X11}) {
        if (fallback == choice.platform || !IsPlatformAvailable(fallback)) {
            continue;
        }
        std::fprintf(stderr,
                     "Refii launcher could not initialize GLFW on %s; trying %s.\n",
                     GLFWPlatformName(choice.platform),
                     GLFWPlatformName(fallback));
        if (TryInitGLFWPlatform(fallback)) {
            return true;
        }
    }
    return false;
}

void ApplyPlatformWindowHints() {
    const int platform = glfwGetPlatform();
    if (platform == GLFW_PLATFORM_WAYLAND) {
        glfwWindowHintString(GLFW_WAYLAND_APP_ID, "com.refii.refii");
    } else if (platform == GLFW_PLATFORM_X11) {
        glfwWindowHintString(GLFW_X11_CLASS_NAME, "refii");
        glfwWindowHintString(GLFW_X11_INSTANCE_NAME, "refii");
    }
}

bool ShouldApplyWindowIcon() {
    return glfwGetPlatform() != GLFW_PLATFORM_WAYLAND;
}
#endif

void ApplyWindowIcon(GLFWwindow* window) {
    const std::string iconPath = LauncherPaths::GetLauncherAssetPath("icon.png").string();
    int width = 0;
    int height = 0;
    int channels = 0;
    unsigned char* pixels = stbi_load(iconPath.c_str(), &width, &height, &channels, 4);
    if (pixels == nullptr || width <= 0 || height <= 0) {
        if (pixels != nullptr) {
            stbi_image_free(pixels);
        }
        return;
    }

    GLFWimage image = {};
    image.width = width;
    image.height = height;
    image.pixels = pixels;
    glfwSetWindowIcon(window, 1, &image);
    stbi_image_free(pixels);
}

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
    if (!InitializeGLFW()) {
        FailWindowStartup(
            "Failed to initialize GLFW: no usable window system.\n"
            "On a Wayland session you can force the X11/XWayland path with\n"
            "  REFII_LAUNCHER_GLFW_PLATFORM=x11");
    }
#else
    if (!glfwInit()) {
        FailWindowStartup("Failed to initialize GLFW.");
    }
#endif

    glfwWindowHint(GLFW_CONTEXT_VERSION_MAJOR, 3);
    glfwWindowHint(GLFW_CONTEXT_VERSION_MINOR, 0);
    glfwWindowHint(GLFW_TRANSPARENT_FRAMEBUFFER, GLFW_TRUE);
#if defined(__linux__)
    ApplyPlatformWindowHints();
#endif

    window = std::unique_ptr<GLFWwindow, decltype(&glfwDestroyWindow)>(
        glfwCreateWindow(width, height, title.c_str(), nullptr, nullptr),
        &glfwDestroyWindow);
    if (!window) {
        FailWindowStartup("Failed to create the launcher window.");
    }
#if defined(__linux__)
    if (ShouldApplyWindowIcon()) {
        ApplyWindowIcon(window.get());
    }
#else
    ApplyWindowIcon(window.get());
#endif

    int bufferWidth = 0;
    int bufferHeight = 0;
    glfwGetFramebufferSize(window.get(), &bufferWidth, &bufferHeight);
    glfwMakeContextCurrent(window.get());

    if (!gladLoadGLLoader((GLADloadproc)glfwGetProcAddress)) {
        FailWindowStartup("Failed to initialize the OpenGL context.");
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

void VinceWindow::Destroy() {
    window.reset();
}
