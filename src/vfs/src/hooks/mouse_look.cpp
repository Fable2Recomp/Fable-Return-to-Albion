// refii — the mouse turns the local player's camera, whole, in the update it is handed.
//
// WHERE THE TURN GOES (TU1; the unpatched build is the same code at its own addresses).
//   - The camera manager update `sub_821B9258` (rex_lhCameraManager_Update) runs the active
//     camera wrapper's Lua FrameUpdate once: the override at manager+0x3C, else the primary at
//     +0x2C (`sub_8219A758(wrapper+0x2C, wrapper+0x18)` at 0x821b9514). The manager the guest
//     itself updates is this machine's own player's — pad 0's, which the keyboard and mouse
//     drive; player two's split-screen manager is updated by coop_hero_hooks.cpp directly.
//   - The wrapper's vtable slot 1, `sub_825ADB80(wrapper, msg)`, is the receiver of a GameAction
//     (msg+0 the action, +4 x, +8 y). It refuses while the player record's +0x7C bit 4 is set
//     (825adcdc..825adce8) or `byte_83496BE9` is (825add14), negates y under CameraLookInverted
//     (+0x2A), and calls `sub_825AEE78(wrapper+0x50, wrapper+0x18, msg, &x, &y)`, which pushes
//     `registry[*(*(wrapper+0x50)+8)]` (ProcessGameAction) and `registry[*(*(wrapper+0x18)+4)]`
//     (the camera's Lua table) on `L = *(*(wrapper+0x50)+4)` with `lua_rawgeti(L, -10000, ref)`
//     (`sub_82229108`, 825aeecc / 825aeee8) and calls ProcessGameAction(self, action, x, y).
//
// THE CAMERAS' LOOK CONTRACT (scripts/camera/*.lua, Lua 5.1 bytecode).
//   SACCamera, and MagicSACCamera which inherits it — the free third-person camera:
//     ProcessGameAction (ROTATE): `RotateRight = -x * (self.RotateSpeed / Timing.GetTickRate())
//       * (0.5 + Player.GetCameraSensitivity(self.Player))` and PlayerIsManuallyRotatingCamera,
//       only if GetCanRotateCamera() (or photo mode) and the camera is alive; under a target lock
//       a flick past 0.8 picks a new target instead and RotateRight is untouched. ASSIGNED, not
//       added. Timing.GetTickRate is `(float)dbl_83319660` (`sub_82241AC8`, the native bound at
//       0x8245c14c), 15.
//     ApplyRotateVector (inside FrameUpdate, after AlignToDirection and LoadUpRotationFromAiming
//       have ADDED their own turn to RotateRight):
//         SmoothRotateRight = GetRollingAverage(SmoothRotateRight or RotateRight, RotateRight,
//                                               SACCamera.RotateDamp)   -- (n*S + R) / (n + 1)
//         PreBoundCamPos turned about CVector3(0,0,1) through PreBoundFocalPos by
//         SmoothRotateRight degrees; RotateRight = 0.
//       At the shipped RotateDamp 1.5 a turn handed in one update lands 0.4, 0.24, 0.144 ... over
//       the following updates — the stick's easing. Vertical is not a pitch but ZOOM's
//       `ManualHeightOffset += -y * 0.05 * (0.5 + s)` past a dead zone of 0.1, decaying back.
//     The camera then looks from its placed position at RollingFocalPos (SetDesiredDirection,
//       FrameUpdate 337..340).
//   FirstPersonCamera: ProcessGameAction ADDS x and y to RotateX/RotateY; UpdateRotations clamps
//     them to ±1, smooths `SmoothedRotate = GetRollingAverage(SmoothedRotate, Rotate,
//     RotateSmoothAmount)`, and turns the camera's angles by `SmoothedRotate * RotateSpeed`
//     (pitch bounded by VerticalAngularBound about BoundAngleVert; yaw bounded only with an
//     OverriddenData.BoundTarget). No sensitivity.
//   RangedCombatCamera, CannedActionCamera, CombatCannedActionCamera, CutSceneCamera: an empty
//     ProcessGameAction. InteractiveCutSceneCamera: `ControlX/ControlY = x, y`, a held stick
//     position its FrameUpdate adds to OverriddenData.XOffset/YOffset as a framing offset — not a
//     turn, and a mouse's per-update delta left there would stay as an offset. None of these
//     takes the mouse; the ranged aim turns through its own cursor below.
//
// SO, before each update the payload takes everything the mouse turned since the last one and
// hands it to the camera through the camera's own receiver — its gates, its invert, its
// sensitivity and its target-lock flick all apply as to the stick — and then makes the turn the
// camera accepted land WHOLE in this update, outside the stick's easing:
//   - SAC: having assigned RotateRight = M, add M to SmoothRotateRight. The rolling average then
//     yields `(n*S + A)/(n+1) + M`: the camera's own turn (A, the align and aim terms) eased as
//     shipped, plus exactly M. After the update M is taken back out of SmoothRotateRight, so the
//     easing state is the camera's own again and no tail of the mouse's turn follows.
//   - FirstPerson: the camera's RotateX/RotateY are restored and the accepted (dx, dy) go into
//     SmoothedRotateX/Y scaled by (n+1)/n, so the smoothed value is the camera's own plus exactly
//     (dx, dy), unclamped; taken back out after the update. The mouse is scaled by the profile's
//     sensitivity here too, so a degree of mouse is the same turn in both cameras.
// THE MOUSE'S CAMERA TURNS ONLY WHEN THE MOUSE DOES (the PC convention), while the host states the
// mouse is the look device (refii_bridge_mouse_look_device). Walking, SACCamera makes no turn of
// its own: AlignToDirection (main.91) runs only when CalculateDesiredDirection (targetlockcamera
// main.2) leaves a DesiredTargetDirection — the look-at button held (lock framing, or the hero's
// facing at DirectionStrengthSoft) or a scripted interesting point — CalculateSlew (main.69)
// returns `RotateRight * 0` as a roll, LoadUpRotationFromAiming (main.111) runs only
// `Targeting.GetInAiming`, MoveBehindHeroOrHenchman (main.72) only on a cut request, `ReCentre`
// is never read, and DriftRate/TurnDriftRate move the focus inside its slot (ApplySlot, main.44).
// What turns the view is the STRING: CalculateXY (main.68) puts the camera between the cage and
// the string from the NEW focus along the line to where the camera was, so a hero moving across
// the view swings it after him; with the stick derived from that camera each update
// (`sub_8283DF30`, called by the hero's receiver at 0x826e6918: the stick rotated by the yaw of the
// camera `sub_821DD3B0` copies), a held strafe walks the hero round the camera and a turn the mouse
// makes is dragged back behind his motion. The zoom level's TrackAmount is the script's own
// counter-term (`pos + CalculateMovement(centre) * TrackAmount`, main.68 pc 6..16, skipped while
// PlayerIsManuallyRotatingCamera). For the update in flight the camera's table is published as the
// global `RefiiMouseLookCamera`, and the core camera chunk (Godot/src/host/CoopHeroLua.cs,
// SACCamera.CalculateXY) carries that camera by its focus's whole movement: it keeps its heading
// and distance, and turns for the mouse, a lock's AlignToDirection and the scripts' cuts. Cleared
// after the update, so player two's split-screen camera and a pad never see it.
//
// A TARGET LOCK TAKES THE MOUSE AS FLICKS. Under a lock (`IsTargetLocking`: Player.HasPlayerMode(
// player, PLAYER_MODE_TARGET_LOCK = 4), the member `sub_821AA5D8(record33, mode)` the binding
// calls, 0x823e78a8) ProcessGameAction (main.113 pc 136..162) does not turn: a look x past 0.8
// with ClearToRotate calls `Targeting.CheckForBetterTargetInDirection(player, x < 0, 20)` — the
// targeting component's slot 49 `sub_82844DF0`, which re-selects in that direction (the 20 is
// never read) — then clears ClearToRotate, which re-arms on an action under 0.8 or a program frame
// without one (pc 115..131). A posted degree is 1/8 of x (RotateSpeed 120 / tick rate 15), so
// 6.4 degrees in one update switched target, and every rest re-armed it. So under a lock the
// mouse's turn is collected, and only a deliberate flick — kFlickDegrees inside kFlickWindow, in
// one direction — is handed, as ONE x of ±1 through the receiver, like a stick pushed past 0.8;
// the push that made it hands nothing more until the mouse rests for an update or reverses. Smaller
// motion is spent. Both locks are this lock: the shipped held trigger and Souls Combat's toggle
// (which sets the force byte the targeting component turns into mode 4).
//
// What the camera turned is stated with how the host draws a turn not yet taken
// (core/mouse_look_state.cpp): orbiting the focus for SAC, at the stated horizontal distance to
// RollingFocalPos; in place for first person; not at all when the camera takes no look input.
//
// THE STATED CAMERA BELONGS TO THE ACCOUNT. The live camera block (manager+0x90) is a copy the
// getter `sub_82265F90` refreshes from the camera only when KeTimeStampBundle's tick count
// (`sub_822660B0`) differs from its stamp at manager+0xD0, so within the millisecond of an earlier
// read it still holds the previous update's camera. After the update the stamp is set off the
// current count and the getter called, so the block the host reads beside the account is the
// camera this update made.
//
// THE RANGED AIM IS NOT A CAMERA TURN. While aiming, the cursor is moved by the aim itself,
// `sub_8287B258(aim, stick)` (rex_lhRangedAim_MoveCursor), from its smoothed right stick
// (`ld r4, 0x18(r31)` at 0x82879964, filled by `sub_82879270`):
//     8287b2d0  lfs f30, arg_18(r1)        ; x (r4's high word)
//     8287b2e0  lfs f29, arg_18+4(r1)      ; y
//     8287b2fc..8287b32c  f27 = flt_83319514 * (1 / dbl_83319660)   ; X speed per tick
//                         f26 = flt_83319518 * (1 / dbl_83319660)   ; Y speed per tick
//     8287b6b0  bl sub_82173E38            ; the aiming entity
//     8287b6b4  bl sub_8219EA40            ; its weapon's record
//     8287b6b8  lfs f0, 0x38(r3)           ; the weapon's aim speed
//     8287b6e0  fmadds f9, f13, f27, f10 / 8287b6e4 stfs f9, 0x20(r29)   ; yaw += x*speed*X
//     8287b6e8  fmadds f8, f12, f26, f11 / 8287b6ec stfs f8, 0x24(r29)   ; pitch += y*speed*Y
// so the mouse's turn joins that stick as `radians / gain` after its smoothing, and the aim's
// clamps and target snapping apply to it as to the stick. While the aim runs the camera leaves
// the turn to it, and the host draws no turn ahead of it.

