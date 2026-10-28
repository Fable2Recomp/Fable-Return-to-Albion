/** @file core/main_android.cpp
 * Android optional-service lifetime for an in-process native library host.
 */
#include <rex/main_android.h>
#include <rex/memory/utils.h>
#include <rex/thread.h>

#if REX_PLATFORM_ANDROID
namespace rex {
void InitializeAndroidPlatform() {
  memory::AndroidInitialize();
  thread::AndroidInitialize();
}
void ShutdownAndroidPlatform() {
  thread::AndroidShutdown();
  memory::AndroidShutdown();
}
}  // namespace rex
#endif
