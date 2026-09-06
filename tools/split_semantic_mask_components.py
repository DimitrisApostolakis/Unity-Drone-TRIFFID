#!/usr/bin/env python3
"""Split combined per-view semantic masks into one binary PNG per component.

The generated filenames follow MaskRaycastProjector's discovery convention:
CLASS_viewXX_componentYY.png.
"""

from __future__ import annotations

import argparse
import re
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage


INPUT_NAME_PATTERN = re.compile(
    r"^view_(?P<view>\d+)_frame_(?P<frame>\d+)_mask\.(?:png|jpe?g)$",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class InputMask:
    path: Path
    view_index: int
    frame_index: int


@dataclass(frozen=True)
class Component:
    label: int
    top: int
    left: int
    bottom: int
    right: int
    pixel_count: int


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Split view_XX_frame_XXXXXX_mask images into one full-resolution "
            "CLASS_viewXX_componentYY.png file per connected component."
        )
    )
    parser.add_argument("input_directory", type=Path, help="Folder containing combined masks.")
    parser.add_argument(
        "--output-directory",
        type=Path,
        help="Destination folder. Defaults to INPUT_DIRECTORY/components.",
    )
    parser.add_argument(
        "--class-name",
        default="building",
        help="Class prefix used in output filenames. Default: building.",
    )
    parser.add_argument(
        "--threshold",
        type=int,
        default=127,
        help="Pixels at or above this grayscale value are foreground. Default: 127.",
    )
    parser.add_argument(
        "--connectivity",
        type=int,
        choices=(4, 8),
        default=8,
        help="Pixel connectivity used to form components. Default: 8.",
    )
    parser.add_argument(
        "--min-pixels",
        type=int,
        default=1,
        help="Smallest component to export. Default: 1 (no size filtering).",
    )
    parser.add_argument(
        "--invert",
        action="store_true",
        help="Treat dark pixels as foreground instead of bright pixels.",
    )
    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="Replace existing generated masks for the selected class.",
    )
    return parser.parse_args()


def validate_args(args: argparse.Namespace) -> tuple[Path, Path, str]:
    input_directory = args.input_directory.expanduser().resolve()
    if not input_directory.is_dir():
        raise ValueError(f"Input directory does not exist: {input_directory}")

    output_directory = (
        args.output_directory.expanduser().resolve()
        if args.output_directory is not None
        else input_directory / "components"
    )
    class_name = args.class_name.strip()
    if not re.fullmatch(r"[A-Za-z0-9_]+", class_name):
        raise ValueError("Class name may contain only letters, digits, and underscores.")
    if not 0 <= args.threshold <= 255:
        raise ValueError("Threshold must be between 0 and 255.")
    if args.min_pixels < 1:
        raise ValueError("Minimum pixels must be at least one.")
    if output_directory == input_directory:
        raise ValueError("Output directory must differ from the input directory.")
    return input_directory, output_directory, class_name


def discover_inputs(input_directory: Path) -> list[InputMask]:
    inputs: list[InputMask] = []
    for path in input_directory.iterdir():
        if not path.is_file():
            continue
        match = INPUT_NAME_PATTERN.fullmatch(path.name)
        if match is None:
            continue
        inputs.append(
            InputMask(
                path=path,
                view_index=int(match.group("view")),
                frame_index=int(match.group("frame")),
            )
        )
    inputs.sort(key=lambda item: (item.view_index, item.frame_index, item.path.name.lower()))
    return inputs


def find_components(
    grayscale: np.ndarray,
    threshold: int,
    connectivity: int,
    invert: bool,
    min_pixels: int,
) -> tuple[np.ndarray, list[Component]]:
    foreground = grayscale < threshold if invert else grayscale >= threshold
    structure = ndimage.generate_binary_structure(2, 1 if connectivity == 4 else 2)
    labels, component_count = ndimage.label(foreground, structure=structure)
    if component_count == 0:
        return labels, []

    pixel_counts = np.bincount(labels.ravel(), minlength=component_count + 1)
    slices = ndimage.find_objects(labels, max_label=component_count)
    components: list[Component] = []
    for label_index, bounds in enumerate(slices, start=1):
        if bounds is None:
            continue
        pixel_count = int(pixel_counts[label_index])
        if pixel_count < min_pixels:
            continue
        y_slice, x_slice = bounds
        components.append(
            Component(
                label=label_index,
                top=int(y_slice.start),
                left=int(x_slice.start),
                bottom=int(y_slice.stop),
                right=int(x_slice.stop),
                pixel_count=pixel_count,
            )
        )

    components.sort(key=lambda item: (item.top, item.left, -item.pixel_count, item.label))
    return labels, components


def save_component(
    labels: np.ndarray,
    component: Component,
    output_path: Path,
) -> None:
    crop = labels[component.top : component.bottom, component.left : component.right]
    binary_crop = np.where(crop == component.label, 255, 0).astype(np.uint8)
    output = Image.new("L", (labels.shape[1], labels.shape[0]), color=0)
    output.paste(Image.fromarray(binary_crop, mode="L"), (component.left, component.top))
    output.save(output_path, format="PNG", compress_level=1)


def prepare_output_directory(
    output_directory: Path,
    class_name: str,
    overwrite: bool,
) -> None:
    output_directory.mkdir(parents=True, exist_ok=True)
    existing = sorted(output_directory.glob(f"{class_name}_view*_component*.png"))
    if existing and not overwrite:
        raise ValueError(
            f"Found {len(existing)} existing generated mask(s) in {output_directory}. "
            "Use --overwrite to replace them."
        )
    if overwrite:
        for path in existing:
            path.unlink()


def split_masks(args: argparse.Namespace) -> int:
    input_directory, output_directory, class_name = validate_args(args)
    inputs = discover_inputs(input_directory)
    if not inputs:
        raise ValueError(
            "No files matching view_XX_frame_XXXXXX_mask.png/.jpg/.jpeg were found in "
            f"{input_directory}."
        )

    prepare_output_directory(output_directory, class_name, args.overwrite)
    next_component_by_view: dict[int, int] = defaultdict(int)
    total_components = 0
    for input_mask in inputs:
        with Image.open(input_mask.path) as image:
            grayscale = np.asarray(image.convert("L"), dtype=np.uint8)
        labels, components = find_components(
            grayscale,
            args.threshold,
            args.connectivity,
            args.invert,
            args.min_pixels,
        )
        first_index = next_component_by_view[input_mask.view_index]
        for offset, component in enumerate(components):
            component_index = first_index + offset
            output_name = (
                f"{class_name}_view{input_mask.view_index:02d}_"
                f"component{component_index:02d}.png"
            )
            save_component(labels, component, output_directory / output_name)
        next_component_by_view[input_mask.view_index] += len(components)
        total_components += len(components)
        print(
            f"view {input_mask.view_index:02d}, frame {input_mask.frame_index:06d}: "
            f"{len(components)} component(s) from {input_mask.path.name}"
        )

    print(f"Created {total_components} component mask(s) in {output_directory}")
    return 0


def main() -> int:
    args = parse_args()
    try:
        return split_masks(args)
    except (OSError, ValueError) as exception:
        print(f"error: {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