#include "vfs_hooks_private.h"

#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge.h"
#include "core/refii_mod.h"
#include "hooks/ppc_externs.h"

PPC_EXTERN_FUNC(rex_lua_getfield);
PPC_EXTERN_FUNC(rex_lua_setfield);
PPC_EXTERN_FUNC(rex_lua_gettop);
PPC_EXTERN_FUNC(rex_lua_settop);
PPC_EXTERN_FUNC(rex_lua_type);
PPC_EXTERN_FUNC(rex_lua_tonumber);
PPC_EXTERN_FUNC(rex_lua_pushnumber);
PPC_EXTERN_FUNC(rex_lua_touserdata);
PPC_EXTERN_FUNC(rex_lua_pushnil);

namespace {

using refii::vfs::LoadGuestU32;

constexpr uint32_t kRotateCamera = 2u;
constexpr uint32_t kZoomCamera = 4u;

/// SACCamera:ProcessGameAction's height dead zone (`arg3 > 0.1 or arg3 < -0.1`).
constexpr float kHeightDeadZone = 0.1f;

/// How the host draws a turn not yet taken (core/mouse_look_state.cpp).
constexpr int32_t kModeNone = 0;
constexpr int32_t kModeOrbit = 1;
constexpr int32_t kModeInPlace = 2;

constexpr int32_t kRegistryIndex = -10000;
constexpr int32_t kGlobalsIndex = -10002;

/// The global naming the camera the mouse drives, for the update in flight (CoopHeroLua.cs).
constexpr const char* kDrivenCamera = "RefiiMouseLookCamera";

/// EPlayerMode.PLAYER_MODE_TARGET_LOCK (scripts/miscellaneous/playermodeenum.lua).
constexpr uint32_t kTargetLockMode = 4u;

/// A flick that switches a lock's target: this much turn in one direction inside the window — 200
/// degrees a second, about 4 cm a second at 800 DPI and the default 0.15 degrees a count, well
/// above the drift of a hand holding the lock and below any deliberate flick.
constexpr double kFlickDegrees = 40.0;
constexpr double kFlickWindowSeconds = 0.2;
constexpr int32_t kTypeNil = 0;
constexpr int32_t kTypeNumber = 3;
constexpr int32_t kTypeTable = 5;
constexpr int32_t kTypeUserdata = 7;

enum class Kind { kOther, kStringAndCage, kFirstPerson };

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

double LoadF64(uint8_t* base, uint32_t address) {
  const uint64_t bits = (static_cast<uint64_t>(LoadGuestU32(base, address)) << 32) |
                        LoadGuestU32(base, address + 4u);
  double value = 0.0;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

float FloatOf(uint32_t bits) {
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

uint32_t BitsOf(float value) {
  uint32_t bits = 0u;
  std::memcpy(&bits, &value, sizeof(bits));
  return bits;
}

uint32_t GameTick(uint8_t* base) {
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  return Plausible(game) ? LoadGuestU32(base, game + 0xFCu) : 0u;
}

/// The camera wrapper the update runs: the override, else the primary (0x821b94f0..0x821b9508).
uint32_t ActiveCamera(uint8_t* base, uint32_t manager) {
  if (!Plausible(manager)) {
    return 0u;
  }
  const uint32_t overriding = LoadGuestU32(base, manager + 0x3Cu);
  return Plausible(overriding) ? overriding : LoadGuestU32(base, manager + 0x2Cu);
}

/// The wrapper's player record, read as `sub_825ADB80` reads it: the hero at
/// `**(*(wrapper+4)+0x10)`, its component 33.
uint32_t PlayerRecord(uint8_t* base, uint32_t wrapper) {
  const uint32_t holder = Plausible(wrapper) ? LoadGuestU32(base, wrapper + 4u) : 0u;
  const uint32_t cell = Plausible(holder) ? LoadGuestU32(base, holder + 0x10u) : 0u;
  const uint32_t hero = Plausible(cell) ? LoadGuestU32(base, cell) : 0u;
  return Plausible(hero) ? refii_mod_component(base, hero, 33u) : 0u;
}

/// `IsTargetLocking` as the camera asks it: Player.HasPlayerMode(player, TARGET_LOCK), whose bound
/// member is `sub_821AA5D8(record33, mode)` (`lwz r3,0x14(r3)` then the mode test).
bool TargetLocked(PPCContext& ctx, uint8_t* base, uint32_t record) {
  if (!Plausible(record)) {
    return false;
  }
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u64 = record;
  work.r4.u64 = kTargetLockMode;
  return refii::mods::CallGuest(work, base, refii::guest::Address(0x821AA5D8u)) &&
         (work.r3.u32 & 0xFFu) != 0u;
}

/// The receiver's own gates (825adcdc..825add14): the record's input bit and `byte_83496BE9`.
bool TakesInput(uint8_t* base, uint32_t record) {
  return Plausible(record) && ((LoadGuestU32(base, record + 0x7Cu) >> 4) & 1u) == 0u &&
         refii::vfs::LoadGuestU8(base, refii::guest::Address(0x83496BE9u)) == 0u;
}

/// The profile's camera sensitivity term, `0.5 + Player.GetCameraSensitivity` (`lfs f1,
/// 0x44(r3)` at sub_824D7F78).
float SensitivityFactor(uint8_t* base, uint32_t record) {
  const float sensitivity = refii::mods::LoadF32(base, record + 0x44u);
  return std::isfinite(sensitivity) ? 0.5f + std::fmin(std::fmax(sensitivity, 0.0f), 1.0f) : 1.0f;
}

/// The camera's Lua table, pushed the way `sub_825AEE78` pushes it, with a few field accessors.
/// Everything runs below the caller's stack pointer; the stack is restored on destruction.
class CameraScript {
 public:
  CameraScript(PPCContext& ctx, uint8_t* base, uint32_t wrapper)
      : work_(refii::mods::Frame(ctx, 0x400u)), base_(base) {
    names_ = work_.r1.u32 + 0x200u;
    const uint32_t function = Plausible(wrapper) ? LoadGuestU32(base, wrapper + 0x50u) : 0u;
    const uint32_t self = Plausible(wrapper) ? LoadGuestU32(base, wrapper + 0x18u) : 0u;
    const uint32_t state = Plausible(function) ? LoadGuestU32(base, function + 4u) : 0u;
    if (!Plausible(self) || !Plausible(state)) {
      return;
    }
    L_ = state;
    ref_ = LoadGuestU32(base, self + 4u);
    top_ = Top();
    if (!PushSelf() || Type(-1) != kTypeTable) {
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

  /// The Lua state the camera runs in.
  uint32_t state() const { return L_; }

  /// The camera's table as the global `name`.
  void Publish(const char* name) {
    if (PushSelf()) {
      SetField(kGlobalsIndex, name);
    }
  }

  /// The camera's GameAction receiver, with this script's frame (the table stays pushed; the
  /// receiver's own pushes are relative to the top).
  void Send(uint32_t wrapper, uint32_t action, float x, float y) {
    PPCContext w = work_;
    const uint32_t message = (w.r1.u32 + 0x100u) & ~0xFu;
    std::memset(base_ + message, 0, 24u);
    refii::mods::StoreU32(base_, message, action);
    refii::mods::StoreF32(base_, message + 4u, x);
    refii::mods::StoreF32(base_, message + 8u, y);
    w.r4.u64 = message;
    refii::mods::CallVirtual(w, base_, wrapper, 1u);
  }

  /// `self[name]` (through the class's __index) when it is a number.
  bool Number(const char* name, double* out) {
    GetField(-1, name);
    const bool number = Type(-1) == kTypeNumber;
    if (number) {
      PPCContext w = Work();
      w.r4.s64 = -1;
      rex_lua_tonumber(w, base_);
      *out = w.f1.f64;
    }
    SetTop(-2);
    return number;
  }

  /// `self[name] or fallback`.
  double NumberOr(const char* name, double fallback) {
    double value = fallback;
    return Number(name, &value) ? value : fallback;
  }

  void SetNumber(const char* name, double value) {
    PPCContext w = Work();
    w.f1.f64 = value;
    rex_lua_pushnumber(w, base_);
    SetField(-2, name);
  }

  /// `self[name]` when it is a CVector3: the userdata holds a pointer to four floats
  /// (`sub_822089B8`: `*lua_newuserdata(L, 4) = vec`, x y z at +0 +4 +8).
  bool Vector(const char* name, float* xyz) {
    GetField(-1, name);
    bool ok = false;
    if (Type(-1) == kTypeUserdata) {
      PPCContext w = Work();
      w.r4.s64 = -1;
      rex_lua_touserdata(w, base_);
      const uint32_t box = w.r3.u32;
      const uint32_t vec = Plausible(box) ? LoadGuestU32(base_, box) : 0u;
      if (Plausible(vec)) {
        for (uint32_t i = 0; i < 3u; ++i) {
          xyz[i] = refii::mods::LoadF32(base_, vec + i * 4u);
        }
        ok = std::isfinite(xyz[0]) && std::isfinite(xyz[1]) && std::isfinite(xyz[2]);
      }
    }
    SetTop(-2);
    return ok;
  }

  /// Whether `self.OverriddenData[name]` is set (CameraBase:GetOverriddenData's table).
  bool Overridden(const char* name) {
    GetField(-1, "OverriddenData");
    bool set = false;
    if (Type(-1) == kTypeTable) {
      GetField(-1, name);
      set = Type(-1) != kTypeNil;
      SetTop(-2);
    }
    SetTop(-2);
    return set;
  }

 private:
  PPCContext Work() const {
    PPCContext w = work_;
    w.r3.u32 = L_;
    return w;
  }

  /// `lua_rawgeti(L, LUA_REGISTRYINDEX, ref)`: the camera's table, as `sub_825AEE78` pushes it.
  bool PushSelf() {
    PPCContext w = Work();
    w.r4.s64 = kRegistryIndex;
    w.r5.u64 = ref_;
    return refii::mods::CallGuest(w, base_, refii::guest::Address(0x82229108u));
  }

  uint32_t Stage(const char* text) {
    const size_t n = std::strlen(text);
    std::memcpy(base_ + names_, text, n);
    base_[names_ + n] = 0;
    return names_;
  }

  int32_t Top() {
    PPCContext w = Work();
    rex_lua_gettop(w, base_);
    return static_cast<int32_t>(w.r3.u32);
  }

  void SetTop(int32_t index) {
    PPCContext w = Work();
    w.r4.s64 = index;
    rex_lua_settop(w, base_);
  }

  int32_t Type(int32_t index) {
    PPCContext w = Work();
    w.r4.s64 = index;
    rex_lua_type(w, base_);
    return static_cast<int32_t>(w.r3.u32);
  }

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
  uint32_t ref_ = 0u;
  int32_t top_ = 0;
};

/// `_G[name] = nil` on `L`, below the caller's stack pointer.
void ClearGlobal(PPCContext& ctx, uint8_t* base, uint32_t L, const char* name) {
  PPCContext w = refii::mods::Frame(ctx, 0x200u);
  const uint32_t text = w.r1.u32 + 0x100u;
  const size_t n = std::strlen(name);
  std::memcpy(base + text, name, n);
  base[text + n] = 0;
  w.r3.u32 = L;
  rex_lua_pushnil(w, base);
  w = refii::mods::Frame(ctx, 0x200u);
  w.r3.u32 = L;
  w.r4.s64 = kGlobalsIndex;
  w.r5.u32 = text;
  rex_lua_setfield(w, base);
}

/// The camera's look integrator, by the class constants only that integrator reads.
Kind KindOf(CameraScript& script) {
  double value = 0.0;
  if (script.Number("RotateDamp", &value)) {
    return Kind::kStringAndCage;
  }
  if (script.Number("RotateSmoothAmount", &value)) {
    return Kind::kFirstPerson;
  }
  return Kind::kOther;
}

/// The live camera block after making the getter re-read the camera; its address, or 0.
uint32_t RefreshLiveCamera(PPCContext& ctx, uint8_t* base, uint32_t manager) {
  PPCContext work = refii::mods::Frame(ctx);
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(0x822660B0u))) {
    return 0u;
  }
  refii::mods::StoreU32(base, manager + 0xD0u, work.r3.u32 - 1u);
  work = refii::mods::Frame(ctx);
  work.r3.u32 = manager;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(0x82265F90u))) {
    return 0u;
  }
  return Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}

/// What the update in flight was handed and what must be taken back out after it: guest tick
/// thread only, between Before and After.
struct Update {
  bool open = false;
  uint32_t wrapper = 0u;
  Kind kind = Kind::kOther;
  float taken = 0.0f;
  float held = 0.0f;
  float absorbed = 0.0f;
  float factor = 0.0f;
  int32_t mode = kModeNone;
  double smooth_added = 0.0;
  double smoothed_dx = 0.0;
  double smoothed_dy = 0.0;
  bool smoothed = false;
  /// The Lua state `RefiiMouseLookCamera` was published on, to clear after the update.
  uint32_t published = 0u;
};

Update g_update;

/// The camera whose last handed turn was refused: it is drawn with no turn ahead until one is
/// accepted (its Lua gates — GetCanRotateCamera, a target lock — are seen only on a hand-over).
uint32_t g_refused = 0u;

/// The game tick the aim last moved its cursor on; the camera leaves the turn to the aim for the
/// tick after it too, as the two updates' order within a tick is not relied on.
uint32_t g_aim_tick = 0u;
bool g_aim_seen = false;

/// A lock's flick in the making: the turn collected in one direction since the window opened.
struct Flick {
  uint32_t wrapper = 0u;
  double sum = 0.0;
  double window = 0.0;
  std::chrono::steady_clock::time_point last{};
  bool seen = false;
  /// The push that handed a flick, in its direction (+1 / -1); 0 when none is spent.
  int spent = 0;
};

Flick g_flick;

/// Under a target lock: the mouse's turn becomes at most one stick flick (see the header).
void FlickTheLock(CameraScript& script, uint32_t wrapper, float yaw) {
  const auto now = std::chrono::steady_clock::now();
  const double since = g_flick.seen && g_flick.wrapper == wrapper
                           ? std::chrono::duration<double>(now - g_flick.last).count()
                           : 0.0;
  if (g_flick.wrapper != wrapper) {
    g_flick = Flick{};
    g_flick.wrapper = wrapper;
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

  script.Send(wrapper, kRotateCamera, static_cast<float>(direction), 0.0f);
  refii_bridge_mouse_look_flicked();
  g_flick.spent = direction;
  g_flick.sum = 0.0;
  g_flick.window = 0.0;
}

bool AimHoldsTheMouse(uint8_t* base) {
  if (!g_aim_seen) {
    return false;
  }
  const uint32_t tick = GameTick(base);
  return tick >= g_aim_tick && tick - g_aim_tick <= 1u;
}

/// SAC: the rotate through the camera's receiver, made whole — or, under a target lock, a flick.
/// Sets the update's account.
void HandToStringAndCage(PPCContext& ctx, CameraScript& script, uint8_t* base, uint32_t wrapper,
                         uint32_t record, float yaw, float pitch, Update& update) {
  update.factor = SensitivityFactor(base, record);
  const bool locked = TargetLocked(ctx, base, record);
  double speed = 0.0;
  const double rate = static_cast<float>(LoadF64(base, refii::guest::Address(0x83319660u)));
  if (!script.Number("RotateSpeed", &speed) || !(rate > 0.0) || !(speed > 0.0)) {
    update.mode = kModeNone;
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
    FlickTheLock(script, wrapper, yaw);
    g_refused = wrapper;
  } else if (x != 0.0f) {
    g_flick.spent = 0;
    g_flick.sum = 0.0;
    double before = 0.0;
    double after = 0.0;
    const bool had = script.Number("RotateRight", &before);
    script.Send(wrapper, kRotateCamera, x, 0.0f);
    if (script.Number("RotateRight", &after) && (!had || after != before)) {
      script.SetNumber("SmoothRotateRight", script.NumberOr("SmoothRotateRight", 0.0) + after);
      update.smooth_added = after;
      update.absorbed = static_cast<float>(-after);
      if (std::fabs(yaw) > 1e-3f) {
        update.factor = static_cast<float>(-after / yaw);
      }
      g_refused = 0u;
    } else {
      g_refused = wrapper;
    }
  }

  if (height) {
    script.Send(wrapper, kZoomCamera, 0.0f, y);
  }
  update.mode = g_refused == wrapper ? kModeNone : kModeOrbit;
}

/// First person: the rotate through the camera's receiver, then moved past its clamp and easing.
/// A camera bound to a target (OverriddenData.BoundTarget / BoundEntity: the yaw clamped about it,
/// or sprung back by SmoothXOffset) keeps its own path whole, and is drawn as it states it.
void HandToFirstPerson(CameraScript& script, uint8_t* base, uint32_t wrapper, uint32_t record,
                       float yaw, float pitch, Update& update) {
  double speed = 0.0;
  double smooth = 0.0;
  if (!script.Number("RotateSpeed", &speed) || !script.Number("RotateSmoothAmount", &smooth) ||
      !(speed > 0.0)) {
    update.mode = kModeNone;
    return;
  }

  // With no easing (RotateSmoothAmount 0) the smoothed value is the clamped raw one and nothing
  // can be added past it: the camera's own path stands then too.
  const bool own = smooth <= 0.0 || script.Overridden("BoundTarget") ||
                   script.Overridden("BoundEntity");
  update.factor = SensitivityFactor(base, record);
  if (yaw != 0.0f || pitch != 0.0f) {
    const double rx0 = script.NumberOr("RotateX", 0.0);
    const double ry0 = script.NumberOr("RotateY", 0.0);
    script.Send(wrapper, kRotateCamera, static_cast<float>(yaw * update.factor / speed),
                static_cast<float>(pitch * update.factor / speed));
    const double dx = script.NumberOr("RotateX", 0.0) - rx0;
    const double dy = script.NumberOr("RotateY", 0.0) - ry0;
    if (dx == 0.0 && dy == 0.0) {
      g_refused = wrapper;
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
  update.mode = own || g_refused == wrapper ? kModeNone : kModeInPlace;
}

}  // namespace

/// Before the guest's update of `manager` (r3 of rex_lhCameraManager_Update): hands the mouse's
/// pending turn to the camera that update is about to run.
extern "C" void RefiiMouseLookBeforeCameraUpdate(PPCContext& ctx, uint8_t* base, uint32_t manager) {
  g_update = Update{};
  if (AimHoldsTheMouse(base)) {
    return;
  }

  float yaw = 0.0f;
  float pitch = 0.0f;
  refii_bridge_mouse_look_take(&yaw, &pitch);
  g_update.open = true;
  g_update.taken = yaw;

  const uint32_t wrapper = ActiveCamera(base, manager);
  const uint32_t record = PlayerRecord(base, wrapper);
  if (!Plausible(wrapper) || !TakesInput(base, record)) {
    // No camera to turn, or the camera takes no input now: the turn is spent, not stored up.
    return;
  }

  CameraScript script(ctx, base, wrapper);
  if (!script.open()) {
    return;
  }
  g_update.wrapper = wrapper;
  g_update.kind = KindOf(script);
  if (g_refused != 0u && g_refused != wrapper) {
    g_refused = 0u;
  }

  switch (g_update.kind) {
    case Kind::kStringAndCage:
      if (refii_bridge_mouse_look_device() != 0) {
        script.Publish(kDrivenCamera);
        g_update.published = script.state();
      }
      HandToStringAndCage(ctx, script, base, wrapper, record, yaw, pitch, g_update);
      break;
    case Kind::kFirstPerson:
      HandToFirstPerson(script, base, wrapper, record, yaw, pitch, g_update);
      break;
    case Kind::kOther:
      break;
  }
}

/// After the update: takes the mouse's turn back out of the camera's easing, refreshes the live
/// camera, and states the account, stamped with the game tick the update ran on.
extern "C" void RefiiMouseLookAfterCameraUpdate(PPCContext& ctx, uint8_t* base, uint32_t manager) {
  if (!g_update.open) {
    return;
  }
  Update update = g_update;
  g_update = Update{};
  if (update.published != 0u) {
    ClearGlobal(ctx, base, update.published, kDrivenCamera);
  }

  // An override the update dropped (manager+0x3C cleared at 0x821b952c) is gone with its state.
  const bool current = update.wrapper != 0u && update.wrapper == ActiveCamera(base, manager);
  float pivot = 0.0f;
  {
    CameraScript script(ctx, base, current ? update.wrapper : 0u);
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

    const uint32_t live = Plausible(manager) ? RefreshLiveCamera(ctx, base, manager) : 0u;
    if (update.mode == kModeOrbit) {
      // The camera looks at RollingFocalPos from where it was placed: the turn's vertical axis
      // lies on the level line of sight, this far out.
      float focus[3] = {0.0f, 0.0f, 0.0f};
      if (script.open() && live != 0u && script.Vector("RollingFocalPos", focus)) {
        const float dx = focus[0] - refii::mods::LoadF32(base, live + 0u);
        const float dy = focus[1] - refii::mods::LoadF32(base, live + 4u);
        pivot = std::sqrt(dx * dx + dy * dy);
      }
      if (!(pivot > 0.05f && pivot < 200.0f)) {
        update.mode = kModeNone;
        pivot = 0.0f;
      }
    }
  }
  if (!current) {
    update.mode = kModeNone;
  }

  refii_bridge_mouse_look_state(GameTick(base), update.taken, update.held, update.absorbed,
                                update.factor, pivot, update.mode);
}

// The ranged aim's cursor move (r3 the aim, r4 its stick: x in the high word, y in the low).
// Everything the mouse turned since the last move joins the stick at the cursor's own gain; the
// camera follows the aim its own way, so the host draws no turn ahead of it.
REX_EXTERN(rex_lhRangedAim_MoveCursor) {
  REX_FUNC_PROLOGUE();
  const uint32_t tick = GameTick(base);
  g_aim_tick = tick;
  g_aim_seen = true;

  float yaw = 0.0f;
  float pitch = 0.0f;
  refii_bridge_mouse_look_take(&yaw, &pitch);
  if (yaw != 0.0f || pitch != 0.0f) {
    const uint32_t aim = ctx.r3.u32;
    const uint32_t cell = Plausible(aim) ? LoadGuestU32(base, aim + 4u) : 0u;
    const uint32_t entity = Plausible(cell) ? LoadGuestU32(base, cell) : 0u;
    uint32_t record = 0u;
    if (Plausible(entity)) {
      PPCContext work = refii::mods::Frame(ctx);
      work.r3.u64 = entity;
      if (refii::mods::CallGuest(work, base, refii::guest::Address(0x8219EA40u))) {
        record = work.r3.u32;
      }
    }

    const double rate = LoadF64(base, refii::guest::Address(0x83319660u));
    const double speed = Plausible(record) ? refii::mods::LoadF32(base, record + 0x38u) : 0.0;
    const double across = refii::mods::LoadF32(base, refii::guest::Address(0x83319514u));
    const double up = refii::mods::LoadF32(base, refii::guest::Address(0x83319518u));
    const double gain_x = rate > 0.0 ? speed * across / rate : 0.0;
    const double gain_y = rate > 0.0 ? speed * up / rate : 0.0;
    if (std::isfinite(gain_x) && std::isfinite(gain_y) && std::fabs(gain_x) > 1e-6 &&
        std::fabs(gain_y) > 1e-6) {
      constexpr double kRadians = 3.14159265358979323846 / 180.0;
      const float x = FloatOf(static_cast<uint32_t>(ctx.r4.u64 >> 32)) +
                      static_cast<float>(yaw * kRadians / gain_x);
      const float y = FloatOf(static_cast<uint32_t>(ctx.r4.u64)) +
                      static_cast<float>(pitch * kRadians / gain_y);
      ctx.r4.u64 = (static_cast<uint64_t>(BitsOf(x)) << 32) | BitsOf(y);
    }
  }

  refii_bridge_mouse_look_state(tick, yaw, 0.0f, 0.0f, 0.0f, 0.0f, kModeNone);
  __imp__rex_lhRangedAim_MoveCursor(ctx, base);
}
