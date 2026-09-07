# Orbit Navigator New Tab visual v6 provenance

Generated on 2026-08-19 for the Orbit Navigator repository using Codex's
built-in ImageGen workflow. The art is original and project-bound. It contains
no stock or third-party imagery, telemetry or browsing-history input, user
content, advertising, or tracking material.

The original generated sheets are retained under `assets/new-tab/source-v6/`.
Runtime files are deterministic derivatives created by
`scripts/Build-NewTabV6Assets.py` with Pillow/Lanczos. The builder does not
invent, interpolate, rotate, or translate animation frames: each runtime
celestial state comes from a distinct generated source cell.

## Runtime package manifest

Only these six v6 files are runtime content. Keep the existing v5, v4, and v3
files as compatibility fallbacks.

| Relative path | Dimensions | Alpha-zero / partial / opaque pixels | SHA-256 |
| --- | ---: | ---: | --- |
| `assets/new-tab/orbit-deep-space-base-v6-a.png` | 2560x1440 | 0 / 0 / 3,686,400 | `EA4BC45E1D172BB68BBED7C5B218FAFC40EE9BBBC191C5BACF67DA7308700BC6` |
| `assets/new-tab/orbit-deep-space-base-v6-b.png` | 2560x1440 | 0 / 0 / 3,686,400 | `5ACACF0A1E5C0220360EF008F9CE711AF1B3F3E76221E578DC12B98ED09A6BE6` |
| `assets/new-tab/orbit-deep-space-base-v6-c.png` | 2560x1440 | 0 / 0 / 3,686,400 | `4225952EFA62BB23012A1DF9E75BC1698C09506B3D1A6AF0B9469054443CDE44` |
| `assets/new-tab/orbit-celestial-frames-atlas-v6.png` | 2048x1024 (8x4; 256px cells) | 1,512,645 / 583,375 / 1,132 | `99F79001983406D8333ABD811B80FFC89C0E019D1DD0E137CD0FBC8CA58D3C46` |
| `assets/new-tab/favorite-star-solar-atlas-v6.png` | 1024x1024 (4x4; 256px cells) | 648,748 / 399,826 / 2 | `52B16657F411468BD5C826F3ED9831A34F3F1D16629400E5FA44FFBE364870D8` |
| `assets/new-tab/workspace-star-solar-atlas-v6.png` | 1024x1024 (4x4; 256px cells) | 585,214 / 463,362 / 0 | `714E036528A75EA8040C04432A4F946025F1EA1BB17B919460625993AAD62C83` |

The three opaque files are background scene plates. Every atlas cell has a
fully transparent outer safety ring, cleared RGB where alpha is zero, and one
connected visible subject at the acceptance alpha threshold. There is no black
matte, opaque corner, detached flare, or separately floating fragment.

## Original generated sources

| Relative source | Dimensions | SHA-256 |
| --- | ---: | --- |
| `source-v6/orbit-deep-space-source-v6-a.png` | 1536x1024 | `059D6B43C604732A20B4EEED407E484660EB8B42D6BCD613963F7C2F2A03FABD` |
| `source-v6/orbit-deep-space-source-v6-b.png` | 1536x1024 | `5E0171EBE6FCC34AC9D127738164F1D451A10247808E02613ABEAAE79C55DF46` |
| `source-v6/orbit-deep-space-source-v6-c.png` | 1536x1024 | `30613F33D9B453B8E6E3575AD53DFC319B5EC78BB80D6050BDA8E482AD9918B8` |
| `source-v6/orbit-comet-frames-source-v6.png` | 1536x1024 | `09A2D55D700A50AB61DA5A4E3D4971F7B1DFEC7777B116FE083E1808E55A2CA9` |
| `source-v6/orbit-planet-frames-source-v6.png` | 1536x1024 | `4F2F6FB42C3E6937C006886FCFCC5924CEA73041E5FFE79DB5942917A5B79B0D` |
| `source-v6/orbit-galaxy-wave-frames-source-v6.png` | 1536x1024 | `66D1F89C29DD4C5334FB622E16DD04AB63E5897192127B0EA3C10555A00A1472` |
| `source-v6/orbit-star-glint-frames-source-v6.png` | 1536x1024 | `DEBD4683AA776F1159E0C52D905AEE83C0307B4DF0BE8938F99C56C083654F53` |
| `source-v6/favorite-star-solar-source-v6.png` | 1536x1024 | `9E7027D14480BB8AA62BDA82C8AEC00D7ADF10F089560EF6485278B38CBBBCDC` |
| `source-v6/workspace-star-solar-source-v6.png` | 1536x1024 | `9E1BF4DF55916B6BCBA199644AFDF477314398089858A59EE3EC6BA09A88C91D` |

## Final ImageGen direction

### Scene variants A, B, and C

```text
Create an original premium Orbit Navigator New Tab deep-space scene in 16:9.
Use near-black indigo space, fine natural micro-stars, and restrained cyan,
violet, and waypoint-gold nebular depth confined primarily to the perimeter.
Keep the central 60% and lower middle deliberately quiet, dark, and low-detail
for an upright browser logo, search, text, and cards. Produce a cinematic,
dimensional navigation atmosphere, not a wallpaper pulse or screensaver.
No text, logo, UI, panels, spacecraft, planet, comet, large foreground object,
radial burst, symmetric curtain, frame, watermark, or black-rectangle artifact.
Create three compositionally distinct variants with the same visual language.
```

