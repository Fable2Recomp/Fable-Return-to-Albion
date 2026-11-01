// refii — the hosted guest's Android start-up, one call each from RefiiHostInitialize
// (lifecycle.cpp). Nothing on the desktop: the guest's own SDL and platform start as they always did.

#pragma once

namespace refii::hostcore {

#if defined(__ANDROID__)
/// The optional Android services, which must exist before the runtime creates guest memory and threads.
void StartAndroidPlatform();

/// Readies SDL for the embedded guest. Godot owns the Android Activity and rendering, so the guest
/// needs only SDL's event queue, and SDL's separate Java host is never initialised.
void ReadyAndroidSdl();
#else
inline void StartAndroidPlatform() {}
inline void ReadyAndroidSdl() {}
#endif

}  // namespace refii::hostcore
