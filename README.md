# DTRACK Plugin for Unity: 3DOF marker fork (unofficial)

**Version v1.2.0-3dof.7**, package `io.github.sugurugoto922.dtrack`

An unofficial fork of the [ART DTRACK Plugin for Unity](https://github.com/ar-tracking/UnityDTrackPlugin)
v1.1.3 that adds **single 3DOF marker ('3d') tracking** and **lossless recording of marker data**
for research use. It is not affiliated with or endorsed by Advanced Realtime Tracking GmbH & Co. KG.

Everything else (DTRACK setup, room coordinates, 6DOF Standard Bodies, Flysticks, Fingertracking,
stateful firewall) works exactly as in the original plugin and is described in the
[original documentation (v1.1.3)](https://github.com/ar-tracking/UnityDTrackPlugin/blob/v1.1.3/README.md).

## What this fork adds

| Part | Description |
|---|---|
| SDK: `3d` parser | `DTrackMarker`, `Frame.Markers` / `GetMarker()` |
| `DTrackReceiverMarkers` | Lets game objects follow single markers (nearest-neighbour assignment) |
| `DTrackMarkerLogger` | Records **every** received frame to CSV on its own threads; never overwrites files, reports gaps, missing data and write errors |
| `DTrack` additions | `RegisterReceiver()` / `UnregisterReceiver()`, `ReceiveError` event, `IsStarted` / `IsReceiving` |
| Receiver registration | Receivers register as components, so several receivers on one game object all get data; each frame is delivered once per receiver |
| Sample | *Marker Tracking Scene* (Package Manager → Samples) |

Assembly name (`ART.DTRACKPlugin`), namespaces and script GUIDs are unchanged, so scenes made with the
original plugin keep working. Changes to ART's files are kept minimal and marked `local extension`;
`git diff base-v1.1.3` shows all of them. See [CHANGELOG.md](CHANGELOG.md) for the release history.

## Installation

Requires [git](https://git-scm.com/) (restart Unity Hub/Editor after installing it). In the Package Manager
choose *+* → *Add package from git URL...*, or add to `Packages/manifest.json`:

```json
"io.github.sugurugoto922.dtrack": "https://github.com/SuguruGOTO922/unity-dtrack-3dof.git#v1.2.0-3dof.7"
```

- **Pin a release tag**, so you know which version recorded your data. To update, change the tag.
- To develop the package, reference a local clone instead: `"file:/path/to/unity-dtrack-3dof"`.
- Don't install it together with the official package `com.ar-tracking.dtrack` (same classes).

## Quick start: single markers

1. **DTRACK**: in *Output Settings*, send to the Unity PC's IP and port, and activate outputs
   **`fr`**, **`ts`** and **`3d`**.
2. **Unity**: add a game object with the **DTrack** component; set *Listen Port* and
   *DTrack Coordinates* (*Powerwall* recommended) to match DTRACK.
3. Add **DTrackReceiverMarkers** and fill *Targets* with the objects that shall follow markers.
4. To record data, add **DTrackMarkerLogger** and call `StartRecording()` / `StopRecording()`.

## Following markers: `DTrackReceiverMarkers`

DTRACK assigns single-marker IDs per frame (an ID changes when a marker is lost and found again), so
markers are matched to targets by position, not by ID:

- An unassigned target picks up the nearest free marker within *Acquire Radius* (m; 0 = unlimited);
  place targets near where their markers appear first.
- An assigned target follows the nearest marker that moved less than *Max Jump Distance* (m) since the
  previous frame.
- After *Lost Timeout* (s) without its marker the target becomes unassigned; *Hide When Lost*
  deactivates it meanwhile.

Scripts can read `Slots` (`IsAssigned`, `IsTracked`, `DTrackId`, `Position`), `AllMarkerPositions` /
`AllMarkerIds`, `LastFrameCounter`, and subscribe to `MarkersUpdated`. Positions are Unity world
coordinates in meters.

> This receiver processes only the latest frame per Unity frame. Use it for display, not for recording.

## Recording: `DTrackMarkerLogger`

Records the markers of **every received DTRACK frame**. Frames are taken on the receive thread and
written by a separate thread, so recording doesn't depend on Unity's frame rate, main-thread stalls or
*Run In Background*.

```csharp
if ( logger.StartRecording() )        // new file each call; optional argument: file path
{
    ...
    logger.StopRecording();           // writes all queued frames and closes the files
    Debug.Log( $"{logger.FilePath}: {logger.FramesWritten} frames, {logger.MissingFrames} missing" );
}
```

- **Start**: returns false (with a Console error) if already recording, if DTrack is missing or disabled,
  if its UDP port couldn't be opened, or if the files can't be created. Recording never starts by itself;
  disabling the component (e.g. leaving Play mode) stops it and writes all data.
- **Files**: the path passed to `StartRecording()`, else *File Path*, else
  `Application.persistentDataPath`. Existing files are **never overwritten**: `_1`, `_2`, … is appended
  (actual path in `FilePath`). Flushed every *Flush Interval Seconds*.
- **Data** (`<name>.csv`), one row per marker; frames without markers get one row with `n_markers` = 0:
  `recv_time_s,dtrack_frame,dtrack_ts,n_markers,marker_id,quality,x_mm,y_mm,z_mm,x,y,z`
  (`recv_time_s`: seconds since start, monotonic; `dtrack_ts`: -1 without output `ts`;
  `x_mm`..`z_mm`: raw DTRACK room coordinates; `x`..`z`: Unity world coordinates in m).
- **Events** (`<name>_events.csv`, `recv_time_s,type,detail`): `start`, `gap` (frame-counter jumps),
  `counter_reset`, `no_frame_counter` (output `fr` not active, so gaps can't be detected),
  `receive_error`, `no_data` / `data_resumed`, `write_error`, `stop` (summary).

Problems are reported, never silent:

- **No data** for *No Data Timeout Seconds*: `IsReceivingData` = false, event
  `DataReceivingChanged( logger, false )` and a Console warning; recording continues. An empty
  recording is reported as an error on stop.
- **Write failure** (e.g. disk full): recording stops, `Failed` / `LastError` are set, event
  `RecordingFailed( logger, error )` and a Console error; later frames are counted in `DroppedFrames`.

Statistics (`FramesWritten`, `GapCount`, `MissingFrames`, `ErrorCount`) stay readable after stopping.
Events are invoked on the main thread.

## Custom receivers

Subclass `DTrackReceiver` and call `Register()` / `Unregister()` in `OnEnable()` / `OnDisable()`, like
the built-in receivers; or implement `IDTrackReceiver` and call `DTrack.RegisterReceiver()`.
`ReceiveDTrackFrame()` runs on the receive thread: only store the frame there and do all Unity work in
`Update()`.

## License

BSD-3-Clause, like the original; see [LICENSE.md](LICENSE.md) (ART's copyright notice plus the
fork's additions).
