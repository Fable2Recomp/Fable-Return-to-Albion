#include "vfs_hooks_private.h"
#include "gui/gui_menu_chunks.h"

REX_EXTERN(rex_lhResourceManager_Open) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhResourceManager_Open);


  const PPCContext original_ctx = ctx;
  refii::vfs::RecordFrontendTextureConsumerResourceBoundary(
      "ResourceManager_Open", original_ctx.r3.u32, 0);
  if (refii::vfs::kDirectResourceManagerOpenOverride) {
    const uint32_t resource_hash = original_ctx.r3.u32;
    const uint32_t out_guest = original_ctx.r4.u32;
    PPCContext boot_ctx = original_ctx;
    refii::vfs::EnsureRefiiBootBankTemplates(
        boot_ctx, base, "ResourceManager_Open");
    refii::vfs::RefiiVfsOpenResult open_result;
    const refii::vfs::RefiiVfsOpenDisposition open_disposition =
        refii::vfs::TryOpenRefiiVfsAsset(
            ctx,
            base,
            original_ctx,
            refii::vfs::RefiiVfsOpenShape::ResourceWrapper,
            refii::vfs::RefiiVfsOpenRequest{
                .api = "ResourceManager_Open",
                .resource_hash = resource_hash,
                .out_guest = out_guest,
                .manager_guest = original_ctx.r5.u32,
                .mode = original_ctx.r7.u32 == 0 ? 1 : original_ctx.r7.u32,
                .resource_flags = original_ctx.r8.u32,
                .resource_offset = original_ctx.r5.u32,
                .resource_size = original_ctx.r6.u32,
                .caller_lr = static_cast<uint32_t>(original_ctx.lr),
                .prefer_resource_manager_texture_shape = true,
            },
            &open_result);
    if (open_disposition == refii::vfs::RefiiVfsOpenDisposition::Opened) {
      ctx.r3.u32 = 1;
      refii::vfs::LogRefiiOwnedTextureStreamBuilt(
          base,
          "ResourceManager_Open",
          static_cast<uint32_t>(original_ctx.lr),
          resource_hash,
          open_result.candidate,
          open_result.resolution,
          open_result.candidate,
          open_result.source,
          out_guest,
          open_result.opened_guest,
          true);
      return;
    }
    if (refii::vfs::IsHandledRefiiVfsOpenDisposition(open_disposition)) {
      return;
    }
    if (open_result.ownership.kind !=
        refii::vfs::RefiiVfsOwnershipKind::RefiiOwned) {
      refii::vfs::LooseStats().refii_native_fallbacks.fetch_add(
          1, std::memory_order_relaxed);
    }
  }

  if (refii::vfs::ShouldLogVfsTraceOperation("ResourceManager_Open")) {
    REXLOG_INFO("VFS trace ResourceManager_Open enter lr=0x{:08X} "
                "ctr=0x{:08X} r3=0x{:08X}({}) r4=0x{:08X}({}) "
                "r5=0x{:08X}({}) r6=0x{:08X}({}) r7=0x{:08X} r8=0x{:08X}",
                original_ctx.lr,
                original_ctx.ctr.u32,
                original_ctx.r3.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r3.u32),
                original_ctx.r4.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r4.u32),
                original_ctx.r5.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r5.u32),
                original_ctx.r6.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r6.u32),
                original_ctx.r7.u32,
                original_ctx.r8.u32);
  }

  __imp__rex_lhResourceManager_Open(ctx, base);

  // Log the returned object with its vtable: complete proxy 0x82002A04, buffered wrapper
  // 0x820F9DA8, else a fail-path object.
  {
    static std::atomic<uint32_t> logged{0};
    if (logged.fetch_add(1, std::memory_order_relaxed) < 8u) {
      const uint32_t out_object =
          original_ctx.r4.u32 != 0
              ? refii::vfs::LoadGuestU32(base, original_ctx.r4.u32)
              : 0u;
      const uint32_t vtable =
          out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object) : 0u;
      const uint32_t inner =
          out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 12u) : 0u;
      REXLOG_INFO("Refii Open RETURNED: result={} out=0x{:08X} object=0x{:08X} "
                  "vtable=0x{:08X} words=[{:08X} {:08X} {:08X} {:08X} {:08X} {:08X}] "
                  "inner=0x{:08X} inner_vtbl=0x{:08X} inner[+124]=0x{:08X} "
                  "inner[+128]={} inner[+200]={}",
                  ctx.r3.u32, original_ctx.r4.u32, out_object, vtable,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 4u) : 0u,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 8u) : 0u,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 12u) : 0u,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 16u) : 0u,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 20u) : 0u,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 24u) : 0u,
                  inner,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner) : 0u,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner + 124u) : 0u,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner + 128u) : 0u,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner + 200u) : 0u);
    }
  }

  if (refii::vfs::ShouldLogVfsTraceOperation("ResourceManager_Open")) {
    REXLOG_INFO("VFS trace ResourceManager_Open exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

// Open → OpenInternal → FindAndCreateStream → Submit each have silent fail paths ending in a
// NULL manifest; these hooks log each stage's verdict.

REX_EXTERN(sub_82B48CB8) {
  REX_FUNC_PROLOGUE();
  const uint32_t mgr = ctx.r3.u32;
  const uint32_t item = ctx.r4.u32;
  const uint32_t shutdown =
      mgr != 0 ? (refii::vfs::LoadGuestU32(base, mgr + 152u) >> 24) : 0xFFu;
  __imp__sub_82B48CB8(ctx, base);
  static std::atomic<uint32_t> logged{0};
  if (logged.fetch_add(1, std::memory_order_relaxed) < 8u) {
    REXLOG_INFO("Refii RequestManager Submit: mgr=0x{:08X} shutdown_byte={} item=0x{:08X} "
                "item_vtbl=0x{:08X} -> result=0x{:08X}",
                mgr, shutdown, item,
                item != 0 ? refii::vfs::LoadGuestU32(base, item) : 0u,
                ctx.r3.u32);
  }
}

REX_EXTERN(sub_82B6DB20) {
  REX_FUNC_PROLOGUE();

  // Replicates REFII_VFS_NATIVE_PASSTHROUGH but keeps the outcome log, which the macro's early
  // return would skip.
  {
    const uint32_t native_item = ctx.r3.u32;
    __imp__sub_82B6DB20(ctx, base);
    const uint32_t success_byte =
        refii::vfs::LoadGuestU32(base, native_item + 116u) >> 24;
    static std::atomic<uint32_t> logged_native_pool_execs{0};
    const uint32_t exec_count =
        logged_native_pool_execs.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (exec_count <= 32u) {
      REXLOG_INFO(
          "Refii pool stream-in exec #{} item=0x{:08X} success={} "
          "hash=0x{:08X} off8=0x{:08X} sz12=0x{:08X} sz16=0x{:08X} "
          "off20=0x{:08X} flags28=0x{:08X} mode104={} sel112={} "
          "off48=0x{:08X}",
          exec_count, native_item, success_byte,
          refii::vfs::LoadGuestU32(base, native_item + 4u),
          refii::vfs::LoadGuestU32(base, native_item + 8u),
          refii::vfs::LoadGuestU32(base, native_item + 12u),
          refii::vfs::LoadGuestU32(base, native_item + 16u),
          refii::vfs::LoadGuestU32(base, native_item + 20u),
          refii::vfs::LoadGuestU32(base, native_item + 28u),
          refii::vfs::LoadGuestU32(base, native_item + 104u),
          refii::vfs::LoadGuestU32(base, native_item + 112u),
          refii::vfs::LoadGuestU32(base, native_item + 48u));
    }
    return;
  }
}

REX_EXTERN(rex_lhResourceManager_OpenInternal) {
  REX_FUNC_PROLOGUE();

  // Passthrough with the verdict stated — see the chain note above FindAndCreateStream.
  {
    const PPCContext entry_ctx = ctx;
    __imp__rex_lhResourceManager_OpenInternal(ctx, base);
    static std::atomic<uint32_t> chain_logged{0};
    if (chain_logged.fetch_add(1, std::memory_order_relaxed) < 8u) {
      const uint32_t out_object =
          entry_ctx.r6.u32 != 0 ? refii::vfs::LoadGuestU32(base, entry_ctx.r6.u32) : 0u;
      // The proxy's inner is what every slot forwards to.
      const uint32_t inner =
          out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 12u) : 0u;
      REXLOG_INFO("Refii OpenInternal: manifest=0x{:08X} size_arg=0x{:08X} out=0x{:08X} "
                  "result={} object=0x{:08X} vtable=0x{:08X} words=[{:08X} {:08X} {:08X}] "
                  "inner=0x{:08X} inner_vtbl=0x{:08X} inner[+24]={} inner[+124]=0x{:08X} "
                  "inner[+128]={} inner[+200]={}",
                  entry_ctx.r3.u32, entry_ctx.r5.u32, entry_ctx.r6.u32,
                  ctx.r3.u32, out_object,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object) : 0u,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 4u) : 0u,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 8u) : 0u,
                  inner,
                  inner,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner) : 0u,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner + 24u) : 0u,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner + 124u) : 0u,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner + 128u) : 0u,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner + 200u) : 0u);
    }
    return;
  }


  const PPCContext original_ctx = ctx;
  if (refii::vfs::ShouldLogVfsTraceOperation(
          "ResourceManager_OpenInternal")) {
    REXLOG_INFO("VFS trace ResourceManager_OpenInternal enter lr=0x{:08X} "
                "manager={} r4=0x{:08X} r5=0x{:08X} out=0x{:08X} "
                "r7=0x{:08X}({}) r8=0x{:08X} manager_ascii_32={}",
                original_ctx.lr,
                refii::vfs::DescribeGuestObject(
                    base, original_ctx.r3.u32, 16, 12),
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                original_ctx.r6.u32,
                original_ctx.r7.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r7.u32),
                original_ctx.r8.u32,
                refii::vfs::FormatGuestAsciiBytes(
                    base, original_ctx.r3.u32 + 32, 160));
  }

  __imp__rex_lhResourceManager_OpenInternal(ctx, base);

  if (refii::vfs::ShouldLogVfsTraceOperation(
          "ResourceManager_OpenInternal")) {
    const uint32_t out_stream =
        refii::vfs::TryReadOutputPointer(base, original_ctx.r6.u32);
    REXLOG_INFO("VFS trace ResourceManager_OpenInternal exit lr=0x{:08X} "
                "result=0x{:08X} out=0x{:08X} out_stream={}",
                original_ctx.lr,
                ctx.r3.u32,
                original_ctx.r6.u32,
                refii::vfs::DescribeGuestObject(base, out_stream, 12, 12));
  }
}

