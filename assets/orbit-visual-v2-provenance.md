# Orbit Navigator visual v2 provenance

Generated on 2026-08-15 with the built-in Codex ImageGen tool. No external,
third-party, stock, or bitmap source art was used.

## Scope and approval status

- `branding/orbit-navigator-program-logo-v2.png` is the user-approved canonical
  Orbit Navigator program identity for New Tab, window, taskbar, executable,
  launcher, installer, Start Menu, and shortcut surfaces.
- `OrbitNewTabLogo` retains a New Tab-specific accessibility role while using
  the same approved program artwork.
- `new-tab/favorite-star-burn-atlas-v2.png` is for Favorite/Bookmark surfaces.
- `new-tab/tab-group-star-burn-atlas-v2.png` is for Tab Group surfaces.

## Approved program-logo source prompt

```text
Use case: logo-brand
Asset type: Orbit Navigator program logo symbol, suitable for a New Tab identity and future branding review
Primary request: create a completely new, unmistakable orbital-navigation logo symbol. It must read immediately as an orbital path plus navigation/compass direction, not as a generic route squiggle and not as a planet illustration. Form one strong circular O-like orbital ring, a bold north-east compass/spacecraft pointer integrated into the ring, and two small waypoint nodes. Strong silhouette, balanced negative space, recognizable at 32 pixels.
Style/medium: polished vector-like logo art, crisp flat shapes, minimal geometry, original design
Composition/framing: one centered symbol only, generous even padding, no wordmark
Color palette: deep teal/sea-glass and warm waypoint gold, with a small white highlight only if essential
Scene/backdrop: perfectly flat solid #ff00ff chroma-key background for background removal
Constraints: background must be one uniform #ff00ff color with no shadows, gradients, texture, reflections, floor plane, lighting variation, or vignette; crisp separated silhouette; no #ff00ff inside the symbol; no black rectangle or black disc; no text; no letters; no watermark; no mockup; no 3D; no detached pieces; no glow beyond the symbol silhouette
Avoid: current Orbit Navigator route mark, thin delicate linework, generic location pin, generic Saturn planet, compass rose clutter, photographic rendering
```

The tool returned native RGBA transparency, so no chroma-key removal was
necessary. The generated source is retained in the local Codex generation
store as `exec-758fefce-8d3f-4b38-b4b3-a72e6954c2f3.png`.

## Favorite/Bookmark stellar source prompt

```text
Use case: stylized-concept
Asset type: source artwork for a small animated Favorite/Bookmark star UI sprite
Primary request: one coherent, realistic burning ball of gas: an orange-gold solar star with visible boiling plasma cells and a compact attached corona. It must look like a living star, not a five-point icon, orb, gemstone, or fireball projectile. The entire corona must remain physically attached to the circular stellar limb with no separate sparks, blobs, particles, planets, or detached flame pieces.
Style/medium: high-detail scientific-inspired stellar surface render, polished UI asset, strong circular silhouette that remains legible at 24-64 pixels
Composition/framing: exactly one centered spherical star, straight-on, identical margin on all sides, subject occupies about 72% of canvas, compact corona contained within about 82% of canvas
Lighting/mood: self-luminous hot plasma, bright gold-white core filaments, deep amber surface convection, restrained corona
Scene/backdrop: perfectly flat solid #00ff00 chroma-key background for background removal
Constraints: background must be one uniform #00ff00 color with no shadows, gradients, texture, reflections, floor plane, lighting variation, or vignette; crisp subject separation; no #00ff00 in subject; no black rectangle, black disc, or dark matte; no cast shadow; no contact shadow; no reflection; no text; no watermark; no borders; no grid; no detached effects; no separate sparks or flare islands
Avoid: five-point star symbol, cartoon sun face, planet texture, glass sphere, lens flare, explosion, noisy particle field, oversized flame licking away from the disc
```

The native RGBA source is retained in the local Codex generation store as
`exec-7f847210-aecb-4aa9-8ffb-ff71c4910f48.png`.

## Tab Group stellar source prompt

