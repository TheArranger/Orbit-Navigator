# Orbit Navigator New Tab visual v4 provenance

Generated on 2026-08-17 with the built-in Codex ImageGen tool. No stock,
third-party, telemetry-derived, or copied browser art was used. All files in
this document are scoped to the New Tab decorative scene or its
Favorite/Bookmark and Tab Group stellar visuals.

## Deep-space base

```text
Use case: stylized-concept
Asset type: Orbit Navigator New Tab deep-space base layer, project-bound raster
Primary request: create an original, cinematic deep-space environment that feels contemporary, calm, and navigational rather than like a static desktop wallpaper.
Scene/backdrop: vast near-black indigo space with layered dust, sparse crisp stars at varied depth, one restrained diagonal veil of blue-violet nebula gas, faint distant galactic structure, and subtle depth variation.
Composition/framing: 16:9 landscape; keep the central 48% quiet and low-contrast for search and New Tab content; distribute detail asymmetrically around outer thirds; no circular frame, no border, no vignette ring, no baked orbital lines or UI elements.
Lighting/mood: elegant, deep, premium, quiet motion-ready; restrained luminous cyan and violet accents, mostly dark.
Constraints: full-bleed opaque background image is acceptable for the base layer; no text, no logos, no UI, no planets, no spaceships, no giant hero star, no watermark; high detail without noisy star density; no retro screensaver aesthetic; no obvious tiling; no black matte issue because this is intentionally the full-bleed base background.
```

Output: `orbit-deep-space-base-v4.png`, RGBA 1672x941, intentionally full
bleed and opaque, SHA-256
`D505E893B833530375E6C31958AE05098C8F501304DE602C62D4B4ED8EB34DD8`.

## Transparent nebula motion layer

```text
Use case: stylized-concept
Asset type: transparent animated-layer overlay for Orbit Navigator New Tab deep-space scene
Primary request: create one isolated, wispy blue-violet nebula veil suitable for extremely slow parallax drift over a dark space base.
Scene/backdrop: genuinely transparent background across the full 16:9 canvas.
Subject: delicate semi-transparent interstellar gas filaments and faint dust, concentrated along the outer left/lower-left and upper-right edges, with the central 55% nearly empty and transparent.
Style/medium: cinematic astronomical gas, high-detail but restrained, soft volumetric translucency, contemporary premium UI backdrop layer.
Composition/framing: 16:9 landscape; all wisps remain fully within canvas; generous transparent margins; asymmetric natural structure.
Lighting/mood: deep cyan, indigo, and muted violet; low luminance, calm.
Constraints: actual alpha transparency; no black background, no black matte, no opaque corners, no rectangular panel, no vignette, no stars, no planets, no orbital lines, no text, no logo, no watermark; no bright focal blob; the isolated gas must remain coherent if translated a few pixels.
```

Output: `orbit-nebula-veil-v4.png`, RGBA 1672x941, 872,878 fully
transparent pixels, 700,474 partial-alpha pixels, zero opaque pixels, SHA-256
`9EEC841FC273CE3B4D6B5BDF6134C4F931A36EA6861C5BA2E0B46ABABF9ED04B`.

## Transparent near-star motion layer

```text
Use case: stylized-concept
Asset type: transparent near-star parallax overlay for Orbit Navigator New Tab
Primary request: create a sparse field of isolated tiny stars and a few fine dust specks at varied apparent depth, for very slow independent drift over a deep-space base.
Scene/backdrop: genuinely transparent full 16:9 canvas.
Subject: approximately 45 to 65 small crisp stars, mostly pinpoints, only four subtle four-point glints, irregular natural distribution with lower density through the central 45% for UI readability.
Style/medium: refined astronomical particle layer, sharp at 1x and 2x display scale, restrained and premium.
Composition/framing: full 16:9 landscape; every star remains within canvas; no dense clusters and no uniform grid.
Color palette: mostly cool white, a handful of pale cyan and muted lavender points.
Constraints: actual alpha transparency; no black background or matte, no opaque corners, no nebula, no planets, no lines, no trails, no text, no logo, no watermark; no bloom larger than 18 pixels; no obvious repetition; suitable for translation by a few pixels without exposing a hard border.
```

Output: `orbit-near-starfield-v4.png`, RGBA 1672x941, 1,454,171 fully
transparent pixels, 119,181 partial-alpha pixels, zero opaque pixels, SHA-256
`DCFC45701D26C904F72E436DA240B0777FEB73775C1E3800A7A10FEE4DC632B7`.

## Favorite/Bookmark flare atlas

