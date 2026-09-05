# Mask raycast batch testing

`MaskRaycastProjector` can discover and project all component masks for the four selected
orbit views without manually creating one request per PNG.

## Expected filenames and frame mapping

Masks must be in one directory and use this filename convention:

```text
CLASS_viewXX_componentYY.png
```

The default mappings are:

| View | Original zero-based frame |
| ---: | ---: |
| 0 | 0 |
| 1 | 1440 |
| 2 | 2250 |
| 3 | 3300 |

These values remain editable in the component's **View Frame Mappings** list.

## Run a batch

1. Select the GameObject containing `MaskRaycastProjector`.
2. Set **Batch Mask Directory** to the mask directory. It may be left empty when the first
   existing mask request already points into that directory.
3. Open the component's three-dot menu and choose **Discover Masks From Batch Directory**.
4. Confirm the Console reports the expected mask count. The discovered requests are sorted by
   view, class, and component number.
5. Choose **Project Configured Masks To Surface** from the same menu.

The projection retains every raw surface hit. It does not reject hits using depth smoothness or
class-specific geometry assumptions.

## CSV report

After a successful projection, a timestamped `mask_projection_report_*.csv` file is written next
to the masks. Set **Report Output Directory** to write it elsewhere. The report contains one row
per mask with pixel counts, samples, hits, misses, hit rate, unique colliders, unique triangles,
and hit-distance statistics.

Disable **Export Csv After Projection** to prevent automatic export. The last in-memory report can
still be written with **Export Last Projection Report CSV**.

## Gizmo inspection

- **Gizmo View Filter = -1** shows every view. Enter `0`, `1`, `2`, or `3` to isolate one view.
- **Gizmo Detection Filter** accepts an exact generated ID such as
  `building_view00_component20`. Leave it empty to show all detections.
- **Color Gizmos By View** assigns a different color to each view.
- **Max Gizmo Hits** limits only visualization; it does not remove stored hits or CSV statistics.

## Provisional building polygons

After projecting the complete batch, choose **Export One Polygon Per Associated Building** from
the component menu. The exporter does not create one polygon per mask. Instead, it:

1. represents every local component by the set of `(collider, triangle)` pairs it hit;
2. associates same-class components from different views when their triangle-overlap threshold
   is satisfied;
3. applies the associations transitively to form physical-building candidates;
4. prefers hits on triangles supported by at least two views; and
5. creates one horizontal convex hull per associated candidate and converts it to WGS84.

The timestamped `associated_building_polygons_*.geojson` is written to the same output directory
as the CSV report. Every feature records its source detection IDs, view indices, raw hit count,
polygon hit count, and whether multi-view triangle support was available.

The default settings are deliberately provisional:

- **Polygon Class Filter:** `building`
- **Minimum Shared Triangles:** `1`
- **Minimum Triangle Overlap Ratio:** `0.1`
- **Minimum Views Per Supported Triangle:** `2`

Candidates with fewer than three distinct WGS84 points cannot form a valid polygon and are
reported as omitted. Raw hits remain unchanged. Triangle overlap can still under-merge buildings
whose visible surfaces differ completely across views or over-merge objects on coarse collider
triangles; reprojection validation is the next association stage.
