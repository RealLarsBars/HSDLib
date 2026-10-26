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
   turns a model that faces the wrong way).
2. **Weights** (the Data Transfer step): copies weights from the fighter's own mesh, onto the bones
   modders weight custom skins to (the fighter's bone map in `BoneMaps.cs`: face, body, shoulders,
   elbows, hands, legs, knees and feet). Weights only come from the matching part of the fighter:
   the model's body takes the fighter's body, its arms (whatever sticks out sideways) the arms, and
   anything under its crotch the legs. A model with stubby legs gets its feet on the fighter's feet
   and its body on the hips. Arms that sit far from the fighter's arms partly stay with the body, so
   they don't get flung around the fighter's shoulders. Weights are smoothed over the surface, cut to
   3 per vertex and normalized. Small separate pieces (claws, teeth, whiskers) move as one piece with
   the body around them.
3. **Import**: one mesh per texture, CMP textures (RGB5A3 when they have transparency), triangle strips.
4. **Dummy meshes**: pads the costume back to its original mesh count, each dummy with as many
   textures as the mesh it replaces (some fighters have meshes with two or none). The model goes into
   single-texture slots, skipping slots the game animates (eye blinks) and, with `--fighter`, slots it
   hides (low-detail model).
5. **Keep the skeleton**: joints are never touched, including their flags. Every output is read back
   and checked against the original (`check` does the same for any skin), so it stays safe for Slippi.

Every costume given with `--dat` gets the same fit and weights; costumes whose bones sit slightly
differently (Falcon's, Kirby's) get the model moved with their bones. `--textures PlFxGr.dat=green/`
swaps in PNGs from another folder (matched by file name) for one costume. `--model` can be a .zip of
the OBJ, MTL and textures, and an `--out` ending in `.zip` collects the costumes in one zip.

Other commands:

- `check <original.dat> <skin.dat>`: same skeleton, flags and mesh count; weights valid.
- `preview <out.png> <costume.dat>... [--anims Pl**.dat Pl**AJ.dat]`: renders each costume in test
  poses, or in the fighter's real animations when given its data and animation files.
- `preview ... --bones` colours the model by the bones it follows.
- `bonemap <costume.dat>`: where the fighter's bone map bones sit (left and right should mirror).
- `inspect <costume.dat>`, `slots <Pl**.dat>`: list joints, meshes and visibility tables.
