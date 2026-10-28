// refii - the recompiled guest payload
//
// The payload is built from the PLAYER'S OWN game image on their machine, so it cannot
// exist on a build machine and NOTHING WE SHIP MAY LINK AGAINST IT. It is loaded by name
// at startup and every symbol reaching into it resolves through this header.
//
// Two kinds of symbol cross the boundary:
//
//   * the image descriptor, reached through the one exported entry point below. The
//     generated `PPCImageConfig` is a DATA symbol, and an imported data symbol needs
//     __declspec(dllimport) on Windows to address correctly; an accessor function does
//     not, so the accessor is the ABI on both platforms.
//
//   * individual guest functions, resolved BY NAME. Not by guest address: `sub_X` is a
//     weak alias a hook may override while `__imp__sub_X` is always the original, and
//     both map to the same address. Call sites pick one deliberately, so an address
//     lookup would silently route a deliberate bypass back through the hook.

#pragma once

#include <rex/image_info.h>
#include <rex/ppc/context.h>

#include <string>

// Versioned by NAME. A payload built by an older installer exports an older entry point
// and fails to resolve, rather than binding to a descriptor whose layout has moved.
#define REFII_PAYLOAD_ENTRY_SYMBOL "refii_payload_v1_image_config"

extern "C" {
using RefiiPayloadImageConfigFn = const rex::PPCImageInfo* (*)();
}

namespace refii::payload {

/// Selects an absolute privately imported module path before the first Load().
/// Android assets and writable modules live outside the APK's native library directory.
bool SetModulePath(const std::string& path, std::string& error);

/// Loads the payload module beside the calling library. Idempotent; the first call
/// decides. False leaves a reason in LoadError().
bool Load(std::string& error);

bool IsLoaded() noexcept;

/// Reason the last Load() failed, or "" when it has not.
const std::string& LoadError() noexcept;

/// The loaded module's path, or "" before a successful Load().
const std::string& ModulePath() noexcept;

/// Image descriptor from the payload. Load() must have succeeded.
const rex::PPCImageInfo& ImageConfig();

/// The payload's guest address -> host function table, NULL-terminated as the generator
/// emits it. Load() must have succeeded.
const PPCFuncMapping* FuncMappings();

/// Resolves one guest function by its exported symbol name, or nullptr.
PPCFunc* ResolveGuestSymbol(const char* symbol);

/// Calls a symbol resolved by ResolveGuestSymbol. An unresolved symbol is fatal and named
/// in the message: it means the payload was generated from a different image than the one
/// this build expects, which is not survivable and must not read as a guest-side bug.
void CallGuest(PPCFunc* function, const char* symbol, PPCContext& ctx, uint8_t* base);

}  // namespace refii::payload
