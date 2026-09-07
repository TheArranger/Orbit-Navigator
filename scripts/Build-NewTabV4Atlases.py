"""Build the normalized Orbit New Tab v4 solar-flare atlases.

The checked-in source sheets are project-bound image-generation outputs.  This
step recenters the photosphere, normalizes its apparent radius, removes alpha
components that are not connected to the star/corona, and leaves a transparent
safety margin around every frame.  It never synthesizes intermediate frames.

Requires Pillow.  Run from the repository root:
    python scripts/Build-NewTabV4Atlases.py
"""

from __future__ import annotations

from collections import deque
from pathlib import Path

from PIL import Image, ImageEnhance, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSET_ROOT = ROOT / "assets" / "new-tab"
SOURCE_ROOT = ASSET_ROOT / "source-v4"

ATLAS_COLUMNS = 4
ATLAS_ROWS = 3
SOURCE_FRAME_SIZE = 362
OUTPUT_FRAME_SIZE = 192
OUTPUT_CENTER = OUTPUT_FRAME_SIZE // 2
OUTPUT_PHOTOSPHERE_RADIUS = 58.0
ALPHA_CONNECTIVITY_THRESHOLD = 6
MINIMUM_SAFETY_MARGIN = 4
SCENE_SOURCE_SIZE = (1672, 941)
SCENE_OUTPUT_SIZE = (2560, 1440)

# Centers/radii were detected from each source frame's continuous bright limb.
# Keeping these explicit makes the checked-in build deterministic and auditable.
FAVORITE_LIMBS = (
    (184, 177, 109), (184, 177, 109), (178, 180, 109), (181, 177, 109),
    (181, 177, 109), (184, 180, 106), (178, 180, 106), (181, 177, 109),
    (184, 180, 106), (184, 180, 109), (178, 180, 109), (181, 177, 109),
)

TAB_GROUP_LIMBS = (
    (193, 192, 121), (184, 189, 121), (181, 183, 118), (163, 189, 118),
    (187, 168, 121), (181, 171, 121), (178, 171, 121), (172, 171, 121),
    (190, 156, 121), (181, 156, 121), (181, 153, 121), (175, 156, 121),
)


def connected_alpha_only(frame: Image.Image) -> Image.Image:
    """Keep only the alpha component attached to the coherent stellar body."""
    rgba = frame.convert("RGBA")
    alpha = rgba.getchannel("A")
    width, height = rgba.size
    pixels = alpha.load()
    visited: set[tuple[int, int]] = set()
    components: list[list[tuple[int, int]]] = []

    for y in range(height):
        for x in range(width):
            if pixels[x, y] < ALPHA_CONNECTIVITY_THRESHOLD or (x, y) in visited:
                continue

            component: list[tuple[int, int]] = []
            pending = deque([(x, y)])
            visited.add((x, y))
            while pending:
                point = pending.popleft()
                component.append(point)
                px, py = point
                for candidate in ((px - 1, py), (px + 1, py), (px, py - 1), (px, py + 1)):
                    cx, cy = candidate
                    if (
                        0 <= cx < width
                        and 0 <= cy < height
                        and candidate not in visited
                        and pixels[cx, cy] >= ALPHA_CONNECTIVITY_THRESHOLD
                    ):
                        visited.add(candidate)
                        pending.append(candidate)
            components.append(component)

    if not components:
        raise ValueError("A source frame had no connected alpha component.")

    keep = set(max(components, key=len))
    alpha_bytes = bytearray(alpha.tobytes())
    for y in range(height):
        row = y * width
        for x in range(width):
            if (x, y) not in keep:
                alpha_bytes[row + x] = 0

    cleaned_alpha = Image.frombytes("L", rgba.size, bytes(alpha_bytes))
    rgba.putalpha(cleaned_alpha)
    return rgba


def normalize_frame(source_frame: Image.Image, limb: tuple[int, int, int]) -> Image.Image:
    center_x, center_y, radius = limb
    scale = OUTPUT_PHOTOSPHERE_RADIUS / radius
    resized_size = round(SOURCE_FRAME_SIZE * scale)
    resized = source_frame.resize((resized_size, resized_size), Image.Resampling.LANCZOS)
    target = Image.new("RGBA", (OUTPUT_FRAME_SIZE, OUTPUT_FRAME_SIZE), (0, 0, 0, 0))
    left = round(OUTPUT_CENTER - (center_x * scale))
    top = round(OUTPUT_CENTER - (center_y * scale))
    target.alpha_composite(resized, (left, top))
    target = connected_alpha_only(target)

    alpha_bounds = target.getchannel("A").getbbox()
    if alpha_bounds is None:
        raise ValueError("A normalized frame became fully transparent.")
    if (
        alpha_bounds[0] < MINIMUM_SAFETY_MARGIN
        or alpha_bounds[1] < MINIMUM_SAFETY_MARGIN
        or alpha_bounds[2] > OUTPUT_FRAME_SIZE - MINIMUM_SAFETY_MARGIN
        or alpha_bounds[3] > OUTPUT_FRAME_SIZE - MINIMUM_SAFETY_MARGIN
    ):
        raise ValueError(f"Frame safety margin was too small: {alpha_bounds}")
    return target


