// THE TRACY LIBRARY IS OPTIONAL ON WINDOWS. Every module that calls into it delay-loads it
// (/DELAYLOAD, set up in the root CMakeLists.txt), and this failure hook decides what happens when
// the file is not there — an antivirus quarantine is the case that matters: instead of the module
// failing to load, and the game with it, each Tracy function resolves to one that returns zero.
//
// Zero is Tracy switched off: the profiler reports not started, so every zone, plot, lock and
// capture call in the remake falls through its own "started" check before it reaches anything
// else; captures report that none started; the dev menu says Tracy is off. Returning zero is safe
// whatever the signature on x64 and ARM64 Windows: the caller cleans up its own arguments, and
// every Tracy entry this code reaches before that check returns a scalar or a small struct in a
// register.
//
// One definition per module (the hook is a per-module variable of the delay-load helper), so this
// file is compiled into each DLL that imports Tracy and into nothing else.

#if defined(_WIN32)

#include <windows.h>

#include <delayimp.h>

#include <cstdint>
#include <cstring>

namespace {

uintptr_t RefiiTracyAbsent() { return 0; }

bool IsTracyClient(const char* dll) {
    return dll != nullptr && _strnicmp(dll, "TracyClient", 11) == 0;
}

FARPROC WINAPI TracyAbsentHook(unsigned notify, PDelayLoadInfo info) {
    if (info == nullptr || !IsTracyClient(info->szDll)) return nullptr;
    switch (notify) {
        // Stand in a module that exports none of Tracy's names, so every lookup fails into the
        // case below rather than the helper raising an exception.
        case dliFailLoadLib:
            return reinterpret_cast<FARPROC>(GetModuleHandleW(nullptr));
        case dliFailGetProc:
            return reinterpret_cast<FARPROC>(&RefiiTracyAbsent);
        default:
            return nullptr;
    }
}

}  // namespace

// Declared const by the MSVC delayimp.h, mutable by mingw's; the definition matches the header.
#if defined(_MSC_VER)
extern "C" const PfnDliHook __pfnDliFailureHook2 = TracyAbsentHook;
#else
extern "C" PfnDliHook __pfnDliFailureHook2 = TracyAbsentHook;
#endif

#endif
