// refii - Fable 3: the mouse turns the local player's camera, whole, in the update it is handed.
//
// Fable 2's hook (Fable2/src/vfs/src/hooks/mouse_look.cpp) is the reference; this file is the same
// contract against Fable 3's binary and camera scripts. Every address is the Fable 3 IDB.
//
// WHERE THE TURN GOES.
//   - The camera manager's update `sub_82255FB8(manager)` (rex_CameraManager_Update; its one
//     caller sub_8226A8C0 runs it per tick under the game lock when the game is not paused) runs
//     the active mode's Lua FrameUpdate once: the override at manager+60, else the primary at
//     manager+44 (`lwz r11,0x3C(r31); cmpwi; lwz r11,0x2C(r31)` at 0x822562EC..0x82256304), then
//     `sub_82259328(mode+44, mode+24)` at 0x82256310 (the FrameUpdate holder, the camera table).
//     An override whose +105 byte is set is dropped after its update (0x82256314..0x82256330).
//   - A mode is the C++ wrapper `sub_82769188` builds around a Lua camera object (vtable
//     0x82001FEC): +24 the camera table's ref holder, +32/+44/+56/+68/+80/+92 the Activate /
//     FrameUpdate / Deactivate / CleanUp / ProcessGameAction / OnLevelLoad function holders
//     (0x82769458..0x827695C0). A holder's `*(+0)` is a ref object: +4 the lua_State
//     (0x8276B2E4), +8 the registry ref (0x8276B30C; the camera table's at 0x82242B68).
//   - The wrapper is an input listener: its vtable slot 1, `sub_82769E88(mode, msg)`
//     (rex_CameraMode_ProcessGameAction), is what the dispatcher `sub_821CE2A8` calls with each
//     32-byte GameAction (`l->vt[1](l, action)` at 0x821CE5D0): msg+0 the action id, +4 x, +8 y.
//     It refuses while the player's input bit is set (`(*flags >> 3) & 1` at 0x82769EE0), while
//     the debug camera holds (`byte_835565BF`, 0x82769FF4) and for action 52; negates y under
//     CameraLookInverted (component 37's +40, 0x82769FCC); and calls
//     `sub_8276B2B8(mode+80, mode+24, &action, &x, &y)`: pushes registry[ProcessGameAction],
//     registry[camera table], the three numbers (tag 3, float32) and pcalls with 4 arguments
//     (0x8276B3C4). Where the ids come from: the control scheme in globals.gdb binds
//     GameActionRotateCameraPad (GameAction 2) and GameActionZoomCameraPad (GameAction 4) to
//     XboxRightJoystick (records #2070b9bb / #f20eddba), x and y the stick's.
//
// THE CAMERAS' LOOK CONTRACT (gamescripts.bnk; the compiled chunks' constant pools).
//   SACCamera, `game:\data\scripts\Camera\StringAndCageCamera.lua` (chunk 842a028f): the same
//     fields as Fable 2's — RotateSpeed, RotateDamp, RotateRight, SmoothRotateRight,
//     ApplyRotateVector, ManualHeightOffset, RollingFocalPos, GetCanRotateCamera, IsTargetLocking,
//     LastRotateFrame, ClearToRotate, CheckForBetterTargetInDirection, and the natives
//     Player.GetCameraSensitivity and Timing.GetTickRate — so ProcessGameAction(ROTATION) ASSIGNS
//     `RotateRight = -x * (RotateSpeed / GetTickRate()) * (0.5 + sensitivity)` and
//     ApplyRotateVector eases it through SmoothRotateRight (GetRollingAverage, RotateDamp), as
//     the Fable 2 header lays out. Timing.GetTickRate is `(float)dbl_8329EA48` (sub_82241980).
//   FirstPersonCamera (chunk 04059234): RotateSmoothAmount, RotateX/RotateY, SmoothedRotateX/Y.
//   RangedCombatCamera (chunk 6e8f631c): TendnessNormal, AngleLimit; an empty ProcessGameAction.
//
// SO, before each update the hook takes everything the mouse turned since the last one and hands
// it to the active mode through the mode's own receiver — its gates, its invert, its sensitivity
// all apply as to the stick — and makes the turn the camera accepted land WHOLE in this update,
// outside the stick's easing, exactly as the Fable 2 hook does for SAC (RotateRight = M, then
// SmoothRotateRight += M, taken back out after) and for first person (the accepted dx, dy moved
// into SmoothedRotateX/Y scaled by (n+1)/n). What the camera turned is stated with how the host
// draws a turn not yet taken (core/mouse_look_state.cpp): ORBIT about the focus at the stated
// horizontal distance to RollingFocalPos, IN PLACE for first person, NONE otherwise.
//
// THE LUA VM IS HAVOKSCRIPT ON 5.1 SHAPES (Fable3/src/hooks/src/gui_natives.cpp): L->top at +28,
// L->base at +32, stack_last at +36 (the room check `top + 16 > L[9]` at 0x822A7CB8); a TValue is
// 8 bytes, value then tag: 0 nil, 1 boolean, 3 number (a float32), 5 table, 7 userdata (payload
// at +16). The stack is read and written directly where the guest itself does (sub_8276B2B8
// writes its three numbers inline); fields go through the guest's own lua_getfield
// `sub_822A7B58(L, idx, name)` and lua_setfield `sub_822A7C90(L, idx, name)` (the ones the
// native registrars use, 0x822A7C90 at 0x822E6244), and the camera table is pushed by the
// guest's own `sub_82242B40(holder, L)` (rex_LuaRef_Push).
//
// THE MODE'S HERO, as the receiver finds it: `sub_82769E28(mode)` looks the key at mode+4 up in
// the game state's player table `*(state+0x40)` (`lwz r4,4(r3)` / `lwz r5,0x40(r10)` at
// 0x82769E3C / 0x82769E4C, state = `*(*0x83556540+0xC)`) through `sub_821FD790` — 12-byte
// {key, player} entries from +8 to +0xC (`li r10,0xC` / `lwz r3,4(r11)` at 0x821FD794 /
// 0x821FD808) — and takes key 1 when that key has none (0x82769E60..0x82769E70); the entity is
// `*(*(player+0x10))` (`sub_8243C9E8`: `addi r3,r3,0xC; lwz r11,4(r3); lwz r10,0(r11)`). Its
// component 37 (the receiver's search, `cmpwi r10,0x25` at 0x82769F6C) carries CameraLookInverted
// at +0x28 (`lbz r11,0x28(r31)` at 0x82769FCC).
//
// A TARGET LOCK TAKES THE MOUSE AS FLICKS, as in Fable 2. The lock is the player mode
// CPlayerModeTargetLock (vtable 0x820C59A0, slot 8 `li r3,4` at 0x82C05EC8), asked by the native
// Player.HasPlayerMode `sub_821B9BC0(component37, mode)` (`lwz r3,0x14(r3)` then `sub_821F87E8`,
// whose `sub_821E2EC0` matches each held mode's slot 8 against `mode`) — the targeting component
// asks it the same way (`lwz r3,0x14(r11); li r4,4; bl sub_821F87E8` at 0x821A1F48..0x821A1F58).
// SACCamera's lock fields (IsTargetLocking, ClearToRotate, LastRotateFrame,
// CheckForBetterTargetInDirection, in the chunk's constant pool) are Fable 2's: a look x past 0.8
// switches target. So under a lock the mouse's turn is collected and only a deliberate flick
// (kFlickDegrees inside kFlickWindowSeconds, one direction) is handed, as ONE x of ±1 through the
// receiver; the push that made it hands nothing more until the mouse rests or reverses. The wheel
// has no lock that switches by it here (no mod statement in Fable 3) and is dropped.
//
// THE RANGED AIM'S CURSOR TAKES THE MOUSE, as the PC build's does. Zoom aim
// (CPlayerModeZoomAimRanged, type 27) runs the aim at mode+0x20 through `sub_82850218(aim, stick)`
// (its only caller, `addi r29,r27,0x20; ld r4,0x160(r27)` at 0x8284DFCC / 0x8284DFD8), whose
// cursor move `sub_82852218` (rex_lhRangedAim_MoveCursor) is called only while the cursor is free
// (`lbz r11,0xCD(r31)` at 0x828506B4) with the smoothed stick (`ld r4,0x24(r31)` at 0x828506C4):
// the 64-bit r4 is stored and read back as x then y (`std r4,arg_18(r1)` at 0x82852230,
// `lfs f30,arg_18(r1)` / `lfs f29,arg_18+4(r1)` at 0x828522FC / 0x8285230C), so x is the high
// word, y the low. Free of a target it moves the cursor, screen fractions at aim+0x2C / +0x30:
//     828526a8  fmadds f9, f13, f27, f12 / stfs f9, 0x2C(r31)   ; x += sx * speed * 2.0/tick
//     828526b0  fmadds f8, f11, f26, f10 / stfs f8, 0x30(r31)   ; y += sy * speed * 1.6/tick
// (speed `lfs f0,0x38(r28)` at 0x82852680, r28 = `sub_821B1398(owner)` at 0x828522EC; f27 / f26
// = flt_8329EC04 / flt_8329EC08 over dbl_8329EA48, 0x82852324..0x82852348), then clamps it to
// ±0.8 and searches under it (`sub_82853D28`); snapped to a target the stick picks between targets
// instead (0x82852420..0x828525C0). The cursor's y is screen-down (`sub_82855340`: `py / height *
// 2 - 1`), so the aim's stick y arrives negated unless the camera is inverted (`sub_8284D670`:
// `lfs f0,flt_82070544` = -1.0 times +0x164 unless +0x17E, which is component 37's +0x28,
// 0x8284D694..0x8284D6A0 / 0x8284DCE8..0x8284DCEC). The PC build moves the same cursor by the
// mouse through the same gain (`sub_F36C70`: `*(this+68) += a2[0] * (flt / tick) * stats[56]`).
// So the mouse's turn joins the stick at the cursor's own gain — a posted degree moves the cursor
// a degree's radians — its invert as the stick's, and the clamps, the search and the snap apply
// to it as to the stick. RangedCombatCamera takes nothing through its receiver and chases the aim
// point, so while an aim moves its cursor the camera update leaves the turn for it.
//
// NOT CARRIED FROM FABLE 2 (stated as remaining, not approximated): the ranged aim turned rigidly
// with the view, and the heading held while the hero moves. Both are Fable 2's wrappers of the
// camera scripts' own functions (RangedCombatCamera.UpdateCurrentDirection, SACCamera.CalculateXY,
// Godot/src/Titles/Fable2/host/CoopHeroLua.cs) compiled by refii_lua for Fable 2's Lua 5.1; Fable 3's
// scripts are HavokScript bytecode with no compiler or decompiler in the tree, so neither the
// wrapper can be loaded nor CalculateXY's Fable 3 body read to place one.

