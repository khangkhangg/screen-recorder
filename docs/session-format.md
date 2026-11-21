# Recording folder and session.json, version 1

This page is for the people who write the macOS and Linux apps. Their recordings should use the same folder layout and the same `session.json`, so one editor can open a recording from any of the 3 apps.

## Files in a recording folder

Each recording makes one folder named by the local start time, such as `2026-11-21 15-06-39`. If that name is taken, the app adds ` (2)`, ` (3)` and so on. Files for sources that are off are not written.

| File | Content |
| --- | --- |
| `final.mp4` | H.264 video of the screen with the camera and logo drawn in, plus one AAC track that mixes the microphone and computer sound |
| `screen.mp4` | H.264 video of the screen only, no overlays, no sound |
| `webcam.mp4` | H.264 video of the full camera frame at 30 frames per second, no shape mask, not mirrored |
| `mic.wav` | Microphone only |
| `system.wav` | Computer sound only |
| `session.json` | The description of the recording below |

WAV files are 48,000 Hz, 2 channels, 32-bit float. All video files have a constant frame rate. When the screen did not change, the last frame is repeated.

## Timing

All files start at media time 0 on one shared clock, so they line up without moving any of them. Pauses are cut from every file, so a pause leaves no gap and all files keep the same length.

Times in `session.json` are media time in seconds, which means time in the files, after pauses are cut.

## Big screens

Screens with more pixels than 2560x1440 record at 30 frames per second at most. On those screens `final.mp4` keeps the full size and `screen.mp4` is scaled to fit inside 1920x1080, keeping its proportions. Each file's real size is in `sources`.

## Fields

The file is UTF-8 JSON with camelCase names. Enum values are written as their names, such as `"Circle"`.

| Field | Type | Meaning |
| --- | --- | --- |
| `version` | integer | Always `1` for this format. A reader refuses a file with a higher version. |
| `startedAt` | string | Local wall clock time when the recording started, ISO 8601 with UTC offset |
| `frameSize` | object | `width` and `height` in pixels of `final.mp4`, the frame the layout is measured against |
| `frameRate` | integer | Frames per second of `final.mp4` and `screen.mp4` |
| `targetKind` | string | `"monitor"`, `"window"` or `"region"` |
| `sources` | array | One entry per file written, see below |
| `layoutChanges` | array | The layout at the start and after each change, see below |
| `pauses` | array | One entry per pause, see below |
| `droppedFrames` | integer | Frames that `final.mp4` skipped because the encoder fell behind. The previous frame is shown in their place. Other files never drop frames. |
| `notes` | array of strings | Plain sentences about problems, such as `"webcam lost at 1.1 s"` or `"screen.mp4 scaled to 1920x1080"` |

Fields of a `sources` entry:

| Field | Type | Meaning |
| --- | --- | --- |
| `file` | string | File name inside the folder, such as `"screen.mp4"` |
| `kind` | string | `"final"`, `"screen"`, `"webcam"`, `"mic"` or `"system"` |
| `offsetSeconds` | number | Media time where the file starts. It is `0` for every file today. |
| `width` | integer or null | Video width in pixels, `null` for sound files |
| `height` | integer or null | Video height in pixels, `null` for sound files |

Fields of a `layoutChanges` entry:

| Field | Type | Meaning |
| --- | --- | --- |
| `atSeconds` | number | Media time when this layout starts. The first entry is at `0`. |
| `layout.camera.visible` | boolean | Whether the camera is drawn |
| `layout.camera.x` | number | Left edge of the camera, as a fraction of frame width |
| `layout.camera.y` | number | Top edge of the camera, as a fraction of frame height |
| `layout.camera.size` | number | Side length of the camera, as a fraction of frame height |
| `layout.camera.shape` | string | `"Circle"`, `"RoundedSquare"` or `"Square"` |
| `layout.logo.visible` | boolean | Whether the logo is drawn |
| `layout.logo.corner` | string | `"TopLeft"`, `"TopRight"`, `"BottomLeft"` or `"BottomRight"` |
| `layout.logo.size` | number | Logo height, as a fraction of frame height |
| `layout.logo.path` | string or null | File name of the logo only, without its folder, because the full path holds the user name |

Fields of a `pauses` entry:

| Field | Type | Meaning |
| --- | --- | --- |
| `atSeconds` | number | Media time where the pause was cut out |
| `durationSeconds` | number | Wall clock seconds the recording was paused |

## Layout rules

The frame is `frameSize`. All fractions turn into pixels against that frame.

1. The camera area is a square with side `size * frame height`. Its top-left corner is at `x * frame width` and `y * frame height`, moved inside the frame if it would stick out.
2. The camera picture is the centered square cut from the camera frame, with side equal to the shorter side of the camera frame. It is mirrored left to right and scaled to fill the camera area.
3. The camera shape masks that square. `Circle` is the inscribed circle, `RoundedSquare` has a corner radius of 18% of the side, and `Square` has no mask.
4. The logo height is `size * frame height`, and its width keeps the logo image's proportions.
5. The logo sits in its corner with a margin of 2% of frame height from both edges, moved inside the frame if it would stick out.
6. The logo is drawn first and the camera on top.
7. If the captured picture does not match the frame's proportions, it is scaled to fit with black bars, and the layout still uses the full frame.

## Example

This file comes from a test run with a 3840x2160 screen, a fake camera that was unplugged after 1.1 seconds, computer sound on and no logo.

```json
{
  "version": 1,
  "startedAt": "2026-11-21T15:06:39.6718465+07:00",
  "frameSize": {
    "width": 3840,
    "height": 2160
  },
  "frameRate": 30,
  "targetKind": "monitor",
  "sources": [
    {
      "file": "final.mp4",
      "kind": "final",
      "offsetSeconds": 0,
      "width": 3840,
      "height": 2160
    },
    {
      "file": "screen.mp4",
      "kind": "screen",
      "offsetSeconds": 0,
      "width": 1920,
      "height": 1080
    },
    {
      "file": "webcam.mp4",
      "kind": "webcam",
      "offsetSeconds": 0,
      "width": 640,
      "height": 480
    },
    {
      "file": "system.wav",
      "kind": "system",
      "offsetSeconds": 0,
      "width": null,
      "height": null
    }
  ],
  "layoutChanges": [
    {
      "atSeconds": 0,
      "layout": {
        "camera": {
          "visible": true,
          "x": 0.02,
          "y": 0.73,
          "size": 0.25,
          "shape": "Circle"
        },
        "logo": {
          "visible": false,
          "corner": "BottomRight",
          "size": 0.08,
          "path": null
        }
      }
    }
  ],
  "pauses": [],
  "droppedFrames": 26,
  "notes": [
    "screen.mp4 scaled to 1920x1080",
    "webcam lost at 1.1 s"
  ]
}
```
