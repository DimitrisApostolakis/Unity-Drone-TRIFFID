# Point duplicate merging

This is a second, model-independent stage after point projection. The mesh remains intact. A virtual grid in local ENU metres indexes nearby observations; a cell can contain any number of separate people, cars or other objects.

## Run

1. Update the existing `refactor/uav-point-polygon-projectors` branch. This update does not change `Drone.unity` or the SRT player.
2. Verify trajectory/camera/mesh alignment first.
3. In `PointProjector`, leave **Collect Merge Evidence** enabled (default) and **Foreground Samples Per Observation** at 25 to start. Run **Project & Export** again using the existing perception JSON. No SANSA inference is needed. An older projection file lacks the evidence this stage needs and is rejected with an explanation.
4. Click **Add / Select Duplicate Merger** on the projector inspector. This adds `PointDuplicateMerger` to the same object only when you click; it does not replace the scene. Its input is set to the last successful projection export. After a domain reload, set the input path manually or rerun projection and use **Use Last PointProjector Export**.
5. In the merger, inspect Matching and the class rules, then click **Merge & Export**.
6. Select the GameObject with Scene Gizmos enabled. Choose Original, Merged or Both. Enable grid cells, member links, labels or rejected links when needed. Debug samples are bounded.

Default outputs under `Application.persistentDataPath`:

| File | Meaning |
|---|---|
| `Exports/uav_points.geojson` | All successfully projected raw observations, including new sampled mask evidence |
| `Exports/uav_objects.geojson` | One representative feature per object group; unmatched observations remain singleton objects |
| `Exports/dedup_report.json` | Membership, candidate-pair scores/reasons, settings and counts |

The merger never overwrites its input. The objects and report have a shared generation ID and are individually replaced atomically. They are not a two-file atomic transaction: after a file-system write failure, check their generation IDs before using them together. Edit-mode matching has a cancellable progress dialog. Cancellation occurs before output publication and preserves prior outputs. Play-mode matching runs synchronously; very large captures may need a future incremental runner. The present target is the eight-view capture.

## Evidence and matching

`PointProjector` takes distinct, deterministic foreground pixel samples distributed through each cropped mask. The bounding box limits the crop; sampled pixels must belong to foreground. Background bbox corners are not used as object footprint evidence. The anchor point, successful surface samples, exact mask RLE and original camera world-to-clip matrix are stored. The raw projection's ray counts now include these additional evidence rays; only anchors are added to its normal visual debug buffer.

A map-derived inverse basis converts world points into a common metric ENU frame. It accounts for the map's rotation, scale and position-flip conventions without changing SRT playback. Its arbitrary origin is Unity world zero; relative distances are in metres. The matrix is stored with the export so the merger does not require live camera movement, a renderer, model inference or raycasting. Projection cameras are frozen snapshots of the assigned SRT playback camera; captures briefly configure and restore that camera synchronously, before any job yields.

Association proceeds as follows:

1. Grid lookup finds nearby candidate centres, including neighbouring cells. Cell size affects lookup cost, not object identity.
2. Candidate classes must match by class ID and name. Distinct observations from the same source view are preserved.
3. Apply the class-specific maximum 3D centre distance and a sampled-extent ratio gate.
4. In both directions, sample points must reproject inside the other observation's actual foreground mask and lie within the surface tolerance of one of its recorded metric surface samples. The lower of the two coverage fractions must pass the agreement threshold. This is approximate sampled surface/mask consistency, not dense 3D IoU or a fresh occlusion-depth test.
5. Require a mutual best match per pair of views, with a score margin over the runner-up. Ambiguous choices remain separate.
6. A group can contain at most one observation per view. Every cross-pair between joining groups must be a supported mutual match (complete-link consistency). A chain A↔B↔C cannot combine A and C without A↔C support.
7. Choose an actual observed anchor closest to the other group anchors (medoid) as the representative; do not average the point off its recorded surface. Preserve all member IDs and source IDs.

IDs hash the sorted member observation IDs and are stable under input reordering. They are scoped to this static capture; they are not long-term tracking IDs. A changed membership changes its ID.

## Starting controls

These are editable starting values, **not calibrated accuracy claims**:

| Class | Maximum centre distance (m) | Surface-sample tolerance (m) |
|---|---:|---:|
| Person | 0.75 | 0.20 |
| Window | 0.60 | 0.15 |
| Civilian vehicle | 2.50 | 0.50 |
| Other point classes | 1.00 | 0.25 |

Default grid cell = 1 m; reciprocal agreement = 0.6; minimum successful samples = 4; maximum sampled-extent ratio = 3; ambiguity margin = 0.1. Scores are agreement minus `0.1 × centre_distance / class_distance_gate`; they are not calibrated probabilities. A centre gate can span at most 16 grid cells to bound neighbourhood enumeration. To support different capture scales, adjust class gates after reviewing evidence, not to obtain a desired object count.

Sparse or failed evidence remains in the raw output and is kept as a singleton. Unmatched single-view observations are not discarded as false positives. Rejected-pair reasons apply only to spatial candidate pairs; unrelated classes and pairs beyond the centre gate are not exhaustively listed.

## People and limits

Three separately detected people can occupy one grid cell and still produce three objects. Matching preserves separate instances already present in each view. The current SANSA pipeline produces connected class-mask components; touching people or touching vehicles can still form a single observation. This merger cannot reliably split such a group. Per-person/vehicle instance boxes and masks upstream are needed to resolve that case. No crowd-splitting heuristic or extra detector is silently added.

This stage assumes a static scene. Moving people/vehicles, severe occlusion, sparse/incorrect reconstruction, camera errors, and mask fragments can prevent correct association. A parked car missing from the mesh can project onto background surfaces; the conservative checks may leave repeat sightings separate. Fix projection errors before changing merge thresholds.

Use **TRIFFID → UAV → Run Duplicate Merge Checks** for synthetic regressions covering multiple people in a cell, mutual matching, ambiguity, complete-link constraints, negative grid boundaries, class/height separation, mask evidence, singleton retention, deterministic IDs and cancellation. The full **Run Projection Core Checks** also runs them, and GitHub Actions compiles/executes the shared C# core. Unity integration and real capture accuracy require local validation.
