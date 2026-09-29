#pragma once

#include "../vfs_hooks_private.h"
#include <rex/ppc/context.h>

// Every translation unit that includes this header is compiled INTO the payload — the
// hook sources are listed in REFII_VFS_HOOK_INTERNAL_SOURCES, not in refii_vfs itself —
// so these are ordinary externs the linker resolves within the module.

#if !defined(PPC_EXTERN_FUNC)
#if defined(REX_EXTERN)
#define PPC_EXTERN_FUNC(symbol) REX_EXTERN(symbol)
#elif defined(REX_FUNC)
#define PPC_EXTERN_FUNC(symbol) extern "C" REX_FUNC(symbol)
#else
#define PPC_EXTERN_FUNC(symbol) extern "C" void symbol([[maybe_unused]] PPCContext& __restrict ctx, uint8_t* base)
#endif
#endif

PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_OpenInternal);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_FindAndCreateStream);
PPC_EXTERN_FUNC(__imp__sub_82B48CB8);
PPC_EXTERN_FUNC(__imp__sub_82B72430);
PPC_EXTERN_FUNC(__imp__sub_82B53438);
PPC_EXTERN_FUNC(__imp__sub_82B504D8);
PPC_EXTERN_FUNC(__imp__sub_82B5AD98);
PPC_EXTERN_FUNC(__imp__sub_82B5BFF8);
PPC_EXTERN_FUNC(__imp__sub_82B5C0C0);
PPC_EXTERN_FUNC(__imp__sub_82B5C0D8);
PPC_EXTERN_FUNC(__imp__sub_82B5C0E0);
PPC_EXTERN_FUNC(__imp__sub_82B5C110);
PPC_EXTERN_FUNC(__imp__sub_82B5C148);
PPC_EXTERN_FUNC(__imp__sub_82B5C160); // Lumen: LHVFS::CWin32Request::GetDevice(void)
PPC_EXTERN_FUNC(__imp__sub_82A20EE8);
PPC_EXTERN_FUNC(__imp__sub_82B70868);
PPC_EXTERN_FUNC(__imp__sub_82B6DB20);
PPC_EXTERN_FUNC(__imp__sub_82B6C340);
PPC_EXTERN_FUNC(__imp__sub_82176018); // Lumen: LHFile::CDataInputStream::SetPosition(uint)
PPC_EXTERN_FUNC(__imp__sub_82B8BA38);
PPC_EXTERN_FUNC(__imp__sub_82B8C158);
PPC_EXTERN_FUNC(__imp__sub_82B8C4A8);
PPC_EXTERN_FUNC(__imp__sub_82B8C5F0);
PPC_EXTERN_FUNC(__imp__sub_82B8CAC0);
PPC_EXTERN_FUNC(__imp__sub_82B8CDF8);
PPC_EXTERN_FUNC(__imp__sub_82B8CEE0);
PPC_EXTERN_FUNC(__imp__sub_82B8D028);
PPC_EXTERN_FUNC(__imp__sub_82B8D170);
PPC_EXTERN_FUNC(__imp__sub_82B8D280);
PPC_EXTERN_FUNC(__imp__sub_82B8D2D0);
PPC_EXTERN_FUNC(__imp__sub_82B8D348);
PPC_EXTERN_FUNC(__imp__sub_82B8D3D8);
PPC_EXTERN_FUNC(__imp__sub_82B8D468);
PPC_EXTERN_FUNC(__imp__sub_82B8D500);
PPC_EXTERN_FUNC(__imp__sub_82B8D718);
PPC_EXTERN_FUNC(__imp__sub_82B8D870);
PPC_EXTERN_FUNC(__imp__sub_82B8D8C8);
PPC_EXTERN_FUNC(__imp__sub_82B8DC78);
PPC_EXTERN_FUNC(__imp__sub_82B8DCE0);
PPC_EXTERN_FUNC(__imp__sub_82B8DDB0);
PPC_EXTERN_FUNC(__imp__sub_82B8DE40);
PPC_EXTERN_FUNC(__imp__sub_82B8DF50);
PPC_EXTERN_FUNC(__imp__sub_82B8DFA0);
PPC_EXTERN_FUNC(__imp__sub_82B96220);
PPC_EXTERN_FUNC(__imp__sub_8252AA18);
PPC_EXTERN_FUNC(__imp__sub_82B4E6D0);
PPC_EXTERN_FUNC(__imp__sub_82B4E8E0);
PPC_EXTERN_FUNC(__imp__sub_82B4EC30);
PPC_EXTERN_FUNC(__imp__sub_82B4ED70);
PPC_EXTERN_FUNC(__imp__sub_82B49028);
PPC_EXTERN_FUNC(__imp__sub_82B49220);
PPC_EXTERN_FUNC(__imp__sub_82B49D80);
PPC_EXTERN_FUNC(__imp__sub_8244B978);
PPC_EXTERN_FUNC(__imp__sub_8244BA28);
PPC_EXTERN_FUNC(__imp__sub_8244BAD8);
PPC_EXTERN_FUNC(__imp__sub_8244BED0);
PPC_EXTERN_FUNC(__imp__sub_82190728);
PPC_EXTERN_FUNC(__imp__sub_82C51CA0);
PPC_EXTERN_FUNC(__imp__sub_82C51EC0);
PPC_EXTERN_FUNC(__imp__sub_82B54D28);
PPC_EXTERN_FUNC(__imp__sub_82B54E58);
PPC_EXTERN_FUNC(__imp__sub_82B54250);
PPC_EXTERN_FUNC(__imp__sub_82B54268);
PPC_EXTERN_FUNC(__imp__sub_82B542D8);
PPC_EXTERN_FUNC(__imp__sub_82B542F0); // Lumen: LHVFS::CBasicBufferedRequest::GetDevice(void)
PPC_EXTERN_FUNC(__imp__sub_822C72E8);
PPC_EXTERN_FUNC(__imp__sub_822CBB98);
PPC_EXTERN_FUNC(__imp__sub_8290DD10);
PPC_EXTERN_FUNC(__imp__sub_83032078);
PPC_EXTERN_FUNC(__imp__sub_83032088);
PPC_EXTERN_FUNC(__imp__sub_82237338);
PPC_EXTERN_FUNC(__imp__sub_82B75328);
PPC_EXTERN_FUNC(__imp__sub_82B75D18);
PPC_EXTERN_FUNC(__imp__sub_82B86B40);
PPC_EXTERN_FUNC(__imp__sub_82C0EE10);
PPC_EXTERN_FUNC(__imp__sub_82B456D8);
PPC_EXTERN_FUNC(__imp__sub_82B45F70);
PPC_EXTERN_FUNC(__imp__sub_8218FA68);
PPC_EXTERN_FUNC(__imp__sub_82BA2830);
PPC_EXTERN_FUNC(__imp__sub_82C6E8F0);

