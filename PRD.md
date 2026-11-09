# PRD: Art Mockup Batch Generator

> Hand this file to Claude in VS Code as the project brief. Start with the "Build order" section at the end.

## 1. Overview

A local web app (runs on `localhost`, no deployment) that places my artwork images into blank frames in room photos ("scenes") and generates ad-ready mockup images in bulk.

- I have about 3 scene photos (bedroom, living room, side table by a wall). Each contains a blank framed artwork.
- I have about 50-60 artwork files stored locally (this will grow).
- Goal: for every artwork, generate one mockup per scene (up to about 180 images), automatically, in one click.

**Key decision: no AI image generation.** The compositing is done with deterministic code (perspective warp + lighting blend), so the artwork stays pixel-accurate to what the customer will receive. It is free per image and fast.

## 2. Goals

1. One-time setup per scene: mark the 4 corners of the blank frame area (manually by clicking, or auto-suggested via Claude API and then adjustable).
2. Batch-generate all artwork x scene combinations with one click.
3. Results look realistic: correct perspective, and the scene's original lighting, shadows and (optionally) glass glare are preserved.
4. Adding a new artwork or a new scene later requires no code changes.
5. Output files are well named and organized, ready to upload to Instagram, marketplaces or a store.

## 3. Non-goals

- No cloud hosting, user accounts or multi-user support.
- No AI image generation or inpainting.
- No editing of the artwork itself (cropping tools beyond the fit modes below, color correction).
- No video or animation.

## 4. User

Single user (me), a small online business owner selling decorative framed art. Comfortable with code (developer), uses Windows/macOS/Linux with VS Code. UI language: English (code and comments in English).

## 5. Tech stack

- **Backend:** Python 3.11+, FastAPI (or Flask) serving a local API and static frontend.
- **Image processing:** OpenCV (`cv2.getPerspectiveTransform` / `warpPerspective`), NumPy, Pillow.
- **Frontend:** plain HTML + vanilla JS (or a minimal Vite setup); canvas for corner picking. No heavy framework needed.
- **Optional Claude API integration:** `anthropic` Python SDK. API key read from a `.env` file (`ANTHROPIC_API_KEY`), used only on the backend and never exposed to the browser. The app must work fully without a key (the AI features are just disabled).
- Config and scene data stored as JSON files on disk (no database).

## 6. Project structure

```
art-mockups/
  .env                  # ANTHROPIC_API_KEY=... (git-ignored)
  .env.example
  requirements.txt
  README.md
  app/
    main.py             # FastAPI app and routes
    compositor.py       # warp + lighting + blend logic
    scenes.py           # load/save scene configs
    ai.py               # optional Claude API helpers
  static/               # frontend (index.html, app.js, styles.css)
  data/
    scenes/             # scene photos (jpg/png) + scenes.json
    artworks/           # my artwork files (input, never modified)
    output/             # generated mockups
      <scene-name>/<artwork-name>__<scene-name>.jpg
```

## 7. Functional requirements

### 7.1 Scene management
- Upload or select a scene image from `data/scenes/`.
- Display the scene on a canvas; the user clicks 4 corners of the blank artwork area (order: top-left, top-right, bottom-right, bottom-left). Corners can be dragged to fine-tune, with zoom and pan for precision.
- Saved per scene in `scenes.json`: `name`, `image`, `corners` (4 x,y points in original image pixels), and settings (below).
- Per-scene settings:
  - `fit_mode`: how the artwork fills the frame area when aspect ratios differ. Options: `cover` (crop to fill, default), `contain` (letterbox with a configurable matte color), `stretch`.
  - `lighting_strength` (0-1, default 0.8): how much of the scene's original shading is applied to the art.
  - `glare_strength` (0-1, default 0): optional subtle glass reflection.
  - `edge_feather_px` (default 1): small edge softening to avoid jagged edges.
- Scenes can be edited, renamed and deleted.
- The user can mark a scene as expecting a specific artwork aspect ratio (optional; used only to show a warning when an artwork differs a lot).

### 7.2 Artwork library
- Point the app at a local folder (default `data/artworks/`). Supported: PNG, JPG/JPEG, WebP.
- Show a thumbnail grid with filename, dimensions, and a checkbox to include or exclude each artwork. "Select all / none".
- Originals are never modified or moved.

### 7.3 Compositing (core feature)
For each (artwork, scene) pair:
1. Load the scene and the artwork. Prepare the artwork according to `fit_mode` so it matches the frame area's aspect ratio (the frame area aspect ratio is estimated from the corner distances).
2. Compute a perspective transform from the artwork rectangle to the 4 scene corners and warp the artwork into the scene (supersample 2x for clean edges, then downscale).
3. **Lighting preservation:** inside the blank frame area of the original scene photo, take the luminance, normalize it by its mean (or a high percentile), and use it as a shading map. Multiply the warped artwork by this map, blended by `lighting_strength`. This keeps real shadows, gradients and light falloff on the art. Clamp to avoid over-darkening or blowing out.
4. Optional glare: overlay a soft, low-opacity gradient or the scene's highlight areas (from the blank region) using screen blend, scaled by `glare_strength`.
5. Composite onto the scene using an anti-aliased mask built from the 4 corners. Pixels outside the mask stay identical to the original scene.
6. Save as high-quality JPEG (quality about 92) at the scene's original resolution. Optional PNG output.

