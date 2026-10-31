# First Person

Play Fable II from the hero's eyes. Walk, fight and look around with the mouse or the right stick.
Your body stays in view when you look down; the head (with its hair and hat) is hidden so it never
fills the screen. **H** switches between first and third person (rebind it in Options › Controls).

Lua and one Godot script: a soft mod, nothing is compiled.

## How it works

| Piece | File | What it does |
|---|---|---|
| The camera | `mod/camera.lua` | The game ships a first-person camera (`camera/firstpersoncamera.lua`, `FirstPersonCamera`): the stand-still "look around" view. While the mod is on, its `IsValid` holds, so the camera manager picks it over the third-person `SACCamera`. Full cutscenes, canned actions and the co-op cameras still come first, since they sit earlier in the manager's lists. It also gives way during a full cutscene and, by default, to the over-the-shoulder gun aim. Conversations and interactive scenes stay in first person: the scene's look-at and framed-shot overrides (`SimpleCamera`, `SACCamera` in the manager's override set) become this camera, which faces what they frame (the speaker's mouth) until you look away. Only the look input turns it (the shipped look mode also turned with the left stick, which here walks). The eye sits a little above and in front of the head, and the shipped body fade is left out. Refii's mouse look already drives this camera. |
| The switch | `mod/init.lua`, `input.json` | `first_person.toggle` (H) flips `FirstPerson.On`, which the camera reads every update. |
| The head | `godot/hide_head.gd` | A `SkeletonModifier3D` on the hero's body skeleton shrinks the `Shadow_Neck` bone, and with it `Shadow_Head` and every face bone, to nothing after the game has posed it. Shrinking only the head left the neck-weighted skin stretched up under the camera, visible at a wide field of view. This affects drawing only. It acts only while the camera is within `head_radius` of the head, so third person shows the head as usual. The shadow has its own skeleton, so it keeps its head. |

The game's own first-person button (Z / middle mouse) keeps its shipped behaviour.

## Settings (Options › Mods › First Person)

| Setting | Default | Meaning |
|---|---|---|
| `start_on` | 1 | start each session in first person |
| `fov` | 80 | field of view, 50–120 degrees, applied live (the shipped first-person view is 75) |
| `eye_height`, `eye_forward` | 0.05, 0.12 | metres above and in front of the head's collision point |
| `pitch_limit` | 75 | how far up or down you can look |
| `face_camera` | 1 | turn the hero to the view: 0 never, 1 while standing, 2 always |
| `third_person_aim` | 1 | gun aim uses the game's over-the-shoulder camera |
| `scenes` | 1 | stay in first person in conversations and interactive scenes, facing the speaker |
| `head_radius` | 0.6 | (not on the page) how close the camera must be for the head to hide |

## Iterating

The launcher packs this folder into `assets/mods/first_person.f2mod` on start. With the game
running, the Studio's Live Game tab (or the debug op `mods-remount` with `lua=true`) reloads the
Lua and the Godot script; the camera wrappers are reinstalled on every run for that reason.