// Render_DrawBinkMovieFrame — the game's own call into Bink, hooked to capture the BINK
// handle so the host can find the CPU-decoded video planes. Hooking inside Bink itself
// does not work; it dispatches through a function-pointer table. See
// hooks/bink_frame_capture.cpp.
// luaD_precall_2 — the GAME VM's copy, and THE single site through which every
// lua_CFunction in that VM is invoked (the indirect call at 0x822289F0). Hooked once to
// intercept the ~120 semantic GUI natives; see hooks/gui_natives.cpp.
//
// The title links TWO copies of the Lua library. luaD_precall (0x8219EF68) belongs to the
// GAMEFACE UI VM — hooking it fires tens of thousands of times and never sees a GUI
// native, because RegisterGUI registers into the game VM. The copies are not even
// layout-identical: the hook-mask byte is at L+56 here and L+54 there.
PPC_EXTERN_FUNC(__imp__sub_82228730);
// The display MessageBox's dtor, Open and Close (vtable 0x820AE668), stated to the host — see
// hooks/display_box_hooks.cpp.
PPC_EXTERN_FUNC(__imp__sub_823CEAE8);
PPC_EXTERN_FUNC(__imp__sub_823CEB58);
PPC_EXTERN_FUNC(__imp__sub_823CEC98);
// hooks/reticule_hooks.cpp.
PPC_EXTERN_FUNC(__imp__sub_8287C658);
PPC_EXTERN_FUNC(__imp__sub_8234EF58);
PPC_EXTERN_FUNC(__imp__sub_82C68F30);
PPC_EXTERN_FUNC(__imp__sub_8219A9A0);
PPC_EXTERN_FUNC(__imp__sub_82458C88);
PPC_EXTERN_FUNC(__imp__sub_821956A0);
PPC_EXTERN_FUNC(__imp__sub_824732A8);
PPC_EXTERN_FUNC(__imp__sub_82473C60);
PPC_EXTERN_FUNC(__imp__sub_8247A720);
PPC_EXTERN_FUNC(__imp__sub_82487790);
PPC_EXTERN_FUNC(__imp__sub_824926A8);
PPC_EXTERN_FUNC(__imp__sub_82882C88);
PPC_EXTERN_FUNC(__imp__sub_821E3108);
PPC_EXTERN_FUNC(__imp__sub_82459280);
PPC_EXTERN_FUNC(__imp__sub_823272E0);
PPC_EXTERN_FUNC(__imp__sub_823BEDC0);

