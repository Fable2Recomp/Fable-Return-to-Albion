#pragma once

// Whether the host can take its own native backtrace (execinfo's backtrace()), stated once for
// the crash record and the hang watchdog. Bionic's execinfo API starts at API33, and Android
// resolves strong imports eagerly, before any of this runs: an API31 build that referenced it
// would not load. That build keeps fault PCs, stack pointers and the guest's frames instead.

#if !defined(_WIN32) && (!defined(__ANDROID__) || __ANDROID_API__ >= 33)
#define REFII_HOST_BACKTRACE 1
#include <execinfo.h>
#else
#define REFII_HOST_BACKTRACE 0
#endif