### 7.4 Preview
- Before running the full batch, the user can preview one artwork x one scene at full size, adjust the scene settings and re-render the preview live.
- Preview a small contact sheet (for example, the first 6 selected artworks on a chosen scene) to check variety.

### 7.5 Batch generation
- Button "Generate all": runs every selected artwork across every selected scene.
- Runs in a background job with a progress bar (current / total, current file) and the ability to cancel.
- Uses multiprocessing or a thread pool for speed (sensible default: CPU count minus 1).
- Skips existing outputs by default (toggle "overwrite") so re-runs only render new files.
- A failed item must not stop the batch; errors are collected and shown at the end with the reason.
- Output naming: `<artwork-stem>__<scene-name>.jpg`, grouped in `output/<scene-name>/`. Provide a "Open output folder" button and an optional "Download as ZIP".

### 7.6 Optional AI features (require `ANTHROPIC_API_KEY`, can be built last)
- **Auto-detect corners:** send the scene image to the Claude API (vision) and ask it to return the 4 corner coordinates of the blank artwork as JSON. Show them on the canvas as a suggestion the user can accept or adjust. Always allow manual correction; never trust the result blindly. Validate that the response is 4 points inside the image bounds.
- **Caption and ad-copy generator:** for each artwork (send a downscaled thumbnail), generate a short product title and Instagram caption in Brazilian Portuguese, with a user-editable prompt template (tone, hashtags, store name). Export as `captions.csv` next to the output images.
- Downscale images before sending (long side about 1024 px) to keep requests small. Handle API errors and rate limits gracefully with retries and clear messages.
- Use a model name stored in config (not hard-coded), so it is easy to change.

## 8. Non-functional requirements

- Runs with one command, for example `uvicorn app.main:app --reload`, then open `http://localhost:8000`.
- Fast: a single composite should take under about 1 s for a 3000 px scene; the full batch of 180 under a few minutes on a normal laptop.
- Handles large images (up to about 6000 px) without running out of memory.
- Respects EXIF orientation of input photos.
- Works offline, except for the optional AI features.
- Secrets: `.env` is git-ignored; the key is never logged or sent to the frontend.
- Clear error messages for unreadable files, missing corners and invalid configs.

## 9. Acceptance criteria

1. I can add a scene, click 4 corners, save, and see the saved corners reload correctly on the next app start.
2. For a test artwork, the preview shows the art correctly aligned to the frame with the right perspective and with the scene's shadows visible on it.
3. Running the batch with 3 scenes and 60 artworks produces 180 files in the correct folders with the correct names, without manual intervention.
4. Re-running the batch skips existing files unless "overwrite" is on.
5. One corrupt artwork file does not stop the batch, and is listed in the error summary.
6. Artwork files in `data/artworks/` are byte-identical before and after a run.
7. With no API key set, the app works fully and the AI buttons are disabled with a short explanation.
8. Changing a scene's `lighting_strength` and re-rendering the preview visibly changes the result.

## 10. Build order (suggested milestones)

1. **Core compositor (CLI first):** `compositor.py` with a function `render(scene_path, corners, artwork_path, settings) -> image`, tested on one scene with hard-coded corners.
2. **Lighting and edges:** shading map, supersampling, feathered mask; tune on real scenes.
3. **Scene config:** `scenes.json` read/write, plus the three fit modes.
4. **Backend API:** list scenes and artworks, save corners and settings, render preview, start/cancel/poll batch jobs.
5. **Frontend:** scene canvas with corner picking, artwork grid with selection, preview panel, batch progress.
6. **Batch engine:** parallel rendering, skip-existing, error collection, ZIP export.
7. **Optional AI:** corner auto-detection, then captions CSV.
8. **Polish:** README with setup steps, `.env.example`, basic tests for the compositor (output size, unchanged pixels outside the mask).

## 11. Defaults and assumptions (change if needed)

- Output format JPEG, quality 92, same resolution as the scene photo.
- Artwork filenames are unique and used as the output name stem.
- Blank frames in scenes are plain white or light-colored, so their luminance is a usable shading map.
- Mixed artwork aspect ratios are expected (portrait, landscape, square), so `fit_mode` matters.
- The frame and matting around the blank area stay as in the original scene photo; only the inner blank area is replaced.

## 12. Open questions for the first build session

- Should the frame's inner area ever need a custom matte (white border inside the frame) around the art? If so, add `matte_px` and `matte_color` to the scene settings.
- Is a fixed output size (for example, 1080x1350 for Instagram) wanted in addition to the full-size scene render?