// The registration side channel — runs between pushcclosure and setfield for EVERY Lua
// native, with the closure on the stack and the name in r5. Hooked to build impl -> name
// without an address table, because the shipped metadata's address column is the THUNK for
// some entries and thunks are shared between different natives.
PPC_EXTERN_FUNC(__imp__sub_823CE420);

// Value-returning GUI thunks, hooked so the host can answer a predicate in place of the guest:
//   sub_822AA418  no args -> BOOLEAN: top = *(L+8); top[1] = 1 (LUA_TBOOLEAN); top[0] = value;
//                 *(L+8) += 8; return 1
//   sub_82197FF0  no args -> FLOAT (lua_Number is float): *(float*)top = value;
//                 *(u32*)(top+4) = 3 (LUA_TNUMBER); return 1
// `sub_82309EE8` (one string -> boolean) is absent: its answer depends on the argument.
PPC_EXTERN_FUNC(__imp__sub_822AA418);
PPC_EXTERN_FUNC(__imp__sub_82197FF0);

// Expression-meter void thunks, hooked so a host-owned native can be suppressed. All six
// return zero Lua results, so the hook sets r3 = 0 and returns.
//   sub_82309D48  no arguments          TurnOn/TurnOffExpressionMeter
//   sub_822B7BB8  1 number              StartBarJobFill
//   sub_823C00C8  (int, int, int)       SetLow/Med/HighFillColour
//   sub_8230A760  (number, number)      SetFillColourPercentages
//   sub_8230A7F0  inline, 5 arguments   TurnOnExpressionStaticFill
//   sub_8230A8C8  inline, 4 arguments   TurnOnExpressionDynamicFill
PPC_EXTERN_FUNC(__imp__sub_82309D48);
PPC_EXTERN_FUNC(__imp__sub_822B7BB8);
PPC_EXTERN_FUNC(__imp__sub_823C00C8);
PPC_EXTERN_FUNC(__imp__sub_8230A760);
PPC_EXTERN_FUNC(__imp__sub_8230A7F0);
PPC_EXTERN_FUNC(__imp__sub_8230A8C8);

