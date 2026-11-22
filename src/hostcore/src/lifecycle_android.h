// refii — the hosted guest's Android start-up, called from RefiiHostInitialize (lifecycle.cpp).
// Nothing on the desktop.

#pragma once

namespace refii::hostcore {

#if defined(__ANDROID__)
/// The optional Android services, which must exist before the runtime creates guest memory and threads.
void StartAndroidPlatform();
#else
inline void StartAndroidPlatform() {}
#endif

}  // namespace refii::hostcore