#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_CameraManager_Update);
REX_EXTERN(__imp__rex_lhRangedAim_MoveCursor);
REX_EXTERN(rex_CameraMode_ProcessGameAction);
REX_EXTERN(rex_LuaRef_Push);
REX_EXTERN(rex_lua_getfield);
REX_EXTERN(rex_lua_setfield);
REX_EXTERN(rex_Player_HasPlayerMode);
REX_EXTERN(rex_Entity_RangedAimStats);

namespace {

/// GameActionRotateCameraPad / GameActionZoomCameraPad (globals.gdb, bound to XboxRightJoystick).
constexpr uint32_t kRotateCamera = 2u;
constexpr uint32_t kZoomCamera = 4u;

/// The game mode, whose `*(+0xC)` is the game state (`lwz r10,0xC(r11)` at 0x82769E48).
constexpr uint32_t kGame = 0x83556540u;

/// The game state's player table (0x82769E4C), keyed by a camera mode's +4.
constexpr uint32_t kStatePlayers = 0x40u;
constexpr uint32_t kModePlayerKey = 4u;

/// The player's component the receiver reads (0x82769F6C), its CameraLookInverted byte (0x82769FCC).
constexpr uint32_t kPlayerComponent = 37u;
constexpr uint32_t kCameraLookInverted = 0x28u;

/// CPlayerModeTargetLock's type (vtable 0x820C59A0 slot 8, `li r3,4` at 0x82C05EC8).
constexpr uint32_t kTargetLockMode = 4u;

/// A flick that switches a lock's target, as Fable 2's: this much turn in one direction inside
/// the window.
constexpr double kFlickDegrees = 40.0;
constexpr double kFlickWindowSeconds = 0.2;

/// The aim's cursor move: the stats' cursor speed (0x82852680), the gains over the tick rate
/// (0x82852328 / 0x8285232C).
constexpr uint32_t kAimStatsSpeed = 0x38u;
constexpr uint32_t kAimGainAcross = 0x8329EC04u;
constexpr uint32_t kAimGainUp = 0x8329EC08u;

/// SACCamera:ProcessGameAction's height dead zone (`y > 0.1 or y < -0.1`), as Fable 2's.
constexpr float kHeightDeadZone = 0.1f;

/// How the host draws a turn not yet taken (core/mouse_look_state.cpp).
constexpr int32_t kModeNone = 0;
constexpr int32_t kModeOrbit = 1;
constexpr int32_t kModeInPlace = 2;

/// The Lua camera mode wrapper's vtable (sub_82769188 at 0x827691A8), and its fields.
constexpr uint32_t kModeVtable = 0x82001FECu;
constexpr uint32_t kModeSelf = 24u;
constexpr uint32_t kModeProcessGameAction = 80u;

/// The camera manager's mode slots (0x822562EC..0x82256304).
constexpr uint32_t kManagerPrimary = 44u;
constexpr uint32_t kManagerOverride = 60u;

/// The debug camera holds the pad (0x82769FF4): nothing reaches the camera scripts.
constexpr uint32_t kDebugCameraFlag = 0x835565BFu;

/// Timing.GetTickRate's double (sub_82241980, `lfd f0, dbl_8329EA48`).
constexpr uint32_t kTickRate = 0x8329EA48u;

/// lua_State layout (gui_natives.cpp; 0x822A7CB8).
constexpr uint32_t kLuaTop = 28u;
constexpr uint32_t kLuaBase = 32u;
constexpr uint32_t kLuaStackLast = 36u;

/// TValue tags.
constexpr uint32_t kTagNil = 0u;
constexpr uint32_t kTagBoolean = 1u;
constexpr uint32_t kTagNumber = 3u;
constexpr uint32_t kTagTable = 5u;
constexpr uint32_t kTagUserdata = 7u;
constexpr uint32_t kUdataPayload = 16u;

enum class Kind { kOther, kStringAndCage, kFirstPerson, kRangedAim };

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

uint32_t LoadU32(const uint8_t* base, uint32_t address) {
  uint32_t be;
  std::memcpy(&be, base + address, sizeof(be));
  return __builtin_bswap32(be);
}

float LoadF32(const uint8_t* base, uint32_t address) {
  const uint32_t bits = LoadU32(base, address);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

double LoadF64(const uint8_t* base, uint32_t address) {
  const uint64_t bits =
      (static_cast<uint64_t>(LoadU32(base, address)) << 32) | LoadU32(base, address + 4u);
  double value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

void StoreU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t be = __builtin_bswap32(value);
  std::memcpy(base + address, &be, sizeof(be));
}

void StoreF32(uint8_t* base, uint32_t address, float value) {
  uint32_t bits;
  std::memcpy(&bits, &value, sizeof(bits));
  StoreU32(base, address, bits);
}

/// A copy of `ctx` with the stack pointer moved below the caller's frame.
PPCContext Frame(const PPCContext& ctx, uint32_t bytes = 0x200u) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - bytes) & ~0xFu;
  return work;
}