### Favorite/bookmark stellar sequence

```text
Create an exact 4-column by 4-row transparent sprite sheet containing sixteen
forward sequential frames of the same realistic golden-orange star. Keep the
photosphere centered and constant in scale and total emitted light. Show
clearly evolving granular plasma, attached solar flare loops, flame licks, and
corona tongues; motion must be local shape evolution, not global brightness
pulsing and not A-B-A. Read left-to-right, top-to-bottom as a coherent loop.
Every complete star and flare must remain inside its cell with generous alpha
margin. Exactly one connected stellar subject per frame. No detached sparks,
cropping, ring, badge, icon, label, grid, matte, disc, opaque cell, or corner.
```

### Workspace/tab-group stellar sequence

```text
Create an exact 4-column by 4-row transparent sprite sheet containing sixteen
forward sequential frames of the same realistic blue-white star with violet,
cyan, and restrained warm-gold plasma. Keep its photosphere centered and
constant in scale and total emitted light. Show clearly evolving turbulent gas,
attached prominence loops, flame licks, and corona tongues—not brightness
pulsing and not A-B-A. Read left-to-right, top-to-bottom as a coherent loop.
Every complete star and flare must remain inside its cell with generous alpha
margin. Exactly one connected stellar subject per frame. No detached sparks,
cropping, ring, badge, icon, label, grid, matte, disc, opaque cell, or corner.
```

### Comet, planet, galaxy-wave, and glint sequences

```text
For each requested celestial element, create an exact 4-column by 4-row
transparent sheet with sixteen sequential states of one coherent complete
subject. Comet: stable icy nucleus with attached evolving ion and dust tail.
Planet: original ringed blue gas world with changing atmospheric bands and
ring illumination. Galaxy wave: localized blue-violet spiral/aurora band whose
dust and shape evolve. Glint: one connected four-point astronomical glint whose
rays grow, bend, and resolve. Keep scale/centroid stable, preserve transparent
padding, and make the loop forward rather than palindrome. Shape and texture
must visibly evolve; do not simulate animation by opacity, translation, or
rotation alone. No detached pieces, crop, label, grid, matte, black disc,
opaque cell, square backdrop, corner alpha, or watermark.
```

## Deterministic processing and measured checks

- Background variants are center-cropped to 16:9, resized once to 2560x1440,
  gently darkened, and minimally sharpened. Their declared quiet-center region
  (`x=28-72%`, `y=16-58%`) measures mean luminance 4.88/7.32/6.58 and p95
  7/10/12 respectively (0-255 scale).
- Solar frames are centered and scaled from the detected photosphere, constrained
  to a 16px transparent safety margin, reduced to the largest attached alpha
  component, and equalized by alpha-weighted integrated luminance. Runtime
  luminance spread is 0.447% for Favorite and 0.479% for Workspace.
- The celestial runtime atlas is element-major: rows are comet, planet,
  galaxy-wave, and glint; each row has eight distinct generated frames. Minimum
  adjacent-frame pixel-change rates at a 12-level BGRA threshold are 11.98%,
  13.17%, 41.36%, and 15.59% respectively.
- `NewTabV6VisualMotionTests` validates dimensions, quiet-center percentiles,
  transparent margins, one connected subject per cell, frame uniqueness,
  material temporal change, luminance stability, v6 runtime resolution,
  hover-freeze registration, private palette, and reduced-motion behavior.

## Runtime, accessibility, and fallback requirements

- All bitmaps and crops are decoded, cached, and frozen once. Per-frame work is
  limited to transforms, dash offsets, opacity, and two cached-frame draws.
- All New Tab motion uses `OrbitSharedVisualMotionClock`: one process-wide
  `CompositionTarget.Rendering` handler capped at 24 fps. Loaded, visible,
  non-minimized, eligible surfaces subscribe; hidden, minimized, frozen,
  reduced-motion, reduced-noise, and High Contrast layers unsubscribe.
- `FreezeNonStellarMotion` is the hover/focus seam. It freezes and unregisters
  the scene, celestial overlay, rings, and satellite without a resume jump; the
  independently registered complete solar star continues to evolve.
- Reduced motion chooses deterministic static frames. Reduced visual noise and
  High Contrast suppress decorative raster layers; High Contrast uses
  `SystemColors` and the existing code-native shape/status cues. Private mode
  adds restrained scarlet/violet-adjacent code-native accents without changing
  those fallbacks.
- Scene and celestial layers are decorative, upright, non-focusable, and
  non-hit-testable. `OrbitStellarOrbitVisual` retains explicit image name/help;
  host action buttons remain responsible for their action names.
- Runtime fallback order must remain v6 -> v5 -> v4 -> v3 (and v2 where the
  existing primitive supports it). Do not delete or rename earlier packaged
  assets in this integration.
- `source-v6/`, this provenance file, and `Build-NewTabV6Assets.py` are build
  inputs/audit material, not runtime App content.