```text
Use case: stylized-concept
Asset type: 12-frame 4-by-3 transparent sprite atlas for an active Favorite/Bookmark star in Orbit Navigator
Primary request: create twelve sequential, animation-ready states of one believable amber-gold burning ball of gas. Across the frames, the photosphere texture convects and evolves; two or three solar prominence loops grow, curl along the limb, and recede; small flame licks travel around the circumference; the corona subtly changes shape. The motion must read as evolving plasma and solar flares, not brightness pulsing, spinning the same image, or translating the whole star.
Subject: the same complete star in every frame, warm golden-orange photosphere with darker granular convection cells, thin white-gold limb, tasteful amber corona, delicate coherent prominence arcs attached to the edge.
Composition/framing: exact 4 columns by 3 rows; equal square cells; one centered complete star per cell; identical star center and apparent disc diameter in every cell; all flares and corona fully contained inside each cell with generous transparent safety margin; chronological loop flows left-to-right then top-to-bottom and frame 12 transitions naturally back to frame 1.
Style/medium: high-quality realistic astronomical visualization adapted to a polished browser UI icon; legible at 28–64 DIP; crisp silhouette, restrained glow.
Background: genuinely transparent in every cell and between cells.
Constraints: actual alpha transparency; no black or colored matte, no opaque cell rectangles, no detached sparks or floating fragments, no clipping at cell edges, no text, no borders, no labels, no watermark; no face, no cartoon sun rays, no star-shaped polygon; avoid simple luminosity changes and avoid identical frames.
```

The checked-in ImageGen source is
`source-v4/favorite-star-flare-source-v4.png`, SHA-256
`4024B225841745217C96CAC1B34360789A28FDB89A12DA29476A19FFE5538485`.
`scripts/Build-NewTabV4Atlases.py` deterministically recenters each detected
photosphere limb, normalizes its radius, retains only the connected alpha
component, adds transparent safety margins, and equalizes whole-star luminance.

Output: `favorite-star-flare-atlas-v4.png`, RGBA 768x576, twelve 192x192
frames, 186,023 fully transparent pixels, SHA-256
`551D17931C88045A33A025DACFE26E293959D55E343F6EEE940EAEC76E9F5977`.

## Tab Group flare atlas

```text
Use case: stylized-concept
Asset type: 12-frame 4-by-3 transparent sprite atlas for an active Tab Group star in Orbit Navigator
Primary request: create twelve sequential, animation-ready states of one believable blue-white burning ball of gas. Across the frames, the photosphere convection texture evolves; cool-violet and electric-cyan solar prominence loops grow, curl along the limb, and recede; fine plasma licks migrate around the circumference; the corona changes its contour subtly. The motion must read as evolving stellar plasma and solar flares, not brightness pulsing, spinning one image, or moving the whole star.
Subject: the same complete star in every frame, blue-white photosphere with darker cobalt granular cells, thin white-hot limb, restrained cyan/violet corona, coherent prominence arcs physically attached to the edge.
Composition/framing: exact 4 columns by 3 rows; equal square cells; one centered complete star per cell; identical center and apparent disc diameter across every frame; all flare loops and corona fully contained within each cell with generous transparent safety margin; chronological loop left-to-right then top-to-bottom, with frame 12 returning naturally toward frame 1.
Style/medium: high-quality realistic astronomical visualization adapted to a polished browser UI icon; legible at 28–64 DIP; crisp silhouette, controlled bloom.
Background: genuinely transparent in every cell and between cells.
Constraints: actual alpha transparency; no black or colored matte, no opaque rectangles, no detached sparks or floating fragments, no clipping at cell edges, no text, no borders, no labels, no watermark; no face, no cartoon sun rays, no polygon star; avoid simple brightness changes and avoid identical frames.
```

The checked-in ImageGen source is
`source-v4/tab-group-star-flare-source-v4.png`, SHA-256
`D8BCBD7C6A8EC9879509C40B08A1805FF381D3A950C818EA74305597B355F9A4`.

Output: `tab-group-star-flare-atlas-v4.png`, RGBA 768x576, twelve
192x192 frames, 203,706 fully transparent pixels, zero opaque background
pixels, SHA-256
`E02D084D190917BC5704C292467D39E61319F9E885E1B2DBC35E2579129D4538`.

## Runtime and accessibility contract

- New Tab v4 uses one shared `CompositionTarget.Rendering` handler capped at
  24fps and unregisters when hidden, unloaded, reduced-motion, reduced-noise,
  or high contrast applies. It owns no per-item timers.
- The full-bleed base remains static. The transparent nebula, near/far stars,
  and orbital route advance on distinct long periods, so the result has depth
  without one wallpaper breathing or ping-ponging.
- Reduced motion freezes a deterministic composition. High contrast and
  reduced noise suppress decorative raster layers. Stellar UIA names/help and
  filled-versus-hollow non-color status cues remain available.
- Every v4 atlas frame has a transparent outer ring, one connected visible
  component, a unique alpha silhouette, and stable whole-star luminance within
  two percent across the loop.
