# Art Mockup Batch Generator (.NET)

Places your artwork into the blank frames of room photos ("scenes") and renders mockups in bulk: still images and
zoom-in videos. Deterministic compositing (perspective warp + lighting blend) — no AI image generation. See [PRD.md](PRD.md).

## Run

```
dotnet run
```

Then open http://localhost:8000. Requires the .NET 10 SDK (change `TargetFramework` in `ArtMockups.csproj` to `net9.0` if needed).
Videos need **ffmpeg**: put `ffmpeg.exe` next to the app, install it (`winget install Gyan.FFmpeg`) or set `ARTMOCKUPS_FFMPEG` to its path.

> The project references `OpenCvSharp4.runtime.win`. On macOS/Linux, swap it for the matching OpenCvSharp4 runtime package.

## Workflow

1. **Scenes** – upload a room photo (or drop it into `data/scenes/`), click the 4 corners of the blank frame area
   (TL → TR → BR → BL), tune fit mode / lighting / glare / feather, preview live, **Save**.
2. **Artworks** – put files in `data/artworks/` (or point to another folder), tick the ones to include.
   Files are named `0001.png`, `0002.png`, … The **Rename new artworks to 0000** button renames every file that is not
   already four digits to the next free numbers (e.g. after `0047`, new files become `0048`, `0049`, …; oldest first) and
   records the old names in `data/artworks-rename-map.csv`.
3. **Generate** – one list of scenes with two columns: tick a scene under **Images** for still mockups and/or under
   **Videos** for the zoom video. Press **Generate**. Everything goes into one flat folder, `data/output/`:
   `0001__<scene>.jpg` and `0001__<scene>.mp4` (the video adds `_loop` when the zoom-back-out option is on).
   Existing files are skipped unless *Overwrite* is on. Failures are listed and never stop the batch.

Artwork files are only ever read (except by the rename button), never modified.

### Zoom videos

The artwork is placed in the frame exactly like in the still mockups (same lighting), then a virtual camera glides from the
full view into the artwork. Because the frame position is known exactly, the video is perfectly steady.

- Format: landscape 1920×1080 or portrait 1080×1920; seconds, FPS, **End fill** (how much of the width the artwork opening
  fills at the end) and smooth ease in/out; optional zoom back out (5 s becomes exactly 10 s).
- It is a flat digital zoom (no 3D parallax). Sharpness at the end of the zoom depends on the scene photo's resolution; the
  UI shows the end-of-zoom upscale (above ~1.5x it can look soft: lower End fill or use a larger scene photo).

## Layout

```
Program.cs                 minimal API + static frontend
Services/Compositor.cs     PreparedScene: mask, shading map, glare, warp, blend
Services/Stores.cs         scenes.json, config.json, artwork library + thumbnails + renaming
Services/BatchService.cs   parallel background batch with cancel/skip/errors
Services/ZoomCamera.cs     virtual camera for the zoom videos
Services/ZoomVideos.cs     zoom-video jobs (ffmpeg encode, progress, cancel)
wwwroot/                   vanilla JS frontend (canvas corner picker)
tests/ArtMockups.Tests/    tests   (dotnet test tests/ArtMockups.Tests)
data/                      scenes/, artworks/, output/  (override root with ARTMOCKUPS_DATA)
```
