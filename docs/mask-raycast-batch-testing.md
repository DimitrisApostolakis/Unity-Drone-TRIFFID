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

After projecting the complete batch, choose **Export One Polygon Per Class Cluster** from
the component menu. The exporter does not create one polygon per mask. Instead, it:

1. selects all raycast hits whose class matches **Polygon Class Filter**;
2. converts their WGS84 positions to a local east/north plane measured in metres;
3. applies DBSCAN to find spatially dense clusters without assuming locally consistent depth;
4. treats isolated points as noise rather than allowing them to connect distant buildings; and
5. joins the outer points of each cluster with one horizontal convex hull.

The timestamped `clustered_building_polygons_*.geojson` is written to the same output directory
as the CSV report. Every feature records its source detection IDs, view indices, cluster hit
count, and hull vertex count. The collection metadata records the cluster and noise totals.

The default settings are deliberately provisional:

- **Polygon Class Filter:** `building`
- **Dbscan Epsilon Meters:** `2`
- **Dbscan Minimum Points:** `5`

Clusters with fewer than three distinct WGS84 points cannot form a valid polygon and are reported
as omitted. Raw hits remain unchanged. Reduce epsilon when nearby buildings merge; increase it
when one building fragments into several clusters. DBSCAN can still join objects when a continuous
chain of dense hits bridges them, so the exported polygons remain provisional.

Tree masks such as `green_trees_view00_component17.png` are discovered automatically as class
`green_trees`. They are projected and included in the CSV and gizmos. With **Polygon Class Filter**
set to `building`, they do not create building polygons and are not yet used to remove conflicting
building hits.
