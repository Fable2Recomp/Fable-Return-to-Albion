# Cutscenes

Knowing when the game has taken a hero's camera and controls away from free play — and leaving LT
to the game while it has.

## Contents

- [Three statements](#three-statements)
- [The event and the query](#the-event-and-the-query)
- [Why these](#why-these)
- [In combat](#in-combat)
- [Co-op](#co-op)
- [The look-at button](#the-look-at-button)
- [The camera](#the-camera)
- [Hidden parts](#hidden-parts)

## Three statements

The game states it three ways; Refii reads each where the game's own setter writes it.

| Bit | Name | The game's statement |
|---|---|---|
| 1 | `REFII_CUTSCENE_FULL` / `Refii.CUTSCENE_FULL` | a full cutscene is playing: what `GUI.IsFullCutscenePlaying` answers. Every player's (`player` 0) |
| 2 | `REFII_CUTSCENE_INTERACTIVE` / `Refii.CUTSCENE_INTERACTIVE` | a foreground interactive cutscene holds the hero: `Player.SetForegroundCutsceneInEffect`. The shipped scenes set it with the vignette and the expression-only HUD when a non-background scene starts, and clear it when the last one ends |
| 4 | `REFII_CUTSCENE_LOOK_AT` / `Refii.CUTSCENE_LOOK_AT` | the game has given the hero's HELD LT a camera: the LOOK_AT scope of `Player.SetOverrideCameraMode`, the native under `CameraManager.SetCameraOverride`. Set by a conversation's talking camera, a quest's `SetLookAtCamera` and a scene's camera records |

## The event and the query

| Surface | Event | Query |
|---|---|---|
| Native | `Cutscene` | `refii_mod_in_cutscene(ctx, base, player)` → the bits holding for player 1 or 2 (0: either) |
| Lua | `Refii.on("cutscene", fn)` | `Refii.in_cutscene([p [, kinds]])` → inside, kinds |
| Godot | signal `cutscene` | `in_cutscene(player = 0, kinds = 7)`, `cutscene_kinds(player = 0)`, `query in_cutscene P` |

The `cutscene` event (100) fires per kind on each edge, before `tick`: `player` (1 or 2; 0 for a
full cutscene), `active`, `kind`, `kinds` (every bit holding for the player after the edge),
`hero`. It is read only while someone listens: a handler subscribed mid-scene hears it start on
its first tick.

```lua
Refii.on("cutscene", function(e)
  if e.active and e.kind == Refii.CUTSCENE_LOOK_AT then Refii.release_lock(e.player) end
end)

local inside, kinds = Refii.in_cutscene(1)                       -- any kind, player one
local talking = Refii.in_cutscene(0, Refii.CUTSCENE_LOOK_AT)     -- either player, look-at only
```

A Godot script's `in_cutscene` answers from the event as this host last heard it (heard while any
script is loaded); `query in_cutscene P` asks the game now and answers `kinds`.

## Why these

The question a mod asks is "is LT the game's right now?". The shipped camera scripts answer it
with the LOOK_AT override: while LT is held, a held LT frames what the scene or quest chose — the
speaker, the point of interest — instead of the normal camera. The interactive and full statements
cover the scenes that hold the hero with no LT camera of their own.

## In combat

A LOOK_AT override counts whether or not it supersedes combat (that flag lives in the camera
data's Lua table, never passed to the game). The shipped camera keeps the normal camera in combat
for an override without it, so there a held LT does what it always did. A mod that steps aside
under `in_cutscene` leaves LT exactly as the shipped game has it.

## Co-op

Each machine reads its own game. The two hero statements are per player, so player two's are
player two's hero's. Online, whether the joiner's game runs a scene's script for its own hero (and
so states INTERACTIVE / LOOK_AT there) is unproven; the full cutscene is the state of the machine
that reads it.

Souls Combat steps aside on it: while `Refii.in_cutscene` holds for a hero, LT's press toggles
nothing, the look-at flag passes through as the game writes it, and the rising edge releases any
lock (`mods-src/souls_combat/mod/lockon.lua`).

## The look-at button

`look_at_button` (94) is raised before the game writes a player hero's look-at flag — the byte
every lock camera script reads, what a held LT states — with `down` writable and `cancel` leaving
the flag as it was. Its writers are the camera's LT-down event (1), the hero's release handler (0)
and the API's own `lock_target` / `release_lock`. So a mod whose lock is its own (Souls Combat's
toggled LT) writes `e.down = locked` and the flag equals its lock whoever asks, with no hook of
its own.

Seam: `rex_CPlayer_SetLookAtButtonDown`.

## The camera

Each player's camera is the game's own camera manager: every frame it walks its sets — debug,
the overrides scenes and quests state, the co-op set, the normal set (canned actions, first person,
the aim cameras, the third-person camera) — and activates the first valid one's best camera.

| Surface | The mode | The hold | The field of view |
|---|---|---|---|
| Native | `refii_mod_camera_mode(player)` · `CameraChanged` | `refii_mod_camera_override(player, mode, fov, pitch_limit, blend, flags)` | `refii_mod_camera_fov(base, player)` |
| Lua | `Refii.camera_mode(p)` · `Refii.on("camera_changed", fn)` | `Refii.camera_override(p, mode [, opts])` | `Refii.fov(p)` |
| Godot | `host.camera_mode(player)` · signal `camera_changed(e)` | `host.camera_override(player, mode, options)` | `host.fov(player)` |

The mode is the game's ECameraMode (`Refii.CAMERA_*`): 2 `CAMERA_GAME` the third-person camera, 3
the gun aim, 4 the magic aim, 7 an interactive scene's camera, 8 `CAMERA_FIRST_PERSON` (the game's
own first-person look and a hold alike), 9 a finishing move's, 10 a canned action's (a chest, a
door, a ladder). -1 before the player has a camera, and for a player whose camera runs on the
other machine of an online game.

**The hold** is the game's own per-player camera override, the one the quests use for their
first-person shots. `mode` is `CAMERA_FIRST_PERSON` or `CAMERA_GAME`; nil or 0 lets it go. The
options are what the game's cameras read from an override:

| Option | Meaning |
|---|---|
| `fov` | degrees (the horizontal angle the game's cameras state); absent, the camera's own (first person 75) |
| `pitch_limit` | degrees above and below level the view may turn; absent, the camera's own (first person 55) |
| `blend` | seconds to blend in and out; absent, the camera's own |
| `aim` | true: while the player aims a gun, the game's aim camera takes over |

The eye is the game's — first person sits at the head's collision point — and no option moves it.
While a player's mode is `CAMERA_FIRST_PERSON` Refii draws that player's hero without its head,
hair and hat (the body and the shadow keep theirs), so a mod needs no `hide_part` for it. Only that
player's view loses it: on split screen the partner's half still draws the head (see
[Hidden parts](#hidden-parts) for how one view is drawn differently).
Whatever the mode, a hero is drawn at the opacity the game's cameras fade it to
(`GraphicAppearance.SetCameraAlpha`: the shipped first-person camera, a scene's point-of-view shot,
a camera pulled in by a wall), hair and hats included, and at 0 only its shadow shows; a first
person that keeps the body in view leaves the shipped camera's fade out.
The hold holds in combat. It yields to the game:

- a scene or quest that states its own camera override replaces it; the hold comes back once theirs
  is released, and after a level or save load;
- a full cutscene and the canned-action cameras (opening a chest, digging, a ladder, a door, a
  finishing move) suspend it;
- it is never saved: the save's camera record is written without it, so a save made in first person
  loads in the game's own camera with or without the mod;
- a held look-at half is the mod's, not a cutscene: `in_cutscene` leaves it out.

Holds are cleared when the mods' Lua loads (each session), so state one from `init.lua` or on
`lua_ready`. In split screen each player holds their own.

```lua
Refii.camera_override(1, Refii.CAMERA_FIRST_PERSON, { fov = 80, pitch_limit = 75, aim = true })
Refii.on("camera_changed", function(e)
	Refii.log("player " .. e.player .. " camera " .. e.mode)
end)
```

**The field of view** is the player's live camera's, read where their view is drawn from. Every
camera writes its own field of view each update, so there is no per-player field of view to set:
a hold's `fov` is the way to state one.

## Hidden parts

`hide_part(entity, part, on [, player])` hides a part of an entity in the drawing: `part` is a bone
of the entity's rig by its rig name, the same in either title (`"Shadow_Neck"`; Fable 3 names its
bones by the name's hash, and is matched that way), and the bone and every bone under it — for the
neck, the head, the face, the hair and the hat — is drawn at nothing after the game has posed it.
Any entity with a rig, not only the heroes. Drawing only: the pose the game holds is untouched, and
the shadow keeps the part. Godot keeps it per entity handle, across the entity's rebuilds, until it
is shown again or the mods' Lua loads again (`lua_ready`).

| Surface | Call |
|---|---|
| Native | `refii_mod_hide_part(entity, part, on)` · `refii_mod_hide_part_for(entity, part, on, player)` |
| Lua | `Refii.hide_part(e, part, on [, p])` |
| Godot | `host.hide_part(handle, part, on, player = 0)` |

**Whose view.** `player` 0 (or absent) hides the part in every view. 1 or 2 hides it only in the
view drawn for that player:

| Screen | `player` 1 or 2 |
|---|---|
| Split screen | that player's half alone; the partner's half still draws the part |
| One view, alone | hidden for player 1 (the view is player one's); player 2's hide draws nothing differently |
| One shared couch screen | hidden for either player: the one view is both players' |
| Online | each machine draws its own player's view: the host hides player 1's parts, the joiner player 2's. A mod's code runs on both machines, so each hides what its own player should not see |

Each part and player is its own statement: `hide_part(e, "Shadow_Neck", true)` and
`hide_part(e, "Shadow_Neck", false, 1)` leave the neck hidden in every view. The first-person head
composes with them: a hero in its player's first person loses the head in that view and any part
hidden for that player besides.

```lua
-- Player one's own head out of player one's view; player two's half still draws it.
Refii.hide_part(Refii.player_hero(1), "Shadow_Head", true, 1)
```

**How a split view differs.** Both halves render the one world from one skeleton, so a pose cannot
differ between them. A part hidden in every view, or in both players' views, is shrunk on the pose
(nothing extra is drawn). A part hidden in one player's view alone stands a second copy of the body
on the same skeleton, whose skin draws that view's parts at nothing, in that view only, and the body
itself is drawn only in a view that hides nothing of its own — two copies and no body when each
player hides something. The copies light as the body does (indoors too) and cast no shadow of their
own; they go when nothing per view is hidden or the screen rejoins. With nothing hidden, nothing is
added.
