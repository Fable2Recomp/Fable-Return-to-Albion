/**
 * @file        src/hooks.cpp
 *
 * @brief       Function hooks and replacements for guest module runtime.
 *
 * @copyright   Copyright (c) 2026 Tom Clay <tomc@tctechstuff.com>
 *              All rights reserved.
 *
 * @license     BSD 3-Clause License
 *              See LICENSE file in the project root for full license text.
 */

#include "generated/refii_init.h"
#include <rex/ppc.h>
#include <rex/kernel/crt/heap.h>
#include <rex/logging.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xfile.h>
#include <rex/system/xthread.h>

#include <cstring>

namespace {

// GetFileSectorInfo — the game queries disc sector positions to optimize read
// ordering.  NtQueryInformationFile(FileSectorInformation) always fails on
// recompiled builds.  Stub it to return 0 (no sector info) and log the call.
ppc_u32_result_t GetFileSectorInfo_entry(ppc_u32_t handle) {
    REXLOG_WARN("GetFileSectorInfo(handle={:#x}) -> 0 (stubbed)",
                static_cast<uint32_t>(handle));
    return 0;
}

// RtlCreateHeap — return a dummy handle. All heap operations (alloc/free/
// realloc/size) are intercepted by rexcrt, so the heap structure is never
// accessed. Prevents 3,800+ AllocFixed errors from the CRT trying to reserve
// pages inside the rexcrt heap region.
ppc_u32_result_t RtlCreateHeap_entry(ppc_u32_t flags, ppc_u32_t base,
    ppc_u32_t reserve_size, ppc_u32_t commit_size, ppc_u32_t lock,
    ppc_u32_t params) {
    return 0xBEEF0001;
}

// lh_malloc_impl — redirect the game's custom pool/page/large allocator to
// the rexcrt o1heap. This prevents the three-tier Lionhead heap from needing
// backing memory via VirtualAlloc (which conflicts with rexcrt heap).
ppc_u32_result_t lh_malloc_impl_entry(ppc_u32_t size) {
    return rex::kernel::crt::GetHeap().Alloc(size);
}

// lh_free — free through the rexcrt heap.
ppc_u32_result_t lh_free_entry(ppc_u32_t ptr) {
    rex::kernel::crt::GetHeap().Free(static_cast<uint32_t>(ptr));
    return 1;
}

// lhHeapRealloc — the game's custom realloc. Args: (heap_ctx, old_ptr,
// old_size, new_size). Redirect to rexcrt realloc.
ppc_u32_result_t lhHeapRealloc_entry(ppc_u32_t heap_ctx, ppc_u32_t old_ptr,
    ppc_u32_t old_size, ppc_u32_t new_size) {
    auto& heap = rex::kernel::crt::GetHeap();
    if (!static_cast<uint32_t>(new_size)) {
        heap.Free(static_cast<uint32_t>(old_ptr));
        return 0;
    }
    if (!static_cast<uint32_t>(old_ptr)) {
        return heap.Alloc(new_size);
    }
    return heap.Realloc(static_cast<uint32_t>(old_ptr),
                        static_cast<uint32_t>(new_size));
}

// lhHeapLarge_Alloc — called directly by lua_newstate and the Lua allocator
// for allocations > 256 bytes, bypassing lh_malloc_impl. Args: (heap_desc,
// size). Ignore heap_desc and redirect to rexcrt.
ppc_u32_result_t lhHeapLarge_Alloc_entry(ppc_u32_t heap_desc, ppc_u32_t size) {
    return rex::kernel::crt::GetHeap().Alloc(size);
}

// lhHeapLarge_Free — counterpart to lhHeapLarge_Alloc.
// Args: (heap_desc, ptr).
ppc_u32_result_t lhHeapLarge_Free_entry(ppc_u32_t heap_desc, ppc_u32_t ptr) {
    rex::kernel::crt::GetHeap().Free(static_cast<uint32_t>(ptr));
    return 1;
}

// lh_lua_allocator — Lua's lua_Alloc callback. Calls lhHeapPool_Alloc and
// lhHeapLarge_Alloc directly with uninitialized heap structures. Redirect the
// entire allocator to rexcrt. Signature: (ud, old_ptr, old_size, new_size).
ppc_u32_result_t lh_lua_allocator_entry(ppc_u32_t ud, ppc_u32_t old_ptr,
    ppc_u32_t old_size, ppc_u32_t new_size) {
    auto& heap = rex::kernel::crt::GetHeap();
    if (!static_cast<uint32_t>(new_size)) {
        if (static_cast<uint32_t>(old_ptr))
            heap.Free(static_cast<uint32_t>(old_ptr));
        return 0;
    }
    if (!static_cast<uint32_t>(old_ptr)) {
        return heap.Alloc(new_size);
    }
    return heap.Realloc(static_cast<uint32_t>(old_ptr),
                        static_cast<uint32_t>(new_size));
}

// lhHeap_Alloc — master dispatch for all Lionhead heap allocations. Redirect
// entirely to rexcrt to prevent split-heap corruption between rexcrt and the
// Lionhead three-tier allocator (pool/large/virtual).
ppc_u32_result_t lhHeap_Alloc_entry(ppc_u32_t size) {
    return rex::kernel::crt::GetHeap().Alloc(size);
}

// lhHeap_Free — master dispatch for all Lionhead heap frees.
ppc_u32_result_t lhHeap_Free_entry(ppc_u32_t ptr) {
    if (!static_cast<uint32_t>(ptr))
        return 1;
    rex::kernel::crt::GetHeap().Free(static_cast<uint32_t>(ptr));
    return 1;
}

// lhHeap_Init — skip Lionhead heap initialization entirely since all
// allocations are redirected to rexcrt.
ppc_u32_result_t lhHeap_Init_entry() {
    return 0;
}


}  // namespace

