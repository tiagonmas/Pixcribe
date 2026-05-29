# Pixcribe

Pixcribe is a local command-line utility that scans a folder of images, generates searchable image descriptions with a local machine learning model, and writes those descriptions into image metadata.

## Current Version

The first version supports:

- JPEG files: `.jpg`, `.jpeg`
- PNG files: `.png`
- Recursive folder scanning
- Local image description through Ollama
- Dry-run mode
- In-place metadata updates when dry-run is not enabled
- Confirmation before replacing existing `Comments` or `Title`
- Optional skip logic based on the last Pixcribe update timestamp
- One model-specific Pixcribe HTML report per image folder
- Optional local face crop extraction with OpenCV

## Metadata Written

Pixcribe writes:

- `Comments`: generated image description
- `Title`: short description generated from existing tags plus the new description, only when tags exist
- `Program Name`: `Pixcribe`
- `PixcribeUpdatedAt`: custom Pixcribe timestamp used to skip recently updated images

For JPEG files, Pixcribe writes XMP metadata.

For PNG files, Pixcribe writes PNG text metadata chunks.

## Local Model Setup

Pixcribe uses Ollama by default because it can run vision models locally for free.

Install Ollama:

```powershell
winget install Ollama.Ollama
```

Pull a local vision model:

```powershell
ollama pull moondream
```

Keep Ollama running locally. Pixcribe expects the API at:

```text
http://localhost:11434
```

You can use another Ollama vision model with `--model`. `moondream` is the default because it is much smaller than `llama3.2-vision` and works better on machines with limited available RAM.

## Build

```powershell
dotnet build
```

## Usage

Dry run:

```powershell
dotnet run -- --folder "D:\Photos" --dry-run
```

Write metadata in place:

```powershell
dotnet run -- --folder "D:\Photos"
```

Skip files Pixcribe updated in the last 30 days:

```powershell
dotnet run -- --folder "D:\Photos" --min-age-days 30
```

Overwrite existing `Comments` and `Title` without asking:

```powershell
dotnet run -- --folder "D:\Photos" --min-age-days 30 --overwrite-existing
```

When Pixcribe prompts before replacing existing `Comments` or `Title`, use:

```text
y = replace this field once
n or Enter = skip this field once
A = always replace this field for the rest of the run
```

Skip existing `Comments` and `Title` without asking:

```powershell
dotnet run -- --folder "D:\Photos" --min-age-days 30 --no-prompt
```

Use a different local Ollama model:

```powershell
dotnet run -- --folder "D:\Photos" --min-age-days 30 --model llava
```

Extract one face crop per processed image:

```powershell
dotnet run -- --folder "D:\Photos" --min-age-days 30 --faceextract
```

Allow a slower local model more time per image:

```powershell
dotnet run -- --folder "D:\Photos" --min-age-days 30 --dry-run --model granite3.2-vision --timeout-seconds 600
```

Try the larger Llama vision model:

```powershell
dotnet run -- --folder "D:\Photos" --min-age-days 30 --dry-run --model llama3.2-vision:latest
```

`llama3.2-vision:latest` may require more available RAM or paging file space than some machines have. If it fails with a memory or paging-file error, use the default `moondream` model.

## Arguments

```text
--folder <path>              Root folder to scan recursively.
--min-age-days <days>        Optional. Skip files Pixcribe updated less than this many days ago.
--dry-run                    Generate descriptions and reports without modifying image files.
--overwrite-existing         Replace existing Comments and Title without prompting.
--no-prompt                  Do not prompt; skip fields that already have values.
--faceextract                Save a cropped image of the largest detected face.
--model <name>               Ollama vision model. Default: moondream.
--ollama-url <url>           Ollama base URL. Default: http://localhost:11434.
--timeout-seconds <seconds>  Per-image Ollama timeout. Default: 300.
--help                       Show help.
```

## Logging

Pixcribe logs:

- scan folder
- supported images found
- files selected for processing
- files skipped because Pixcribe updated them recently, when `--min-age-days` is provided
- progress count and percentage
- current file path
- generated image description
- generated title, when applicable
- final summary
- number of HTML reports written

## HTML Reports

Pixcribe writes a model-specific HTML file into each scanned folder that contains supported images.

Example:

```text
pixcribe-moondream.html
pixcribe-llama3.2-vision-latest.html
```

Each report shows:

- image preview
- file name
- title
- description
- tags
- model used

Generated titles are concise summaries derived from the image description and existing tags. They are capped at 120 characters for conservative image metadata compatibility and are not just the first words of the description.

During `--dry-run`, Pixcribe does not write image metadata. HTML reports are still created or updated because they are separate report files, not changes to the images.

## Face Extraction

When `--faceextract` is used, Pixcribe detects the largest face in each processed image and writes a new cropped image beside the source file.

Output files use this naming pattern:

```text
original-name-pixcribe-face-123456.jpg
```

In `--dry-run`, face crop files are not written; Pixcribe only logs where they would be created.

## Notes

This version uses the .NET runtime, a local Ollama server, and OpenCvSharp for local face detection.

Metadata writing is intentionally scoped to JPEG XMP and PNG text chunks for the first version. If broader compatibility with Windows Explorer, Adobe tools, HEIC, WebP, or TIFF becomes important, the next step should be adding ExifTool integration.