/// The mode the update runs: the override, else the primary.
uint32_t ActiveCamera(const uint8_t* base, uint32_t manager) {
  if (!Plausible(manager)) {
    return 0u;
  }
  const uint32_t overriding = LoadU32(base, manager + kManagerOverride);
  const uint32_t mode = Plausible(overriding) ? overriding : LoadU32(base, manager + kManagerPrimary);
  return Plausible(mode) && LoadU32(base, mode) == kModeVtable ? mode : 0u;
}

/// The receiver's own standing gate the hook can see: the debug camera holds the pad.
bool TakesInput(const uint8_t* base) { return base[kDebugCameraFlag] == 0u; }

uint32_t Word(const uint8_t* base, uint32_t at) { return Plausible(at) ? LoadU32(base, at) : 0u; }

/// What a weak reference at `holder` resolves to: `*(*(holder+4))` (lhSmartPtr_Deref
/// 0x829A2650: `lwz r11,4(r3); lwz r10,0(r11)`), 0 when dead.
uint32_t Resolve(const uint8_t* base, uint32_t holder) {
  const uint32_t at = Word(base, Word(base, holder + 4u));
  return Plausible(at) ? at : 0u;
}

/// The entity's component `id`: its {id, component} pairs from +0x54 to +0x58 (the receiver's
/// search, `lwz r11,0x54(r3); lwz r7,0x58(r3)` at 0x82769F4C), or 0.
uint32_t Component(const uint8_t* base, uint32_t entity, uint32_t id) {
  const uint32_t first = Word(base, entity + 0x54u);
  const uint32_t last = Word(base, entity + 0x58u);
  if (!Plausible(first) || last < first || last - first > 8u * 256u) {
    return 0u;
  }
  for (uint32_t at = first; at < last; at += 8u) {
    if (LoadU32(base, at) == id) {
      const uint32_t component = LoadU32(base, at + 4u);
      return Plausible(component) ? component : 0u;
    }
  }
  return 0u;
}

