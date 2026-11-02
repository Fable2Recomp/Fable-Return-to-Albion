// refii - Fable 3's render scenes: no object added to a scene the 3D Engine has already destroyed.
//
// THE SCENES. The render scenes live in the table *0x8355F454 (classic_feed.cpp in the host
// bridge), one slot per scene index; scene+1000 is the scene's typed object list.
// sub_82258F00(scene, object) adds an object to a scene, through that list (sub_821DBAE0 on
// scene+1000), and leaves at once when the object is null (`beq cr6,0x82258f8c` on r4).
//
// THE RACE. The 3D Engine's command stream destroys a scene by index (sub_822A21A8 ->
// sub_82C4F358), while the frame adds objects from the queue it drains itself (sub_82302EB8 looks
// the scene up by the queued index and calls sub_821F17B0, whose object's vtable slot 13,
// sub_821BC5C0, swaps its arguments and branches here). A new game destroys the front end's scene
// (index 127) while objects queued for it are still being added; the lookup reads the emptied slot
// and the add read scene+1004: a guest access violation at guest 0x3EC, on desktop and Android
// alike, after picking a hero, on about every other try. Under a debugger the timing changed
// and it never happened.
//
// The scene is gone with everything in it, so the object is not added: the same way out as the
// guest's own for a null object.

#include <atomic>
#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc.h>

REX_EXTERN(__imp__rex_Scene_AddObject);

REX_EXTERN(rex_Scene_AddObject) {
  const uint32_t scene = ctx.r3.u32;
  if (scene == 0u) {
    static std::atomic<uint32_t> skipped{0};
    const uint32_t count = skipped.fetch_add(1u, std::memory_order_relaxed) + 1u;
    if (count <= 4u) {
      REXLOG_WARN("Refii scenes: object 0x{:08X} added to a scene already destroyed; skipped ({} so far)",
                  ctx.r4.u32, count);
    }
    return;
  }

  __imp__rex_Scene_AddObject(ctx, base);
}