```text
Use case: stylized-concept
Asset type: source artwork for a small animated Tab Group star UI sprite
Primary request: one coherent, realistic burning ball of gas: a blue-white and subtle violet stellar plasma sphere with visible boiling convection cells and a compact attached corona. It must look like a living hot star, not a planet, moon, gemstone, or detached energy orb. The entire corona must remain physically attached to the circular stellar limb with no separate sparks, blobs, particles, satellites, or detached flame pieces.
Style/medium: high-detail scientific-inspired stellar surface render, polished UI asset, strong circular silhouette that remains legible at 24-64 pixels
Composition/framing: exactly one centered spherical star, straight-on, identical margin on all sides, subject occupies about 72% of canvas, compact corona contained within about 82% of canvas
Lighting/mood: self-luminous blue-white plasma, icy cyan filaments, deep cobalt-violet surface convection, restrained corona
Scene/backdrop: perfectly flat solid #00ff00 chroma-key background for background removal
Constraints: background must be one uniform #00ff00 color with no shadows, gradients, texture, reflections, floor plane, lighting variation, or vignette; crisp subject separation; no #00ff00 in subject; no black rectangle, black disc, or dark matte; no cast shadow; no contact shadow; no reflection; no text; no watermark; no borders; no grid; no detached effects; no separate sparks or flare islands
Avoid: five-point star symbol, cartoon sun face, planet texture, glass sphere, lens flare, explosion, noisy particle field, oversized flame licking away from the disc
```

The native RGBA source is retained in the local Codex generation store as
`exec-0104007b-d9e7-4c65-b07f-d66c5e20fcd2.png`.

## Windows platform identity derivation

`scripts/Build-IdentityIcon.ps1` deterministically derives all Windows identity
outputs from the approved RGBA source. It no longer constructs the superseded
code-native route mark. The generator validates alpha-channel input,
transparent corners, sufficient transparent canvas, visible content, and
partial-alpha antialiasing before writing any platform output.

The Windows ICO contains PNG-compressed 32-bit RGBA frames at exactly 16, 20,
24, 32, 40, 48, 64, and 256 pixels. Matching PNG derivatives live beneath
`branding/windows/` for platform consumers and verification.

- Approved source SHA-256:
  `85DE697E14B95E0975D21CDD0C0197C41384E3B79D3F189FF9CF72DDBB5F5598`
- `branding/orbit-navigator.ico`:
  `3D86CE2611CD953C7A1BA058027004C2160FE77F0353865CC6F0129BAB68D365`
- `branding/windows/orbit-navigator-16.png`:
  `E89E3EAD8272279C611550BEEDD65B9CBAEE137F7DE094D07047241D43EC0A02`
- `branding/windows/orbit-navigator-20.png`:
  `77EE11BFCA01CFD74F3F211CA6EA5A85C4E41451B05CFEC73AE943287417980D`
- `branding/windows/orbit-navigator-24.png`:
  `6B45949681169E7FEB7977CAAAF2A05980F4C2357CA240D493B85D44D78FAEAA`
- `branding/windows/orbit-navigator-32.png`:
  `45B0889695F9766D504E9A55425696B16929C6D320E580816126D50F7769597C`
- `branding/windows/orbit-navigator-40.png`:
  `99A327976CB6B4AEB104311DA51C6821AD71E0BC6ACFB749ACAE21E52EA5D548`
- `branding/windows/orbit-navigator-48.png`:
  `C934DCEAE997E1D4ADBBB03353A006F2A27EF02FC7207F31B72D90263D2D3C9B`
- `branding/windows/orbit-navigator-64.png`:
  `89BB4A9EEE95BF890ACA13A121CC5EB183D0718B7142C71196B431602945F258`
- `branding/windows/orbit-navigator-256.png`:
  `6591CE6DE0A5634E00A8E5CA70E5D9E03A7D5A9BD7CDD34D8153F04D6450CE20`

## Atlas construction and alpha validation

The two stellar sources were processed into 4-column by 3-row, 12-frame,
1024x768 RGBA atlases. Each 256x256 cell contains one complete star. Motion is
a subtle rotation and luminance evolution of the full plasma disc and its
attached corona; Presentation crossfades adjacent complete cells. There are no
independently positioned flame, spark, or ember layers.

Processing retained the center-connected alpha component, removed alpha at or
below 8, cleared every frame's outer pixel ring, removed isolated components,
and zeroed RGB for fully transparent pixels. Focused tests validate transparent
corners and edges, a single connected visible component in every source frame,
cohesive scaled WPF rendering, and no detached-motion offset.

Final alpha audit:

