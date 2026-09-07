"""Build deterministic Orbit New Tab v6 visual-motion assets.

The checked-in source images are original project-bound ImageGen outputs.  The
builder creates three quiet 16:9 scene variants, two coherent 16-frame solar
atlases, and one element-major 8-frame celestial atlas.  It never invents or
interpolates frames: every runtime frame comes from a distinct generated cell.

Requires Pillow. Run from the repository root:
    python scripts/Build-NewTabV6Assets.py
"""

from __future__ import annotations

from collections import deque
from pathlib import Path

from PIL import Image, ImageEnhance, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSET_ROOT = ROOT / "assets" / "new-tab"
SOURCE_ROOT = ASSET_ROOT / "source-v6"

SCENE_OUTPUT_SIZE = (2560, 1440)
SOURCE_COLUMNS = 4
SOURCE_ROWS = 4
SOLAR_FRAME_SIZE = 256
SOLAR_PHOTOSPHERE_RADIUS = 72.0
SOLAR_MARGIN = 16
CELESTIAL_FRAMES_PER_ELEMENT = 8
CELESTIAL_FRAME_SIZE = 256
CELESTIAL_MARGIN = 18
ALPHA_COMPONENT_THRESHOLD = 8

SCENE_SOURCES = (
    ("orbit-deep-space-source-v6-a.png", "orbit-deep-space-base-v6-a.png"),
    ("orbit-deep-space-source-v6-b.png", "orbit-deep-space-base-v6-b.png"),
    ("orbit-deep-space-source-v6-c.png", "orbit-deep-space-base-v6-c.png"),
)

CELESTIAL_SOURCES = (
    "orbit-comet-frames-source-v6.png",
    "orbit-planet-frames-source-v6.png",
    "orbit-galaxy-wave-frames-source-v6.png",
    "orbit-star-glint-frames-source-v6.png",
)


def source_cell(source: Image.Image, index: int) -> Image.Image:
    column = index % SOURCE_COLUMNS
    row = index // SOURCE_COLUMNS
    x0 = round(column * source.width / SOURCE_COLUMNS)
    x1 = round((column + 1) * source.width / SOURCE_COLUMNS)
    y0 = round(row * source.height / SOURCE_ROWS)
    y1 = round((row + 1) * source.height / SOURCE_ROWS)
    return source.crop((x0, y0, x1, y1))


def largest_alpha_component(
    image: Image.Image,
    threshold: int = ALPHA_COMPONENT_THRESHOLD,
) -> Image.Image:
    """Keep one attached visible subject and clear all invisible RGB debris."""
    rgba = image.convert("RGBA")
    alpha = rgba.getchannel("A")
    width, height = rgba.size
    pixels = alpha.load()
    visited = bytearray(width * height)
    largest: list[int] = []

    for y in range(height):
        for x in range(width):
            index = y * width + x
            if visited[index] or pixels[x, y] < threshold:
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
                    if visited[candidate] or pixels[nx, ny] < threshold:
                        continue
                    visited[candidate] = 1
                    pending.append(candidate)
            if len(component) > len(largest):
                largest = component

    if not largest:
        raise ValueError("A source frame had no connected visible component.")

    keep = bytearray(width * height)
    for index in largest:
        keep[index] = 1
    data = rgba.load()
    for y in range(height):
        row = y * width
        for x in range(width):
            red, green, blue, original_alpha = data[x, y]
            if not keep[row + x] or original_alpha < threshold:
                data[x, y] = (0, 0, 0, 0)
            else:
                data[x, y] = (red, green, blue, original_alpha)
    return rgba


