// The seams between the converter core and the browser application.
//
// Each of these is owned by the app's UI or level layer, which the Godot migration
// replaces. They are defined inertly here so the core links headlessly; when the UI
// moves to Godot these become library inputs (callbacks / injected services) rather
// than stubs.
//
// Everything else the converter needs is REAL code compiled into the target — the
// texture parser, the PNG/JPG/DDS/TIFF writers, the animation system, ISO mounting.
// Do not add a stub here without first checking the symbol is not simply a source
// that belongs in CMakeLists.txt.

#include <string>
#include <vector>

#include "UI/OutputLog.h"

/// Owned by the level/UI layer; the converter only consults it to locate texture
/// bodies, and refii resolves textures in C# instead.
std::vector<std::string> g_level_vfs_texture_body_bnks;

/// Defined in textures/export/TextureExport.cpp, which is an ImGui menu. The GLB
/// carries texture NAMES and refii decodes .tex itself, so embedding is not wanted.
bool decode_tex_to_rgba(const std::vector<unsigned char>&, std::vector<unsigned char>&,
                        int&, int&, bool*, int) { return false; }

/// UI log panel (54 ImGui references). Silent headlessly.
namespace OutputLog { void log(Level, std::string) {} }

/// Referenced by AnimBank; defined only in the app's Lua panel.
std::string read_lua_file_content(const std::string&) { return {}; }

/// The browser's global application state, declared `extern State S;` in State.h.
///
/// This is the real coupling between the converter and the app — accessed as `S.field`
/// throughout, which is why searching for `State::` or `g_State` finds nothing. A
/// default-constructed instance is enough headlessly; the converter reads settings off
/// it but does not need the app's live values. Under the Godot migration this becomes
/// an explicit options struct passed in, not a global.
#include "Utilities/State.h"
State S;
