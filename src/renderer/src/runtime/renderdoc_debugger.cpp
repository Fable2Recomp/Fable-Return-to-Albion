#include "renderdoc_debugger.h"

#include "runtime/cvars.h"

#include <cstdlib>
#include <fstream>
#include <filesystem>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include <rex/logging.h>

#if REFII_RENDERER_HAS_RENDERDOC
#include <renderdoc_app.h>
#endif

#if REFII_RENDERER_HAS_RENDERDOC
#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#elif defined(__linux__) || defined(__FreeBSD__) || defined(__OpenBSD__) || defined(__sun__) || \
    defined(__APPLE__)
#include <dlfcn.h>
#if defined(__linux__)
#include <unistd.h>
#endif
#endif
#endif

namespace refii::renderer {
namespace {

#if REFII_RENDERER_HAS_RENDERDOC
std::mutex g_renderdoc_mutex;
RENDERDOC_API_1_7_0* g_renderdoc_api = nullptr;
bool g_renderdoc_probe_attempted = false;
bool g_renderdoc_logged_unavailable = false;
bool g_renderdoc_vulkan_layer_prepared = false;
bool g_renderdoc_vulkan_layer_logged = false;
std::string g_renderdoc_loaded_path;
std::string g_renderdoc_pending_capture_comments;
uint32_t g_renderdoc_capture_start_count = 0;
bool g_renderdoc_capture_path_configured = false;

std::filesystem::path CurrentExecutableDir();
RENDERDOC_API_1_7_0* GetRenderDocApi();

void ApplyRenderDocOptions(RENDERDOC_API_1_7_0* api) {
  if (RenderDocOverlayEnabled()) {
    api->MaskOverlayBits(eRENDERDOC_Overlay_All, eRENDERDOC_Overlay_Default);
  } else {
    api->MaskOverlayBits(~eRENDERDOC_Overlay_Enabled, 0);
  }
}

void ConfigureRenderDocCapturePath(RENDERDOC_API_1_7_0* api) {
  if (api == nullptr || g_renderdoc_capture_path_configured) {
    return;
  }

  const char* current_template = api->GetCaptureFilePathTemplate();
  if (current_template != nullptr && current_template[0] != '\0') {
    g_renderdoc_capture_path_configured = true;
    return;
  }

  const std::filesystem::path capture_dir =
      CurrentExecutableDir() / "renderdoc";
  std::error_code ec;
  std::filesystem::create_directories(capture_dir, ec);
  if (ec) {
    REXLOG_WARN("RenderDoc capture directory could not be created: {}",
                capture_dir.string());
    g_renderdoc_capture_path_configured = true;
    return;
  }

  const std::filesystem::path capture_template =
      capture_dir / "refii_plume";
  api->SetCaptureFilePathTemplate(capture_template.string().c_str());
  g_renderdoc_capture_path_configured = true;
  REXLOG_INFO("RenderDoc captures for Refii renderer will use template {}",
              capture_template.string());
}

std::string RenderDocCapturePath(uint32_t capture_index) {
  RENDERDOC_API_1_7_0* api = GetRenderDocApi();
  if (api == nullptr) {
    return {};
  }

  uint32_t path_length = 0;
  if (!api->GetCapture(capture_index, nullptr, &path_length, nullptr) ||
      path_length == 0) {
    return {};
  }

  std::string path(path_length, '\0');
  uint64_t timestamp = 0;
  if (!api->GetCapture(capture_index, path.data(), &path_length,
                       &timestamp)) {
    return {};
  }
  if (!path.empty() && path.back() == '\0') {
    path.pop_back();
  }
  return path;
}

void AddRenderDocCandidate(std::vector<std::filesystem::path>& candidates,
                           const std::filesystem::path& path) {
  if (path.empty()) {
    return;
  }

  candidates.push_back(path);
}

void AddRenderDocEnvCandidate(std::vector<std::filesystem::path>& candidates,
                              const char* name,
                              std::string_view library_name) {
  const char* value = std::getenv(name);
  if (value == nullptr || value[0] == '\0') {
    return;
  }

  const std::filesystem::path path(value);
  AddRenderDocCandidate(candidates, path);
  AddRenderDocCandidate(candidates, path / library_name);
}

std::filesystem::path CurrentExecutableDir() {
#if defined(__linux__)
  char path[4096] = {};
  const ssize_t size = readlink("/proc/self/exe", path, sizeof(path) - 1);
  if (size > 0) {
    path[size] = '\0';
    return std::filesystem::path(path).parent_path();
  }
#endif
  return {};
}

std::string GetEnvString(const char* name) {
  const char* value = std::getenv(name);
  return value == nullptr ? std::string() : std::string(value);
}

void SetEnvString(const char* name, const std::string& value) {
#if defined(_WIN32)
  _putenv_s(name, value.c_str());
#else
  setenv(name, value.c_str(), 1);
#endif
}

void PrependPathEnv(const char* name, const std::filesystem::path& path) {
  const std::string path_string = path.string();
  if (path_string.empty()) {
    return;
  }

  const std::string existing = GetEnvString(name);
  if (existing.find(path_string) != std::string::npos) {
    return;
  }

#if defined(_WIN32)
  constexpr char kSeparator = ';';
#else
  constexpr char kSeparator = ':';
#endif
  SetEnvString(name, existing.empty() ? path_string
                                      : path_string + kSeparator + existing);
}

void AppendLayerEnv(const char* name, std::string_view layer_name) {
  std::string existing = GetEnvString(name);
  if (existing.find(layer_name) != std::string::npos) {
    return;
  }

  if (!existing.empty()) {
    existing.push_back(':');
  }
  existing.append(layer_name);
  SetEnvString(name, existing);
}

std::vector<std::filesystem::path> BuildRenderDocCandidates(
    std::string_view library_name) {
  std::vector<std::filesystem::path> candidates;

  AddRenderDocCandidate(candidates, ConfiguredRendererRenderDocRuntimePath());
  AddRenderDocEnvCandidate(candidates, "RENDERDOC_LIBRARY", library_name);
  AddRenderDocEnvCandidate(candidates, "RENDERDOC_LIB", library_name);
  AddRenderDocEnvCandidate(candidates, "RENDERDOC_PATH", library_name);
  AddRenderDocEnvCandidate(candidates, "RENDERDOC_HOME", library_name);

  const std::filesystem::path executable_dir = CurrentExecutableDir();
  if (!executable_dir.empty()) {
    AddRenderDocCandidate(candidates, executable_dir / library_name);
    AddRenderDocCandidate(candidates, executable_dir / "lib" / library_name);
    AddRenderDocCandidate(candidates,
                          executable_dir / "renderdoc" / library_name);
    AddRenderDocCandidate(candidates,
                          executable_dir.parent_path() / "lib" / library_name);
  }

#if defined(REFII_RENDERER_RENDERDOC_ROOT)
  const std::filesystem::path source_root(REFII_RENDERER_RENDERDOC_ROOT);
  AddRenderDocCandidate(candidates, source_root / "build" / "lib" / library_name);
  AddRenderDocCandidate(candidates, source_root / "build" / "bin" / library_name);
  AddRenderDocCandidate(candidates, source_root / "renderdoc" / library_name);
#endif

#if defined(REFII_RENDERER_RENDERDOC_RUNTIME_DIR)
  AddRenderDocCandidate(
      candidates,
      std::filesystem::path(REFII_RENDERER_RENDERDOC_RUNTIME_DIR) / library_name);
#endif

#if defined(__linux__) || defined(__FreeBSD__) || defined(__OpenBSD__) || defined(__sun__)
  AddRenderDocCandidate(candidates,
                        std::filesystem::path("/usr/lib") / library_name);
  AddRenderDocCandidate(candidates,
                        std::filesystem::path("/usr/lib64") / library_name);
  AddRenderDocCandidate(
      candidates,
      std::filesystem::path("/usr/lib/x86_64-linux-gnu") / library_name);
  AddRenderDocCandidate(candidates,
                        std::filesystem::path("/usr/local/lib") / library_name);
  AddRenderDocCandidate(candidates,
                        std::filesystem::path("/usr/lib/renderdoc") / library_name);
  AddRenderDocCandidate(candidates,
                        std::filesystem::path("/opt/renderdoc") / library_name);
  AddRenderDocCandidate(candidates,
                        std::filesystem::path("/opt/renderdoc/lib") / library_name);
#elif defined(__APPLE__)
  AddRenderDocCandidate(candidates,
                        std::filesystem::path("/usr/local/lib") / library_name);
#endif

  AddRenderDocCandidate(candidates, std::filesystem::path(library_name));
  return candidates;
}

void* LoadRenderDocModuleByName(std::string_view library_name) {
#if defined(_WIN32)
  HMODULE module = GetModuleHandleA(std::string(library_name).c_str());
  if (module == nullptr) {
    module = LoadLibraryA(std::string(library_name).c_str());
  }
  if (module != nullptr) {
    g_renderdoc_loaded_path = std::string(library_name);
  }
  return module;
#elif defined(__linux__) || defined(__FreeBSD__) || defined(__OpenBSD__) || defined(__sun__) || \
    defined(__APPLE__)
  void* module = dlopen(std::string(library_name).c_str(),
                        RTLD_NOW | RTLD_NOLOAD | RTLD_GLOBAL);
  if (module != nullptr) {
    g_renderdoc_loaded_path = std::string(library_name);
    return module;
  }

  for (const std::filesystem::path& candidate :
       BuildRenderDocCandidates(library_name)) {
    module = dlopen(candidate.c_str(), RTLD_NOW | RTLD_GLOBAL);
    if (module != nullptr) {
      g_renderdoc_loaded_path = candidate.string();
      return module;
    }
  }
  return nullptr;
#else
  (void)library_name;
  return nullptr;
#endif
}

pRENDERDOC_GetAPI ResolveRenderDocGetApi() {
#if defined(_WIN32)
  HMODULE module =
      reinterpret_cast<HMODULE>(LoadRenderDocModuleByName("renderdoc.dll"));
  if (module == nullptr) {
    return nullptr;
  }
  return reinterpret_cast<pRENDERDOC_GetAPI>(
      GetProcAddress(module, "RENDERDOC_GetAPI"));
#elif defined(__linux__) || defined(__FreeBSD__) || defined(__OpenBSD__) || defined(__sun__)
  void* module = LoadRenderDocModuleByName("librenderdoc.so");
  if (module == nullptr) {
    return nullptr;
  }
  return reinterpret_cast<pRENDERDOC_GetAPI>(dlsym(module, "RENDERDOC_GetAPI"));
#elif defined(__APPLE__)
  void* module = LoadRenderDocModuleByName("librenderdoc.dylib");
  if (module == nullptr) {
    return nullptr;
  }
  return reinterpret_cast<pRENDERDOC_GetAPI>(dlsym(module, "RENDERDOC_GetAPI"));
#else
  return nullptr;
#endif
}

RENDERDOC_API_1_7_0* GetRenderDocApi() {
  if (!RenderDocDebuggerEnabled()) {
    return nullptr;
  }

  std::lock_guard lock(g_renderdoc_mutex);
  if (g_renderdoc_api != nullptr) {
    ApplyRenderDocOptions(g_renderdoc_api);
    return g_renderdoc_api;
  }
  if (g_renderdoc_probe_attempted) {
    return nullptr;
  }

  g_renderdoc_probe_attempted = true;
  pRENDERDOC_GetAPI get_api = ResolveRenderDocGetApi();
  if (get_api == nullptr) {
    if (!g_renderdoc_logged_unavailable) {
      REXLOG_WARN("RenderDoc requested for Refii renderer, but RENDERDOC_GetAPI "
                  "was not available. Set renderer_renderdoc_runtime_path or "
                  "launch through RenderDoc so the runtime can be injected.");
      g_renderdoc_logged_unavailable = true;
    }
    return nullptr;
  }

  void* api = nullptr;
  if (get_api(eRENDERDOC_API_Version_1_7_0, &api) != 1 || api == nullptr) {
    REXLOG_WARN("RenderDoc runtime was found, but API 1.7.0 was not available.");
    return nullptr;
  }

  g_renderdoc_api = static_cast<RENDERDOC_API_1_7_0*>(api);
  ApplyRenderDocOptions(g_renderdoc_api);
  ConfigureRenderDocCapturePath(g_renderdoc_api);

  int major = 0;
  int minor = 0;
  int patch = 0;
  g_renderdoc_api->GetAPIVersion(&major, &minor, &patch);
  REXLOG_INFO(
      "RenderDoc graphics debugger attached for Refii renderer: {}.{}.{} ({})",
      major, minor, patch,
      g_renderdoc_loaded_path.empty() ? "preloaded" : g_renderdoc_loaded_path);
  return g_renderdoc_api;
}
#endif

}  // namespace

bool RenderDocDebuggerAvailable() {
#if REFII_RENDERER_HAS_RENDERDOC
  return GetRenderDocApi() != nullptr;
#else
  return false;
#endif
}

void InitializeRenderDocDebugger() {
  (void)RenderDocDebuggerAvailable();
}

void PrepareRenderDocVulkanLayer() {
#if REFII_RENDERER_HAS_RENDERDOC && \
    (defined(__linux__) || defined(__FreeBSD__) || defined(__OpenBSD__) || defined(__sun__))
  if (!RenderDocDebuggerEnabled() || g_renderdoc_vulkan_layer_prepared) {
    return;
  }

  RENDERDOC_API_1_7_0* api = GetRenderDocApi();
  if (api == nullptr) {
    return;
  }

  std::filesystem::path renderdoc_library(g_renderdoc_loaded_path);
  if (renderdoc_library.empty() || !renderdoc_library.is_absolute()) {
    const std::filesystem::path executable_dir = CurrentExecutableDir();
    if (!executable_dir.empty()) {
      renderdoc_library = executable_dir / "lib" / "librenderdoc.so";
    }
  }
  if (renderdoc_library.empty()) {
    return;
  }

  const std::filesystem::path layer_dir =
      CurrentExecutableDir() / "renderdoc";
  const std::filesystem::path layer_manifest =
      layer_dir / "renderdoc_capture.json";
  std::error_code ec;
  std::filesystem::create_directories(layer_dir, ec);
  if (ec) {
    REXLOG_WARN("RenderDoc Vulkan layer manifest directory could not be "
                "created: {}",
                layer_dir.string());
    return;
  }

  std::ofstream manifest(layer_manifest, std::ios::out | std::ios::trunc);
  if (!manifest) {
    REXLOG_WARN("RenderDoc Vulkan layer manifest could not be written: {}",
                layer_manifest.string());
    return;
  }

  manifest
      << "{\n"
      << "  \"file_format_version\" : \"1.1.2\",\n"
      << "  \"layer\" : {\n"
      << "    \"name\": \"VK_LAYER_RENDERDOC_Capture\",\n"
      << "    \"type\": \"GLOBAL\",\n"
      << "    \"library_path\": \"" << renderdoc_library.string() << "\",\n"
      << "    \"api_version\": \"1.4.324\",\n"
      << "    \"implementation_version\": \"45\",\n"
      << "    \"description\": \"Debugging capture layer for RenderDoc\",\n"
      << "    \"functions\": {\n"
      << "      \"vkGetInstanceProcAddr\": "
         "\"VK_LAYER_RENDERDOC_CaptureGetInstanceProcAddr\",\n"
      << "      \"vkGetDeviceProcAddr\": "
         "\"VK_LAYER_RENDERDOC_CaptureGetDeviceProcAddr\",\n"
      << "      \"vkNegotiateLoaderLayerInterfaceVersion\": "
         "\"VK_LAYER_RENDERDOC_CaptureNegotiateLoaderLayerInterfaceVersion\"\n"
      << "    },\n"
      << "    \"pre_instance_functions\": {\n"
      << "      \"vkEnumerateInstanceExtensionProperties\": "
         "\"VK_LAYER_RENDERDOC_CaptureEnumerateInstanceExtensionProperties\"\n"
      << "    },\n"
      << "    \"instance_extensions\": [\n"
      << "      { \"name\": \"VK_EXT_debug_utils\", \"spec_version\": \"1\" }\n"
      << "    ],\n"
      << "    \"device_extensions\": [\n"
      << "      { \"name\": \"VK_EXT_debug_marker\", "
         "\"spec_version\": \"4\" },\n"
      << "      { \"name\": \"VK_EXT_tooling_info\", "
         "\"spec_version\": \"1\" }\n"
      << "    ],\n"
      << "    \"enable_environment\": {\n"
      << "      \"ENABLE_VULKAN_RENDERDOC_CAPTURE\": \"1\"\n"
      << "    }\n"
      << "  }\n"
      << "}\n";
  manifest.close();

  SetEnvString("ENABLE_VULKAN_RENDERDOC_CAPTURE", "1");
  PrependPathEnv("VK_LAYER_PATH", layer_dir);
  PrependPathEnv("VK_ADD_LAYER_PATH", layer_dir);
  PrependPathEnv("VK_ADD_IMPLICIT_LAYER_PATH", layer_dir);
  AppendLayerEnv("VK_INSTANCE_LAYERS", "VK_LAYER_RENDERDOC_Capture");

  g_renderdoc_vulkan_layer_prepared = true;
  if (!g_renderdoc_vulkan_layer_logged) {
    REXLOG_INFO(
        "RenderDoc Vulkan layer prepared for Refii renderer: manifest={} "
        "library={}",
        layer_manifest.string(), renderdoc_library.string());
    g_renderdoc_vulkan_layer_logged = true;
  }
#else
#endif
}

void TriggerRenderDocCapture() {
#if REFII_RENDERER_HAS_RENDERDOC
  RENDERDOC_API_1_7_0* api = GetRenderDocApi();
  if (api == nullptr) {
    return;
  }

  api->TriggerCapture();
  REXLOG_INFO("RenderDoc capture requested from Refii renderer.");

  if (RenderDocReplayLaunchEnabled()) {
    const uint32_t pid = api->LaunchReplayUI(1, nullptr);
    if (pid == 0) {
      REXLOG_WARN("RenderDoc replay UI launch request failed.");
    }
  }
#endif
}

bool BeginRenderDocFrameCapture(const char* title) {
#if REFII_RENDERER_HAS_RENDERDOC
  RENDERDOC_API_1_7_0* api = GetRenderDocApi();
  if (api == nullptr) {
    return false;
  }
  if (!ConsumeRenderDocCaptureRequest()) {
    return false;
  }
  if (api->IsFrameCapturing()) {
    REXLOG_WARN("RenderDoc capture requested, but a capture is already active.");
    return false;
  }

  g_renderdoc_capture_start_count = api->GetNumCaptures();
  g_renderdoc_pending_capture_comments.clear();
  api->StartFrameCapture(nullptr, nullptr);
  if (title != nullptr && title[0] != '\0') {
    api->SetCaptureTitle(title);
  }
  if (api->IsFrameCapturing() == 0) {
    REXLOG_WARN(
        "RenderDoc frame capture was requested for Refii renderer, but "
        "RenderDoc did not enter frame capture mode.");
    return false;
  }

  REXLOG_INFO("RenderDoc frame capture started for Refii renderer.");
  return true;
#else
  (void)title;
  return false;
#endif
}

void SetRenderDocFrameCaptureTitle(const char* title) {
#if REFII_RENDERER_HAS_RENDERDOC
  RENDERDOC_API_1_7_0* api = GetRenderDocApi();
  if (api == nullptr || !api->IsFrameCapturing() || title == nullptr ||
      title[0] == '\0') {
    return;
  }
  api->SetCaptureTitle(title);
#else
  (void)title;
#endif
}

void SetRenderDocFrameCaptureComments(const char* comments) {
#if REFII_RENDERER_HAS_RENDERDOC
  RENDERDOC_API_1_7_0* api = GetRenderDocApi();
  if (api == nullptr || !api->IsFrameCapturing() || comments == nullptr ||
      comments[0] == '\0') {
    return;
  }
  g_renderdoc_pending_capture_comments = comments;
#else
  (void)comments;
#endif
}

void EndRenderDocFrameCapture(bool capture_started) {
#if REFII_RENDERER_HAS_RENDERDOC
  if (!capture_started) {
    return;
  }

  RENDERDOC_API_1_7_0* api = GetRenderDocApi();
  if (api == nullptr) {
    return;
  }

  const uint32_t capture_ok = api->EndFrameCapture(nullptr, nullptr);
  if (capture_ok) {
    REXLOG_INFO("RenderDoc frame capture completed for Refii renderer.");
    if (!g_renderdoc_pending_capture_comments.empty()) {
      api->SetCaptureFileComments(
          nullptr, g_renderdoc_pending_capture_comments.c_str());
      g_renderdoc_pending_capture_comments.clear();
    }
    const uint32_t capture_count = api->GetNumCaptures();
    for (uint32_t index = g_renderdoc_capture_start_count;
         index < capture_count; ++index) {
      const std::string path = RenderDocCapturePath(index);
      if (!path.empty()) {
        REXLOG_INFO("RenderDoc Refii renderer capture saved: {}", path);
      }
    }
    if (RenderDocReplayLaunchEnabled()) {
      const uint32_t pid = api->LaunchReplayUI(1, nullptr);
      if (pid == 0) {
        REXLOG_WARN("RenderDoc replay UI launch request failed.");
      }
    }
  } else {
    REXLOG_WARN("RenderDoc frame capture ended without producing a capture.");
  }
#else
  (void)capture_started;
#endif
}

void PollRenderDocCaptureRequest() {
  if (RenderDocDebuggerEnabled()) {
    InitializeRenderDocDebugger();
  }
}

}  // namespace refii::renderer
