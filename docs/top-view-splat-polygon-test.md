# UAV projection workflow

The previous synthetic top-view capture workflow has been replaced by the photo/SRT `PolygonProjector` workflow. See [UAV projection setup and debugging](uav-projection.md).

`SrtDroneRaycastPlayer` still simulates the drone trajectory. `PointProjector` handles the selected video views; `PolygonProjector` handles segmentation masks from the original drone photograph using its exact capture SRT.
