# UAV projection in Unity

Open `projects/Drone` and the `Drone` scene. `DroneSimulation` now has three focused components:

| Component | Responsibility |
|---|---|
| `SrtDroneRaycastPlayer` | Simulate the trajectory, calibrate camera poses, and provide shared map raycasting/georeferencing |
| `PointProjector` | Import `point_detections.json` and raycast one foreground pixel per observation |
| `PolygonProjector` | Import `polygon_segmentations.json` and project its mask boundaries using the original photo and capture SRT |

`TopViewSplatProjector` has been replaced by `PolygonProjector`. Its script GUID is preserved so existing scene references survive; its obsolete synthetic capture/height/mask-directory settings are removed. This route uses a real drone photo. It no longer captures a synthetic splat image.

## Shared setup and trajectory

In `SrtDroneRaycastPlayer`, retain the existing SRT and transform JSON, Map Root, Drone View Camera, collider, lens, altitude and alignment settings. The existing trajectory position, timing, interpolation, loop and camera-preview behavior is retained. `Play Trajectory` pauses/resumes time advancement.

Projection uses a frozen snapshot of the actual assigned SRT camera. Each capture synchronously saves the player/camera state, sets the source-image aspect, runs the same `ConfigurePose` path as playback at the exact SRT record, copies the camera and its exact projection/view matrices, then restores local transforms, full camera settings and playback state before returning or yielding. The temporary state is not rendered. There is no separate ENU-up orientation calculation. Every capture checks nine snapshot rays against the real camera and verifies restoration; a mismatch aborts export. Parsing and interpolation are in the `.Telemetry.cs` partial; projection calibration is in `.Projection.cs`; both belong to the same component and keep its existing serialized field names/GUID.

Projectors work in Edit and Play Mode, on the Unity main thread. Collider loading/alignment must finish before pressing Project. The map must remain fixed during a projection job.

## Project the eight views to points

In `PointProjector`:

1. Assign the shared player (already assigned in `Drone.unity`).
2. Set **Observations JSON Path** to the perception `point_detections.json`. Absolute Windows paths are supported, e.g. a file under `D:\Projects\TRIFFID-HUA-Perception-UAV\uav_samples\merged_cut_detections`.
3. Ensure the player's SRT is the same one used for those exported frames.
4. Choose output path/root, then **Validate Inputs** and **Project & Export**.

The default output is `Exports/uav_points.geojson` under `Application.persistentDataPath`. The status box prints the resolved output path.

The source `telemetry.frame_index + 1` identifies the exact SRT FrameCnt. `source.frame_index` identifies the decoded MP4 frame and can differ. The telemetry timestamp is checked against the SRT. This prevents a lower-rate or cut-video frame number from silently selecting the wrong pose.

The pipeline uses `raycast_point_xy`, which lies inside the foreground mask, instead of assuming the mathematical centroid lies on the object. Rays pass through pixel centres; mask-derived bounding boxes and true centroids are retained as properties.

## Project a photo to polygons

The perception command `segment-top-view` must segment the **same original photo** used here. It produces `polygon_segmentations.json` with task `polygon_segmentation`.

In `PolygonProjector`:

1. Assign the shared player and **Observations JSON Path**.
2. Set **Photo Path** to the original, unmodified PNG/JPG submitted for segmentation.
3. Set **Photo SRT Path** to the DJI SRT containing that photo's capture telemetry.
4. Set **Capture Frame Count** to the exact DJI `FrameCnt` at capture (1-based). A single-record SRT is supported; use that record's actual FrameCnt.
5. Validate, then project/export. The default output is `Exports/uav_polygons.geojson` under `Application.persistentDataPath`.

The SRT must include latitude, longitude, relative/absolute altitude, gimbal yaw/pitch/roll and focal length. Capture selection is exact; the code does not guess a timestamp or assume nadir orientation. Image dimensions and filename are checked against the JSON. These checks cannot prove that two same-name/same-size images have identical content: always supply the original photo. A cropped, resized or reoriented photo requires matching recalibrated inputs.

Projection snapshots the actual SRT playback camera using the photo aspect ratio. Image orientation, including near-nadir behavior, follows the player's existing camera convention. Lens and alignment settings come from the shared player. Absolute-altitude reference and map calibration still need to match the capture. No synthetic camera height or image rotation offset is applied.

