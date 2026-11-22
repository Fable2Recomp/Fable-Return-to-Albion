// refii — the hosted guest's Android start-up (lifecycle_android.h). Built on Android only.

#include "lifecycle_android.h"

#include <rex/main_android.h>

#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility push(hidden)
#endif
namespace refii::hostcore {

void StartAndroidPlatform() {
    rex::InitializeAndroidPlatform();
}

}  // namespace refii::hostcore
#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility pop
#endif
