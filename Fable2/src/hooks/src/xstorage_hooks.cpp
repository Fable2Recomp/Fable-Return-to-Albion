// Native override for the XDK's XStorageEnumerate client stub
// (sub_82D00278 / XStorageEnumerate in the IDB).
//
// The real stub marshals a CMarshaller RPC through XLiveBase message
// 0x5000A, which Refii does not emulate. Without this hook the title's
// storage pump (news/gifting content: Presence, LiveDate, items.dat,
// gold.dat) blocks on the enumerate and retries it forever. Answering
// "success, zero items" is a truthful reply for a fresh server and lets
// the pump run to completion: absent files are skipped, the individual
// downloads fail fast, and the state machine parks in its terminal
// phase. See sub_823289B0 / sub_823A2C10 in the IDB for the pump.

#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc/context.h>

namespace {

void StoreGuestU32(uint8_t* base, uint32_t guest_address, uint32_t value) {
  const uint32_t be = __builtin_bswap32(value);
  std::memcpy(base + guest_address, &be, sizeof(be));
}

}  // namespace

REX_EXTERN(__imp__rex_XStorageEnumerate);

// DWORD XStorageEnumerate(dwUserIndex, wszServerPath, dwStartingIndex,
//                         dwMaxResultsToReturn, cbResults, pResults,
//                         pXOverlapped)
REX_EXTERN(rex_XStorageEnumerate) {
  const uint32_t results_ptr = ctx.r8.u32;
  const uint32_t overlapped_ptr = ctx.r9.u32;
  if (!results_ptr || overlapped_ptr) {
    // Unexpected shape (async or no results target): keep the original
    // failing path rather than mis-completing an overlapped.
    __imp__rex_XStorageEnumerate(ctx, base);
    return;
  }
  // XSTORAGE_ENUMERATE_RESULTS { dwTotalNumItems, dwNumItemsReturned,
  //                              pItems }
  StoreGuestU32(base, results_ptr + 0, 0);
  StoreGuestU32(base, results_ptr + 4, 0);
  StoreGuestU32(base, results_ptr + 8, 0);
  REXLOG_INFO("XStorageEnumerate: served empty result set natively");
  ctx.r3.u64 = 0;  // ERROR_SUCCESS
}
