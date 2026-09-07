# Orbit Navigator New Tab visual v5 provenance

Generated on 2026-08-18 for the Orbit Navigator repository using Codex's built-in
ImageGen workflow. No stock, third-party, telemetry-derived, browser-history, or
user-content imagery is present. The generated source files are retained under
`assets/new-tab/source-v5/`; runtime assets are deterministic derivatives built
by `scripts/Build-NewTabV5Assets.py` with Pillow/Lanczos.

## Runtime outputs

| Relative path | Dimensions | Alpha-zero / partial / opaque pixels | SHA-256 |
| --- | ---: | ---: | --- |
| `assets/new-tab/orbit-deep-space-base-v5.png` | 2560×1440 | 0 / 0 / 3,686,400 | `5B0759376C6EBFEAEB2264DD70B6BBFDB55254ADACD3E0A2FAD78E775BC0D4EB` |
| `assets/new-tab/orbit-celestial-elements-atlas-v5.png` | 1024×1024 (2×2; 512px cells) | 888,084 / 160,090 / 402 | `023E33DEC6DBCA4D65995AE936AD7A37E5CDE725564C63F8C160010F5C875010` |
| `assets/new-tab/favorite-star-solar-atlas-v5.png` | 1024×1024 (4×4; 256px frames) | 673,702 / 374,873 / 1 | `E5853C35EAAB9BD0D4783C1012C94A516B6041AF942F0990B8FAB436F5E2D459` |
| `assets/new-tab/workspace-star-solar-atlas-v5.png` | 1024×1024 (4×4; 256px frames) | 613,832 / 434,744 / 0 | `4267125F70BD096904893D86B73258FBA48EA9155CD12B42540E59B53550CFB9` |

Opaque pixels occur only inside luminous astronomical subjects. Every atlas
cell has an alpha-zero outer safety ring; no cell corner, matte, panel, or
backdrop is opaque. At alpha threshold 6, every runtime cell contains exactly
one connected visible component.

## Original sources

| Relative source | Dimensions | SHA-256 |
| --- | ---: | --- |
| `source-v5/orbit-deep-space-source-v5.png` | 1672×941 | `1C75C2CFF96C9A6633DFB10917CFE19C30D00B6B68E52A31F40E6347AF6F4D59` |
| `source-v5/orbit-comet-source-v5.png` | 1536×1024 | `FA0DE13FC890BB80BF5E0E35613A72DFCC1B73308B09E309AE87EA5D086D56AA` |
| `source-v5/orbit-planet-source-v5.png` | 1254×1254 | `EF803B4FFAA37CDEBE986BA84D7157C983FF0FBFF7FA945C1FBC35DB77F250FB` |
| `source-v5/orbit-galaxy-wave-source-v5.png` | 1672×941 | `FA6FBC31096BC23228B2AEFDF9C6B6AAAFEF8F32E5451AF8F9D9323DDBF3B438` |
| `source-v5/orbit-star-glints-source-v5.png` | 1536×1024 | `B8686C1288EA11893848132322E513F2AFC586BC747041EE1D52F08F937D2394` |
| `source-v5/favorite-star-solar-source-v5.png` | 1254×1254 | `EDC2A63F16988D422D0AFDB62FB7BEC6AB1229A4B996F247E2DFCA374110ECC5` |
| `source-v5/workspace-star-solar-source-v5.png` | 1254×1254 | `DB9C00C49B26714DFC2FFAD91751951D655E2F5C67F8D4BF5AEE7B3CBDA7B179` |

## Final ImageGen prompt set

### Deep-space base

```text
Use case: stylized-concept
Asset type: Orbit Navigator New Tab full-bleed deep-space background source
Primary request: create an original, breathtaking deep-space environment that feels dimensional and navigational, suitable behind an upright browser New Tab interface
Scene/backdrop: deep indigo and near-black space, fine natural star field, a broad restrained galactic dust lane and faint luminous nebular depth around the outer edges
Style/medium: high-end cinematic astronomical concept art with physically believable detail, calm rather than game-like
Composition/framing: true 16:9 landscape; keep the central 55% and lower middle visually quiet and dark for readable logo, search, cards, and text; place richer depth toward the perimeter; no horizon
Lighting/mood: elegant, deep, calm, awe-inspiring; subtle cyan, violet, and warm waypoint-gold accents
Constraints: no text, no logos, no browser UI, no spacecraft, no planets, no comet, no frames or borders, no watermark, no obvious radial pulse, no symmetric wallpaper curtains, no black rectangle artifacts
Avoid: low-resolution texture, cheap starburst wallpaper, screensaver look, busy center, opaque panel shapes
```

### Comet element

```text
Use case: stylized-concept
Asset type: transparent celestial sprite source for Orbit Navigator New Tab
Primary request: one elegant fast comet with a small icy-white nucleus, layered cyan-violet plasma tail, and a few attached dust filaments
Style/medium: high-end cinematic astronomical VFX element
Composition/framing: complete comet and full tail visible, traveling diagonally from upper right toward lower left, centered with at least 12% transparent padding on every edge
Lighting/mood: luminous but restrained, believable ion glow
Constraints: genuinely transparent background with alpha; one connected coherent comet; no detached fragments; no cropped tail; no text, logo, border, watermark, black matte, black disc, square backdrop, opaque corners
Avoid: cartoon fireball, lens-flare rectangle, noisy particles far from the tail
```

### Planet element

```text
Use case: stylized-concept
Asset type: transparent celestial sprite source for Orbit Navigator New Tab
Primary request: one original small deep-blue exoplanet with a thin warm-gold sunlit atmospheric rim and a complete delicate ring system
Style/medium: high-end cinematic astronomical VFX element, physically believable
Composition/framing: complete planet and all rings visible in a three-quarter view, centered with at least 15% transparent padding
Lighting/mood: calm, refined, subtle light
Constraints: genuinely transparent background with alpha; one connected coherent object; no cropped rings; no detached moons; no text, logo, border, watermark, black matte, black disc, square backdrop, opaque corners
Avoid: Saturn copy, cartoon planet, oversized glow
```