Binary masks use the perception's cropped **row-major RLE**, not COCO RLE. Only the current observation's crop is decoded. Pixel-edge tracing preserves concavity and holes; diagonal-only foreground islands become separate polygon members. Collinear boundary vertices are removed without changing the pixel footprint; long edges are sampled at the configured maximum spacing. Separate exteriors export as a `MultiPolygon`.

Every contour sample, including holes, must hit the collider. If a sample misses, the whole observation is recorded as skipped. The code does not bridge missing rays or fill holes. Collapsed and self-intersecting geographic rings are also rejected. There is a vertex/memory budget to reject excessively fragmented masks.

## Inspector and visual debugging

Each projector groups Inputs, Output, Debug and Performance; the polygon inspector also groups Photo Capture and Polygon Sampling. Advanced sections start collapsed. Settings are locked while a job runs.

The read-only **Camera (from SRT Player)** field shows the shared camera used by both projectors. Assign it on `SrtDroneRaycastPlayer` under **Drone View Camera**; projection requires that camera and has no independent lens or pose settings.

- **Validate Inputs:** check paths, schema, capture pose, dimensions and RLE without exporting.
- **Project & Export:** process with the configured ray budget per frame/editor update.
- **Cancel:** release the temporary camera and preserve the previous output file.
- **Clear Debug:** discard stored visual samples.

Select the projector's GameObject and enable Scene Gizmos. Cyan markers indicate hits, red rays indicate misses, and yellow outlines indicate accepted polygon rings. Enable Show Rays / Show Labels as needed. Maximum Debug Samples bounds retained samples; debug visualization does not create scene marker objects or rerun physics each frame. A missed ray's red line is a short direction indicator, not the full configured raycast range.

Both outputs are GeoJSON FeatureCollections in `[longitude, latitude, altitude]` order. Features preserve class/source/observation IDs and mask-derived image metadata; surface-hit records include world position, normal, distance, collider and triangle index. Point and polygon files are independent. `projection.skipped_observations` lists failures.

**The point export contains raw observations with no duplicate filtering or cross-view merging.** Each observation whose foreground ray hits the map exports one feature, even when several views see the same object. Point projection casts one ray per observation; duplicate-matching samples, grids and thresholds are removed. Unity no longer produces `uav_objects.geojson` or `dedup_report.json`. Existing files from older runs are not updated or deleted; use the newly exported `uav_points.geojson`.

If you previously added the duplicate-merger component to your local scene or prefab, remove that component (it may appear as **Missing (Mono Script)** after updating). The checked-in scene never included it. The PointProjector inspector now contains only projection inputs, output, debug and performance settings.

Completed output is written by atomic replacement. Invalid input or cancellation leaves the previous file intact. Projection raycasts reuse a growing non-allocating hit buffer; a full buffer is retried so target filtering cannot silently discard nearer hits.

## Validation

Use **TRIFFID → UAV → Run Projection Core Checks** for exhaustive 3×3 binary-mask topology checks plus RLE, pixel-centre, GeoJSON ring and perception-schema checks. No Unity test-framework dependency is needed.

GitHub Actions runs these same C# core checks on pushes/PRs affecting the UAV code. They can also be run outside Unity with Mono:

```bash
NEWTONSOFT_DLL=/path/to/Newtonsoft.Json.dll tests/run-core-checks.sh
```

The small vector shim is outside `Assets` and is used only by the standalone checks. These core checks do not validate Unity's camera optics, physics, inspector rendering or your actual scene alignment. Before relying on geographic output, inspect both routes against known scene locations in Unity, test cancellation, and confirm the simulated trajectory continues unchanged while projection runs.

## SRT camera parity check

After a camera-snapshot update, rerun **PointProjector → Project & Export** and inspect `uav_points.geojson`. Existing detection masks/supports do not need new model inference. Old exports retain the previous projection results and cannot demonstrate the fix. New exports record `projection.camera_configuration = srt_player_camera_snapshot_v1`.

Use **Check Projection Camera Parity** in the SRT player inspector (also available from its component context menu). This checks up to eight loaded SRT records, two image aspects (3840×2160 and square), and nine pixel rays per capture. It verifies camera state and playback restoration and does not require model inference or map ray hits. The live Game View aspect is restored after testing. This is a Unity runtime check and is not executed by the standalone C# core runner.

This change preserves the configured input paths, flips, map transforms, lens choices and trajectory code; no scene asset changes are required. Camera parity addresses the previously separate projection-camera orientation. It does not establish that the collider matches the Gaussian rendering or that georeferencing is accurate.
