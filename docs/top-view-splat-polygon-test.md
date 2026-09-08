# Top-view splat polygon experiment

`TopViewSplatProjector` is an independent experiment. It does not change the existing
four-view `MaskRaycastProjector` workflow.

The experiment uses this sequence:

1. Configure the first SRT frame through `SrtDroneRaycastPlayer`.
2. Raycast the exact centre pixel of that frame onto the configured map collider.
3. Place a temporary orthographic camera above that surface hit using the map's geodetic
   East/North/Up axes.
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
3. Assign the scene's `GaussianSplatRenderer` to **Gaussian Splat Renderer**.
4. Leave **Reference Frame Count** at `1` to use the first SRT frame.
5. Leave **Auto Fit Gaussian Bounds** enabled for the first test. The camera remains centred
   on the first-frame centre hit; auto-fit only determines how much area is visible around it.
6. Keep **Capture Scene Layers** empty (`Nothing`) when only the splat should appear. The
   Gaussian URP render feature is independent of the ordinary camera culling mask, while the
   visible collider mesh is thereby excluded from the capture.
7. Set an output such as `D:\TRIFFID\outputs\orbit_01\top_view_splat.png` in
   **Capture Png Path**. Absolute paths ignore the selected path root.
8. Open the component's context menu and run **1. Capture Top View Splat PNG**.

The component also writes `top_view_splat.png.json`. It records the centre, camera pose,
coverage, orientation and resolution used for the image.

## Mask requirements

- The mask must be generated from the captured PNG without resizing, cropping, padding,
  rotation or mirroring.
- Its width and height must exactly match **Capture Width** and **Capture Height**.
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
| Camera Height Metres | `150` |
| Capture Width / Height | `2048 / 2048` |
| Bounds Padding Fraction | `0.05` |
| Foreground Threshold | `0.5` |
| Minimum Component Pixels | `64` |
| Contour Simplification Pixels | `2` |
| Contour Inset Pixels | `0.35` |
| Minimum Successful Ray Fraction | `0.5` |

If auto-fit includes distant splat outliers and makes the site too small in the image, disable
it and set **Manual Coverage Width/Height Metres**. Do not change the camera settings after
producing the masks; capture a new image and regenerate the masks whenever the top-view
configuration changes.