/// The mode's hero, as `sub_82769E28` and `sub_8243C9E8` find it (see the header), or 0.
uint32_t HeroOf(const uint8_t* base, uint32_t mode) {
  const uint32_t table = Word(base, Word(base, Word(base, kGame) + 0xCu) + kStatePlayers);
  if (!Plausible(mode) || !Plausible(table)) {
    return 0u;
  }
  const uint32_t first = Word(base, table + 8u);
  const uint32_t last = Word(base, table + 0xCu);
  if (!Plausible(first) || last < first || last - first > 12u * 64u) {
    return 0u;
  }
  const auto find = [&](uint32_t key) -> uint32_t {
    for (uint32_t at = first; at < last; at += 12u) {
      if (LoadU32(base, at) == key) {
        return LoadU32(base, at + 4u);
      }
    }
    return 0u;
  };
  uint32_t player = find(LoadU32(base, mode + kModePlayerKey));
  if (!Plausible(player)) {
    player = find(1u);
  }
  return Plausible(player) ? Resolve(base, player + 0xCu) : 0u;
}

/// Player.HasPlayerMode(component37, TARGET_LOCK): the guest's own native.
bool TargetLocked(PPCContext& ctx, uint8_t* base, uint32_t hero) {
  const uint32_t player = Component(base, hero, kPlayerComponent);
  if (!Plausible(player)) {
    return false;
  }
  PPCContext work = Frame(ctx);
  work.r3.u64 = player;
  work.r4.u64 = kTargetLockMode;
  rex_Player_HasPlayerMode(work, base);
  return (work.r3.u32 & 0xFFu) != 0u;
}

/// The hero's CameraLookInverted, which the aim's stick and the camera's receiver both honour.
bool LookInverted(const uint8_t* base, uint32_t hero) {
  const uint32_t player = Component(base, hero, kPlayerComponent);
  return Plausible(player) && base[player + kCameraLookInverted] != 0u;
}

/// The camera's Lua table, pushed the way `sub_82242B40` pushes it, with a few field accessors.
/// Everything runs below the caller's stack pointer; the Lua stack is restored on destruction.
class CameraScript {
 public:
  CameraScript(PPCContext& ctx, uint8_t* base, uint32_t mode)
      : work_(Frame(ctx, 0x400u)), base_(base) {
    names_ = work_.r1.u32 + 0x200u;
    const uint32_t function = Plausible(mode) ? LoadU32(base, mode + kModeProcessGameAction) : 0u;
    const uint32_t self = Plausible(mode) ? LoadU32(base, mode + kModeSelf) : 0u;
    const uint32_t state = Plausible(function) ? LoadU32(base, function + 4u) : 0u;
    if (!Plausible(self) || !Plausible(state) || !Plausible(LoadU32(base, mode + kModeSelf))) {
      return;
    }
    L_ = state;
    self_holder_ = mode + kModeSelf;
    top_ = Top();
    if (!PushSelf() || Tag(-1) != kTagTable) {
      SetTop(top_);
      L_ = 0u;
    }
  }

  ~CameraScript() {
    if (L_ != 0u) {
      SetTop(top_);
    }
  }

  CameraScript(const CameraScript&) = delete;
  CameraScript& operator=(const CameraScript&) = delete;

  bool open() const { return L_ != 0u; }

  /// The mode's GameAction receiver, with this script's frame (the table stays pushed; the
  /// receiver's own pushes are relative to the top).
  void Send(uint32_t mode, uint32_t action, float x, float y) {
    PPCContext w = work_;
    const uint32_t message = (w.r1.u32 + 0x100u) & ~0xFu;
    std::memset(base_ + message, 0, 32u);
    StoreU32(base_, message, action);
    StoreF32(base_, message + 4u, x);
    StoreF32(base_, message + 8u, y);
    w.r3.u32 = mode;
    w.r4.u32 = message;
    rex_CameraMode_ProcessGameAction(w, base_);
  }

  /// `self[name]` (through the class's __index) when it is a number.
  bool Number(const char* name, double* out) {
    GetField(-1, name);
    const bool number = Tag(-1) == kTagNumber;
    if (number) {
      *out = static_cast<double>(LoadF32(base_, Slot(-1)));
    }
    SetTop(-2);
    return number;
  }

  /// `self[name] or fallback`.
  double NumberOr(const char* name, double fallback) {
    double value = fallback;
    return Number(name, &value) ? value : fallback;
  }