### Galaxy-wave element

```text
Use case: stylized-concept
Asset type: transparent celestial sprite source for Orbit Navigator New Tab
Primary request: one graceful edge-on spiral galaxy wave, an elongated curved band of fine blue-violet stars and faint gold dust with a subtle luminous core
Style/medium: cinematic astronomical VFX element with delicate particle detail
Composition/framing: complete elongated galaxy arc visible, sweeping gently left-to-right, centered with at least 12% transparent padding
Lighting/mood: quiet and ethereal, not a bright explosion
Constraints: genuinely transparent background with alpha; all visible particles remain visually connected to the galactic band; no detached chunks; no text, logo, border, watermark, black matte, black disc, square backdrop, opaque corners
Avoid: opaque cloud rectangle, busy central flare, hard edge
```

### Star-glint element

```text
Use case: stylized-concept
Asset type: transparent star-glint sprite source for Orbit Navigator New Tab
Primary request: a compact group of four elegant astronomical star glints at different sizes, each with a crisp core and restrained cyan-white diffraction rays
Style/medium: premium cinematic optical star sprites
Composition/framing: all four complete glints visible and clearly separated inside one canvas, generous transparent space around each and every edge
Lighting/mood: calm, precise, refined
Constraints: genuinely transparent background with alpha; no cropped rays; no detached garbage fragments; no text, logo, border, watermark, black matte, black disc, square backdrop, opaque corners
Avoid: cartoon sparkles, emoji stars, rainbow lens flare, excessive bloom
```

### Favorite/bookmark stellar atlas

```text
Use case: stylized-concept
Asset type: transparent 4x4 sprite-sheet source for Orbit Navigator bookmark/favorite solar animation
Primary request: sixteen coherent sequential animation frames of the SAME realistic golden-orange star, showing forward-evolving turbulent plasma, attached solar flare loops, flame licks, and corona motion—not brightness pulsing
Style/medium: premium cinematic astronomical VFX sprite art, physically believable solar photosphere and prominences
Composition/framing: exact 4 columns by 4 rows, sixteen equal square cells, no gutters or labels; in every cell the complete round star and every attached flare remain centered with generous transparent margin; constant photosphere radius and stable center in all frames
Motion continuity: read left-to-right then top-to-bottom as one forward loop; localized plasma advects around the surface, different attached corona tongues rise/curl/subside, final frame transitions naturally toward first; never palindrome or simple A-B-A
Lighting/mood: hot natural gold, orange, white plasma; stable overall emitted light across frames
Constraints: genuinely transparent canvas and transparent cell backgrounds; exactly one connected stellar component per frame; no detached sparks or fragments; no clipped corona; no rings, badges, icons, text, arrows, grid lines, borders, watermark, black matte, black disc, opaque cell, square backdrop
Avoid: pulsing globe, identical silhouette, cartoon sun, smile shape, planet texture
```

### Workspace/tab-group stellar atlas

```text
Use case: stylized-concept
Asset type: transparent 4x4 sprite-sheet source for Orbit Navigator workspace/tab-group stellar animation
Primary request: sixteen coherent sequential animation frames of the SAME realistic hotter blue-white star with warm-gold plasma accents, showing forward-evolving turbulent gas, attached prominence loops, flame licks, and corona motion—not brightness pulsing
Style/medium: premium cinematic astronomical VFX sprite art, physically believable stellar photosphere and prominences
Composition/framing: exact 4 columns by 4 rows, sixteen equal square cells, no gutters or labels; in every cell the complete round star and every attached flare remain centered with generous transparent margin; constant photosphere radius and stable center in all frames
Motion continuity: read left-to-right then top-to-bottom as one forward loop; localized plasma travels over the surface while distinct attached corona tongues evolve; final frame transitions naturally toward first; never palindrome or simple A-B-A
Lighting/mood: refined blue-white core, cyan corona, restrained warm-gold filaments; stable overall emitted light across frames
Constraints: genuinely transparent canvas and transparent cell backgrounds; exactly one connected stellar component per frame; no detached sparks or fragments; no clipped corona; no rings, badges, icons, text, arrows, grid lines, borders, watermark, black matte, black disc, opaque cell, square backdrop
Avoid: pulsing orb, identical silhouette, cartoon star, smile shape, planet texture
```

## Deterministic processing and runtime constraints

- Sources are never decoded per animation frame. `Build-NewTabV5Assets.py`
  emits frozen atlases; WPF crops and freezes them once.
- The builder keeps the largest alpha-connected subject, clears invisible RGB
  debris, normalizes solar center/apparent radius, guarantees an alpha-zero
  safety ring, and equalizes integrated alpha-weighted luminance. It does not
  generate, interpolate, or backfill frames.
- All motion uses `OrbitSharedVisualMotionClock`: one process-wide
  `CompositionTarget.Rendering` handler, capped at 24 fps and detached when no
  loaded/visible/non-minimized eligible surface remains.
- Hover/focus data-card state freezes non-stellar layers through
  `FreezeNonStellarMotion`; the complete stellar atlas continues independently.
- Reduced motion selects deterministic static frames. Reduced visual noise
  suppresses raster media and selects code-native stellar/ring cues. High
  contrast uses `SystemColors` and suppresses the raster scene/overlay.
- All raster layers are decorative and non-hit-testable. Host buttons retain
  action names; composite stellar visuals expose one combined image peer with
  explicit name/help and suppress their nested star peer.
