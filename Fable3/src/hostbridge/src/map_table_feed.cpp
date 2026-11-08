// refii - Fable 3's map tables, as its hooks state them (Fable3/src/hooks/src/map_table_hooks.cpp,
// the table in src/runtime/src/core/stated_scene_objects.cpp): the holder whose scene the guest
// presents in place of the world (sub_82984588), the camera it presents it through (the holder's
// camera message, re-sent through sub_829846D8 each frame), and the mesh render objects added to
// it (sub_82984678) as the map entities' commands (sub_821F4630) move them.
//
// Nothing here reads guest memory: every word is one a hook took from a call's arguments.

#include <cstdint>

#include "core/payload_bridge.h"
#include "host_abi.h"

namespace {

/// Header words before the rows.
constexpr uint32_t kHeaderWords = 4u + REFII_SCENE_CAMERA_WORDS;
constexpr uint32_t kRowWords = 1u + REFII_SCENE_OBJECT_WORDS;

}  // namespace

extern "C" {

/// The presented map table, in words (floats as bits):
///   [0] the presented holder (0: none — the world is presented; nothing else is filled)
///   [1] 1 once the holder's camera has been stated
///   [2..11] the camera: position x y z, orientation quaternion x y z w (D3D; row 1 forward,
///           row 2 up), vertical fov in radians, near, far — guest world space, Z up
///   [12] serial, moving whenever the holder's object set changed
///   [13] rows that follow
///   then per mesh 18 words: [0] render object id, [1] model resource id (FNV-1 of the
///   backslashed path, the props index's key), [2] flags (0x800 displacement, 0x2000 ignores
///   viewport culling), [3] record id, [4] message class, [5..8] orientation quaternion x y z w,
///   [9..11] position x y z, [12] uniform scale, [13..16] tint r g b a, [17] alpha.
/// Returns the words written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFable3MapTable(uint32_t* out, uint32_t capacity) {
    if (out == nullptr || capacity < kHeaderWords) {
        return 0u;
    }
    for (uint32_t i = 0u; i < kHeaderWords; ++i) {
        out[i] = 0u;
    }

    int32_t camera_stated = 0;
    const uint32_t holder = refii_bridge_scene_presented_read(out + 2, &camera_stated);
    out[0] = holder;
    out[1] = camera_stated != 0 ? 1u : 0u;
    if (holder == 0u) {
        return kHeaderWords;
    }

    uint32_t serial = 0u;
    const uint32_t rows = refii_bridge_scene_objects_read(holder, out + kHeaderWords, capacity - kHeaderWords, &serial);
    out[12] = serial;
    out[13] = rows;
    return kHeaderWords + rows * kRowWords;
}

}  // extern "C"
