#include "renderdoc_debugger.h"

#include "runtime/cvars.h"

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdlib>
#include <fstream>
#include <filesystem>
#include <iterator>
#include <mutex>
#include <string>
#include <string_view>
#include <utility>
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
#endif

#if defined(__linux__)
#include <unistd.h>
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
std::string g_renderdoc_capture_path_template;
std::string g_renderdoc_last_error;
int g_renderdoc_api_major = 0;
int g_renderdoc_api_minor = 0;
int g_renderdoc_api_patch = 0;
uint32_t g_renderdoc_capture_start_count = 0;
uint32_t g_renderdoc_known_capture_count = 0;
bool g_renderdoc_capture_path_configured = false;
bool g_renderdoc_known_capture_count_initialized = false;
std::atomic_bool g_renderdoc_refii_owned_capture_active{false};
std::mutex g_renderdoc_capture_publish_mutex;

std::filesystem::path CurrentExecutableDir();
RENDERDOC_API_1_7_0* GetRenderDocApi();

void SetRenderDocLastError(std::string error) {
  std::lock_guard lock(g_renderdoc_mutex);
  g_renderdoc_last_error = std::move(error);
}

std::filesystem::path ResolveRenderDocCaptureDirectory() {
  const std::filesystem::path executable_dir = CurrentExecutableDir();
  if (!executable_dir.empty()) {
    return executable_dir / "renderdoc" / "captures";
  }
  return std::filesystem::path("renderdoc") / "captures";
}

std::string SanitizeRenderDocCaptureTag(std::string_view tag) {
  std::string result;
  result.reserve(tag.size());
  for (unsigned char ch : tag) {
    if (std::isalnum(ch) || ch == '_' || ch == '-' || ch == '.') {
      result.push_back(static_cast<char>(ch));
    } else if (!result.empty() && result.back() != '_') {
      result.push_back('_');
    } else if (result.empty()) {
      result.push_back('_');
    }
  }
  while (!result.empty() && result.back() == '_') {
    result.pop_back();
  }
  return result.empty() ? std::string("refii_plume") : result;
}

void ApplyRenderDocOptions(RENDERDOC_API_1_7_0* api) {
  if (RenderDocOverlayEnabled()) {
    api->MaskOverlayBits(eRENDERDOC_Overlay_All, eRENDERDOC_Overlay_Default);
  } else {
    api->MaskOverlayBits(~eRENDERDOC_Overlay_Enabled, 0);
  }
  // Source-of-truth fix (2026-07-18): this renderer binds textures through a
  // bindless descriptor heap, so RenderDoc's usage tracking does not see most
  // sampled resources as referenced — they were captured WITHOUT contents and
  // every replay sampled blank (the recurring "replay is white / IMG
  // UNDEFINED" trap that has repeatedly derailed capture-based debugging).
  // RefAllResources forces every live resource, with initial contents, into
  // the capture: replays sample real data and per-frame-rewritten textures
  // (resolve destinations, video planes) dump their true pre-frame contents.
  // Costs bigger/slower captures; that is the right trade for a debugger.
  api->SetCaptureOptionU32(eRENDERDOC_Option_RefAllResources, 1u);
}

