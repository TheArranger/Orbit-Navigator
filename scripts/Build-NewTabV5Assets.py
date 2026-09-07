"""Build deterministic Orbit New Tab v5 raster assets.

The checked-in source PNGs are original project-bound ImageGen outputs. This
builder produces one high-resolution scene base, one compact transparent 2x2
celestial-elements atlas, and two normalized 4x4 stellar animation atlases.
It removes disconnected alpha debris, recenters and radius-normalizes each
photosphere, equalizes whole-star emitted light, and preserves real generated
frame-to-frame plasma/corona variation without synthesizing interpolation.

Requires Pillow. Run from the repository root:
    python scripts/Build-NewTabV5Assets.py
"""

from __future__ import annotations

from collections import deque
from pathlib import Path

from PIL import Image, ImageEnhance, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSET_ROOT = ROOT / "assets" / "new-tab"
SOURCE_ROOT = ASSET_ROOT / "source-v5"

SCENE_OUTPUT_SIZE = (2560, 1440)
ELEMENT_CELL_SIZE = 512
ELEMENT_MARGIN = 44
SOLAR_COLUMNS = 4
SOLAR_ROWS = 4
SOLAR_FRAME_SIZE = 256
SOLAR_CENTER = SOLAR_FRAME_SIZE / 2
SOLAR_PHOTOSPHERE_RADIUS = 74.0
SOLAR_MARGIN = 7
ALPHA_COMPONENT_THRESHOLD = 6

ELEMENT_SOURCES = (
    "orbit-comet-source-v5.png",
    "orbit-planet-source-v5.png",
    "orbit-galaxy-wave-source-v5.png",
    "orbit-star-glints-source-v5.png",
)


def largest_alpha_component(image: Image.Image) -> Image.Image:
    """Keep one coherent visible component and clear invisible RGB garbage."""
    rgba = image.convert("RGBA")
    alpha = rgba.getchannel("A")
    width, height = rgba.size
    pixels = alpha.load()
    visited = bytearray(width * height)
    largest: list[int] = []

    for y in range(height):
        for x in range(width):
            index = y * width + x
            if visited[index] or pixels[x, y] < ALPHA_COMPONENT_THRESHOLD:
                continue

            component: list[int] = []
            pending = deque([index])
            visited[index] = 1
            while pending:
                current = pending.popleft()
                component.append(current)
                px = current % width
                py = current // width
                for nx, ny in ((px - 1, py), (px + 1, py), (px, py - 1), (px, py + 1)):
                    if nx < 0 or nx >= width or ny < 0 or ny >= height:
                        continue
                    candidate = ny * width + nx
                    if visited[candidate] or pixels[nx, ny] < ALPHA_COMPONENT_THRESHOLD:
                        continue
                    visited[candidate] = 1
                    pending.append(candidate)

            if len(component) > len(largest):
                largest = component

    if not largest:
        raise ValueError("A source image had no connected alpha component.")

    keep = bytearray(width * height)
    for index in largest:
        keep[index] = 1
    source_pixels = rgba.load()
    for y in range(height):
        row = y * width
        for x in range(width):
            red, green, blue, original_alpha = source_pixels[x, y]
            if not keep[row + x]:
                source_pixels[x, y] = (0, 0, 0, 0)
            elif original_alpha < ALPHA_COMPONENT_THRESHOLD:
                source_pixels[x, y] = (0, 0, 0, 0)
            else:
                source_pixels[x, y] = (red, green, blue, original_alpha)
    return rgba


