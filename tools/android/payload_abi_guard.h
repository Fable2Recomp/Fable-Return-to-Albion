#pragma once

// Generated PPC and hook TUs share the VFS proxy, not a compiler-specific native
// recursive_mutex. Keep this assertion in the forced include used by the PCH too.
#ifndef REFII_PAYLOAD_LOG_BRIDGE
#error "The Android payload requires the shared-runtime bridge"
#endif
#include <rex/thread/mutex.h>
#include <type_traits>
static_assert(std::is_same_v<rex::thread::global_recursive_mutex,
                            rex::thread::payload_global_recursive_mutex>,
              "Payload headers must select the shared-runtime mutex proxy");
static_assert(std::is_empty_v<rex::thread::global_recursive_mutex>,
              "The payload proxy must not own a second native mutex");