  /// `self[name] = value`.
  void SetNumber(const char* name, double value) {
    if (!Room(2u)) {
      return;
    }
    const uint32_t top = Top32();
    StoreF32(base_, top, static_cast<float>(value));
    StoreU32(base_, top + 4u, kTagNumber);
    SetTop32(top + 8u);
    SetField(-2, name);
  }

  /// `self[name]` when it is a CVector3: a full userdata whose payload (+16) is a pointer to the
  /// 16-byte vector, x y z at +0 +4 +8 (the constructor `sub_82241630`: `*lua_newuserdata(L, 4) =
  /// block` at 0x82241754, a CVector3 argument read as `(*udata)[4]` then `lvx128` at
  /// 0x822416A0). Finite, and within `within` metres of `near`, or false.
  bool Vector(const char* name, const float* near, float within, float* xyz) {
    GetField(-1, name);
    bool ok = false;
    if (Tag(-1) == kTagUserdata) {
      const uint32_t udata = LoadU32(base_, Slot(-1));
      const uint32_t boxed = Plausible(udata) ? LoadU32(base_, udata + kUdataPayload) : 0u;
      ok = Plausible(boxed) && ReadVector(boxed, near, within, xyz);
    }
    SetTop(-2);
    return ok;
  }

  /// Whether `self.OverriddenData[name]` is set (CameraBase:GetOverriddenData's table).
  bool Overridden(const char* name) {
    GetField(-1, "OverriddenData");
    bool set = false;
    if (Tag(-1) == kTagTable) {
      GetField(-1, name);
      set = Tag(-1) != kTagNil;
      SetTop(-2);
    }
    SetTop(-2);
    return set;
  }

 private:
  bool ReadVector(uint32_t at, const float* near, float within, float* xyz) const {
    if (!Plausible(at)) {
      return false;
    }
    float v[3];
    for (uint32_t i = 0; i < 3u; ++i) {
      v[i] = LoadF32(base_, at + i * 4u);
      if (!std::isfinite(v[i])) {
        return false;
      }
    }
    const float dx = v[0] - near[0];
    const float dy = v[1] - near[1];
    const float dz = v[2] - near[2];
    if (dx * dx + dy * dy + dz * dz > within * within) {
      return false;
    }
    for (uint32_t i = 0; i < 3u; ++i) {
      xyz[i] = v[i];
    }
    return true;
  }

  PPCContext Work() const {
    PPCContext w = work_;
    w.r3.u32 = L_;
    return w;
  }

  /// registry[*(*(holder)+4)] pushed, as `sub_82242B40(holder, L)` does for the camera table.
  bool PushSelf() {
    if (!Room(1u)) {
      return false;
    }
    PPCContext w = work_;
    w.r3.u32 = self_holder_;
    w.r4.u32 = L_;
    rex_LuaRef_Push(w, base_);
    return true;
  }

  uint32_t Stage(const char* text) {
    const size_t n = std::strlen(text);
    std::memcpy(base_ + names_, text, n);
    base_[names_ + n] = 0;
    return names_;
  }

  uint32_t Top32() const { return LoadU32(base_, L_ + kLuaTop); }
  void SetTop32(uint32_t top) { StoreU32(base_, L_ + kLuaTop, top); }

  /// Whether `slots` more values fit before stack_last (the guest's own check, 0x822A7CB8).
  bool Room(uint32_t slots) const {
    return Top32() + slots * 8u <= LoadU32(base_, L_ + kLuaStackLast);
  }

  /// The slot at a negative index from the top (-1 the top).
  uint32_t Slot(int32_t index) const {
    return Top32() + static_cast<uint32_t>(static_cast<int32_t>(index) * 8);
  }

  int32_t Top() const {
    return static_cast<int32_t>((Top32() - LoadU32(base_, L_ + kLuaBase)) / 8u);
  }

  /// lua_settop: a negative index pops down to it, a non-negative one sets the count from base.
  void SetTop(int32_t index) {
    if (index < 0) {
      SetTop32(Top32() + static_cast<uint32_t>((index + 1) * 8));
    } else {
      SetTop32(LoadU32(base_, L_ + kLuaBase) + static_cast<uint32_t>(index) * 8u);
    }
  }

  uint32_t Tag(int32_t index) const { return LoadU32(base_, Slot(index) + 4u); }

  void GetField(int32_t index, const char* name) {
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_getfield(w, base_);
  }

  void SetField(int32_t index, const char* name) {
    PPCContext w = Work();
    w.r4.s64 = index;
    w.r5.u32 = Stage(name);
    rex_lua_setfield(w, base_);
  }

  PPCContext work_;
  uint8_t* base_;
  uint32_t names_ = 0u;
  uint32_t L_ = 0u;
  uint32_t self_holder_ = 0u;
  int32_t top_ = 0;
};

/// The camera's look integrator, by the class constants only that integrator reads.
Kind KindOf(CameraScript& script) {
  double value = 0.0;
  if (script.Number("RotateDamp", &value)) {
    return Kind::kStringAndCage;
  }
  if (script.Number("RotateSmoothAmount", &value)) {
    return Kind::kFirstPerson;
  }
  if (script.Number("TendnessNormal", &value)) {
    return Kind::kRangedAim;
  }
  return Kind::kOther;
}

/// What the update in flight was handed and what must be taken back out after it: guest tick
/// thread only, between Before and After.
struct Update {
  bool open = false;
  uint32_t mode = 0u;
  Kind kind = Kind::kOther;
  float taken = 0.0f;
  float taken_pitch = 0.0f;
  float held = 0.0f;
  float absorbed = 0.0f;
  float factor = 0.0f;
  int32_t draw = kModeNone;
  double smooth_added = 0.0;
  double smoothed_dx = 0.0;
  double smoothed_dy = 0.0;
  bool smoothed = false;
};