def integrated_luminance(frame: Image.Image) -> float:
    weighted_luminance = 0.0
    for red, green, blue, alpha in frame.get_flattened_data():
        if alpha == 0:
            continue
        weighted_luminance += (0.2126 * red + 0.7152 * green + 0.0722 * blue) * alpha
    if weighted_luminance == 0:
        raise ValueError("A normalized frame contained no visible pixels.")
    return weighted_luminance


def equalize_luminance(frames: list[Image.Image]) -> list[Image.Image]:
    """Remove whole-star brightness pulsing while preserving local plasma change."""
    measurements = [integrated_luminance(frame) for frame in frames]
    # Scale brighter frames down to the dimmest natural state. This avoids
    # highlight clipping while keeping alpha/corona evolution untouched.
    target = min(measurements)
    results: list[Image.Image] = []
    for frame, measured in zip(frames, measurements, strict=True):
        factor = max(0.88, min(1.0, target / measured))
        adjusted = ImageEnhance.Brightness(frame).enhance(factor)
        # One bounded correction pass absorbs 8-bit quantization.
        correction = max(0.98, min(1.0, target / integrated_luminance(adjusted)))
        results.append(ImageEnhance.Brightness(adjusted).enhance(correction))
    return results


def build_scene_asset(source_name: str, output_name: str, sharpen: bool) -> None:
    source_path = SOURCE_ROOT / source_name
    output_path = ASSET_ROOT / output_name
    source = Image.open(source_path).convert("RGBA")
    if source.size != SCENE_SOURCE_SIZE:
        raise ValueError(f"Expected {source_path} to be {SCENE_SOURCE_SIZE}, got {source.size}.")
    output = source.resize(SCENE_OUTPUT_SIZE, Image.Resampling.LANCZOS)
    if sharpen:
        output = output.filter(ImageFilter.UnsharpMask(radius=1.1, percent=32, threshold=3))
    output.save(output_path, format="PNG", optimize=True)
    print(output_path.relative_to(ROOT))


def build_atlas(source_name: str, output_name: str, limbs: tuple[tuple[int, int, int], ...]) -> None:
    source_path = SOURCE_ROOT / source_name
    output_path = ASSET_ROOT / output_name
    source = Image.open(source_path).convert("RGBA")
    expected_size = (SOURCE_FRAME_SIZE * ATLAS_COLUMNS, SOURCE_FRAME_SIZE * ATLAS_ROWS)
    if source.size != expected_size:
        raise ValueError(f"Expected {source_path} to be {expected_size}, got {source.size}.")
    if len(limbs) != ATLAS_COLUMNS * ATLAS_ROWS:
        raise ValueError("The limb table must contain exactly one entry per atlas frame.")

    frames: list[Image.Image] = []
    for index, limb in enumerate(limbs):
        column = index % ATLAS_COLUMNS
        row = index // ATLAS_COLUMNS
        source_frame = source.crop(
            (
                column * SOURCE_FRAME_SIZE,
                row * SOURCE_FRAME_SIZE,
                (column + 1) * SOURCE_FRAME_SIZE,
                (row + 1) * SOURCE_FRAME_SIZE,
            )
        )
        frames.append(normalize_frame(source_frame, limb))

    frames = equalize_luminance(frames)
    atlas = Image.new(
        "RGBA",
        (OUTPUT_FRAME_SIZE * ATLAS_COLUMNS, OUTPUT_FRAME_SIZE * ATLAS_ROWS),
        (0, 0, 0, 0),
    )
    for index, normalized in enumerate(frames):
        column = index % ATLAS_COLUMNS
        row = index // ATLAS_COLUMNS
        atlas.alpha_composite(normalized, (column * OUTPUT_FRAME_SIZE, row * OUTPUT_FRAME_SIZE))

    atlas.save(output_path, format="PNG", optimize=True)
    print(output_path.relative_to(ROOT))


def main() -> None:
    build_scene_asset(
        "orbit-deep-space-base-source-v4.png",
        "orbit-deep-space-base-v4.png",
        sharpen=True,
    )
    build_scene_asset(
        "orbit-nebula-veil-source-v4.png",
        "orbit-nebula-veil-v4.png",
        sharpen=False,
    )
    build_scene_asset(
        "orbit-near-starfield-source-v4.png",
        "orbit-near-starfield-v4.png",
        sharpen=True,
    )
    build_atlas(
        "favorite-star-flare-source-v4.png",
        "favorite-star-flare-atlas-v4.png",
        FAVORITE_LIMBS,
    )
    build_atlas(
        "tab-group-star-flare-source-v4.png",
        "tab-group-star-flare-atlas-v4.png",
        TAB_GROUP_LIMBS,
    )


if __name__ == "__main__":
    main()
