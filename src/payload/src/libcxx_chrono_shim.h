// refii — the primary template libc++ does not have.
//
// COMPILED INTO THE PAYLOAD, force-included ahead of everything else. rex/chrono/chrono.h and
// rex/chrono/chrono_steady_cast.h define four explicit specializations of
// std::chrono::clock_time_conversion, and an explicit specialization is only legal if the
// PRIMARY template has been declared. libstdc++ declares it; libc++ implements neither it nor
// clock_cast, both C++20 [time.clock.cast]. So the SDK headers compile on one standard library
// and not the other:
//
//   rex/chrono/chrono.h:120:8: error: explicit specialization of undeclared template
//                              struct 'clock_time_conversion'
//
// WHICH IS WHY THIS IS A WINDOWS PROBLEM AND NOT A LINUX ONE. The payload is compiled by the
// bundled zig on both, but on Linux refii_stage_stdcxx.cmake stages libstdc++'s headers and
// the build uses those; that staging returns early on WIN32 (there is no libstdc++ to stage —
// zig ships libc++ only), so the Windows payload compiles against libc++ and hits this.
//
// NOTHING CALLS clock_cast. The only call in the SDK is threading_posix.cpp, which Windows
// does not build; every other mention in the headers is a comment. So the specializations only
// have to be well-formed, not reachable, and an empty primary is enough to make them so.
// Defining one implies clock_cast<> would silently do nothing if it were ever called — it
// cannot be, because libc++ has no clock_cast to call.
//
// SELF-GUARDING, so it is force-included on every platform and does nothing on libstdc++.

#pragma once

// <version> FIRST. _LIBCPP_VERSION is defined by the library's own headers, so testing for it
// before including one always reads as "not libc++" and the guard silently never fires.
#include <version>

#if defined(_LIBCPP_VERSION)

#include <chrono>

namespace std::chrono {
template <class Dest, class Source>
struct clock_time_conversion {};
}  // namespace std::chrono

#endif  // _LIBCPP_VERSION