PPC_EXTERN_FUNC(__imp__sub_82A41B48);
PPC_EXTERN_FUNC(__imp__sub_82A41E30);
PPC_EXTERN_FUNC(__imp__sub_82A41D88);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_EndScene);
PPC_EXTERN_FUNC(__imp__sub_82A1AC40);
PPC_EXTERN_FUNC(__imp__sub_821C3C60);
PPC_EXTERN_FUNC(__imp__sub_82A54500);
PPC_EXTERN_FUNC(__imp__sub_821A61B0);
PPC_EXTERN_FUNC(__imp__sub_82191F50);
PPC_EXTERN_FUNC(__imp__sub_82A67188);
PPC_EXTERN_FUNC(__imp__sub_82C6EA48);
PPC_EXTERN_FUNC(__imp__sub_82C6EA80);
PPC_EXTERN_FUNC(__imp__sub_82C6EAD8);
PPC_EXTERN_FUNC(__imp__sub_8218DFB8);
PPC_EXTERN_FUNC(__imp__sub_82231CB8);
PPC_EXTERN_FUNC(sub_821F7800); // Lumen: LHString::CCharString::CCharString(void)
PPC_EXTERN_FUNC(__imp__sub_8286FDB0);
PPC_EXTERN_FUNC(__imp__sub_828D4110);
PPC_EXTERN_FUNC(__imp__sub_822C9388); // Lumen: PulseEvent
PPC_EXTERN_FUNC(__imp__sub_82382558); // subtitles state: self-gated getter incl. the first-boot non-en-uk default
PPC_EXTERN_FUNC(__imp__sub_82B4F400);
PPC_EXTERN_FUNC(__imp__sub_822DFE08); // the command-stream drain; headless it spins its whole budget
PPC_EXTERN_FUNC(__imp__sub_825738F0); // CECGraphicAppearance: states the highlight RGBA
PPC_EXTERN_FUNC(__imp__sub_82427FD0); // CECTargeted: states that an entity became a target
PPC_EXTERN_FUNC(__imp__sub_82573318); // CECGraphicAppearance: every appearance setter ends here
PPC_EXTERN_FUNC(__imp__sub_82572238); // CECGraphicAppearance: save/load of Alpha, TintColour, ...
PPC_EXTERN_FUNC(__imp__sub_82571DF8); // CECGraphicAppearance: constructor
PPC_EXTERN_FUNC(__imp__sub_82571FF0); // CECGraphicAppearance: destructor
PPC_EXTERN_FUNC(__imp__sub_8220DE88); // CECGraphicAppearanceMorph: the composite submit
PPC_EXTERN_FUNC(__imp__sub_8217FA50); // CECGraphicAppearanceMorph: rebuilds the part records
PPC_EXTERN_FUNC(__imp__sub_825B38B0); // texture composite command: one original, its layers
PPC_EXTERN_FUNC(__imp__sub_82A48B90); // morph descriptor constructor: a part and its weighted targets
PPC_EXTERN_FUNC(__imp__sub_8222AAC8); // render command to one model handle; type 14 = overlays
PPC_EXTERN_FUNC(__imp__sub_832325A8); // CParticleSystemEntityInstance: states the world position
PPC_EXTERN_FUNC(__imp__sub_83231C40); // CParticleSystemEntityInstance: states the orientation
PPC_EXTERN_FUNC(__imp__sub_825F81E0); // spell FX: create from <Spell>SpellParticleFXInfo.<slot>
PPC_EXTERN_FUNC(__imp__sub_82374728);
PPC_EXTERN_FUNC(__imp__sub_82377980);
PPC_EXTERN_FUNC(__imp__sub_82377E10);
PPC_EXTERN_FUNC(__imp__sub_82378020);
PPC_EXTERN_FUNC(__imp__sub_82378110);
PPC_EXTERN_FUNC(__imp__sub_82378118);
PPC_EXTERN_FUNC(__imp__sub_82214890);
PPC_EXTERN_FUNC(__imp__sub_82B96E78); // Lumen: nullsub_1
PPC_EXTERN_FUNC(__imp__sub_82C2FC78);
PPC_EXTERN_FUNC(__imp__sub_82EA63A8);
PPC_EXTERN_FUNC(__imp__sub_8231A908);
PPC_EXTERN_FUNC(__imp__sub_8231A748);
PPC_EXTERN_FUNC(__imp__sub_8231A368);
PPC_EXTERN_FUNC(__imp__sub_826D6108);
PPC_EXTERN_FUNC(__imp__sub_8231A9A8);
PPC_EXTERN_FUNC(__imp__sub_8231AA98);
PPC_EXTERN_FUNC(__imp__sub_8231AAA0);
PPC_EXTERN_FUNC(__imp__sub_8231AAA8);
PPC_EXTERN_FUNC(__imp__sub_8231AAD0);
PPC_EXTERN_FUNC(sub_82319928);
PPC_EXTERN_FUNC(__imp__sub_82372500);
PPC_EXTERN_FUNC(__imp__sub_82313268);
PPC_EXTERN_FUNC(__imp__sub_82313270);
PPC_EXTERN_FUNC(__imp__sub_82313278);
PPC_EXTERN_FUNC(__imp__sub_82377A00);
PPC_EXTERN_FUNC(__imp__sub_82377DC8);
PPC_EXTERN_FUNC(__imp__sub_823781E0);
PPC_EXTERN_FUNC(__imp__sub_82378618);
PPC_EXTERN_FUNC(__imp__sub_821DE2E8);
PPC_EXTERN_FUNC(__imp__sub_826D5E48);
PPC_EXTERN_FUNC(__imp__sub_826DB598);
PPC_EXTERN_FUNC(__imp__sub_82378F00);
PPC_EXTERN_FUNC(__imp__sub_82379D78);
PPC_EXTERN_FUNC(__imp__sub_82379088);
PPC_EXTERN_FUNC(__imp__sub_82379170);
// The guest's once-per-tick thunk, on its own "GameThread" — see guest_tick_pump.cpp.
PPC_EXTERN_FUNC(__imp__sub_822C1BD0);
// Menu spawn and list boundary: `sub_826CE8D0` is `g_Menu:CreateMenuItem`'s C++ side (name,
// parent, id, event type in r4/r7/r8/r9); `sub_826CE110` wraps `DeleteAllSpawnedMenuItems`.
PPC_EXTERN_FUNC(__imp__sub_826CE8D0);
PPC_EXTERN_FUNC(__imp__sub_826CE110);

