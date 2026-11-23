# Captions for recordings

Draft for the user's approval, 2026-11-23. Research and measurements are in `.superpowers/research/2026-11-23-caption-models.md` of the main checkout.

## Intent

The user makes demo videos and wants the spoken words as captions, without a second tool. The user chose Whisper. The feature is optional and off until the user turns it on.

## Goals

- After a recording stops, a caption file appears next to the video with no extra step.
- Turning captions on is one setting, plus one consent to a download.
- The recorder stays as small and as fast as it is. Captions add nothing to the app's own files and nothing to a recording while it runs.
- A failed caption job never damages or delays the recording.

## Non-goals

- No live captions during a recording.
- No captions drawn into the picture, and no subtitle track inside the MP4. Both can come later.
- No caption editor.
- No translation.

## Anti-goals

- Sound or text leaves the PC.
- The app downloads anything without the user's yes.
- A caption job makes a running recording drop frames or run short of memory.

## What the user gets

- A setting "Captions" in the Settings window with 3 choices: Off, English, Any language (detected). It starts at Off and is saved in `settings.json` as `captions`.
- The first time the user picks a choice other than Off, a window says what will be downloaded, from where, how large, and where it is stored, with Download and Cancel. Cancel puts the setting back to Off.
- After Stop, the card shows "Making captions" with a percentage, then "Captions saved". The recording is ready to play the whole time.
- The result is 2 files in the recording folder with the name of the video the card plays: `final.srt` and `final.vtt`, or `screen.srt` and `screen.vtt` when there is no finished video. Players such as VLC and the Windows Media Player load a caption file with the same name as the video.
- Captions come from the microphone track. When a recording has no microphone track, they come from the computer sound.
- A recording with no sound track gets no caption job.

## How it works

- The engine is whisper.cpp, run as a separate helper program. The app starts it after Stop and reads its progress. The app's own files do not grow.
- Measured on the build PC (Intel Core i5-14500HX): 110 s of speech took 9.3 s with 4 threads, and the helper peaked at 716 MB of private memory.
- The download has 2 parts. The helper is `whisper-bin-x64.zip` of whisper.cpp build `b5454` from the project's GitHub releases, 8.9 MB. Of it the app keeps `whisper-cli.exe`, `whisper.dll`, `ggml.dll`, `ggml-base.dll` and the `ggml-cpu-*.dll` files, about 11 MB. The model is `ggml-base.en-q5_1.bin` for English or `ggml-base-q5_1.bin` for Any language, 60 MB each, from the project's model repository on Hugging Face.
- Every downloaded file is checked against a SHA-256 value that is fixed in the app's code. A file that does not match is deleted and the user is told.
- The files live in `%LOCALAPPDATA%\ScreenRecorder\captions`. The license texts of whisper.cpp and of the model are stored with them.
- The second model is downloaded only when the user picks the other choice.

## Rules

- The helper runs with below-normal priority and 4 threads.
- One caption job runs at a time. Jobs wait in a line.
- When a recording starts, a running caption job is stopped and goes back to the front of the line. It starts again after that recording has stopped. A job never runs during a countdown or a recording.
- The helper gets a 16 kHz mono sound file that the app writes into the recording folder as a temporary file and deletes afterwards.
- When the app quits, a running job is stopped and its temporary files are deleted. Recordings without captions stay as they are; the app does not catch up on them at the next start.
- A failed job shows "Captions failed" on the card and adds a note to `session.json`. Nothing else changes.
- Deleting the folder `captions` by hand is safe. The app asks to download again the next time captions are needed.
- The only network use of the app is this download, after the user's yes.

## Approaches

1. whisper.cpp as a separate helper program, downloaded once. The app stays small, the engine cannot crash the recorder, and the Rust app can use the same helper. **Picked.**
2. whisper.cpp linked into the app as a library. One process, but the app grows by about 11 MB plus the model, and a fault in the engine takes the recorder down.
3. The speech recognition that Windows includes. No download, but the research found it either online-only, tied to packaged apps, or marked experimental.

## Left to the builder

- The resampler from the 48 kHz stereo float track to 16 kHz mono, and whether `whisper-cli` can read the track directly, which would remove the temporary file.
- How progress is read from the helper.
- Line length of the captions, as long as a caption has at most 2 lines of about 42 characters.
- The layout of the consent window and of the Settings row.

## Later

The Rust app gets the same feature with the same helper and the same setting. Voice-over is a separate design, together with the Claude plugin.