Update g_update;

/// The mode whose last handed turn was refused: drawn with no turn ahead until one is accepted
/// (its Lua gates — GetCanRotateCamera, a target lock, the input bit — are seen only on a hand-over).
uint32_t g_refused = 0u;

/// Camera updates seen: the account's tick (the host reads it as a sequence, HostBridge.GetMouseLook).
uint32_t g_updates = 0u;

/// The first accepted and the first refused hand-over, logged once each: the run's evidence.
bool g_logged_accepted = false;
bool g_logged_refused = false;

/// The hero whose camera the mouse drives (the manager the guest updates itself), as of its last
/// update: only that hero's aim takes the mouse.
uint32_t g_mouse_hero = 0u;

/// The last aim that moved its cursor for the mouse's hero, and the camera update count it did so
/// at. An aim seen at this count or the one before is running — the two updates' order within a
/// tick is not relied on.
uint32_t g_aim_hero = 0u;
uint32_t g_aim_update = 0u;
bool g_logged_aim = false;

bool AimRunning(uint32_t hero) {
  return hero != 0u && hero == g_aim_hero && g_updates - g_aim_update <= 1u;
}

/// A lock's flick in the making: the turn collected in one direction since the window opened.
struct Flick {
  uint32_t mode = 0u;
  double sum = 0.0;
  double window = 0.0;
  std::chrono::steady_clock::time_point last{};
  bool seen = false;
  /// The push that handed a flick, in its direction (+1 / -1); 0 when none is spent.
  int spent = 0;
};

Flick g_flick;

/// Under a target lock: the mouse's turn becomes at most one stick flick (see the header).
void FlickTheLock(CameraScript& script, uint32_t mode, float yaw) {
  const auto now = std::chrono::steady_clock::now();
  const double since = g_flick.seen && g_flick.mode == mode
                           ? std::chrono::duration<double>(now - g_flick.last).count()
                           : 0.0;
  if (g_flick.mode != mode) {
    g_flick = Flick{};
    g_flick.mode = mode;
  }
  g_flick.last = now;
  g_flick.seen = true;

  const int direction = yaw > 0.0f ? 1 : (yaw < 0.0f ? -1 : 0);
  if (direction == 0 || (g_flick.spent != 0 && g_flick.spent != direction)) {
    g_flick.spent = 0;
  }
  if (direction == 0 || g_flick.spent != 0) {
    g_flick.sum = 0.0;
    g_flick.window = 0.0;
    return;
  }

  // The turn this update carries came over `since`; past the window only its share inside the
  // window counts, so a slow drag over long updates never adds up to a flick.
  double share = yaw;
  if (since > kFlickWindowSeconds) {
    share *= kFlickWindowSeconds / since;
  }
  if ((g_flick.sum > 0.0) != (share > 0.0) || g_flick.window + since > kFlickWindowSeconds) {
    g_flick.sum = 0.0;
    g_flick.window = 0.0;
  } else {
    g_flick.window += since;
  }
  g_flick.sum += share;
  if (std::fabs(g_flick.sum) < kFlickDegrees) {
    return;
  }

  script.Send(mode, kRotateCamera, static_cast<float>(direction), 0.0f);
  refii_bridge_mouse_look_flicked();
  g_flick.spent = direction;
  g_flick.sum = 0.0;
  g_flick.window = 0.0;
}

/// SAC: the rotate through the mode's receiver, made whole — or, under a target lock, a flick.
/// Sets the update's account.
void HandToStringAndCage(PPCContext& ctx, CameraScript& script, uint8_t* base, uint32_t mode,
                         uint32_t hero, float yaw, float pitch, Update& update) {
  update.factor = 1.0f;
  const bool locked = TargetLocked(ctx, base, hero);
  double speed = 0.0;
  const double rate = static_cast<float>(LoadF64(base, kTickRate));
  if (!script.Number("RotateSpeed", &speed) || !(rate > 0.0) || !(speed > 0.0)) {
    update.draw = kModeNone;
    return;
  }

  // x in the receiver's units: a posted degree is a degree of turn at sensitivity 0.5.
  const double per_unit = speed / rate;
  const float x = static_cast<float>(yaw / per_unit);
  float y = static_cast<float>(pitch / per_unit);
  const bool height = std::fabs(y) > kHeightDeadZone;
  if (!height) {
    update.held = pitch;
    y = 0.0f;
  }

  if (locked) {
    FlickTheLock(script, mode, yaw);
    g_refused = mode;
  } else if (x != 0.0f) {
    g_flick.spent = 0;
    g_flick.sum = 0.0;
    double before = 0.0;
    double after = 0.0;
    const bool had = script.Number("RotateRight", &before);
    script.Send(mode, kRotateCamera, x, 0.0f);
    if (script.Number("RotateRight", &after) && (!had || after != before)) {
      script.SetNumber("SmoothRotateRight", script.NumberOr("SmoothRotateRight", 0.0) + after);
      update.smooth_added = after;
      update.absorbed = static_cast<float>(-after);
      if (std::fabs(yaw) > 1e-3f) {
        update.factor = static_cast<float>(-after / yaw);
      }
      g_refused = 0u;
    } else {
      g_refused = mode;
    }
  }

  if (height) {
    script.Send(mode, kZoomCamera, 0.0f, y);
  }
  update.draw = g_refused == mode ? kModeNone : kModeOrbit;
}