// The pause-tab lever poller, run EVERY GUI TICK — the one heartbeat that beats while the
// pause book is open (`sub_822C1BD0`, the game-loop tick, stops there). The pause-screen
// samplers ride it: a sampler gated on "screen 23 is up" can never fire from a tick the
// pause stops.
PPC_EXTERN_FUNC(__imp__sub_8229ABD8);
// The front end's emitter: `sub_826E4798` is `sub_826CE8D0` against `dword_83496AC0` rather
// than the in-game GUI manager `dword_83496B08`.
PPC_EXTERN_FUNC(__imp__sub_826E4798);
// Six inline list boundaries (world map / abilities / shop / logbook / gift-gold / front end):
// they fire `"g_Menu:DeleteAllSpawnedMenuItems()"` through the GUI Lua exec, not `sub_826CE110`.
PPC_EXTERN_FUNC(__imp__sub_829A2F40);
PPC_EXTERN_FUNC(__imp__sub_829A45A0);
PPC_EXTERN_FUNC(__imp__sub_82994218);
PPC_EXTERN_FUNC(__imp__sub_829B8648);
PPC_EXTERN_FUNC(__imp__sub_829BA860);
PPC_EXTERN_FUNC(__imp__sub_82430468);
PPC_EXTERN_FUNC(__imp__sub_826E1F70);
PPC_EXTERN_FUNC(sub_823DCB10);
PPC_EXTERN_FUNC(sub_823DCC70);
PPC_EXTERN_FUNC(__imp__sub_82426470);
PPC_EXTERN_FUNC(__imp__sub_823889A8);
PPC_EXTERN_FUNC(__imp__sub_82388A18);
PPC_EXTERN_FUNC(__imp__sub_82388A88);
PPC_EXTERN_FUNC(__imp__sub_82388B48);
PPC_EXTERN_FUNC(__imp__sub_823865F8);
PPC_EXTERN_FUNC(__imp__sub_82388040);
PPC_EXTERN_FUNC(__imp__sub_82389040);
PPC_EXTERN_FUNC(__imp__sub_82389210);
PPC_EXTERN_FUNC(__imp__sub_82360300);
PPC_EXTERN_FUNC(__imp__sub_8235D5E8);
PPC_EXTERN_FUNC(__imp__sub_8235E1E0);
PPC_EXTERN_FUNC(__imp__sub_8235E5A0);
PPC_EXTERN_FUNC(__imp__sub_8235E420);
PPC_EXTERN_FUNC(__imp__sub_82360418);
PPC_EXTERN_FUNC(__imp__sub_82360618);
PPC_EXTERN_FUNC(__imp__sub_82360968);
PPC_EXTERN_FUNC(__imp__sub_82361AF8);
PPC_EXTERN_FUNC(__imp__sub_82180418);
PPC_EXTERN_FUNC(__imp__sub_82467B08);
PPC_EXTERN_FUNC(__imp__sub_822C6C68);
PPC_EXTERN_FUNC(sub_823C0D90);
PPC_EXTERN_FUNC(__imp__sub_823C1658);
PPC_EXTERN_FUNC(__imp__sub_8237AA98);
PPC_EXTERN_FUNC(__imp__sub_8237AC80);
PPC_EXTERN_FUNC(__imp__sub_822B88A8);
PPC_EXTERN_FUNC(sub_822D6408);
PPC_EXTERN_FUNC(__imp__sub_82361128);
PPC_EXTERN_FUNC(__imp__sub_82361200);
PPC_EXTERN_FUNC(sub_8222CED0);
PPC_EXTERN_FUNC(sub_82214DD8);
PPC_EXTERN_FUNC(sub_822651A0);
PPC_EXTERN_FUNC(sub_82275438);
PPC_EXTERN_FUNC(sub_823C1CB0);
PPC_EXTERN_FUNC(sub_82467E40);
PPC_EXTERN_FUNC(sub_824679F8);
PPC_EXTERN_FUNC(__imp__sub_821E2C08);
PPC_EXTERN_FUNC(__imp__sub_82A415C0);
PPC_EXTERN_FUNC(__imp__sub_82A41758);
PPC_EXTERN_FUNC(__imp__sub_82A41B00);
PPC_EXTERN_FUNC(sub_82A97368);
PPC_EXTERN_FUNC(sub_82426518);
PPC_EXTERN_FUNC(sub_82426CA8);
PPC_EXTERN_FUNC(sub_8222AB78);
PPC_EXTERN_FUNC(sub_8222ABE0);
PPC_EXTERN_FUNC(sub_82A8F6E8);
PPC_EXTERN_FUNC(__imp__sub_822F3B20);
PPC_EXTERN_FUNC(__imp__sub_82CC38E8); // Lumen: SwitchToThread
PPC_EXTERN_FUNC(__imp__rex_Sleep);
PPC_EXTERN_FUNC(sub_822F22B8);
PPC_EXTERN_FUNC(sub_822F2308);
PPC_EXTERN_FUNC(sub_82313400);
PPC_EXTERN_FUNC(sub_82374D78);
PPC_EXTERN_FUNC(sub_82374F70);
PPC_EXTERN_FUNC(__imp__RtlInitializeCriticalSection);
PPC_EXTERN_FUNC(__imp__rex_VirtualAlloc);
PPC_EXTERN_FUNC(__imp__rex_lh_malloc);
PPC_EXTERN_FUNC(rex_lh_free);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_RequestManager_FindStream);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_RequestManager_BuildHashTable);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Provider_FindStreamByHash);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Provider_OpenStream);
PPC_EXTERN_FUNC(__imp__rex_lhBank_OpenStreamByIndex);
PPC_EXTERN_FUNC(__imp__rex_lhBank_OpenStreamByName);
PPC_EXTERN_FUNC(__imp__rex_lhStream_Buffered_GetSize);
PPC_EXTERN_FUNC(__imp__rex_lhStream_Buffered_Init);
PPC_EXTERN_FUNC(__imp__rex_lhStream_Buffered_Read);
PPC_EXTERN_FUNC(__imp__rex_lhStream_CreateBuffered);
PPC_EXTERN_FUNC(__imp__rex_lhStream_CreateFromPosition);
PPC_EXTERN_FUNC(__imp__rex_lhStream_CreateReader);
PPC_EXTERN_FUNC(__imp__rex_lhStream_CreateReadStream);
PPC_EXTERN_FUNC(__imp__rex_lhStream_CreateSubStream);
PPC_EXTERN_FUNC(__imp__rex_lhStream_DiskStream_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhStream_DiskStream_GetSize);
PPC_EXTERN_FUNC(__imp__rex_lhStream_DiskStream_Read);
PPC_EXTERN_FUNC(__imp__rex_lhStream_DecompressStream_Read);
PPC_EXTERN_FUNC(__imp__rex_lhStream_GetPosition);
PPC_EXTERN_FUNC(__imp__rex_lhStream_GetInnerSize);
PPC_EXTERN_FUNC(__imp__rex_lhStream_ReadUInt32);
PPC_EXTERN_FUNC(__imp__rex_lhStream_Reader_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhStream_Reader_Read);
PPC_EXTERN_FUNC(__imp__rex_lhRefCounted_AddRef);
PPC_EXTERN_FUNC(__imp__rex_lhRefCounted_Release);
PPC_EXTERN_FUNC(__imp__rex_lhRefCounted_ReturnMinusOne);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_FindAndCreateStream);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_GetCached);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_Open);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_OpenInternal);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_vtbl_FindStream);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_vtbl_GetStreamSize);
PPC_EXTERN_FUNC(__imp__rex_lhResourceName_ComputeHash);
PPC_EXTERN_FUNC(__imp__rex_lhResourceName_ComputeHashFromName);
PPC_EXTERN_FUNC(__imp__rex_lhResource_InitModelHeaders);
PPC_EXTERN_FUNC(__imp__rex_lhResource_CreateModelCache);
PPC_EXTERN_FUNC(__imp__rex_lhResource_SetupModelCache);
PPC_EXTERN_FUNC(__imp__rex_lhResource_SetLoadMode);
PPC_EXTERN_FUNC(__imp__rex_lhResource_InitSystem);
PPC_EXTERN_FUNC(__imp__rex_lhResource_InitLoader);
PPC_EXTERN_FUNC(__imp__rex_lhMeshResource_Load);
PPC_EXTERN_FUNC(__imp__rex_lhMeshResource_Deserialize);
PPC_EXTERN_FUNC(__imp__rex_lhMeshResource_Validate);
PPC_EXTERN_FUNC(__imp__rex_lhScopedLock_Acquire);
PPC_EXTERN_FUNC(__imp__RtlEnterCriticalSection);
PPC_EXTERN_FUNC(__imp__RtlLeaveCriticalSection);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_Construct);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_Setup);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_BeginScene);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_StartThread);
PPC_EXTERN_FUNC(__imp__rex_lhGame_UpdateThread);
PPC_EXTERN_FUNC(__imp__rex_lhGameData_Init);
PPC_EXTERN_FUNC(__imp__rex_lhGameInit_SetupUI);
PPC_EXTERN_FUNC(__imp__rex_lhGameInit_SetupAudio);
PPC_EXTERN_FUNC(__imp__rex_lhGameInit_CheckUpdateMode);
PPC_EXTERN_FUNC(__imp__sub_822F3D90);
PPC_EXTERN_FUNC(__imp__sub_82451F18);
PPC_EXTERN_FUNC(__imp__sub_8237FE30);
PPC_EXTERN_FUNC(__imp__sub_82374980);
PPC_EXTERN_FUNC(__imp__sub_82BBA8D0);
PPC_EXTERN_FUNC(__imp__sub_8231A368);
PPC_EXTERN_FUNC(__imp__sub_8237FD60);
PPC_EXTERN_FUNC(__imp__sub_82A4E998);
PPC_EXTERN_FUNC(__imp__sub_822F4210);
PPC_EXTERN_FUNC(__imp__sub_822F4750);
PPC_EXTERN_FUNC(__imp__sub_822F4948);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_BeginGPUFrame);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_EndGPUFrame);
PPC_EXTERN_FUNC(__imp__rex_lhBank_Open);
PPC_EXTERN_FUNC(__imp__rex_lhBank_OpenWithManifest);
PPC_EXTERN_FUNC(__imp__rex_lhBank_ParseHeader);
PPC_EXTERN_FUNC(__imp__rex_lhBank_DirectoryIterator_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhBank_DirIter_FindStreamNext);
PPC_EXTERN_FUNC(__imp__rex_lhBank_DirIter_GetName);
PPC_EXTERN_FUNC(__imp__rex_lhBank_DirIter_FindStreamByIndex);
PPC_EXTERN_FUNC(__imp__rex_CreateFileA);
PPC_EXTERN_FUNC(__imp__rex_GetFileAttributesA);
PPC_EXTERN_FUNC(__imp__rex_FindNextFileA);
PPC_EXTERN_FUNC(__imp__rex_FindFirstFileA);
PPC_EXTERN_FUNC(__imp__rex_ReadFile);
PPC_EXTERN_FUNC(__imp__rex_ReadFileAsync);
PPC_EXTERN_FUNC(__imp__rex_WriteFile);
PPC_EXTERN_FUNC(__imp__rex_GetFileSize);
PPC_EXTERN_FUNC(__imp__rex_GetFileSizeEx);
PPC_EXTERN_FUNC(__imp__rex_GetFileInformation);
PPC_EXTERN_FUNC(__imp__rex_GetFileSectorInfo);
PPC_EXTERN_FUNC(__imp__rex_GetFileTime);
PPC_EXTERN_FUNC(__imp__rex_SetFilePointer);
PPC_EXTERN_FUNC(__imp__rex_SetEndOfFile);
PPC_EXTERN_FUNC(__imp__rex_FlushFileBuffers);
PPC_EXTERN_FUNC(__imp__rex_DeleteFileA);
PPC_EXTERN_FUNC(__imp__rex_CloseHandle);
PPC_EXTERN_FUNC(__imp__rex_lh_OpenDiscFile);
PPC_EXTERN_FUNC(__imp__rex_lhFile_CloseAndRelease);
PPC_EXTERN_FUNC(__imp__rex_lhFile_Close);
PPC_EXTERN_FUNC(__imp__rex_lhFile_Read);
PPC_EXTERN_FUNC(__imp__rex_lhFile_Write);
PPC_EXTERN_FUNC(__imp__rex_lhFile_SeekFromStart);
PPC_EXTERN_FUNC(__imp__rex_lhFile_SeekFromEnd);
PPC_EXTERN_FUNC(__imp__rex_lhFile_GetSize);
PPC_EXTERN_FUNC(__imp__rex_lhFile_GetTotalSize);
PPC_EXTERN_FUNC(__imp__rex_lhFile_IsOpen);
PPC_EXTERN_FUNC(__imp__rex_lhFile_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhFile_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhFile_ResolveAndOpen);
PPC_EXTERN_FUNC(__imp__sub_82C72E60);
PPC_EXTERN_FUNC(__imp__sub_82C73028);
PPC_EXTERN_FUNC(__imp__sub_82C8C718);
PPC_EXTERN_FUNC(rex_lhVFS_GetGlobalDataProvider);

// The guest's XAudio API shims — the audio seam Godot plays through.
// Hooked in src/hooks/xaudio_voices.cpp. Named by address rather than through
// refii_config.toml on purpose: a config name only takes effect after a full codegen, and
// it would rename the very symbols these hooks bind to, silently detaching the mirror.
PPC_EXTERN_FUNC(__imp__sub_82CD92B0);  // XAudioDestroyVoice
PPC_EXTERN_FUNC(__imp__sub_82CD93E0);  // XAudioSetChannelVolumes
PPC_EXTERN_FUNC(__imp__sub_82CD9490);  // XAudioSubmitSourceBuffer
PPC_EXTERN_FUNC(__imp__sub_82CD9548);  // XAudioStartVoice
PPC_EXTERN_FUNC(__imp__sub_82CD9590);  // XAudioStopVoice
PPC_EXTERN_FUNC(__imp__sub_82CD97B0);  // XAudioSetVolume
PPC_EXTERN_FUNC(__imp__sub_82CD9828);  // XAudioSetPitch
PPC_EXTERN_FUNC(__imp__sub_82CD9B00);  // XAudioCreateSourceVoice