PPC_HOOK(rex_GetFileSectorInfo, GetFileSectorInfo_entry)
PPC_HOOK(rex_RtlCreateHeap, RtlCreateHeap_entry)
PPC_HOOK(rex_lh_malloc_impl, lh_malloc_impl_entry)
PPC_HOOK(rex_lh_free, lh_free_entry)
PPC_HOOK(rex_lhHeapRealloc, lhHeapRealloc_entry)
PPC_HOOK(rex_lhHeapLarge_Alloc, lhHeapLarge_Alloc_entry)
PPC_HOOK(rex_lhHeapLarge_Free, lhHeapLarge_Free_entry)
PPC_HOOK(rex_lh_lua_allocator, lh_lua_allocator_entry)
PPC_HOOK(rex_lhHeap_Alloc, lhHeap_Alloc_entry)
PPC_HOOK(rex_lhHeap_Free, lhHeap_Free_entry)
PPC_HOOK(rex_lhHeap_Init, lhHeap_Init_entry)

// ---------------------------------------------------------------------------
// Synchronous disc I/O — replace the async NtReadFile+APC wrapper
// ---------------------------------------------------------------------------

// Guest-side layout passed by the disc I/O scheduler (sub_82B5C1B0).
// The caller packs the IO_STATUS_BLOCK and the 64-bit file byte offset into
// a single structure at request+40/+60/+80 (one per chunk).
#pragma pack(push, 1)
struct DiscReadIOSB {
    rex::be<uint32_t> status;      // +0  NTSTATUS
    rex::be<uint32_t> information; // +4  bytes transferred
    rex::be<uint32_t> offset_lo;   // +8  byte offset low  (LARGE_INTEGER.LowPart)
    rex::be<uint32_t> offset_hi;   // +12 byte offset high (LARGE_INTEGER.HighPart)
};
#pragma pack(pop)

// Guest address of the APC shim (sub_82CD0760).  The original code passes
// this + 1 to NtReadFile (PPC function pointer convention); the SDK strips
// the low bit when enqueuing.
static constexpr uint32_t kApcShimAddr = 0x82CD0760;

