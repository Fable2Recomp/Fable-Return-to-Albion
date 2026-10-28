/**
 * @file rex/main_android.h
 * @brief Android platform information for the native library host.
 */
#pragma once

#include <rex/platform.h>

#if REX_PLATFORM_ANDROID
#include <android/api-level.h>

namespace rex {
/** Returns the running OS API level rather than the build-time minimum API.
 * The Android port targets API 31+; this Bionic function is available since 29.
 * A failed query returns -1, preserving the NDK contract and disabling newer
 * optional API branches rather than inventing a version.
 */
inline int GetAndroidApiLevel() noexcept { return android_get_device_api_level(); }

/** Initialize optional Android memory and thread services before guest boot.
 * Call only while the native runtime is quiescent. Repeated calls are safe.
 */
void InitializeAndroidPlatform();

/** Release Android optional-service handles after all guest workers have exited.
 * The initial APK milestone uses process restart instead of live guest teardown.
 */
void ShutdownAndroidPlatform();
}  // namespace rex
#endif
