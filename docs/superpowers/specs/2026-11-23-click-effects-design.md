# Click effects in recordings

Approved by the user on 2026-11-23.

## Intent

Recordings show the mouse pointer but not the clicks. A viewer of a demo video cannot tell when a click happened. The user wants an option that marks clicks in the video, with a choice of effect type.

## What the user gets

- A setting "Click effect" in the Settings window with 5 choices: Off, Ring, Ripple, Dot, Halo. It starts at Off.
- The choice is saved in `settings.json` as `ClickEffect`. A file without the field means Off.
- The effect is drawn into the video only. Nothing appears on the user's own screen.
- It appears in `screen.mp4` and in `final.mp4`. Screenshots get no effect.
- Left clicks are amber `#EF9F27`. Right clicks and middle clicks are blue `#378ADD`.

## The 4 effects

Sizes are in DIP and are multiplied by the scale of the captured monitor. Times are in milliseconds since the button went down. The centre is the pointer position at that moment, except for Halo.

| Effect | Shape | Timeline |
| --- | --- | --- |
| Ring | one circle outline, 3 wide | radius 3 to 28 and opacity 1 to 0 over 400, ease-out |
| Ripple | 2 circle outlines, 3 wide and 2 wide | each like Ring over 450; the second starts 140 later |
| Dot | one filled circle | radius 0 to 15 at opacity 0.75 during the first 120, then radius 15 to 18 and opacity 0.75 to 0 until 400 |
| Halo | one filled circle of radius 22 that follows the pointer | opacity 0.25 at rest; on a click it rises to 0.6 at 120 and returns to 0.25 at 300, in the click's color; amber at rest |

Ease-out is `1 - (1 - t)^2`.

## Rules

- No click is missed, even one that is shorter than a frame.
- A click outside the captured area draws nothing. Halo is hidden while the pointer is outside the captured area.
- Clicks while the recording is paused or in the countdown draw nothing.
- With the setting Off, the recorder installs no hook and does no extra work per frame.
- The effect does not change frame timing: drawing it must not allocate per frame.
- The effect must not capture or store what the user clicks on. Only position, button and time are kept, in memory, for at most 1 second.

## Approaches

1. A low-level mouse hook (`WH_MOUSE_LL`) on a thread with a message loop. It gets every click with its position. **Picked**, because polling the button state once per frame can miss a short click.
2. Polling `GetAsyncKeyState` and `GetCursorPos` on every frame. Simpler, but a click between 2 frames can be lost at 30 frames per second.

## Left to the builder

- Where the hook thread lives and how its events reach the recording thread.
- How screen coordinates map to frame pixels for monitor, window and region targets.
- The layout of the row in the Settings window.

## Later

The Rust app gets the same feature as a task of its Stage 2 plan. A color picker is not part of this.
