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

#include <cstring>

namespace {

// GetFileSectorInfo — the game queries disc sector positions to optimize read
// ordering. return 0 (no sector info).
ppc_u32_result_t GetFileSectorInfo_entry(ppc_u32_t handle) {
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

// XPhysicalAlloc -- redirect all physical memory allocations to the rexcrt
// o1heap. MmAllocatePhysicalMemoryEx fails due to Xenia physical heap
// exhaustion; lhHeightfield_CreateBuffers falls back through lhMemAlloc ->
// XMemAlloc -> XPhysicalAlloc, which we intercept here.
// Original signature: XPhysicalAlloc(size, max_addr, alignment, protect)
ppc_u32_result_t XPhysicalAlloc_entry(ppc_u32_t size, ppc_u32_t max_addr,
    ppc_u32_t alignment, ppc_u32_t protect) {
    auto& heap = rex::kernel::crt::GetHeap();
    uint32_t addr = heap.Alloc(static_cast<uint32_t>(size), /*zero=*/true);
    if (!addr) {
        REXLOG_ERROR("XPhysicalAlloc hook: o1heap alloc({}) failed",
                     static_cast<uint32_t>(size));
    }
    return addr;
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
PPC_HOOK(rex_XPhysicalAlloc, XPhysicalAlloc_entry)