- Approved program logo: RGBA 1254x1254, 58.788% transparent at alpha <= 8.
- Favorite atlas: RGBA 1024x768, 45.845% transparent at alpha <= 8.
- Tab Group atlas: RGBA 1024x768, 49.891% transparent at alpha <= 8.
- All twelve asset corners have alpha 0.

## New Tab visual v3 addendum — 2026-08-17

The v3 New Tab field and stellar masters were generated with the built-in
Codex ImageGen workflow. No stock, third-party, telemetry-derived, or copied
browser art was used. The result is scoped to New Tab decorative surfaces and
Favorite/Bookmark or Tab Group stellar cues; it does not change application
identity artwork.

### Deep-space field prompt

```text
Create an original premium deep-space visual field for the Orbit Navigator browser New Tab page. Wide landscape composition, approximately 16:9. The art should feel like elegant orbital navigation: a sparse luminous teal-and-violet nebula veil, a few precise graceful orbital route arcs and tiny waypoint stars, deep dimensional space, refined cinematic detail, restrained and calm. Keep the central 45 percent visually quiet and low-contrast so search and content cards remain legible; place richer light structure mainly near the far left and right edges. This is a background atmosphere, not a logo. No text, no UI panels, no browser mockup, no planets, no spacecraft, no constellations shaped like symbols, no grids, no noisy dense star field, no heavy bloom, no black rectangle or framed vignette. Make the composition full-bleed with no visible rectangular panel edge. Use native RGBA with genuine smooth transparency in the atmospheric and outer regions wherever possible so it can blend over the app's own deep navy canvas; no checkerboard, no fake transparency, no opaque black matte. Original artwork, sophisticated, modern, subtly navigational.
```

The native RGBA source is retained in the local Codex generation store as
`exec-3adabcbf-2c5b-458b-b10c-a7bece1ff55c.png`.

### Favorite/Bookmark stellar prompt

```text
Create one complete, centered, photorealistic orange-gold stellar sphere for an Orbit Navigator Favorite/Bookmark visual. It must look like a real animated ball of burning gas: richly detailed turbulent plasma cells, luminous filaments, granular convection, a crisp round photosphere, and a restrained attached corona hugging the entire circumference. Strong depth and stellar realism at small icon sizes. The entire star and every corona filament must form one cohesive connected body fully inside the square canvas with generous transparent padding on all four sides. No detached flares, no separate sparks, no floating fragments, no cropped edges, no orbit ring, no star-point icon, no badge, no text, no shadow, no glow panel. Native RGBA with genuine transparent background and smoothly antialiased partial-alpha corona; absolutely no black matte, colored disc backdrop, checkerboard, or opaque corners. Single object only, straight-on orthographic view, square output, premium cinematic scientific illustration suitable as the master source for a subtle looping sprite atlas.
```

The native RGBA source is retained in the local Codex generation store as
`exec-27420989-f4ff-44f8-873a-06c9a0e0e60c.png`.

### Tab Group stellar prompt

```text
Create one complete, centered, photorealistic blue-white and subtle violet stellar sphere for an Orbit Navigator Tab Group visual. It must look like a real animated ball of hot burning gas: richly detailed turbulent plasma cells, luminous magnetic filaments, granular convection, a crisp round photosphere, and a restrained attached cyan-violet corona hugging the entire circumference. Strong depth and stellar realism at small icon sizes, visually distinct from an orange Favorite star. The entire star and every corona filament must form one cohesive connected body fully inside the square canvas with generous transparent padding on all four sides. No detached flares, no separate sparks, no floating fragments, no cropped edges, no orbit ring, no star-point icon, no badge, no text, no shadow, no glow panel. Native RGBA with genuine transparent background and smoothly antialiased partial-alpha corona; absolutely no black matte, colored disc backdrop, checkerboard, or opaque corners. Single object only, straight-on orthographic view, square output, premium cinematic scientific illustration suitable as the master source for a subtle looping sprite atlas.
```

The native RGBA source is retained in the local Codex generation store as
`exec-bc9552bb-d67e-4bbb-adfe-719ffcc6688a.png`.

### Plasma-evolution edit prompts

The built-in ImageGen edit workflow produced a second photosphere state for
each master. The exact Favorite edit prompt was:

