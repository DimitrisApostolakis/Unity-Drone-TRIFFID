#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
: "${NEWTONSOFT_DLL:?Set NEWTONSOFT_DLL to Newtonsoft.Json.dll (Unity package or Mono package).}"
task_check_dir="$(mktemp -d)"
trap 'rm -rf "$task_check_dir"' EXIT
cp "$NEWTONSOFT_DLL" "$task_check_dir/Newtonsoft.Json.dll"
mcs -langversion:latest -r:"$NEWTONSOFT_DLL" -out:"$task_check_dir/checks.exe" \
  tests/Support/UnityVectors.cs \
  'projects/Drone/Assets/Custom Scripts/UavProjection/UavObservationDocument.cs' \
  'projects/Drone/Assets/Custom Scripts/UavProjection/UavMaskContours.cs' \
  'projects/Drone/Assets/Custom Scripts/UavProjection/UavGeoJsonRing.cs' \
  projects/Drone/Assets/Editor/UavProjectionChecks.cs
mono "$task_check_dir/checks.exe"