def fit_component(image: Image.Image, cell_size: int, margin: int) -> Image.Image:
    cleaned = largest_alpha_component(image)
    bounds = cleaned.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("A cleaned element became fully transparent.")
    cropped = cleaned.crop(bounds)
    available = cell_size - (margin * 2)
    scale = min(available / cropped.width, available / cropped.height)
    size = (max(1, round(cropped.width * scale)), max(1, round(cropped.height * scale)))
    resized = cropped.resize(size, Image.Resampling.LANCZOS)
    output = Image.new("RGBA", (cell_size, cell_size), (0, 0, 0, 0))
    output.alpha_composite(resized, ((cell_size - size[0]) // 2, (cell_size - size[1]) // 2))
    return largest_alpha_component(output)


def source_cell(source: Image.Image, index: int) -> Image.Image:
    column = index % SOLAR_COLUMNS
    row = index // SOLAR_COLUMNS
    x0 = round(column * source.width / SOLAR_COLUMNS)
    x1 = round((column + 1) * source.width / SOLAR_COLUMNS)
    y0 = round(row * source.height / SOLAR_ROWS)
    y1 = round((row + 1) * source.height / SOLAR_ROWS)
    return source.crop((x0, y0, x1, y1))


def detect_photosphere(frame: Image.Image) -> tuple[float, float, float]:
    weighted_points: list[tuple[float, float, float]] = []
    for index, (red, green, blue, alpha) in enumerate(frame.get_flattened_data()):
        luminance = 0.2126 * red + 0.7152 * green + 0.0722 * blue
        if alpha < 92 or luminance < 96:
            continue
        weight = alpha * luminance
        weighted_points.append((index % frame.width, index // frame.width, weight))
    if not weighted_points:
        raise ValueError("Could not detect a stellar photosphere.")

    total_weight = sum(point[2] for point in weighted_points)
    center_x = sum(point[0] * point[2] for point in weighted_points) / total_weight
    center_y = sum(point[1] * point[2] for point in weighted_points) / total_weight
    distances = sorted(
        (((x - center_x) ** 2 + (y - center_y) ** 2) ** 0.5, weight)
        for x, y, weight in weighted_points
    )
    accumulated = 0.0
    for distance, weight in distances:
        accumulated += weight
        if accumulated >= total_weight * 0.80:
            return center_x, center_y, distance
    raise ValueError("Could not measure a stellar photosphere radius.")


def normalize_solar_frame(source_frame: Image.Image) -> Image.Image:
    cleaned = largest_alpha_component(source_frame)
    center_x, center_y, radius = detect_photosphere(cleaned)
    scale = SOLAR_PHOTOSPHERE_RADIUS / radius
    resized = cleaned.resize(
        (max(1, round(cleaned.width * scale)), max(1, round(cleaned.height * scale))),
        Image.Resampling.LANCZOS,
    )
    target = Image.new("RGBA", (SOLAR_FRAME_SIZE, SOLAR_FRAME_SIZE), (0, 0, 0, 0))
    target.alpha_composite(
        resized,
        (
            round(SOLAR_CENTER - center_x * scale),
            round(SOLAR_CENTER - center_y * scale),
        ),
    )
    target = largest_alpha_component(target)
    bounds = target.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("A normalized stellar frame became transparent.")
    if (
        bounds[0] < SOLAR_MARGIN
        or bounds[1] < SOLAR_MARGIN
        or bounds[2] > SOLAR_FRAME_SIZE - SOLAR_MARGIN
        or bounds[3] > SOLAR_FRAME_SIZE - SOLAR_MARGIN
    ):
        # Preserve the generated flare shape while guaranteeing a transparent
        # safety ring. The photosphere remains centered and the same scale
        # adjustment is applied to every pixel in this one source frame.
        target = fit_component(target, SOLAR_FRAME_SIZE, SOLAR_MARGIN + 2)
    return target


def integrated_luminance(frame: Image.Image) -> float:
    total = 0.0
    for red, green, blue, alpha in frame.get_flattened_data():
        total += (0.2126 * red + 0.7152 * green + 0.0722 * blue) * alpha
    if total <= 0:
        raise ValueError("A stellar frame had no emitted light.")
    return total


def equalize_luminance(frames: list[Image.Image]) -> list[Image.Image]:
    values = [integrated_luminance(frame) for frame in frames]
    target = min(values)
    output: list[Image.Image] = []
    for frame, value in zip(frames, values, strict=True):
        factor = max(0.72, min(1.0, target / value))
        adjusted = ImageEnhance.Brightness(frame).enhance(factor)
        correction = max(0.97, min(1.0, target / integrated_luminance(adjusted)))
        output.append(ImageEnhance.Brightness(adjusted).enhance(correction))
    return output


def build_scene() -> None:
    source = Image.open(SOURCE_ROOT / "orbit-deep-space-source-v5.png").convert("RGB")
    output = source.resize(SCENE_OUTPUT_SIZE, Image.Resampling.LANCZOS)
    output = output.filter(ImageFilter.UnsharpMask(radius=1.0, percent=24, threshold=3))
    path = ASSET_ROOT / "orbit-deep-space-base-v5.png"
    output.save(path, format="PNG", optimize=True)
    print(path.relative_to(ROOT))


def build_element_atlas() -> None:
    atlas = Image.new(
        "RGBA",
        (ELEMENT_CELL_SIZE * 2, ELEMENT_CELL_SIZE * 2),
        (0, 0, 0, 0),
    )
    for index, source_name in enumerate(ELEMENT_SOURCES):
        source = Image.open(SOURCE_ROOT / source_name).convert("RGBA")
        element = fit_component(source, ELEMENT_CELL_SIZE, ELEMENT_MARGIN)
        atlas.alpha_composite(
            element,
            ((index % 2) * ELEMENT_CELL_SIZE, (index // 2) * ELEMENT_CELL_SIZE),
        )
    path = ASSET_ROOT / "orbit-celestial-elements-atlas-v5.png"
    atlas.save(path, format="PNG", optimize=True)
    print(path.relative_to(ROOT))


def build_solar_atlas(source_name: str, output_name: str) -> None:
    source = Image.open(SOURCE_ROOT / source_name).convert("RGBA")
    frames = [normalize_solar_frame(source_cell(source, index)) for index in range(16)]
    frames = equalize_luminance(frames)
    atlas = Image.new(
        "RGBA",
        (SOLAR_FRAME_SIZE * SOLAR_COLUMNS, SOLAR_FRAME_SIZE * SOLAR_ROWS),
        (0, 0, 0, 0),
    )
    for index, frame in enumerate(frames):
        atlas.alpha_composite(
            frame,
            ((index % SOLAR_COLUMNS) * SOLAR_FRAME_SIZE, (index // SOLAR_COLUMNS) * SOLAR_FRAME_SIZE),
        )
    path = ASSET_ROOT / output_name
    atlas.save(path, format="PNG", optimize=True)
    print(path.relative_to(ROOT))


def main() -> None:
    build_scene()
    build_element_atlas()
    build_solar_atlas("favorite-star-solar-source-v5.png", "favorite-star-solar-atlas-v5.png")
    build_solar_atlas("workspace-star-solar-source-v5.png", "workspace-star-solar-atlas-v5.png")


if __name__ == "__main__":
    main()
