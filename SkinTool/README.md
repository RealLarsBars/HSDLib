# skintool

Puts a model into Melee costume files without Blender. It runs on Linux, macOS and Windows (.NET 8),
using HSDRaw's core library only.

```sh
dotnet run --project SkinTool -- build \
  --model "Monty Mole/choropu.obj" \
  --fighter PlFx.dat \
  --dat PlFxNr.dat --dat PlFxOr.dat --dat PlFxLa.dat --dat PlFxGr.dat \
  --out out/
```

What `build` does, in the order the usual Blender + HSDRaw tutorial does it by hand:

1. **Fit**: scales the model to sit on the fighter's skeleton (between "as tall as the fighter" and
   "as wide as its arm span", but never wider than the arm span; `--scale` adjusts it, `--rotate`
   turns a model that faces the wrong way; `--rotate x,y,z` also stands up one that lies down or is
   upside down, in degrees about each axis; `--move forward,up` slides the weighted model forward or
   back and down, as shares of the fighter's height, for a model that sits too far forward, like Spyro
   with his long neck; it never goes up off the floor).
2. **Weights** (the Data Transfer step): copies weights from the fighter's own mesh, onto the bones
   modders weight custom skins to (the fighter's bone map in `BoneMaps.cs`: face, body, shoulders,
   elbows, hands, legs, knees and feet). Where a mapped limb bone isn't one the costume's own model
   follows, the joint stacked in the same spot that it does follow is used instead (only one of a
   stacked pair bends: Yoshi's right hip, Zelda's hips), or else the joint above it (a helper the game
   moves on its own, like Samus's cannon muzzle). Weights only come from the matching part of the fighter:
   the model's body takes the fighter's body, its arms (whatever sticks out sideways) the arms, and
   anything under its crotch the legs. A model with stubby legs gets its feet on the fighter's feet
   and its body on the hips. Arms that sit far from the fighter's arms partly stay with the body, so
   they don't get flung around the fighter's shoulders. Where the fighter's arms hang down in its bind pose
   (Ganondorf), a model's arms held out in a T-pose are bent down to hang the same way, from where they
   leave the model's body, and take their weights from the fighter's arms (they bend at its elbow and wrist, a
   separate hand goes with the sleeve it sits in, and they aren't stiffened to keep the hands off the floor). Weights are smoothed over the surface, cut to
   3 per vertex and normalized. Small separate pieces (claws, teeth, whiskers) move as one piece with
   the body around them. Above the neck everything follows the head alone (see Head below).
3. **Import**: one mesh per texture, CMP textures (RGB5A3 when they have transparency), triangle strips.
4. **Dummy meshes**: pads the costume back to its original mesh count, each dummy with as many
   textures as the mesh it replaces (some fighters have meshes with two or none). The model goes into
   single-texture slots, skipping slots the game animates (eye blinks) and, with `--fighter`, slots it
   hides (low-detail model).
5. **Keep the skeleton**: joints are never touched, including their flags. Every output is read back
   and checked against the original (`check` does the same for any skin), so it stays safe for Slippi.

`--model` takes an OBJ (with its MTL), or an FBX, DAE, glTF/GLB or 3DS file (read with Assimp). FBX
files are turned to stand up the way their up axis says; textures packed inside a file are written
out next to it. Positions at the same place are shared, so texture seams don't split the surface.

Every costume given with `--dat` gets the same fit and weights; costumes whose bones sit slightly
differently (Falcon's, Kirby's) get the model moved with their bones. `--textures PlFxGr.dat=green/`
swaps in PNGs from another folder (matched by file name) for one costume. `--model` can be a .zip of
the model and its textures, and an `--out` ending in `.zip` collects the costumes in one zip.

**Second copies.** Rips sometimes carry two versions of one part on top of each other (Gusteau's gameplay
and cutscene body and head). Drawn together they fight over which is in front and come apart in moves, so
only one is kept: the one not named for cutscenes, or else the first.

**Legs.** A model with no real legs (a round body on little feet) is found from its shape. Its body
works as legs: the bottom 30% (`--leg-band`) bends with the fighter's legs, left half with the left
leg, fading into the hips towards the top, the middle and the back. It's sized to 75% of the
fighter's height. Arms far below the fighter's mostly stay with the body. Small openings (the open
bases of claws and teeth) are closed, and every mesh is drawn from both sides, so bending never
shows holes.

**Head.** A head wider than the body (Squidward) would otherwise be read as arms and fan out with the
fighter's arms. The tool looks for a neck: a narrow spot in the upper half with a head above it at
least half as wide again and as wide as the body. Everything above that line follows the fighter's
head bone alone, blending in over a short band, so the head and face move as one piece. `--neck 0.6`
sets the line by hand (a share of the model's height), `--neck none` turns it off. The size check
(`--overlay`) draws the line dashed in yellow.

**Stretch check.** With `--fighter` and `--anims`, a build poses the first costume in the fighter's
test moves and measures how far each triangle's longest edge grows. When more than 2.5% of the skin's
area grows past 4x, which the original costumes and a well fitted model never get near, it prints a
`warning:` line naming the part and the move (`stretch <costume.dat> <Pl**.dat> <Pl**AJ.dat>` runs it
on any costume).

**Character select portraits.** `--portraits <folder>` (with `--fighter` and `--anims`) draws each
costume as a 136x188 portrait with a see-through background, as community skins ship them
(`PlFxNr_csp.png`, also put next to the costumes): the fighter standing (Wait1), turned towards the
camera, cropped to the head and upper body, flat lit, with a soft drop shadow up and to the left.
Peach's items and Ganondorf's sword stay hidden. `portrait <out.png> <costume.dat> <Pl**.dat>
<Pl**AJ.dat> [--anim Wait1] [--at 0] [--yaw -30]` draws one in another pose.

**Rules per model.** When the automatic choice is wrong, say how the model works, on the command
line or in a `skin.txt` next to the OBJ (or in the zip):

```
legs = body       # auto | separate | body | cut (cut: two legs down the fighter's legs)
arms = auto       # auto | fighter (always onto the fighter's arms) | body
neck = auto       # auto | none | 0.6 (where the head starts, as a share of the height)
scale = 1.1
width = 1.0       # stretch sideways only
height = 1.2      # stretch upwards only (from the floor)
rotate = 180
leg-band = 0.3
```

**Weapons.** Peach's golf club, frying pan and tennis racket, the swords of Marth, Roy, Link, Young
Link and Ganondorf, the Links' shields and the Ice Climbers' hammers are rigid meshes on a weapon
bone that the fighter's data file switches on and off (`weapons <costume.dat>` lists them). The
body never goes in those slots, and each costume keeps its own weapon unless one is given:
`--weapon pan=pan.obj` (or a zip) fits the model over the original, its longest side along the
original's, scaled to the same length, its wide end (a pan's head, a sword's guard) at the original's
wide end. `--weapon-flip pan` turns it end for end if it comes out backwards. The preview, given
`--fighter`, adds a row with each weapon in the hand.

**Other shapes.** Samus's morph ball (and her curled-up body), Yoshi's egg and Kirby's stones are
shapes the game shows instead of the body. A skin keeps the costume's own, so the fighter doesn't
vanish in down-B.

**Picking and sizing.** A zip or folder may hold several models: `models <zip> --out <folder>`
lists them with a picture of each (`<n>.png`), and `--pick <path inside>` (`--weapon-pick
pan=<path>`) chooses one. `--overlay size.png --fit-only` draws the fitted model over the first
costume without building, to check its size and turn.

Other commands:

- `export <out.obj> <skin.dat> [--fighter Pl**.dat]`: writes a finished skin's model back out (OBJ, MTL and
  textures, in its bind pose) to build it again with a newer skintool when the model it came from is gone.
  With the fighter's data, its weapons go in their own OBJs beside it (`sword.obj`, for `--weapon`).
- `check <original.dat> <skin.dat>`: same skeleton, flags and mesh count; weights valid.
- `preview <out.png> <costume.dat>... [--anims Pl**.dat Pl**AJ.dat]`: renders each costume in test
  poses, or in the fighter's real animations when given its data and animation files.
- `preview ... --bones` colours the model by the bones it follows.
- `bonemap <costume.dat>`: where the fighter's bone map bones sit (left and right should mirror), and
  which of them the costume's model doesn't follow (and what's used instead).
- `preview ... --anims <Pl.dat> <PlAJ.dat> --poses AttackAirB@0.25,AttackAirF@0.3`: draws chosen moves.
- `inspect <costume.dat>`, `slots <Pl**.dat>`: list joints, meshes and visibility tables.
- `preview ... --slots 55,56` draws only those mesh slots, to see what a slot holds.

## Legs pressed together

A model whose legs touch all the way up (Peach's overalls in Super Mario Odyssey) never shows a gap between
them, but its lower body is a slim column (under 12% of its height to each side) where a blob on feet is wide
right down to them. Its legs then run three quarters of the way up that column. They aren't smoothed into each
other, and below the knees the triangles joining one leg to the other are left out, so a stride doesn't pull
them into a sheet.

## Animelee style

`--style animelee` (or `style = animelee` in skin.txt) gives a skin the cel-shaded Animelee look. A black
outline is added: a copy of the model pushed out a little along its normals, with its faces wound inward,
drawn from one side only so just its rim shows. Its points move with the nearest model point. The
materials are drawn without the stage's lighting, and the textures' colours are flattened into a few
bands. Cut-out parts (lashes, hair cards) get no outline, and a model with its own outline keeps it.
The outline doubles the triangle count.

`skintool animelee <skin.dat|skins.zip|folder> --out <out.dat|out.zip|folder>` does the same to finished
costume files, made by hand or by this tool. Every mesh slot stays where it is: each mesh's outline is
added inside its own slot, drawn from one side only, with its texture coordinates on an 8x8 patch of the
mesh's texture that no part of the mesh uses, painted black. So the game's visibility tables still line
up, and a part the game hides (a sword in its sheath) takes its outline with it. Meshes with blinking-eye
texture animations, two textures, see-through textures or no free patch keep no outline, but still get
flat lighting. Non-costume files in a zip (portraits) are copied as they are.

## Leftover layers and closed eyes

A small part with a plain grey texture lying on another part (the shadow over Peach's eyes) is blended in
by the game it came from; drawn solid it covers what's under it, so it's left out. Super Mario Odyssey rips
come with the face's eyes in one expression (Peach's shut in a smile) and each expression's eye area in its
own file: when `Name_LEye.SMD` and `Name_REye.SMD` sit next to the model, their open eye areas are stitched
into the face in place of its own, and the eyelashes of the other expressions are left out.

## Pieces that touch

A model made of separate pieces (a shirt with the arms as their own meshes, legs inside a pair of shorts)
keeps them joined: where a point of one piece touches another (within 1% of the model's size), both sides
take the same weights, and the weights around the join ease into them, so the join can't open in any pose.
Small details that move as one piece (buttons, claws) and tails are left alone. `--attach no` (or
`attach = no` in skin.txt) turns it off, for loose pieces meant to swing on their own.

## Tail

`--tail yes` (or `tail = yes` in skin.txt) says the model has a tail. Whatever sticks out behind the
model, at heights where it reaches much further back than usual, follows the fighter's tail bones from
root to tip (Fox, Falco, Pikachu, Pichu, Mewtwo, Yoshi; `BoneMaps.Tails`), blending in from the body at
the root. Without it, everything behind the model follows the body, as before.