/// First person: the rotate through the receiver, then moved past its clamp and easing. A camera
/// bound to a target keeps its own path whole, and is drawn as it states it.
void HandToFirstPerson(CameraScript& script, uint32_t mode, float yaw, float pitch, Update& update) {
  double speed = 0.0;
  double smooth = 0.0;
  if (!script.Number("RotateSpeed", &speed) || !script.Number("RotateSmoothAmount", &smooth) ||
      !(speed > 0.0)) {
    update.draw = kModeNone;
    return;
  }

  const bool own = smooth <= 0.0 || script.Overridden("BoundTarget") ||
                   script.Overridden("BoundEntity");
  update.factor = 1.0f;
  if (yaw != 0.0f || pitch != 0.0f) {
    const double rx0 = script.NumberOr("RotateX", 0.0);
    const double ry0 = script.NumberOr("RotateY", 0.0);
    script.Send(mode, kRotateCamera, static_cast<float>(yaw / speed), static_cast<float>(pitch / speed));
    const double dx = script.NumberOr("RotateX", 0.0) - rx0;
    const double dy = script.NumberOr("RotateY", 0.0) - ry0;
    if (dx == 0.0 && dy == 0.0) {
      g_refused = mode;
    } else {
      g_refused = 0u;
      if (!own) {
        const double scale = (smooth + 1.0) / smooth;
        script.SetNumber("RotateX", rx0);
        script.SetNumber("RotateY", ry0);
        script.SetNumber("SmoothedRotateX", script.NumberOr("SmoothedRotateX", 0.0) + dx * scale);
        script.SetNumber("SmoothedRotateY", script.NumberOr("SmoothedRotateY", 0.0) + dy * scale);
        update.smoothed = true;
        update.smoothed_dx = dx;
        update.smoothed_dy = dy;
        update.absorbed = static_cast<float>(dx * speed);
      }
    }
  }
  update.draw = own || g_refused == mode ? kModeNone : kModeInPlace;
}

/// Before the manager's update: hands the mouse's pending turn to the mode it is about to run.
void BeforeCameraUpdate(PPCContext& ctx, uint8_t* base, uint32_t manager) {
  g_update = Update{};
  // The wheel has no use in Fable 3 yet (no lock a mod states switches by it): taken and dropped,
  // as Fable 2 drops it without such a statement.
  refii_bridge_mouse_look_take_wheel();

  const uint32_t mode = ActiveCamera(base, manager);
  const uint32_t hero = HeroOf(base, mode);
  g_mouse_hero = hero;
  if (mode != 0u && AimRunning(hero)) {
    // The ranged-aim camera takes no look input; the turn waits for the aim's cursor move.
    CameraScript script(ctx, base, mode);
    if (script.open() && KindOf(script) == Kind::kRangedAim) {
      return;
    }
  }

  float yaw = 0.0f;
  float pitch = 0.0f;
  refii_bridge_mouse_look_take(&yaw, &pitch);
  g_update.open = true;
  g_update.taken = yaw;
  g_update.taken_pitch = pitch;

  if (mode == 0u || !TakesInput(base)) {
    // No camera to turn, or the camera takes no input now: the turn is spent, not stored up.
    return;
  }

  CameraScript script(ctx, base, mode);
  if (!script.open()) {
    return;
  }
  g_update.mode = mode;
  g_update.kind = KindOf(script);
  if (g_refused != 0u && g_refused != mode) {
    g_refused = 0u;
  }

  switch (g_update.kind) {
    case Kind::kStringAndCage:
      HandToStringAndCage(ctx, script, base, mode, hero, yaw, pitch, g_update);
      break;
    case Kind::kFirstPerson:
      HandToFirstPerson(script, mode, yaw, pitch, g_update);
      break;
    case Kind::kRangedAim:
    case Kind::kOther:
      break;
  }
}

/// After the update: takes the mouse's turn back out of the camera's easing and states the
/// account.
void AfterCameraUpdate(PPCContext& ctx, uint8_t* base, uint32_t manager) {
  ++g_updates;
  if (!g_update.open) {
    return;
  }
  Update update = g_update;
  g_update = Update{};

  // An override the update dropped (manager+60 cleared at 0x82256330) is gone with its state.
  const bool current = update.mode != 0u && update.mode == ActiveCamera(base, manager);
  float pivot = 0.0f;
  {
    CameraScript script(ctx, base, current ? update.mode : 0u);
    if (script.open() && update.smooth_added != 0.0) {
      script.SetNumber("SmoothRotateRight",
                       script.NumberOr("SmoothRotateRight", 0.0) - update.smooth_added);
      // ApplyRotateVector zeroes RotateRight when it turned; a camera without its PreBound
      // positions yet did not, and the turn handed to it is taken back out unspent.
      const double left = script.NumberOr("RotateRight", 0.0);
      if (left != 0.0) {
        script.SetNumber("RotateRight", left - update.smooth_added);
        update.absorbed = 0.0f;
      }
    }
    if (script.open() && update.smoothed) {
      script.SetNumber("SmoothedRotateX", script.NumberOr("SmoothedRotateX", 0.0) - update.smoothed_dx);
      script.SetNumber("SmoothedRotateY", script.NumberOr("SmoothedRotateY", 0.0) - update.smoothed_dy);
    }

    if (update.draw == kModeOrbit) {
      // The camera looks at RollingFocalPos from its placed position: the turn's vertical axis
      // lies on the level line of sight, this far out. The position is the camera the view feed
      // last stated (view_feed.cpp, the manager's final block each high-frequency step); the
      // focus moves metres per second, so last step's camera places the pivot to within the
      // frame's motion.
      float stated[11];
      float focus[3] = {0.0f, 0.0f, 0.0f};
      if (script.open() && refii_bridge_camera_read(stated) != 0 &&
          script.Vector("RollingFocalPos", stated, 200.0f, focus)) {
        const float dx = focus[0] - stated[0];
        const float dy = focus[1] - stated[1];
        pivot = std::sqrt(dx * dx + dy * dy);
      }
      if (!(pivot > 0.05f && pivot < 200.0f)) {
        update.draw = kModeNone;
        pivot = 0.0f;
      }
    }
  }
  if (!current) {
    update.draw = kModeNone;
  }

  if (update.taken != 0.0f || update.taken_pitch != 0.0f) {
    const bool accepted = update.absorbed != 0.0f;
    if (accepted && !g_logged_accepted) {
      g_logged_accepted = true;
      REXLOG_INFO("[mouse-look] first turn taken: mode 0x{:08X} kind {} yaw {:.2f} pitch {:.2f} "
                  "absorbed {:.2f} factor {:.3f} draw {} pivot {:.2f} m",
                  update.mode, static_cast<int>(update.kind), update.taken, update.taken_pitch,
                  update.absorbed, update.factor, update.draw, pivot);
    } else if (!accepted && !g_logged_refused) {
      g_logged_refused = true;
      REXLOG_INFO("[mouse-look] first turn refused: mode 0x{:08X} kind {} yaw {:.2f} pitch {:.2f} "
                  "(no camera, the debug camera, a gate in the script, or a non-look camera)",
                  update.mode, static_cast<int>(update.kind), update.taken, update.taken_pitch);
    }
  }

  refii_bridge_mouse_look_state(g_updates, update.taken, update.taken_pitch, update.held,
                                update.absorbed, 0.0f, update.factor, pivot, 0.0f, 0.0f, 0.0f,
                                update.draw, 0u);
}

}  // namespace