```text
Use case: precise-object-edit. Asset type: alternate sequential plasma state for an Orbit Navigator Favorite/Bookmark sprite atlas. Input image 1 is the edit target and visual anchor. Change only the internal orange-gold plasma convection, magnetic filaments, and the attached corona into a second believable moment of the same living star. Shift the bright surface knots and filament curls enough to read as real gas evolution during a slow crossfade, while preserving the exact subject identity, orange-gold palette, round photosphere diameter, overall centered scale, framing, transparent padding, and premium scientific realism. Keep one complete cohesive stellar body fully inside the square. Every corona filament must remain physically attached to the stellar limb. Preserve native RGBA transparency and smooth partial-alpha edges. Absolutely no black matte, dark disc, colored panel, checkerboard, detached flare, separate spark, floating fragment, extra object, shadow, text, watermark, cropped edge, or change to canvas dimensions. This is frame-state B of the same star, not a redesigned star.
```

Its generated source is retained as
`exec-64dd23a4-fb3b-4b43-b891-5e0ba500bc77.png`.

The exact Tab Group edit prompt was:

```text
Use case: precise-object-edit. Asset type: alternate sequential plasma state for an Orbit Navigator Tab Group sprite atlas. Input image 1 is the edit target and visual anchor. Change only the internal blue-white/cobalt-violet plasma convection, magnetic filaments, and the attached cyan-violet corona into a second believable moment of the same living star. Shift the bright surface knots and filament curls enough to read as real hot-gas evolution during a slow crossfade, while preserving the exact subject identity, blue-white and subtle violet palette, round photosphere diameter, overall centered scale, framing, transparent padding, and premium scientific realism. Keep one complete cohesive stellar body fully inside the square. Every corona filament must remain physically attached to the stellar limb. Preserve native RGBA transparency and smooth partial-alpha edges. Absolutely no black matte, dark disc, colored panel, checkerboard, detached flare, separate spark, floating fragment, extra object, shadow, text, watermark, cropped edge, or change to canvas dimensions. This is frame-state B of the same star, not a redesigned star.
```

Its generated source is retained as
`exec-fb054315-75f3-47c5-9d74-7435cc3b8357.png`.

Both edit outputs supplied useful photosphere detail but returned opaque preview
backdrops, so neither opaque backdrop was imported. Construction uses only the
edited RGB inside a feathered photosphere interior. The original validated
master supplies every frame's alpha channel, attached corona, outer padding,
and silhouette. This makes an opaque matte or detached generated component
structurally unable to enter the final atlases.

### v3 construction, motion, and alpha audit

- `new-tab/orbit-deep-space-field-v3.png` is the 1672x941 native alpha field.
  Every pixel is partially transparent and there are zero fully opaque pixels,
  so it blends over the themed New Tab canvas without an opaque panel edge.
- The two master stars were downsampled into 4-column by 3-row, 12-frame,
  768x576 RGBA atlases. Each 192x192 cell contains the same complete generated
  stellar body and attached corona. The internal photosphere follows a smooth
  A-to-B-to-A plasma evolution while alpha and the connected corona remain
  identical in every frame. No fragment layer, particle emitter, estimated
  frame, whole-body wobble, or detached animation piece is introduced.
- Presentation crossfades cached frozen frame crops. One shared visible-only
  composition clock is capped at 24 frames per second on an eight-second cycle;
  a bounded 17-slot per-instance phase offset prevents mechanical synchronization
  while retaining the same clock and zero per-item timers;
  high contrast suppresses decorative raster art and reduced motion uses one
  stable static frame.
- Focused WPF tests verify every atlas cell has a transparent outer ring and one
  connected visible stellar component, that every frame has the exact same
  alpha silhouette, that photosphere RGB materially evolves, and that
  crossfaded high-DPI renders remain cohesive.

Final files:

- `new-tab/orbit-deep-space-field-v3.png`: RGBA 1672x941,
  SHA-256 `B1DE15E1D658E556F19CBD5DBC3CC3B83C8DAF70EE6611ECC52ECD2536B10C7E`.
- `new-tab/favorite-star-burn-atlas-v3.png`: RGBA 768x576, 53.556% alpha <= 8,
  four corner alpha values 0, SHA-256
  `679BE0516E39B2EDC61F7E2FEDC335E2B38AF1512073E23EC6D7D34F376676BD`.
- `new-tab/tab-group-star-burn-atlas-v3.png`: RGBA 768x576, 49.992% alpha <= 8,
  four corner alpha values 0, SHA-256
  `EBF95CC41D58AD76B6B649850B3D38AFA749C63CBE30F47915AC1F6583A94087`.