REX_EXTERN(sub_82426470) {
  REX_FUNC_PROLOGUE();

  // THE CAPTURE RUNS BEFORE THE PASSTHROUGH. It used to sit after it, and
  // `REFII_VFS_NATIVE_PASSTHROUGH` runs the guest's function and RETURNS — so the whole block
  // below was dead code and not one menu statement was ever recorded. A 6,777-line run that
  // opened the logbook, options, equipment, world map and abilities produced zero, which is
  // indistinguishable from "the guest states no menus": every screen silently fell back to
  // reading its rows out of the button pool's geometry.
  //
  // WHAT THIS FUNCTION IS. Not `dostring` — a DISPATCHER. `lbz r10, 0x64(r11)` at 0x82426498
  // on the GUI manager (`dword_83496B08`) decides: non-zero runs the chunk now through
  // `sub_82372500`, zero QUEUES it into the `std::map<presentation, list<LHString>>` at
  // `g_Gui+0x58`, flushed later by `sub_821738A0`. The manager's constructor sets +0x64 = 1
  // (0x8237119C) and the first `Update` clears it (0x8228716C) and never sets it back, so in
  // practice every chunk after the first tick is deferred.
  //
  // Capturing on ENTRY is deliberate: it sees every chunk in SUBMISSION order, which is the
  // order the statements were made, and it pairs with the attribute stagers in
  // gui_menu_hooks.cpp — `sub_826CE8D0` writes String1..Float4 and only then submits
  // "g_Menu:SpawnButton()" (0x826CEB3C). Do not expect the guest's Lua side effects to exist
  // yet at this point; they happen at the flush.
  {
    // r4 == 0 is a chunk the guest DISCARDS outright (`beq cr6` at 0x82426488), and it is the
    // key the model files the statement under. Recording it would invent a menu at address 0.
    if (ctx.r4.u32 != 0) {
      // r3 is an LHString handle — `char***`, reached with exactly two dereferences, the way
      // `sub_82372500` does it (`lwz r11, 0(r30)` / `lwz r6, 0(r11)`). The best-effort reader
      // used before probes one to three levels and returns pointer bytes whenever they happen
      // to be printable, so it could not read these reliably. The permissive reader stays as a
      // fallback for a caller shape we have not enumerated; the `g_Menu` test filters it.
      std::string chunk = refii::vfs::ReadGuestLhString(base, ctx.r3.u32);
      if (chunk.empty()) {
        chunk = refii::vfs::ReadMaybeGuestString(base, ctx.r3.u32, 512);
      }

      if (chunk.find("g_Menu") != std::string::npos) {
        refii::vfs::RunMenuChunk(chunk, ctx.r4.u32);

        // The list boundary. `sub_826CE110` is the class-level "clear the menu" helper and only
        // the pause and inventory paths reach it; the logbook submits
        // `g_Menu:RepopulateMenuFromPopulationScript('...')` inline (0x826D8E34), so the
        // boundary has to be taken from the chunk text here.
        if (chunk.find("g_Menu:DeleteAllSpawnedMenuItems") != std::string::npos) {
          // KEYED BY THE SCREEN THE GUEST STATES, like the other boundaries.
          //
          // This one published the population as belonging to NO screen, and it is the boundary
          // most of the in-game lists actually come through — the weapons list was captured
          // with its rows correct (`Melee (3)<exclamation.img>`, `Ranged (2)`) while screen 15
          // was up, and reported as screen 0xFFFFFFFF. Any screen bound to a `capture:` source
          // would then refuse its OWN rows, which is worse than the bleed the key was added to
          // stop.
          uint32_t screen_id = 0;
          refii_bridge_menu_spawn_begin_for(
              refii::vfs::InGameScreenRoots(base, nullptr, &screen_id) ? screen_id : 0xFFFFFFFFu);
        }
      }
    }
  }

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82426470);


  const PPCContext original_ctx = ctx;
  std::string command =
      refii::vfs::ReadMaybeGuestString(base, original_ctx.r3.u32, 512);
  std::string known_command =
      refii::vfs::KnownGuiCommandForCallerLr(original_ctx.lr);
  if (command.empty()) {
    command = known_command;
  }

  // The list boundary is handled above the native passthrough — see the note there.
  const bool gameface_command =
      command.find("GUI:") != std::string::npos ||
      command.find("GameFace") != std::string::npos ||
      command.find("gameface") != std::string::npos;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (gameface_command ||
       refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "GuiCommandExecutor_82426470", 64);
  const uint32_t global =
      base != nullptr ? refii::vfs::LoadGuestU32(base, 0x83496B08u) : 0;
  const uint8_t immediate_mode =
      refii::vfs::LooksLikeGuestPointer(global)
          ? refii::vfs::LoadGuestU8(base, global + 100)
          : 0;
  if (trace) {
    const uint32_t ref_string =
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32)
            : 0;
    const uint32_t data_string =
        refii::vfs::LooksLikeGuestPointer(ref_string)
            ? refii::vfs::LoadGuestU32(base, ref_string)
            : 0;
    REXLOG_INFO("VFS trace GUI command executor enter sub_82426470 "
                "lr=0x{:08X} command={} command_obj=0x{:08X} gui_ctx=0x{:08X} "
                "global=0x{:08X} immediate={} known={} obj_words={} "
                "ref=0x{:08X} ref_words={} data=0x{:08X} data_ascii={} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                command,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                global,
                immediate_mode,
                known_command,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 8),
                ref_string,
                refii::vfs::FormatGuestWordsIfPointer(base, ref_string, 4),
                data_string,
                refii::vfs::FormatGuestAsciiBytes(base, data_string, 128),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82426470(ctx, base);

  if (gameface_command) {
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "gui_command_executor_82426470",
        fmt::format("command={} lr=0x{:08X} result=0x{:08X} "
                    "command_obj=0x{:08X} gui_ctx=0x{:08X} immediate={}",
                    command,
                    static_cast<uint32_t>(original_ctx.lr),
                    ctx.r3.u32,
                    original_ctx.r3.u32,
                    original_ctx.r4.u32,
                    immediate_mode));
  }

  if (trace) {
    REXLOG_INFO("VFS trace GUI command executor exit sub_82426470 "
                "lr=0x{:08X} command={} result=0x{:08X} handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                command,
                ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(sub_82372500) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82372500);


  const PPCContext original_ctx = ctx;
  const std::string command =
      refii::vfs::ReadMaybeGuestString(base, original_ctx.r3.u32, 512);
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() ||
       command.find("GUI:") != std::string::npos ||
       command.find("GameFace") != std::string::npos ||
       command.find("gameface") != std::string::npos) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "GuiCommandImmediate_82372500", 64);

  if (trace) {
    const uint32_t ref_string =
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32)
            : 0;
    const uint32_t data_string =
        refii::vfs::LooksLikeGuestPointer(ref_string)
            ? refii::vfs::LoadGuestU32(base, ref_string)
            : 0;
    REXLOG_INFO("VFS trace GUI command immediate enter sub_82372500 "
                "lr=0x{:08X} command={} command_obj=0x{:08X} exec=0x{:08X} "
                "obj_words={} ref=0x{:08X} ref_words={} data=0x{:08X} "
                "data_ascii={} exec_words={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                command,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 8),
                ref_string,
                refii::vfs::FormatGuestWordsIfPointer(base, ref_string, 4),
                data_string,
                refii::vfs::FormatGuestAsciiBytes(base, data_string, 128),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 24),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82372500(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GUI command immediate exit sub_82372500 "
                "lr=0x{:08X} command={} result=0x{:08X} exec_words={} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                command,
                ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 24),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}
