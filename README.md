# Art Mockup Batch Generator (.NET)

Places your artwork into the blank frames of room photos ("scenes") and renders mockups in bulk.
Deterministic compositing (perspective warp + lighting blend) — no AI image generation. See [PRD.md](PRD.md).

## Run

```
dotnet run
```

Then open http://localhost:8000. Requires the .NET 10 SDK (change `TargetFramework` in `ArtMockups.csproj` to `net9.0` if needed).

> The project references `OpenCvSharp4.runtime.win`. On macOS/Linux, swap it for the matching OpenCvSharp4 runtime package.

## Workflow

1. **Scenes** – upload a room photo (or drop it into `data/scenes/`), click the 4 corners of the blank frame area
   (TL → TR → BR → BL), tune fit mode / lighting / glare / feather, preview live, **Save**.
2. **Artworks** – put files in `data/artworks/` (or point to another folder), tick the ones to include.
3. **Generate** – pick scenes, **Generate all**. Output: `data/output/<scene>/<artwork>__<scene>.jpg`.
   Existing files are skipped unless *Overwrite* is on. Failures are listed and never stop the batch.

Artwork files are only ever read, never modified.

## Optional AI features

Copy `.env.example` to `.env` and set `ANTHROPIC_API_KEY`. Enables corner auto-detection and a
`captions.csv` generator (Brazilian Portuguese by default; edit the prompt in the Generate tab).
The model name is `aiModel` in `data/config.json` (default `claude-sonnet-5-5`). Without a key the app works fully and AI buttons are disabled.

## Layout

```
Program.cs                 minimal API + static frontend
Services/Compositor.cs     PreparedScene: mask, shading map, glare, warp, blend
Services/Stores.cs         scenes.json, config.json, artwork library + thumbnails
Services/BatchService.cs   parallel background batch with cancel/skip/errors
Services/AiService.cs      optional Claude API helpers (key stays on the backend)
wwwroot/                   vanilla JS frontend (canvas corner picker)
tests/ArtMockups.Tests/    compositor tests   (dotnet test tests/ArtMockups.Tests)
data/                      scenes/, artworks/, output/  (override root with ARTMOCKUPS_DATA)
```