// The camera manager's update (r3 the manager): the mouse's turn handed before, its account
// stated after.
REX_EXTERN(rex_CameraManager_Update) {
  const uint32_t manager = ctx.r3.u32;
  BeforeCameraUpdate(ctx, base, manager);
  __imp__rex_CameraManager_Update(ctx, base);
  AfterCameraUpdate(ctx, base, manager);
}

// The ranged aim's cursor move (r3 the aim, r4 its stick: x in the high word, y in the low). For
// the mouse's hero's aim, everything the mouse turned since the last move joins the stick at the
// cursor's own gain (see the header), and nothing is drawn ahead of it; any other aim runs as is.
REX_EXTERN(rex_lhRangedAim_MoveCursor) {
  const uint32_t aim = ctx.r3.u32;
  // The aim's owner, as the move itself resolves it (`lwz r11,4(r31)` / lhSmartPtr_Deref at
  // 0x82852290..0x828522BC).
  const uint32_t hero = Resolve(base, aim);
  if (!Plausible(hero) || hero != g_mouse_hero || !TakesInput(base)) {
    __imp__rex_lhRangedAim_MoveCursor(ctx, base);
    return;
  }
  g_aim_hero = hero;
  g_aim_update = g_updates;

  float yaw = 0.0f;
  float pitch = 0.0f;
  refii_bridge_mouse_look_take(&yaw, &pitch);
  if (yaw != 0.0f || pitch != 0.0f) {
    // The stats the move reads its speed from: `sub_821B1398(owner)` (0x828522E8..0x828522F0).
    uint32_t stats = 0u;
    {
      PPCContext work = Frame(ctx);
      work.r3.u64 = hero;
      rex_Entity_RangedAimStats(work, base);
      stats = work.r3.u32;
    }
    const double rate = LoadF64(base, kTickRate);
    const double speed = Plausible(stats) ? LoadF32(base, stats + kAimStatsSpeed) : 0.0;
    const double gain_x = rate > 0.0 ? speed * LoadF32(base, kAimGainAcross) / rate : 0.0;
    const double gain_y = rate > 0.0 ? speed * LoadF32(base, kAimGainUp) / rate : 0.0;
    if (std::isfinite(gain_x) && std::isfinite(gain_y) && std::fabs(gain_x) > 1e-6 &&
        std::fabs(gain_y) > 1e-6) {
      constexpr double kRadians = 3.14159265358979323846 / 180.0;
      // Upward is screen-up, the cursor's -y, unless the camera is inverted (the stick's rule).
      const double up = LookInverted(base, hero) ? 1.0 : -1.0;
      float sx = 0.0f;
      float sy = 0.0f;
      const uint32_t xb = static_cast<uint32_t>(ctx.r4.u64 >> 32);
      const uint32_t yb = static_cast<uint32_t>(ctx.r4.u64);
      std::memcpy(&sx, &xb, sizeof(sx));
      std::memcpy(&sy, &yb, sizeof(sy));
      sx += static_cast<float>(yaw * kRadians / gain_x);
      sy += static_cast<float>(up * pitch * kRadians / gain_y);
      uint32_t xo = 0u;
      uint32_t yo = 0u;
      std::memcpy(&xo, &sx, sizeof(xo));
      std::memcpy(&yo, &sy, sizeof(yo));
      ctx.r4.u64 = (static_cast<uint64_t>(xo) << 32) | yo;
      if (!g_logged_aim) {
        g_logged_aim = true;
        REXLOG_INFO("[mouse-look] first aim move: aim 0x{:08X} yaw {:.2f} pitch {:.2f} gain {:.4f} "
                    "{:.4f} stick {:.3f} {:.3f}",
                    aim, yaw, pitch, gain_x, gain_y, sx, sy);
      }
    }
  }

  refii_bridge_mouse_look_state(g_updates, yaw, pitch, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f,
                                0.0f, kModeNone, 0u);
  __imp__rex_lhRangedAim_MoveCursor(ctx, base);
}
