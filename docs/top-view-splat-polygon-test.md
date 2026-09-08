# Top-view splat polygon experiment

`TopViewSplatProjector` is an independent experiment. It does not change the existing
four-view `MaskRaycastProjector` workflow.

The experiment uses this sequence:

1. Configure the first SRT frame through `SrtDroneRaycastPlayer`.
2. Raycast the exact centre pixel of that frame onto the configured map collider.
3. Place a temporary top-down camera above that surface hit using the map's geodetic
   East/North/Up axes. Its base height is the SRT-configured drone camera's vertical distance
   from the centre hit, converted with the transform JSON; a signed metre offset is then added.
4. Render the Gaussian splat to a PNG.
5. Run the few-shot segmentation model on that PNG outside Unity.
6. Load the resulting binary top-view mask(s).
7. Find four-connected foreground components, trace and simplify each outer contour, and
   raycast its ordered vertices from the same top-view camera.
8. Export one WGS84 GeoJSON polygon per accepted connected component.

## Unity setup

1. Add `TopViewSplatProjector` to the same GameObject as `SrtDroneRaycastPlayer`, or to any
   other active GameObject.
2. Assign the existing `SrtDroneRaycastPlayer` to **Player**.
3. Assign the scene's `GaussianSplatRenderer` to **Gaussian Splat Renderer**. Its local Forward
   axis aligns the image, and its bounds can also provide automatic `Orthographic` coverage.
4. Leave **Reference Frame Count** at `1` to use the first SRT frame.
5. Select **Projection Mode**:
   - `SrtPerspective` copies the lens, physical sensor, focal length/FOV and aspect from the
     camera configured from that SRT frame.
   - `Orthographic` uses parallel rays. Enable **Auto Fit Gaussian Bounds** to fit the splat,
     or disable it and provide the manual coverage width and height in metres.
6. Set **Height Offset Meters** to `0` to keep the reference SRT height. Positive values move
   the top camera upward and negative values move it downward along geodetic Up. The final
   camera must remain above the centre hit.
7. Leave **Image Rotation Degrees** at `0` to align the image with the splat's local Forward
   axis. A non-zero value adds a manual rotation around geodetic Up.
8. Keep **Capture Scene Layers** empty (`Nothing`) when only the splat should appear. The
   Gaussian URP render feature is independent of the ordinary camera culling mask, while the
   visible collider mesh is thereby excluded from the capture.
9. Set an output including the `.png` filename, such as
   `D:\TRIFFID\outputs\orbit_01\top_view_splat.png`, in **Capture Png Path**. Absolute paths
   ignore the selected path root; a directory by itself is not a valid capture path.
10. Open the component's context menu and run **1. Capture Top View Splat PNG**.

## Game View preview

Before capturing, open the component context menu and run **0. Show Or Refresh Game View
Preview**. Unity creates a non-saved `TopViewGamePreviewCamera`, enables it at a high camera
depth and focuses the Game View. The preview uses the same projection, position, lens,
orientation, culling mask and background as the PNG capture.

Choose a Game View aspect matching the reported capture dimensions—for example `16:9` for
`2048x1152`—to avoid display stretching. After changing projection, height, rotation or
coverage, run the preview command again. Use **0b. Stop Game View Preview** when finished; the
preview camera is also removed automatically when the component is disabled.

At **Image Rotation Degrees = 0**, the top of the image follows the assigned splat's projected
local Forward axis. If no renderer is assigned, Unity world Forward is used. The camera still
looks straight down along geodetic Up; only the image-plane orientation changes.

The component also writes `top_view_splat.png.json`. It records the centre, camera pose,
coverage, orientation and resolution used for the image. Polygon projection currently rebuilds
that camera from the current Inspector settings rather than reading the manifest, so do not
change those settings between capture and mask projection.

## Mask requirements

- The mask must be generated from the captured PNG without resizing, cropping, padding,
  rotation or mirroring.
- Its width and height must exactly match the captured PNG. When **Match SRT Camera Aspect**
  is enabled, the component derives the output height from **Capture Width** and the SRT camera
  aspect, so the Inspector's **Capture Height** is not used.
- PNG, JPG and JPEG are accepted. PNG is preferable because JPEG artefacts can modify the
  contour.
- By default a pixel is foreground when the maximum RGB channel is at least `0.5`.
- Enable **Use Alpha As Foreground** for masks stored in the alpha channel.
- Enable **Invert Foreground** for black-foreground/white-background masks.
- A single aggregate semantic mask is supported: every connected region becomes a separate
  candidate polygon. Separate instance masks are also supported.

## Polygon test

1. Put the few-shot output mask(s) in a dedicated directory.
2. Set **Mask Path** to that file or directory.
3. Set **Mask File Name Contains** to `mask`, or leave it empty to read every supported image.
4. Set **Polygon Class Name**, initially `building`.
5. Set **Polygon Geo Json Path** to the desired output.
6. Run **2. Project Top View Masks To Polygons** from the component context menu.

The output contains two-dimensional GeoJSON positions in `[longitude, latitude]` order. The
mean raycast altitude is retained as a feature property instead of being placed in the polygon
coordinates.

## First-test values

| Setting | Suggested value |
|---|---:|
| Reference Frame Count | `1` |
| Projection Mode | `SrtPerspective` |
| Height Offset Meters | `0` |
| Image Rotation Degrees | `0` |
| Match SRT Camera Aspect | enabled |
| Capture Width | `2048` |
| Bounds Padding Fraction | `0.05` |
| Foreground Threshold | `0.5` |
| Minimum Component Pixels | `64` |
| Contour Simplification Pixels | `2` |
| Contour Inset Pixels | `0.35` |
| Minimum Successful Ray Fraction | `0.5` |

In `SrtPerspective`, changing **Height Offset Meters** changes both camera altitude and visible
coverage while preserving the SRT lens. In `Orthographic`, height changes the ray origin but
coverage comes from auto-fit or the manual coverage values. If auto-fit includes distant splat
outliers and makes the site too small in the image, disable it and set **Manual Coverage
Width/Height Metres**. Do not change the projection, height, resolution or coverage after
producing the masks; capture a new image and regenerate the masks whenever these settings
change.