// rex_NtReadFileApc — replaces sub_82CC7DD0 which calls NtReadFile with an APC
// callback, bypassing the CRT ReadFile hooks.  We do the read synchronously via
// the SDK's file API and then enqueue the APC for deferred delivery.  The APC
// fires when the thread enters an alertable wait in WaitForCompletion, which is
// AFTER the dispatch function sets busy=1 — preserving the original ordering.
// PPC args: r3=handle, r4=buf_guest, r5=len, r6=iosb_guest, r7=apc_ctx
extern "C" PPC_FUNC(rex_NtReadFileApc) {
    uint32_t handle    = ctx.r3.u32;
    uint32_t buf_guest = ctx.r4.u32;
    uint32_t len       = ctx.r5.u32;
    uint32_t req_guest = ctx.r6.u32;
    uint32_t apc_ctx   = ctx.r7.u32;

    auto* ks  = rex::system::kernel_state();
    auto* mem = ks->memory();
    auto* req = mem->TranslateVirtual<DiscReadIOSB*>(req_guest);

    // Reconstruct 64-bit byte offset from the two halves after the IOSB.
    uint64_t byte_offset =
        (static_cast<uint64_t>(static_cast<uint32_t>(req->offset_hi)) << 32) |
        static_cast<uint32_t>(req->offset_lo);

    // Look up XFile from guest handle.
    auto file_ref = ks->object_table()
                        ->LookupObject<rex::system::XFile>(handle);
    if (!file_ref) {
        REXLOG_WARN("NtReadFileApc: invalid handle {:#x}", handle);
        req->status      = static_cast<uint32_t>(0xC0000008u); // STATUS_INVALID_HANDLE
        req->information = 0u;
        ctx.r3.u64 = 0;
        return;
    }

    // Synchronous read through the SDK's host-side file API.
    uint8_t* host_buf = mem->TranslateVirtual(buf_guest);
    size_t   bytes_read = 0;
    rex::X_STATUS status = file_ref->file()->ReadSync(
        host_buf, len, static_cast<size_t>(byte_offset), &bytes_read);

    REXLOG_WARN("NtReadFileApc: handle={:#x} off={:#x} len={:#x} -> {:#x} ({} bytes)",
                handle, byte_offset, len, status, bytes_read);

    // Fill guest IOSB.
    req->status      = static_cast<uint32_t>(status);
    req->information = static_cast<uint32_t>(bytes_read);

    // Queue the APC for deferred delivery — fires when the thread enters an
    // alertable wait in WaitForCompletion, after the dispatch sets busy=1.
    // Mirrors how the SDK's NtReadFile enqueues APCs.
    auto* thread = rex::system::XThread::GetCurrentThread();
    thread->EnqueueApc(kApcShimAddr, apc_ctx, req_guest, 0);

    // Return 1 (success) or 0 (failure) to the disc I/O dispatcher.
    ctx.r3.u64 = XSUCCEEDED(status) ? 1u : 0u;
}

// ---------------------------------------------------------------------------
// Diagnostic helpers
// ---------------------------------------------------------------------------

// Extract bank name (wchar_t big-endian in guest memory) as ASCII.
// Bank wstring: +12 = data/inline, +28 = length, +32 = capacity.
// Inline if capacity < 8, otherwise +12 is a pointer.
static void get_bank_name(uint8_t* base, uint32_t bank, char* out, size_t out_size) {
    uint32_t capacity = PPC_LOAD_U32(bank + 32);
    uint32_t wstr_addr = (capacity < 8) ? (bank + 12) : PPC_LOAD_U32(bank + 12);
    size_t i = 0;
    for (; i < out_size - 1; ++i) {
        uint16_t wc = PPC_LOAD_U16(wstr_addr + i * 2);
        if (!wc) break;
        out[i] = (wc < 128) ? static_cast<char>(wc) : '?';
    }
    out[i] = '\0';
}

// ---------------------------------------------------------------------------
// Diagnostic wrapper hooks for heightfield resource loading investigation
// ---------------------------------------------------------------------------

// lhHeightfield_ResourceTypeCheck — log entry to confirm heightfield resource
// processing callback is invoked.
PPC_EXTERN_FUNC(__imp__rex_lhHeightfield_ResourceTypeCheck);
extern "C" PPC_FUNC(rex_lhHeightfield_ResourceTypeCheck) {
    REXLOG_WARN("lhHeightfield_ResourceTypeCheck called (r3={:#x})", ctx.r3.u32);
    __imp__rex_lhHeightfield_ResourceTypeCheck(ctx, base);
}

