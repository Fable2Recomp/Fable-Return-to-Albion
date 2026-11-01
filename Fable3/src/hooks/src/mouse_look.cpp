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
// NOT CARRIED FROM FABLE 2 (stated as remaining, not approximated): the target lock's flick and
// wheel switching (Fable 2 asks Player.HasPlayerMode(record, TARGET_LOCK) natively; Fable 3's
// member is not found yet, so under a lock the mouse is handed as a stick is and the script's own
// 0.8 flick rule applies); the ranged aim turned rigidly with the view (the aim component's point
// offsets are Fable 2's); and the heading held while the hero moves (Fable 2's core camera chunk
// runs in refii_lua, which Fable 3 does not have).

#include <cmath>
#include <cstdint>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_CameraManager_Update);
REX_EXTERN(rex_CameraMode_ProcessGameAction);
REX_EXTERN(rex_LuaRef_Push);
REX_EXTERN(rex_lua_getfield);
REX_EXTERN(rex_lua_setfield);

namespace {

/// GameActionRotateCameraPad / GameActionZoomCameraPad (globals.gdb, bound to XboxRightJoystick).
constexpr uint32_t kRotateCamera = 2u;
constexpr uint32_t kZoomCamera = 4u;

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

/// SAC: the rotate through the mode's receiver, made whole. Sets the update's account.
void HandToStringAndCage(CameraScript& script, const uint8_t* base, uint32_t mode, float yaw,
                         float pitch, Update& update) {
  update.factor = 1.0f;
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

  if (x != 0.0f) {
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

  float yaw = 0.0f;
  float pitch = 0.0f;
  refii_bridge_mouse_look_take(&yaw, &pitch);
  g_update.open = true;
  g_update.taken = yaw;
  g_update.taken_pitch = pitch;

  const uint32_t mode = ActiveCamera(base, manager);
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
      HandToStringAndCage(script, base, mode, yaw, pitch, g_update);
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
