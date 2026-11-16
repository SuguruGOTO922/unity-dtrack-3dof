
# List of ART DTRACK Plugin for Unity Game Engine releases

## Unreleased (unofficial fork)

- README rewritten as a short guide to this fork's additions; the original documentation is linked
  (ART DTRACK Plugin v1.1.3 README on GitHub)

## v1.2.0-3dof.7 (2026-11-16, unofficial fork)

- `DTrackReceiver` registers itself as component (`RegisterReceiver`) instead of its game object: several
  receivers on one game object (e.g. `DTrackReceiverMarkers` and `DTrackMarkerLogger`) all receive data,
  independent of component order
- `DTrack` delivers each frame only once per receiver, even if registered by both `RegisterTarget` and `RegisterReceiver`

## v1.2.0-3dof.6 (2026-11-14, unofficial fork)

- Package renamed to `io.github.sugurugoto922.dtrack` ("DTRACK Plugin for Unity (3DOF marker fork)"),
  hosted at https://github.com/SuguruGOTO922/unity-dtrack-3dof; marked as unofficial fork of the
  ART DTRACK Plugin v1.1.3 in package.json and README; installation instructions for this fork
- Copyright notice for the fork's additions (LICENSE.md and new source files); license unchanged (BSD-3-Clause)

## v1.2.0-3dof.5 (2026-11-14, local fork)

- `DTrackMarkerLogger`: `StartRecording()` fails if DTrack is disabled or its UDP port could not be opened;
  'no_data'/'data_resumed' events, `IsReceivingData`, `DataReceivingChanged`; empty recordings reported
- `DTrackMarkerLogger`: write failures stop the recording (`Failed`, `LastError`, `RecordingFailed`,
  `DroppedFrames`, 'write_error' event); `StopRecording()` never throws
- `DTrack`: properties `IsStarted`, `IsReceiving`

## v1.2.0-3dof.4 (2026-11-14, local fork)

- `DTrackMarkerLogger` never overwrites existing files: appends `_1`, `_2`, ... to the file name
  (atomic creation with `FileMode.CreateNew`)

## v1.2.0-3dof.3 (2026-11-14, local fork)

- `DTrackMarkerLogger` no longer records while enabled: `StartRecording()` / `StopRecording()`
  control recording (new file per call); `IsRecording`; `CurrentFilePath`/`CurrentEventsFilePath`
  renamed to `FilePath`/`EventsFilePath`

## v1.2.0-3dof.2 (2026-11-14, local fork)

- `DTrackMarkerLogger` records every received frame (receive thread + writer thread) with raw
  DTRACK values, receive time, and an events file reporting frame counter gaps and receive errors;
  slot based logging removed
- `DTrack`: `RegisterReceiver`/`UnregisterReceiver` for IDTrackReceiver components, event `ReceiveError`

## v1.2.0-3dof.1 (2026-11-14, local fork, based on v1.1.3)

- SDK parses single 3DOF markers ('3d'): `DTrackMarker`, `Frame.Markers` / `GetMarker()`
- New `DTrackReceiverMarkers`: assigns markers to a fixed set of GameObjects by
  nearest-neighbour tracking (DTRACK marker IDs are not persistent)
- New `DTrackMarkerLogger`: records marker positions to CSV
- New sample 'Marker Tracking Scene'

## v1.1.3 (2024-05-03)

- (Optionally) enables UDP connection through stateful firewall
- Fixing DTrack1 parsing error (cf. GitHub issue #6)

## v1.1.2 (2023-04-01)

- Some reorganization to allow release as UPM package, esp. in OpenUPM
  (https://www.openupm.com/packages/com.ar-tracking.dtrack/)
- Example script to navigate a 'camera' using a Flystick joystick

## v1.1.1 (2022-11-30)

- Supports DTRACK room calibration modes 'Powerwall' and 'Normal'
- Supports Fingertracking (gl) with 'Leap Motion Realistic Female Hands'

## v1.1.0 (2022-10-24)

- Supports Fingertracking (gl) with 'Leap Motion Realistic Male Hands'
- Introduces new Unity events on changed Flystick button state and
  on changed Flystick joystick/trigger values
- Including example script to listen for Flystick Unity events

## v1.0.3 (2022-05-13)

- Supports additional Flystick2+ buttons and trigger
- Fixing Flystick parsing error (cf. GitHub issue #3)

## v1.0.2 (2020-07-28)

- Fixing locale issue (cf. GitHub issue #1)
- Fixing incorrect accessor (cf. GitHub issue #2)

## v1.0.0 (2020-02-25)

- Initial release for Unity 2019.1.0f2 or later
- Supports 6DOF standard targets (6d), Flysticks (6df2)

