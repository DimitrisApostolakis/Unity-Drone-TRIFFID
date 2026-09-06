# Mask raycast batch testing

`MaskRaycastProjector` can discover and project all component masks for the four selected
orbit views without manually creating one request per image.

## Expected filenames and frame mapping

Masks may be split across multiple directories and must use this filename convention:

```text
CLASS_viewXX_componentYY.png
CLASS_viewXX_componentYY.jpg
CLASS_viewXX_componentYY.jpeg
```

PNG is recommended for binary masks because it is lossless. JPEG is accepted, but compression
artefacts near mask boundaries can produce extra foreground pixels.

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
3. If another class is stored elsewhere, add its folder under **Additional Batch Mask
   Directories**. For example, add the `detections_generalist_green_trees/masks` folder while
   keeping the building folder as the primary directory.
4. Open the component's three-dot menu and choose **Discover Masks From Batch Directory**.
5. Confirm the Console reports the expected mask count. The discovered requests are sorted by
   view, class, and component number.
6. Choose **Project Configured Masks To Surface** from the same menu.

The projection retains every raw surface hit. It does not reject hits using depth smoothness or
class-specific geometry assumptions.

## CSV report

After a successful projection, a timestamped `mask_projection_report_*.csv` file is written next
to the masks. Set **Report Output Directory** to write it elsewhere. The report contains one row
per mask with pixel counts, samples, hits, misses, hit rate, unique colliders, unique triangles,
and hit-distance statistics. Core and original-boundary sample/hit totals are written separately
so the two stages can be diagnosed independently.

Disable **Export Csv After Projection** to prevent automatic export. The last in-memory report can
still be written with **Export Last Projection Report CSV**.

## Gizmo inspection

- **Gizmo View Filter = -1** shows every view. Enter `0`, `1`, `2`, or `3` to isolate one view.
- **Gizmo Detection Filter** accepts an exact generated ID such as
  `building_view00_component20`. Leave it empty to show all detections.
- **Color Gizmos By View** assigns a different color to each view.
- **Max Gizmo Hits** limits only visualization; it does not remove stored hits or CSV statistics.

## Provisional building polygons

After projecting the complete batch, choose **Export One Polygon Per Class Cluster** from
the component menu. Projection now retains two independent hit sets per mask:

- **core hits** sampled from the eroded mask, used only to find reliable object instances;
- **boundary hits** sampled from the original non-eroded mask contour, used to recover the
  observed outer extent.

The exporter then:

1. selects hits whose class matches **Polygon Class Filter** and converts them to a local
   east/north plane measured in metres;
2. applies DBSCAN only to core hits;
3. retains the dominant core cluster of every connected mask component, suppressing small
   satellite clusters caused by rays landing on unrelated surfaces;
4. assigns original-mask boundary hits only to the dominant cluster of the same detection and
   only when they are within **Boundary Assignment Distance Meters**; and
5. splats accepted hits into a metric occupancy grid, traces its largest exterior ring, and
   simplifies the contour before converting it to WGS84.

The timestamped `clustered_building_polygons_*.geojson` is written to the same output directory
as the CSV report. Every feature records its source detection IDs, view indices, core and assigned
boundary hit counts, occupied-cell count, and final polygon vertex count. A convex hull is used
only as a fallback when a valid grid contour cannot be traced.

The default settings are deliberately provisional:

- **Polygon Class Filter:** `building`
- **Dbscan Epsilon Meters:** `2`
- **Dbscan Minimum Points:** `5`
- **Boundary Assignment Distance Meters:** `4`
- **Polygon Grid Cell Size Meters:** `0.5`
- **Polygon Hit Radius Meters:** `0.75`
- **Polygon Simplification Meters:** `0.5`
- **Polygon Boundary Stride Pixels:** `4`

**Erosion Radius Pixels** affects core clustering but no longer shrinks the boundary used for the
polygon. Reduce **Polygon Hit Radius Meters** when contours are too inflated; increase it slightly
when one contour contains many narrow gaps. Smaller grid cells retain more detail at higher cost,
while the simplification tolerance controls how angular the final GeoJSON ring remains.

Clusters with fewer than three valid contour positions are reported as omitted. Raw core and
boundary hits remain unchanged. A semantically wrong but geometrically dense building mask can
still produce a polygon; the exporter deliberately does not reject objects by view count or area.

Tree masks such as `green_trees_view00_component17.png` are discovered automatically as class
`green_trees`. They are projected and included in the CSV and gizmos. With **Polygon Class Filter**
set to `building`, they do not create building polygons and are not yet used to remove conflicting
building hits.
