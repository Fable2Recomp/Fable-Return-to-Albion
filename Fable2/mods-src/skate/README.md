# Skateboard

Ride a skateboard through Albion, and score tricks.

| | Keyboard | Pad |
|---|---|---|
| Step on or off | K | LB + Back |
| Push, brake | W, S | left stick up, down |
| Steer; spin in the air | A, D | left stick left, right |
| Hop | Space | A |
| Flourish | left mouse button | X |
| Grab, held | right mouse button | B |
| Hobbe's Wheelie, held | Shift | RB |
| Rail Rider | F | Y |
| Show or hide these controls | N | LB + D-pad up |

A Flourish or Grab from the ground hops first. Hold a direction as you press: nothing or left is a
Flourish or Pie Grab, right a Back-Flourish or Hero's Grip, back a Cheeky Shove or Balverine's Tail,
forward The Improbable or Gargoyle's Nose.

Hills speed you up and slow you down, and a wall met at an angle turns you along it.

Land with the board within 40 degrees of the way you're going, either way round, with the Flourish
done and the Grab let go, and the combo banks: its points times its count of tricks. Up to 70 degrees,
a Grab still held or a Flourish nearly done is a close call, for half the points and a good deal of
your speed. Anything worse throws you.

A Hobbe's Wheelie, held on the ground or into a landing, keeps a combo going instead of banking it:
keep the marker in the middle with W and S. For a Rail Rider, press F in the air close to any ledge
running your way, and keep balance with A and D. Space pops off, and you pop off by yourself when the
ledge runs out. Losing balance on either throws you. Hold a Rail Rider for 2.5 seconds on one ledge
and it becomes a Guild Grinder, worth 1000 points more.

On a phone the **Skate** pill in the top row of the touch controls is K, and the move stick steers.
While you ride, the touch controls' buttons are the pad's tricks and say so: A Hop, X Flourish,
B Grab, Y Grind, and a **Wheelie** pill stands beside Skate.

## How it works

`mod/init.lua` plants the hero's feet with the movement-speed override, turns gravity off and moves
the hero each tick from its own heading, speed and height, with `Refii.line_of_sight` finding the
ground and walls. Being thrown is `Refii.knockback`. A Rail Rider looks for steps in the ground with
rays, so it works anywhere and can misjudge a rough collision shape.

`godot/skate.gd` draws the board, its flips and grabs and the score, and reads the stick. A
`SkeletonModifier3D` there poses the hero's body after the game has, for drawing only, so a crouch, a
grab and a push need no animation of their own.

The sounds are `sounds/skate/*.wav`, built by `source/make_sounds.py`. The model is
`art/inventory/weapons/skateboard/skateboard.mdl`, built by `source/make_model.py` and compiled
against the Master Flintlock Rifle. All the art is the mod's own.