def fit_component(
    image: Image.Image,
    cell_size: int,
    margin: int,
    threshold: int = ALPHA_COMPONENT_THRESHOLD,
) -> Image.Image:
    cleaned = largest_alpha_component(image, threshold)
    bounds = cleaned.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("A cleaned frame became fully transparent.")
    cropped = cleaned.crop(bounds)
    available = cell_size - (margin * 2)
    scale = min(available / cropped.width, available / cropped.height)
    size = (max(1, round(cropped.width * scale)), max(1, round(cropped.height * scale)))
    resized = cropped.resize(size, Image.Resampling.LANCZOS)
    output = Image.new("RGBA", (cell_size, cell_size), (0, 0, 0, 0))
    output.alpha_composite(resized, ((cell_size - size[0]) // 2, (cell_size - size[1]) // 2))
    return largest_alpha_component(output, threshold)


def detect_photosphere(frame: Image.Image) -> tuple[float, float, float]:
    weighted: list[tuple[float, float, float]] = []
    for index, (red, green, blue, alpha) in enumerate(frame.getdata()):
        luminance = 0.2126 * red + 0.7152 * green + 0.0722 * blue
        if alpha < 96 or luminance < 92:
            continue
        weight = alpha * luminance
        weighted.append((index % frame.width, index // frame.width, weight))
    if not weighted:
        raise ValueError("Could not detect a stellar photosphere.")

    total = sum(point[2] for point in weighted)
    center_x = sum(point[0] * point[2] for point in weighted) / total
    center_y = sum(point[1] * point[2] for point in weighted) / total
    distances = sorted(
        (((x - center_x) ** 2 + (y - center_y) ** 2) ** 0.5, weight)
        for x, y, weight in weighted
    )
    accumulated = 0.0
    for distance, weight in distances:
        accumulated += weight
        if accumulated >= total * 0.80:
            return center_x, center_y, distance
    raise ValueError("Could not measure a stellar photosphere radius.")


def normalize_solar_frame(source_frame: Image.Image) -> Image.Image:
    cleaned = largest_alpha_component(source_frame, 6)
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
            round(SOLAR_FRAME_SIZE / 2 - center_x * scale),
            round(SOLAR_FRAME_SIZE / 2 - center_y * scale),
        ),
    )
    target = largest_alpha_component(target, 6)
    bounds = target.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("A normalized stellar frame became transparent.")
    if (
        bounds[0] < SOLAR_MARGIN
        or bounds[1] < SOLAR_MARGIN
        or bounds[2] > SOLAR_FRAME_SIZE - SOLAR_MARGIN
        or bounds[3] > SOLAR_FRAME_SIZE - SOLAR_MARGIN
    ):
        target = fit_component(target, SOLAR_FRAME_SIZE, SOLAR_MARGIN, 6)
    return target


def integrated_luminance(frame: Image.Image) -> float:
    total = 0.0
    for red, green, blue, alpha in frame.getdata():
        total += (0.2126 * red + 0.7152 * green + 0.0722 * blue) * alpha
    if total <= 0:
        raise ValueError("A frame had no emitted light.")
    return total


def equalize_luminance(frames: list[Image.Image]) -> list[Image.Image]:
    values = [integrated_luminance(frame) for frame in frames]
    target = min(values)
    output: list[Image.Image] = []
    for frame, value in zip(frames, values, strict=True):
        factor = max(0.68, min(1.0, target / value))
        adjusted = ImageEnhance.Brightness(frame).enhance(factor)
        correction = max(0.96, min(1.0, target / integrated_luminance(adjusted)))
        output.append(ImageEnhance.Brightness(adjusted).enhance(correction))
    return output


def build_scenes() -> None:
    for source_name, output_name in SCENE_SOURCES:
        source = Image.open(SOURCE_ROOT / source_name).convert("RGB")
        target_ratio = SCENE_OUTPUT_SIZE[0] / SCENE_OUTPUT_SIZE[1]
        source_ratio = source.width / source.height
        if source_ratio > target_ratio:
            crop_width = round(source.height * target_ratio)
            left = (source.width - crop_width) // 2
            source = source.crop((left, 0, left + crop_width, source.height))
        else:
            crop_height = round(source.width / target_ratio)
            top = (source.height - crop_height) // 2
            source = source.crop((0, top, source.width, top + crop_height))
        output = source.resize(SCENE_OUTPUT_SIZE, Image.Resampling.LANCZOS)
        output = ImageEnhance.Brightness(output).enhance(0.82)
        output = output.filter(ImageFilter.UnsharpMask(radius=0.8, percent=16, threshold=4))
        path = ASSET_ROOT / output_name
        output.save(path, format="PNG", optimize=True)
        print(path.relative_to(ROOT))


def build_celestial_atlas() -> None:
    atlas = Image.new(
        "RGBA",
        (CELESTIAL_FRAME_SIZE * CELESTIAL_FRAMES_PER_ELEMENT,
         CELESTIAL_FRAME_SIZE * len(CELESTIAL_SOURCES)),
        (0, 0, 0, 0),
    )
    selected_indices = tuple(range(0, 16, 2))
    for element_index, source_name in enumerate(CELESTIAL_SOURCES):
        source = Image.open(SOURCE_ROOT / source_name).convert("RGBA")
        for output_index, source_index in enumerate(selected_indices):
            frame = fit_component(
                source_cell(source, source_index),
                CELESTIAL_FRAME_SIZE,
                CELESTIAL_MARGIN,
                8,
            )
            atlas.alpha_composite(
                frame,
                (output_index * CELESTIAL_FRAME_SIZE,
                 element_index * CELESTIAL_FRAME_SIZE),
            )
    path = ASSET_ROOT / "orbit-celestial-frames-atlas-v6.png"
    atlas.save(path, format="PNG", optimize=True)
    print(path.relative_to(ROOT))


def build_solar_atlas(source_name: str, output_name: str) -> None:
    source = Image.open(SOURCE_ROOT / source_name).convert("RGBA")
    frames = [normalize_solar_frame(source_cell(source, index)) for index in range(16)]
    frames = equalize_luminance(frames)
    atlas = Image.new(
        "RGBA",
        (SOLAR_FRAME_SIZE * SOURCE_COLUMNS, SOLAR_FRAME_SIZE * SOURCE_ROWS),
        (0, 0, 0, 0),
    )
    for index, frame in enumerate(frames):
        atlas.alpha_composite(
            frame,
            ((index % SOURCE_COLUMNS) * SOLAR_FRAME_SIZE,
             (index // SOURCE_COLUMNS) * SOLAR_FRAME_SIZE),
        )
    path = ASSET_ROOT / output_name
    atlas.save(path, format="PNG", optimize=True)
    print(path.relative_to(ROOT))


def main() -> None:
    build_scenes()
    build_celestial_atlas()
    build_solar_atlas("favorite-star-solar-source-v6.png", "favorite-star-solar-atlas-v6.png")
    build_solar_atlas("workspace-star-solar-source-v6.png", "workspace-star-solar-atlas-v6.png")


if __name__ == "__main__":
    main()
