// The Worker needs Tracy's stack-frame tables. The client's unity build compiles them only on its
// libbacktrace callstack path (TracyCallstack.cpp), so this supplies them on every other — Windows,
// and any target whose callstack mode is not libbacktrace — by the client's own condition, never a
// list of platforms.

#include "client/TracyCallstack.h"

#ifndef TRACY_USE_LIBBACKTRACE
#  include "common/TracyStackFrames.cpp"
#endif
