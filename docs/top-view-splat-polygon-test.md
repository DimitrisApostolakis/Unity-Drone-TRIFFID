# Top-view splat polygon pipeline

The Unity scene now contains one focused workflow:

1. `SrtDroneRaycastPlayer` loads the DJI SRT and transform JSON.
2. The centre pixel of the selected SRT frame is raycast onto the map collider.
3. `TopViewSplatProjector` places an SRT-perspective camera directly above that hit.
4. Unity captures the Gaussian splat to PNG.
5. An external segmentation model produces one or more top-view masks.
6. Unity traces every connected foreground component, raycasts its contour and exports a
   WGS84 GeoJSON polygon.

## Scene setup

The `DroneSimulation` object in `Drone.unity` already contains both required components.

### SrtDroneRaycastPlayer

- Assign the DJI `.srt` file and the matching transform `.json`.
- Assign **Map Root**, **Target Collider**, and **Drone View Camera**.
- Keep **Estimate Fov From Focal Length** enabled when the SRT provides `focal_len`.
- Use the alignment and gimbal correction values that align the SRT centre ray with the splat.
- Enable **Raycast Target Only** if the top-view rays must be restricted to the assigned collider.

### TopViewSplatProjector

- **Player**: the `SrtDroneRaycastPlayer` on `DroneSimulation`.
- **Gaussian Splat Renderer**: the active splat renderer. Its local Forward axis defines the top
  of the captured image.
- **Reference Frame Count**: the SRT `FrameCnt` whose centre ray defines the capture centre.
- **Height Offset Meters**: signed geodetic metres added to the reference SRT camera height.
  The final height above the centre hit is `reference SRT height + offset`.
- **Image Rotation Degrees**: optional rotation around geodetic Up; normally `0`.
- **Capture Width**: output width. Height is always derived from the configured SRT camera
  aspect ratio so the image and raycasting camera stay identical.

The top-view camera always uses the lens, sensor/FOV and perspective projection configured from
the selected SRT frame. Orthographic and fixed-Unity-height modes are intentionally not part of
this pipeline.

## Capture and preview

Use the component context menu in this order:

1. **0. Show Or Refresh Game View Preview**
2. **1. Capture Top View Splat PNG**
3. Run segmentation on the captured PNG without resizing, cropping, padding, rotation or
   mirroring it.
4. **2. Project Top View Masks To Polygons**
5. **0b. Stop Game View Preview** when the preview is no longer needed.

Both capture and polygon paths must include a filename, for example:

- `D:\TRIFFID\outputs\top_view_splat.png`
- `D:\TRIFFID\outputs\top_view_polygons.geojson`

The capture also writes a `.png.json` manifest containing the exact camera pose, lens,
orientation, height and image dimensions. Do not change those Inspector settings between the
capture and polygon export; regenerate the image and masks after any camera change.

## Mask input

- **Mask Path** accepts one PNG/JPG/JPEG or a directory.
- Every supported image in a directory is processed; filenames do not need to contain `mask`.
- Image dimensions must exactly match the captured top-view PNG.
- Separate instance/component files and aggregate semantic masks are both supported.
- In an aggregate mask, disconnected foreground regions become separate polygons. Touching
  instances remain one connected component and therefore one polygon.
- PNG is recommended because JPEG artefacts can alter the contour.
- Use **Foreground Threshold**, **Use Alpha As Foreground**, and **Invert Foreground** only to
  match the encoding of the segmentation output.

## Polygon controls

- **Minimum Component Pixels** rejects tiny disconnected regions.
- **Contour Simplification Pixels** reduces noisy mask-edge vertices before raycasting.
- **Maximum Contour Vertices** caps raycast work on very detailed contours.
- **Contour Inset Pixels** moves edge samples slightly inside the mask.
- **Minimum Successful Ray Fraction** rejects contours that do not hit enough valid surface.

The exported GeoJSON uses valid two-dimensional `[longitude, latitude]` Polygon coordinates.
Mean surface altitude and diagnostic counts are stored in each feature's properties.
