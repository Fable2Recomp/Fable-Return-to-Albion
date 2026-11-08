// refii - Fable 3's Limited Collector's Edition content, made permanently available.
//
// THE GATE IS A PACKAGE LICENCE, NOT AN EDITION FLAG. Every edition check the shipped scripts make
// is `PackageManager.IsPackagePremium("FableIII_DayOne_LCE")` (gamescripts.bnk:
// scripts\quests\scriptactivation.lua and gc_2010_scriptactivation.lua): QV150's AbleToRun (the
// LCE gift the butler hands over in the Sanctuary, guildbutlerroomeventcustomfunctions.lua) and
// QV090's AbleToRun (the Cursewood tomb quest). Both quests' scripts and levels ship on the disc;
// the Day One package is the licence that switches them on. Every address is the Fable 3 TU3 IDB
// ("disc" marks one TU3 lacks).
//
//   PackageManager.IsPackagePremium -> sub_822EE0A0, the whole body:
//       822ECB70  lwz  r3, 0x1C(r3)        the package system the namespace object holds
//       822ECB74  b    sub_822EAD60
//   registered by name at 0x822EDE74 in sub_822EDDA8 (the "PackageManager" namespace) and
//   referenced from nowhere else. The Lua thunk sub_8232BAF0 builds a CCharString from the
//   script's argument (0x8232BB68) and calls it as (namespace r3, CCharString* r4), returning the
//   byte in r3. sub_822EAD60 finds the package by name in the system's list (sub_822EAE98) and
//   answers its premium byte (package+25), 0 when no such package is installed.
//
// The narrowest point is that thunk: sub_822EAD60 itself is also an interface slot (disc 0x8210DF58),
// which the C++ package code (the save game's DLC checks among it) calls, so it is left as the
// guest has it. This hook answers 1 for the Day One package only and passes every other name to
// the guest, so the DLC offers manager (qu140_dlc_offersmanager.lua, which awards a package's
// items when it is installed and premium) still sees exactly what is installed.
//
// NOT HOOKED, and why:
// - IsCollectorsEdition (sub_823E92A0, `lbz r3,0x72(r3); blr`, CECStats+0x72) is called by no
//   shipped script, and its body is shared: the same function is registered as Villager
//   "IsAdopted" (0x8285A6B4, sub_828595B0). Answering 1 there would make every child adopted.
// - CECPlayerWebsiteUnlocks (component 266, 12 bytes, sub_82581A80) has no predicate: its vtable
//   disc 0x82003DD0 is a destructor, type/name getters and nullsubs, and "NeedsUnlockedFromWebsite"
//   (hash disc dword_8355B888, stored at disc 0x831D0AF8) is never read.
// - AddLCEMoney / AddLCEItem (sub_82456C78 / sub_824565B8) queue received items the scripts give;
//   SetFreePackUnlocked (sub_8242C490) is Road to Rule progress (sub_82443FC0 "UNLOCK_FREE_PACK");
//   the pre-order villager (PreorderCharacterManager) needs website data that does not exist.

#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

REX_EXTERN(__imp__rex_PackageManager_IsPackagePremium);

namespace {

constexpr char kDayOneLimitedEdition[] = "FableIII_DayOne_LCE";

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

char Lower(char ch) { return ch >= 'A' && ch <= 'Z' ? static_cast<char>(ch - 'A' + 'a') : ch; }

/// Whether the CCharString whose handle word sits at `at` (handle -> rep, rep+0 = char*) names
/// the Day One package, compared without case as the script spells it.
bool NamesDayOnePackage(const uint8_t* base, uint32_t at) {
  if (!Plausible(at)) {
    return false;
  }
  const uint32_t rep = LoadGuestU32(base, at);
  if (!Plausible(rep)) {
    return false;
  }
  const uint32_t chars = LoadGuestU32(base, rep);
  if (!Plausible(chars)) {
    return false;
  }
  for (uint32_t i = 0; i < sizeof(kDayOneLimitedEdition); ++i) {
    if (Lower(static_cast<char>(base[chars + i])) != Lower(kDayOneLimitedEdition[i])) {
      return false;
    }
  }
  return true;
}

}  // namespace

// bool PackageManager.IsPackagePremium(const CCharString& name)
REX_EXTERN(rex_PackageManager_IsPackagePremium) {
  if (NamesDayOnePackage(base, ctx.r4.u32)) {
    static const bool once = [] {
      REXLOG_INFO("unlock: IsPackagePremium(FableIII_DayOne_LCE) answered true (sub_822EE0A0)");
      return true;
    }();
    (void)once;
    ctx.r3.u64 = 1;
    return;
  }
  __imp__rex_PackageManager_IsPackagePremium(ctx, base);
}