void ConfigureRenderDocCapturePath(RENDERDOC_API_1_7_0* api) {
  if (api == nullptr || g_renderdoc_capture_path_configured) {
    return;
  }

  const std::filesystem::path capture_dir = ResolveRenderDocCaptureDirectory();
  if (capture_dir.empty()) {
    g_renderdoc_capture_path_configured = true;
    return;
  }

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
  const std::string capture_template_string = capture_template.string();
  api->SetCaptureFilePathTemplate(capture_template_string.c_str());
  g_renderdoc_capture_path_template = capture_template_string;
  g_renderdoc_capture_path_configured = true;
  REXLOG_INFO("RenderDoc captures for Refii renderer will use template {}",
              capture_template_string);
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

void InitializeRenderDocKnownCaptureCount(RENDERDOC_API_1_7_0* api) {
  if (api == nullptr || g_renderdoc_known_capture_count_initialized) {
    return;
  }

  g_renderdoc_known_capture_count = api->GetNumCaptures();
  g_renderdoc_known_capture_count_initialized = true;
}

void PublishCompletedExternalRenderDocCaptures(RENDERDOC_API_1_7_0* api) {
  if (api == nullptr ||
      g_renderdoc_refii_owned_capture_active.load(std::memory_order_acquire) ||
      api->IsFrameCapturing() != 0) {
    return;
  }

  std::lock_guard publish_lock(g_renderdoc_capture_publish_mutex);
  InitializeRenderDocKnownCaptureCount(api);
  const uint32_t capture_count = api->GetNumCaptures();
  if (capture_count <= g_renderdoc_known_capture_count) {
    return;
  }

  for (uint32_t index = g_renderdoc_known_capture_count; index < capture_count;
       ++index) {
    const std::string path = RenderDocCapturePath(index);
    if (!path.empty()) {
      REXLOG_INFO("RenderDoc external Refii renderer capture saved: {}", path);
      if (!g_renderdoc_pending_capture_comments.empty()) {
        api->SetCaptureFileComments(
            path.c_str(), g_renderdoc_pending_capture_comments.c_str());
      }
    }
  }
  g_renderdoc_pending_capture_comments.clear();
  g_renderdoc_known_capture_count = capture_count;
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

#if defined(__linux__) || defined(__FreeBSD__) || defined(__OpenBSD__) || defined(__sun__)
std::vector<std::filesystem::path> SplitPathList(const std::string& list) {
  std::vector<std::filesystem::path> paths;
  size_t start = 0;
  while (start <= list.size()) {
    const size_t sep = list.find(':', start);
    const size_t end = sep == std::string::npos ? list.size() : sep;
    if (end > start) {
      paths.emplace_back(list.substr(start, end - start));
    }
    if (sep == std::string::npos) {
      break;
    }
    start = sep + 1;
  }
  return paths;
}

// The Vulkan loader instantiates the capture layer by NAME
// (VK_LAYER_RENDERDOC_Capture), and an implicit-layer manifest found in the
// loader's standard scan wins over the VK_LAYER_PATH manifest staged next to
// the executable. If that manifest names a DIFFERENT librenderdoc file than
// the staged one, the process ends up with TWO module instances: the in-app
// API configures one while the loader's layer instance takes the captures
// with default settings — captures silently land in /tmp/RenderDoc with
// RefAllResources unset (2026-07-19 incident: a system-wide
// /etc/vulkan/implicit_layer.d manifest pointed at a separate RenderDoc
// checkout). Scan the loader's implicit-layer directories in its search
// order and surface those library paths as the HIGHEST-priority dlopen
// candidates, so configuration and capture always share one instance,
// wherever it lives.
void AddImplicitVulkanRenderDocLayerLibraries(
    std::vector<std::filesystem::path>& candidates) {
  std::vector<std::filesystem::path> manifest_dirs;
  const auto add_dir = [&manifest_dirs](std::filesystem::path dir) {
    if (!dir.empty()) {
      manifest_dirs.push_back(dir / "vulkan" / "implicit_layer.d");
    }
  };
  const std::string home = GetEnvString("HOME");
  const std::string xdg_config_home = GetEnvString("XDG_CONFIG_HOME");
  add_dir(xdg_config_home.empty()
              ? (home.empty() ? std::filesystem::path()
                              : std::filesystem::path(home) / ".config")
              : std::filesystem::path(xdg_config_home));
  const std::string xdg_config_dirs = GetEnvString("XDG_CONFIG_DIRS");
  for (const auto& dir :
       SplitPathList(xdg_config_dirs.empty() ? "/etc/xdg" : xdg_config_dirs)) {
    add_dir(dir);
  }
  add_dir("/etc");
  const std::string xdg_data_home = GetEnvString("XDG_DATA_HOME");
  add_dir(xdg_data_home.empty()
              ? (home.empty() ? std::filesystem::path()
                              : std::filesystem::path(home) / ".local/share")
              : std::filesystem::path(xdg_data_home));
  const std::string xdg_data_dirs = GetEnvString("XDG_DATA_DIRS");
  for (const auto& dir : SplitPathList(
           xdg_data_dirs.empty() ? "/usr/local/share:/usr/share"
                                 : xdg_data_dirs)) {
    add_dir(dir);
  }

  for (const std::filesystem::path& manifest_dir : manifest_dirs) {
    std::error_code ec;
    std::filesystem::directory_iterator it(manifest_dir, ec);
    if (ec) {
      continue;
    }
    for (const auto& entry : it) {
      if (entry.path().extension() != ".json") {
        continue;
      }
      std::ifstream stream(entry.path());
      if (!stream) {
        continue;
      }
      std::string contents((std::istreambuf_iterator<char>(stream)),
                           std::istreambuf_iterator<char>());
      if (contents.find("VK_LAYER_RENDERDOC_Capture") == std::string::npos) {
        continue;
      }
      const size_t key = contents.find("\"library_path\"");
      if (key == std::string::npos) {
        continue;
      }
      const size_t open_quote = contents.find('"', contents.find(':', key));
      if (open_quote == std::string::npos) {
        continue;
      }
      const size_t close_quote = contents.find('"', open_quote + 1);
      if (close_quote == std::string::npos) {
        continue;
      }
      std::filesystem::path library(
          contents.substr(open_quote + 1, close_quote - open_quote - 1));
      // Relative manifest paths resolve against the manifest's directory
      // (bare filenames mean "system library search" — leave those to the
      // plain library_name candidate at the end of the list).
      if (library.is_relative() && library.has_parent_path()) {
        library = entry.path().parent_path() / library;
      }
      if (library.is_absolute()) {
        AddRenderDocCandidate(candidates, library);
      }
    }
  }
}
#endif

std::vector<std::filesystem::path> BuildRenderDocCandidates(
    std::string_view library_name) {
  std::vector<std::filesystem::path> candidates;

#if defined(__linux__) || defined(__FreeBSD__) || defined(__OpenBSD__) || defined(__sun__)
  AddImplicitVulkanRenderDocLayerLibraries(candidates);
#endif

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
  // RTLD_LOCAL, never RTLD_GLOBAL: librenderdoc exports an interposing
  // dlopen plus the whole GL/EGL surface. In the global scope those hooks
  // capture every later dlopen in the process — the GPU plugin's Vulkan
  // loader included — injecting RenderDoc's intercept outside the layer
  // init path (the intermittent black xenia window). The in-app API is
  // resolved from this handle, and the Vulkan layer is loaded by the
  // Vulkan loader itself from the manifest path, so nothing needs these
  // symbols to be globally visible.
  void* module = dlopen(std::string(library_name).c_str(),
                        RTLD_NOW | RTLD_NOLOAD | RTLD_LOCAL);
  if (module != nullptr) {
    g_renderdoc_loaded_path = std::string(library_name);
    return module;
  }

  for (const std::filesystem::path& candidate :
       BuildRenderDocCandidates(library_name)) {
    module = dlopen(candidate.c_str(), RTLD_NOW | RTLD_LOCAL);
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
  // Native/xenia backend: loading librenderdoc RTLD_GLOBAL used to hook
  // Vulkan process-wide (its exported interposing dlopen captured the GPU
  // plugin's Vulkan loader when the load won the race against
  // vkCreateInstance — 2026-07-19 intermittent black xenia window). The
  // load is now RTLD_LOCAL (see LoadRenderDocModuleByName), which removes
  // that hazard; this gate stays as defense-in-depth so a debugger nobody
  // armed is never pulled into the native process. This is the single
  // choke point every probe/arm path funnels through. Plume mode is
  // unchanged.
  if (!PlumeRendererBackendEnabled() && !RenderDocNativeArmEnabled()) {
    static bool logged_native_refusal = false;
    if (!logged_native_refusal) {
      REXLOG_INFO(
          "Refii RenderDoc load refused for the native/xenia backend "
          "(renderer_renderdoc_native_arm is off; loading librenderdoc "
          "blacks out the native display)");
      logged_native_refusal = true;
    }
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
    g_renderdoc_last_error =
        "RENDERDOC_GetAPI was not available. Set "
        "renderer_renderdoc_runtime_path or launch through RenderDoc.";
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
    g_renderdoc_last_error =
        "RenderDoc runtime was found, but API 1.7.0 was not available.";
    REXLOG_WARN("RenderDoc runtime was found, but API 1.7.0 was not available.");
    return nullptr;
  }

  g_renderdoc_api = static_cast<RENDERDOC_API_1_7_0*>(api);
  ApplyRenderDocOptions(g_renderdoc_api);
  ConfigureRenderDocCapturePath(g_renderdoc_api);
  InitializeRenderDocKnownCaptureCount(g_renderdoc_api);

  int major = 0;
  int minor = 0;
  int patch = 0;
  g_renderdoc_api->GetAPIVersion(&major, &minor, &patch);
  g_renderdoc_api_major = major;
  g_renderdoc_api_minor = minor;
  g_renderdoc_api_patch = patch;
  g_renderdoc_last_error.clear();
  REXLOG_INFO(
      "RenderDoc graphics debugger attached for Refii renderer: {}.{}.{} ({})",
      major, minor, patch,
      g_renderdoc_loaded_path.empty() ? "preloaded" : g_renderdoc_loaded_path);
  return g_renderdoc_api;
}
#endif

}  // namespace

std::string RenderDocCaptureDirectory() {
#if REFII_RENDERER_HAS_RENDERDOC
  return ResolveRenderDocCaptureDirectory().string();
#else
  return (std::filesystem::path("renderdoc") / "captures").string();
#endif
}

bool RenderDocDebuggerAvailable() {
#if REFII_RENDERER_HAS_RENDERDOC
  return GetRenderDocApi() != nullptr;
#else
  return false;
#endif
}

RenderDocDebuggerStatus GetRenderDocDebuggerStatus() {
  RenderDocDebuggerStatus status;
  status.compiled = REFII_RENDERER_HAS_RENDERDOC != 0;
  status.enabled = RenderDocDebuggerEnabled();
  status.capture_request_pending = RenderDocCaptureRequestPending();
  status.replay_launch_enabled = RenderDocReplayLaunchEnabled();
#if REFII_RENDERER_HAS_RENDERDOC
  RENDERDOC_API_1_7_0* api = GetRenderDocApi();
  status.available = api != nullptr;
  if (api != nullptr) {
    status.capturing = api->IsFrameCapturing() != 0;
    status.capture_count = api->GetNumCaptures();
  }
  {
    std::lock_guard lock(g_renderdoc_mutex);
    status.probe_attempted = g_renderdoc_probe_attempted;
    status.api_major = g_renderdoc_api_major;
    status.api_minor = g_renderdoc_api_minor;
    status.api_patch = g_renderdoc_api_patch;
    status.loaded_path = g_renderdoc_loaded_path;
    status.capture_template = g_renderdoc_capture_path_template;
    status.last_error = g_renderdoc_last_error;
  }
  if (!status.enabled && status.last_error.empty()) {
    status.last_error = "RenderDoc integration is disabled.";
  } else if (status.enabled && !status.probe_attempted &&
             status.last_error.empty()) {
    status.last_error = "RenderDoc has not been probed yet.";
  }
#else
  status.last_error = "RenderDoc support was not compiled into this build.";
#endif
  return status;
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
      << "    \"api_version\": \"1.3.0\",\n"
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
  if (PlumeRendererBackendEnabled()) {
    // Plume's capture pipeline has run on explicit activation for weeks —
    // unchanged.
    PrependPathEnv("VK_LAYER_PATH", layer_dir);
    PrependPathEnv("VK_ADD_LAYER_PATH", layer_dir);
    PrependPathEnv("VK_ADD_IMPLICIT_LAYER_PATH", layer_dir);
    AppendLayerEnv("VK_INSTANCE_LAYERS", "VK_LAYER_RENDERDOC_Capture");
  } else {
    // Native/xenia (2026-07-19): the generated manifest is IMPLICIT-style
    // (GLOBAL type + enable_environment gate + pre_instance_functions).
    // Forcing it explicitly via VK_INSTANCE_LAYERS makes the vendored layer
    // fail vkCreateInstance extension negotiation against the modern loader
    // (loader policy LLP_LAYER_17; armed xenia runs died
    // ErrorExtensionNotPresent — runs 087-090). Implicit activation via
    // VK_ADD_IMPLICIT_LAYER_PATH + the ENABLE gate is RenderDoc's intended
    // injection mode and negotiates cleanly.
    PrependPathEnv("VK_ADD_IMPLICIT_LAYER_PATH", layer_dir);
  }

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

bool RenderDocFrameCaptureActive() {
#if REFII_RENDERER_HAS_RENDERDOC
  RENDERDOC_API_1_7_0* api = GetRenderDocApi();
  return api != nullptr && api->IsFrameCapturing() != 0;
#else
  return false;
#endif
}

bool StartRenderDocFrameCaptureNow(const char* title) {
#if REFII_RENDERER_HAS_RENDERDOC
  RENDERDOC_API_1_7_0* api = GetRenderDocApi();
  if (api == nullptr) {
    SetRenderDocLastError("RenderDoc capture requested, but API is unavailable.");
    return false;
  }
  if (api->IsFrameCapturing()) {
    SetRenderDocLastError(
        "RenderDoc capture requested, but a capture is already active.");
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
    SetRenderDocLastError(
        "RenderDoc did not enter frame capture mode after StartFrameCapture.");
    REXLOG_WARN(
        "RenderDoc frame capture was requested for Refii renderer, but "
        "RenderDoc did not enter frame capture mode.");
    return false;
  }

  REXLOG_INFO("RenderDoc frame capture started for Refii renderer.");
  SetRenderDocLastError({});
  g_renderdoc_refii_owned_capture_active.store(true,
                                               std::memory_order_release);
  return true;
#else
  (void)title;
  return false;
#endif
}

bool BeginRenderDocImmediateFrameCapture(const char* title) {
  return StartRenderDocFrameCaptureNow(title);
}

bool BeginRenderDocFrameCapture(const char* title, bool ready_to_capture) {
  if (!ready_to_capture || !RenderDocCaptureRequestPending()) {
    return false;
  }

  const bool capture_started = StartRenderDocFrameCaptureNow(title);
  if (capture_started) {
    ClearRenderDocCaptureRequest();
  }
  return capture_started;
}

void SetRenderDocFrameCaptureFileTag(const char* tag) {
#if REFII_RENDERER_HAS_RENDERDOC
  RENDERDOC_API_1_7_0* api = GetRenderDocApi();
  if (api == nullptr || tag == nullptr || tag[0] == '\0') {
    return;
  }

  std::filesystem::path capture_dir = ResolveRenderDocCaptureDirectory();
  if (capture_dir.empty()) {
    return;
  }

  std::error_code ec;
  std::filesystem::create_directories(capture_dir, ec);
  if (ec) {
    REXLOG_WARN("RenderDoc capture directory could not be created: {}",
                capture_dir.string());
    return;
  }

  const std::filesystem::path capture_template =
      capture_dir / SanitizeRenderDocCaptureTag(tag);
  const std::string capture_template_string = capture_template.string();
  if (capture_template_string == g_renderdoc_capture_path_template) {
    return;
  }

  api->SetCaptureFilePathTemplate(capture_template_string.c_str());
  g_renderdoc_capture_path_template = capture_template_string;
  g_renderdoc_capture_path_configured = true;
  REXLOG_INFO("RenderDoc capture template set for Refii renderer: {}",
              capture_template_string);
#else
  (void)tag;
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
    g_renderdoc_refii_owned_capture_active.store(false,
                                                 std::memory_order_release);
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
    g_renderdoc_known_capture_count = capture_count;
    g_renderdoc_known_capture_count_initialized = true;
    g_renderdoc_refii_owned_capture_active.store(false,
                                                 std::memory_order_release);
    if (RenderDocReplayLaunchEnabled()) {
      const uint32_t pid = api->LaunchReplayUI(1, nullptr);
      if (pid == 0) {
        REXLOG_WARN("RenderDoc replay UI launch request failed.");
      }
    }
  } else {
    g_renderdoc_refii_owned_capture_active.store(false,
                                                 std::memory_order_release);
    REXLOG_WARN("RenderDoc frame capture ended without producing a capture.");
  }
#else
  (void)capture_started;
#endif
}

#if REFII_RENDERER_HAS_RENDERDOC
// External capture request channel: writing <capture_dir>/capture_request
// asks refii to capture its next frame(s) — the file's first integer is an
// optional consecutive-frame count (default 1, capped 8). Unlike the F12
// hotkey or a screenshot, this needs no window focus and no injected input:
// the request reaches the process directly and always captures refii. The
// file is consumed (deleted) as the acknowledgement.
void PollRenderDocCaptureRequestFile(RENDERDOC_API_1_7_0* api) {
  if (api == nullptr) {
    return;
  }
  // ~2 Hz at 60 fps: one stat per 30 poll ticks keeps the frame loop clean.
  static uint32_t poll_counter = 0;
  if ((++poll_counter % 30u) != 0u) {
    return;
  }
  static std::filesystem::path request_path;
  if (request_path.empty()) {
    const std::string capture_directory = RenderDocCaptureDirectory();
    if (capture_directory.empty()) {
      return;
    }
    request_path =
        std::filesystem::path(capture_directory) / "capture_request";
  }
  std::error_code exists_error;
  if (!std::filesystem::exists(request_path, exists_error) || exists_error) {
    return;
  }
  uint32_t frame_count = 1;
  {
    std::ifstream request_file(request_path);
    uint32_t requested = 0;
    if (request_file >> requested && requested >= 1u) {
      frame_count = std::min<uint32_t>(requested, 8u);
    }
  }
  std::error_code remove_error;
  std::filesystem::remove(request_path, remove_error);
  if (remove_error) {
    // Could not consume the request; skip triggering rather than loop on it.
    REXLOG_WARN("RenderDoc capture request file could not be removed: {}",
                request_path.string());
    return;
  }
  if (frame_count > 1u) {
    api->TriggerMultiFrameCapture(frame_count);
  } else {
    api->TriggerCapture();
  }
  REXLOG_INFO("RenderDoc capture requested via request file: frames={}",
              frame_count);
}
#endif

void PollRenderDocCaptureRequest() {
  if (RenderDocDebuggerEnabled()) {
    RENDERDOC_API_1_7_0* api = GetRenderDocApi();
    PublishCompletedExternalRenderDocCaptures(api);
#if REFII_RENDERER_HAS_RENDERDOC
    PollRenderDocCaptureRequestFile(api);
#endif
  }
}

// Compose-trace channel: writing <capture_dir>/capture_render asks refii to
// wrap ONE full present-pump iteration (record + submit + present) in an
// immediate Start/EndFrameCapture pair. Unlike capture_request (frame-boundary
// TriggerCapture, which on the split present pump catches only the final
// present blit), this captures the complete direct compose/scene draw stream.
// The file is consumed (deleted) as the acknowledgement. This channel was
// lost in the module reorg and reinstated 2026-07-18; it is the primary
// visual-confirmation capture for compose defects.
bool ConsumeRenderDocComposeTraceRequest() {
#if REFII_RENDERER_HAS_RENDERDOC
  if (!RenderDocDebuggerEnabled() || GetRenderDocApi() == nullptr) {
    return false;
  }
  // One stat per 30 calls (~2 Hz at 60 fps), same cadence as capture_request.
  static uint32_t poll_counter = 0;
  if ((++poll_counter % 30u) != 0u) {
    return false;
  }
  static std::filesystem::path request_path;
  if (request_path.empty()) {
    const std::string capture_directory = RenderDocCaptureDirectory();
    if (capture_directory.empty()) {
      return false;
    }
    request_path =
        std::filesystem::path(capture_directory) / "capture_render";
  }
  std::error_code exists_error;
  if (!std::filesystem::exists(request_path, exists_error) || exists_error) {
    return false;
  }
  std::error_code remove_error;
  std::filesystem::remove(request_path, remove_error);
  if (remove_error) {
    REXLOG_WARN("RenderDoc compose-trace request file could not be removed: {}",
                request_path.string());
    return false;
  }
  REXLOG_INFO("Refii compose-trace capture armed via capture_render file");
  return true;
#else
  return false;
#endif
}

}  // namespace refii::renderer
