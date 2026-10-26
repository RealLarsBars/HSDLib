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
   "as wide as its arm span"; `--scale` adjusts it, `--rotate` turns a model that faces the wrong way).
2. **Weights** (the Data Transfer step): copies weights from the fighter's own mesh. The model is
   stretched over the fighter's body for the lookup only, so hands find hands and feet find feet
   whatever the proportions. A model with stubby legs gets its feet on the fighter's feet and its
   body on the hips. Small bones (eyes, fingers) and everything hanging off the head (ears, jaw) are
   folded into their parents, so faces only follow the head. Weights are smoothed over the surface,
   cut to 3 per vertex and normalized. Small separate pieces (claws, teeth, whiskers) move rigidly
   with the bone the body around them follows.
3. **Import**: one mesh per texture, CMP textures (RGB5A3 when they have transparency), triangle strips.
4. **Dummy meshes**: pads the costume back to its original mesh count, skipping slots the game
   animates (eye blinks) and, with `--fighter`, slots it hides (low-detail model).
5. **Keep the skeleton**: joints are never touched, including their flags. Every output is read back
   and checked against the original (`check` does the same for any skin), so it stays safe for Slippi.

Every costume given with `--dat` gets the same fit and weights. `--textures PlFxGr.dat=green/` swaps in
PNGs from another folder (matched by file name) for one costume.

Other commands:

- `check <original.dat> <skin.dat>`: same skeleton, flags and mesh count; weights valid.
- `preview <out.png> <costume.dat>... [--anims Pl**.dat Pl**AJ.dat]`: renders each costume in test
  poses, or in the fighter's real animations when given its data and animation files.
- `inspect <costume.dat>`, `slots <Pl**.dat>`: list joints, meshes and visibility tables.