// lhFile_Read — log file reads with filename extracted from lhFile struct.
// lhFile layout: +0 vtable, +4 string_ref (-> lhString), +8 handle, +12 stream
// lhString layout: +0 char* data pointer
PPC_EXTERN_FUNC(__imp__rex_lhFile_Read);
extern "C" PPC_FUNC(rex_lhFile_Read) {
    static int call_count = 0;
    ++call_count;
    {
        const char* filename = "<unknown>";
        uint32_t str_ref = PPC_LOAD_U32(ctx.r3.u32 + 4);
        if (str_ref) {
            uint32_t str_data = PPC_LOAD_U32(str_ref);
            if (str_data)
                filename = reinterpret_cast<const char*>(PPC_RAW_ADDR(str_data));
        }
        REXLOG_WARN("lhFile_Read #{} file=\"{}\" (buf={:#x}, size={:#x})",
                    call_count, filename, ctx.r4.u32, ctx.r5.u32);
    }
    __imp__rex_lhFile_Read(ctx, base);
}

// lhResource_LoaderThread — log thread start/exit to confirm the background
// resource loader thread is running.
PPC_EXTERN_FUNC(__imp__rex_lhResource_LoaderThread);
extern "C" PPC_FUNC(rex_lhResource_LoaderThread) {
    REXLOG_WARN("lhResource_LoaderThread STARTED");
    __imp__rex_lhResource_LoaderThread(ctx, base);
    REXLOG_WARN("lhResource_LoaderThread EXITED");
}

// ---------------------------------------------------------------------------
// Level loading diagnostic hooks
// ---------------------------------------------------------------------------

// lhBank_OpenStreamByIndex — log bank stream opens.
// PPC args: r3=result_ptr, r4=bank, r5=index, r6=flag
PPC_EXTERN_FUNC(__imp__rex_lhBank_OpenStreamByIndex);
extern "C" PPC_FUNC(rex_lhBank_OpenStreamByIndex) {
    char name[128];
    get_bank_name(base, ctx.r4.u32, name, sizeof(name));
    uint32_t index = ctx.r5.u32;

    __imp__rex_lhBank_OpenStreamByIndex(ctx, base);

    uint32_t result = PPC_LOAD_U32(ctx.r3.u32);
    REXLOG_WARN("lhBank_OpenStreamByIndex bank=\"{}\" index={} -> {}",
                name, index, result ? "OK" : "NULL");
}

// lhBank_OpenStreamByName — log named bank lookups.
// PPC args: r3=result_ptr, r4=bank, r5=name_struct, r6=flag
// name_struct+28 = pre-computed hash
PPC_EXTERN_FUNC(__imp__rex_lhBank_OpenStreamByName);
extern "C" PPC_FUNC(rex_lhBank_OpenStreamByName) {
    char name[128];
    get_bank_name(base, ctx.r4.u32, name, sizeof(name));
    uint32_t name_hash = ctx.r5.u32 ? PPC_LOAD_U32(ctx.r5.u32 + 28) : 0;

    __imp__rex_lhBank_OpenStreamByName(ctx, base);

    uint32_t result = PPC_LOAD_U32(ctx.r3.u32);
    REXLOG_WARN("lhBank_OpenStreamByName bank=\"{}\" hash={:#010x} -> {}",
                name, name_hash, result ? "OK" : "NULL");
}

// lhResourceManager_Open — log resource manager opens to see if the loader
// thread actually processes resource requests during level loading.
// PPC args: r3=name/param, r4=result_ptr, r5=offset, r6=size, r7=flag1, r8=flag2
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_Open);
extern "C" PPC_FUNC(rex_lhResourceManager_Open) {
    uint32_t r3 = ctx.r3.u32, r5 = ctx.r5.u32, r6 = ctx.r6.u32;

    __imp__rex_lhResourceManager_Open(ctx, base);

    REXLOG_WARN("lhResourceManager_Open (r3={:#x}, offset={:#x}, size={:#x}) -> {}",
                r3, r5, r6, ctx.r3.u32 ? "OK" : "FAIL");
}

// lhWorld_Update — log once to confirm the game transitions to gameplay.
PPC_EXTERN_FUNC(__imp__rex_lhWorld_Update);
extern "C" PPC_FUNC(rex_lhWorld_Update) {
    static bool logged = false;
    if (!logged) {
        REXLOG_WARN("lhWorld_Update called — game world is ticking");
        logged = true;
    }
    __imp__rex_lhWorld_Update(ctx, base);
}


