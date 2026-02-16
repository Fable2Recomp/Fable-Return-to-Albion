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
#include <rex/runtime/guest/function.h>

using namespace rex::runtime::guest;

namespace {

// GetFileSectorInfo replacement — the game queries disc sector positions to
// optimize read ordering. return 0 (no sector info).
dword_result_t GetFileSectorInfo_entry(dword_t handle) {
    return 0;
}

}  // namespace

GUEST_FUNCTION_HOOK(sub_82CC7BB8, GetFileSectorInfo_entry)

GUEST_FUNCTION_STUB(__imp__XamShowGamerCardUIForXUID);
GUEST_FUNCTION_STUB(__imp__XamShowAchievementsUI);
GUEST_FUNCTION_STUB(__imp__XamShowMarketplaceUI);
GUEST_FUNCTION_STUB(__imp__IoInvalidDeviceRequest);
GUEST_FUNCTION_STUB(__imp__ObReferenceObject);
GUEST_FUNCTION_STUB(__imp__IoDeleteDevice);
GUEST_FUNCTION_STUB(__imp__IoCompleteRequest);
GUEST_FUNCTION_STUB(__imp__RtlUpcaseUnicodeChar);
GUEST_FUNCTION_STUB(__imp__ObIsTitleObject);
GUEST_FUNCTION_STUB(__imp__IoCheckShareAccess);
GUEST_FUNCTION_STUB(__imp__IoRemoveShareAccess);
GUEST_FUNCTION_STUB(__imp__XNetLogonGetTitleID);
GUEST_FUNCTION_STUB(__imp__NetDll_WSAGetOverlappedResult);
GUEST_FUNCTION_STUB(__imp__NetDll_XNetRegisterKey);
GUEST_FUNCTION_STUB(__imp__NetDll_XNetUnregisterKey);
GUEST_FUNCTION_STUB(__imp__NetDll_XNetUnregisterInAddr);
GUEST_FUNCTION_STUB(__imp__NetDll_XNetConnect);
GUEST_FUNCTION_STUB(__imp__NetDll_XNetGetConnectStatus);
GUEST_FUNCTION_STUB(__imp__IoSetShareAccess);
GUEST_FUNCTION_STUB(__imp__XamUserGetMembershipTierFromXUID);
GUEST_FUNCTION_STUB(__imp__XamUserGetOnlineCountryFromXUID);
GUEST_FUNCTION_STUB(__imp__XamVoiceSubmitPacket);
