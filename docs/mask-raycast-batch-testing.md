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

Combined semantic masks are also accepted when **Discover Aggregate Class Masks** is enabled:

```text
view_XX_frame_XXXXXX_mask.png
```

Because this name does not contain a class, **Aggregate Mask Class Name** supplies it.

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

## Combined FSS-SAM3 masks

Use the four original FSS-SAM3 files directly. Set **Batch Mask Directory** to their folder, enable
**Discover Aggregate Class Masks**, set **Aggregate Mask Class Name** to `building`, and choose
**Multi View Consensus** under **Polygon Cluster Association Mode**. This mode confirms a spatial
cell only when nearby projected building hits come from at least two distinct views. Connected
confirmed cells seed one building cluster, and nearby single-view hits recover its observed edge.
Use **Aggregate Class Mask** only as a diagnostic mode that retains every DBSCAN cluster.

The earlier connected-component splitter remains available for models whose output already has
well-separated instances. It is not required for aggregate or consensus mode. From the repository
root, split all four combined masks with:

```powershell
py tools\split_semantic_mask_components.py `
  "D:\TRIFFID\outputs\orbit_01\fss_sam3\building\masks" `
  --class-name building
```

The script uses 8-connected components and writes full-resolution binary PNGs to the `components`
subfolder with names such as `building_view00_component00.png`. Its default **Min Pixels** value is
one, so it does not discard small structures such as kiosks. Use **Dominant Cluster Per Detection**
only when those generated components genuinely correspond to separate objects.

The script requires Pillow, NumPy, and SciPy. Install them in the Python environment used to run
the command if they are not already available:

```powershell
py -m pip install Pillow numpy scipy
```

Rerunning against a populated output folder stops safely. Pass `--overwrite` only when the existing
generated component masks should be replaced.

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
2. either applies DBSCAN or builds a metric occupancy grid carrying the distinct view IDs that
   support each cell;
3. in **Multi View Consensus** mode, keeps cells supported by the configured number of views,
   connects neighbouring confirmed cells, and attaches nearby single-view core hits;
4. assigns original-mask boundary hits to the relevant same-detection cluster in dominant mode,
   or to the nearest retained cluster in aggregate and consensus modes; and
5. splats accepted hits into a metric occupancy grid, traces its largest exterior ring, and
   simplifies the contour before converting it to WGS84.

The timestamped `clustered_building_polygons_*.geojson` is written to the same output directory
as the CSV report. Every feature records its source detection IDs, view indices, core and assigned
boundary hit counts, occupied-cell count, and final polygon vertex count. Consensus features also
record their supporting views, maximum view support, confirmed-cell count, and the number of
single-view core hits used only for boundary expansion. A convex hull is used only as a fallback
when a valid grid contour cannot be traced.

The default settings are deliberately provisional:

- **Polygon Class Filter:** `building`
- **Polygon Cluster Association Mode:** `Multi View Consensus` for multi-view semantic masks
- **Consensus Grid Cell Size Meters:** `0.5`
- **Consensus Overlap Tolerance Meters:** `1.25`
- **Consensus Minimum Supporting Views:** `2`
- **Consensus Single View Expansion Distance Meters:** `1.5`
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

In consensus mode a single-view hit cannot seed a polygon. It can only expand a nearby cluster
that already contains the required multi-view support. **Consensus Overlap Tolerance Meters**
absorbs small camera/collider alignment errors; lowering it separates nearby structures, while
raising it confirms more sparse or slightly misaligned evidence.

Clusters with fewer than three valid contour positions are reported as omitted. Raw core and
boundary hits remain unchanged. A semantically wrong but geometrically dense building mask is
not rejected by geometry alone. The optional semantic-conflict stage compares every building
core hit with nearby core hits from contradictory classes. It rejects a candidate only when both
the configured hit-count and overlap-ratio thresholds are reached. It deliberately does not
reject objects by view count or area.

Tree masks such as `green_trees_view00_component17.png` are discovered automatically as class
`green_trees`. They are projected and included in the CSV and gizmos. With **Polygon Class Filter**
set to `building`, the default semantic filter uses them as contradictory evidence with these
initial settings:

- **Exclude Semantic Conflicts:** enabled
- **Semantic Conflict Class Filters:** `green_trees,tree,vegetation`
- **Semantic Conflict Distance Meters:** `1.5`
- **Semantic Conflict Ratio Threshold:** `0.65`
- **Semantic Conflict Minimum Hits:** `5`

For each retained feature the GeoJSON stores its conflict ratio and the tree detection IDs that
contributed to it. Removed candidates are listed under
`metadata.rejected_semantic_conflicts`, so thresholds can be tuned without guessing. Lower the
ratio threshold to remove more candidates; raise it when legitimate buildings near tree canopies
are rejected. This filter can only remove false buildings supported by one of the configured
contradictory classes; unrelated false building masks still require better detections or another
semantic class.

For a controlled before/after test, project all masks once, disable **Exclude Semantic
Conflicts**, and export a baseline. The polygons remain present but include their measured
`semantic_conflict_ratio`. Then enable the option and export again; no second raycast is needed.
Compare the two timestamped GeoJSON files and inspect
`metadata.rejected_semantic_conflicts` in the filtered file.
