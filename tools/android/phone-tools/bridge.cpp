// librefii_phone_bridge.so: tells the launcher where the APK's native libraries were extracted,
// which is where the phone compiler's tools are (AndroidPhoneLauncher.refii_phone_native_directory).
#include <dlfcn.h>

#include <string>

extern "C" __attribute__((visibility("default"))) const char* refii_phone_native_directory() {
  static const std::string directory = [] {
    Dl_info info{};
    if (dladdr(reinterpret_cast<void*>(&refii_phone_native_directory), &info) == 0 ||
        info.dli_fname == nullptr) {
      return std::string();
    }
    std::string path = info.dli_fname;
    size_t slash = path.rfind('/');
    return slash == std::string::npos ? std::string() : path.substr(0, slash);
  }();
  return directory.c_str();
}
