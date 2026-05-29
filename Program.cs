using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenCvSharp;

var options = AppOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine(AppOptions.HelpText);
    return 0;
}

if (options.Error is not null)
{
    Console.Error.WriteLine(options.Error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(AppOptions.HelpText);
    return 2;
}

if (!Directory.Exists(options.Folder))
{
    Console.Error.WriteLine($"Folder does not exist: {options.Folder}");
    return 2;
}

var metadata = new ImageMetadataService();
var describer = new OllamaImageDescriber(options.OllamaUrl, options.Model, options.Timeout);
var faceExtractor = options.FaceExtract ? new FaceExtractor() : null;
try
{
    await describer.EnsureModelAvailableAsync(CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var allImages = Directory.EnumerateFiles(options.Folder, "*", SearchOption.AllDirectories)
    .Where(ImageMetadataService.IsSupportedImage)
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToList();
var reportItems = new Dictionary<string, ImageReportItem>(StringComparer.OrdinalIgnoreCase);

Console.WriteLine($"Pixcribe scan folder: {options.Folder}");
Console.WriteLine($"Supported images found: {allImages.Count}");
Console.WriteLine($"Mode: {(options.DryRun ? "dry run" : "write")}");
Console.WriteLine($"Model: {options.Model} at {options.OllamaUrl}");
Console.WriteLine($"Model timeout: {options.Timeout.TotalSeconds:0} seconds");
Console.WriteLine();

var candidates = new List<(string Path, ImageMetadata Existing)>();
DateTimeOffset? cutoff = options.MinAge is null ? null : DateTimeOffset.UtcNow - options.MinAge.Value;
foreach (var image in allImages)
{
    try
    {
        var existing = metadata.Read(image);
        if (cutoff is not null && existing.PixcribeUpdatedAt is not null && existing.PixcribeUpdatedAt > cutoff)
        {
            reportItems[image] = ImageReportItem.FromMetadata(image, existing);
            continue;
        }

        reportItems[image] = ImageReportItem.FromMetadata(image, existing);
        candidates.Add((image, existing));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not read metadata: {image}");
        Console.WriteLine($"  {ex.Message}");
    }
}

Console.WriteLine($"Images to process: {candidates.Count}");
Console.WriteLine($"Skipped as recently updated: {allImages.Count - candidates.Count}");
Console.WriteLine();

var processed = 0;
var skipped = 0;
var failed = 0;
var facesExtracted = 0;
var promptState = new PromptState();

for (var index = 0; index < candidates.Count; index++)
{
    var (path, existing) = candidates[index];
    var percent = candidates.Count == 0 ? 100 : (index * 100.0 / candidates.Count);
    Console.WriteLine($"[{index + 1}/{candidates.Count}] {percent:0.0}% {path}");

    try
    {
        var description = await describer.DescribeAsync(path, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(description))
        {
            Console.WriteLine("  Skipped: the model returned an empty description.");
            skipped++;
            continue;
        }

        var title = existing.Tags.Count > 0
            ? TitleGenerator.Create(existing.Tags, description)
            : null;

        Console.WriteLine($"  Description: {description}");
        if (title is not null)
        {
            Console.WriteLine($"  Title: {title}");
        }

        reportItems[path] = ImageReportItem.FromGenerated(path, existing, title, description);

        var shouldWriteComments = ShouldWriteField("Comments", existing.Comments, description, options, promptState);
        var shouldWriteTitle = title is not null && ShouldWriteField("Title", existing.Title, title, options, promptState);

        if (!shouldWriteComments && !shouldWriteTitle)
        {
            Console.WriteLine("  Skipped: no metadata fields approved for update.");
            skipped++;
            continue;
        }

        var update = new ImageMetadataUpdate(
            Comments: shouldWriteComments ? description : existing.Comments,
            Title: shouldWriteTitle ? title : existing.Title,
            ProgramName: "Pixcribe",
            PixcribeUpdatedAt: DateTimeOffset.UtcNow,
            Tags: existing.Tags);

        if (options.DryRun)
        {
            Console.WriteLine("  Dry run: no file changes written.");
        }
        else
        {
            metadata.Write(path, update);
            Console.WriteLine("  Metadata updated.");
        }

        if (faceExtractor is not null)
        {
            var faceResult = faceExtractor.ExtractLargestFace(path, options.DryRun);
            switch (faceResult.Status)
            {
                case FaceExtractStatus.Extracted:
                    facesExtracted++;
                    Console.WriteLine($"  Face extracted: {faceResult.OutputPath}");
                    break;
                case FaceExtractStatus.WouldExtract:
                    Console.WriteLine($"  Dry run: face crop would be written to {faceResult.OutputPath}");
                    break;
                case FaceExtractStatus.NoFaceFound:
                    Console.WriteLine("  Face extraction: no face found.");
                    break;
            }
        }

        processed++;
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  Failed: {ex.Message}");
    }

    Console.WriteLine();
}

Console.WriteLine("Pixcribe summary");
Console.WriteLine($"  Found: {allImages.Count}");
Console.WriteLine($"  To process: {candidates.Count}");
Console.WriteLine($"  Processed: {processed}");
Console.WriteLine($"  Skipped: {skipped}");
Console.WriteLine($"  Failed: {failed}");
Console.WriteLine($"  Faces extracted: {facesExtracted}");
Console.WriteLine($"  Complete: 100.0%");

var reportResult = HtmlReportWriter.WritePerFolderReports(options.Folder, options.Model, reportItems.Values);
Console.WriteLine($"  HTML reports: {reportResult.Written}");
if (reportResult.Failed > 0)
{
    Console.WriteLine($"  HTML report failures: {reportResult.Failed}");
}

return failed == 0 && reportResult.Failed == 0 ? 0 : 1;

static bool ShouldWriteField(string fieldName, string? existingValue, string newValue, AppOptions options, PromptState promptState)
{
    if (string.IsNullOrWhiteSpace(existingValue))
    {
        return true;
    }

    if (options.OverwriteExisting)
    {
        return true;
    }

    if (options.NoPrompt)
    {
        return false;
    }

    if (promptState.ShouldAlwaysUpdate(fieldName))
    {
        return true;
    }

    Console.Write($"  {fieldName} already exists. Replace it? [y/N/A] ");
    var answer = Console.ReadLine();
    if (answer is not null && answer.Equals("a", StringComparison.OrdinalIgnoreCase))
    {
        promptState.MarkAlwaysUpdate(fieldName);
        return true;
    }

    return answer is not null && answer.Equals("y", StringComparison.OrdinalIgnoreCase);
}

internal sealed class PromptState
{
    private readonly HashSet<string> _alwaysUpdateFields = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldAlwaysUpdate(string fieldName) => _alwaysUpdateFields.Contains(fieldName);

    public void MarkAlwaysUpdate(string fieldName) => _alwaysUpdateFields.Add(fieldName);
}

internal sealed record AppOptions(
    string Folder,
    TimeSpan? MinAge,
    bool DryRun,
    bool OverwriteExisting,
    bool NoPrompt,
    bool FaceExtract,
    string Model,
    Uri OllamaUrl,
    TimeSpan Timeout,
    bool ShowHelp,
    string? Error)
{
    public static string HelpText =>
        """
        Pixcribe

        Usage:
          pixcribe --folder <path> [options]

        Options:
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

        Example:
          pixcribe --folder "D:\Photos" --dry-run
        """;

    public static AppOptions Parse(string[] args)
    {
        string? folder = null;
        double? minAgeDays = null;
        var dryRun = false;
        var overwriteExisting = false;
        var noPrompt = false;
        var faceExtract = false;
        var model = "moondream";
        var ollamaUrl = new Uri("http://localhost:11434");
        var timeout = TimeSpan.FromSeconds(300);

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--help":
                    case "-h":
                        return new AppOptions("", TimeSpan.Zero, false, false, false, false, model, ollamaUrl, timeout, true, null);
                    case "--folder":
                        folder = NextValue(args, ref i, arg);
                        break;
                    case "--min-age-days":
                        var minAgeText = NextValue(args, ref i, arg);
                        if (!double.TryParse(minAgeText, out var parsedDays) || parsedDays < 0)
                        {
                            return Invalid($"Invalid --min-age-days value: {minAgeText}");
                        }
                        minAgeDays = parsedDays;
                        break;
                    case "--dry-run":
                        dryRun = true;
                        break;
                    case "--overwrite-existing":
                        overwriteExisting = true;
                        break;
                    case "--no-prompt":
                        noPrompt = true;
                        break;
                    case "--faceextract":
                        faceExtract = true;
                        break;
                    case "--model":
                        model = NextValue(args, ref i, arg);
                        break;
                    case "--ollama-url":
                        var urlText = NextValue(args, ref i, arg);
                        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var parsedUri))
                        {
                            return Invalid($"Invalid --ollama-url value: {urlText}");
                        }
                        ollamaUrl = parsedUri;
                        break;
                    case "--timeout-seconds":
                        var timeoutText = NextValue(args, ref i, arg);
                        if (!double.TryParse(timeoutText, out var parsedTimeout) || parsedTimeout <= 0)
                        {
                            return Invalid($"Invalid --timeout-seconds value: {timeoutText}");
                        }
                        timeout = TimeSpan.FromSeconds(parsedTimeout);
                        break;
                    default:
                        return Invalid($"Unknown argument: {arg}");
                }
            }
        }
        catch (ArgumentException ex)
        {
            return Invalid(ex.Message);
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            return Invalid("Missing required argument: --folder");
        }

        if (overwriteExisting && noPrompt)
        {
            return Invalid("--overwrite-existing and --no-prompt cannot be used together.");
        }

        return new AppOptions(
            Path.GetFullPath(folder),
            minAgeDays is null ? null : TimeSpan.FromDays(minAgeDays.Value),
            dryRun,
            overwriteExisting,
            noPrompt,
            faceExtract,
            model,
            ollamaUrl,
            timeout,
            false,
            null);
    }

    private static string NextValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {name}");
        }

        index++;
        return args[index];
    }

    private static AppOptions Invalid(string error) =>
        new("", TimeSpan.Zero, false, false, false, false, "moondream", new Uri("http://localhost:11434"), TimeSpan.FromSeconds(300), false, error);
}

internal sealed class OllamaImageDescriber
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OllamaImageDescriber(Uri baseUrl, string model, TimeSpan timeout)
    {
        _httpClient = new HttpClient { BaseAddress = baseUrl, Timeout = timeout };
        _model = model;
    }

    public async Task EnsureModelAvailableAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("/api/tags", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama is not reachable at {_httpClient.BaseAddress}. Start Ollama and try again.");
        }

        var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(cancellationToken: cancellationToken);
        var installed = tags?.Models?.Select(model => model.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList() ?? [];
        if (installed.Any(name => ModelNameMatches(name, _model)))
        {
            return;
        }

        var installedText = installed.Count == 0 ? "none" : string.Join(", ", installed);
        throw new InvalidOperationException(
            $"Ollama model '{_model}' is not installed. Install it with: ollama pull {_model}{Environment.NewLine}" +
            $"Installed models: {installedText}");
    }

    public async Task<string> DescribeAsync(string imagePath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        var description = await GenerateAsync(bytes, "Describe this image in one concise searchable sentence. Mention visible objects, setting, people, text, colors, and notable context. Do not invent unseen details.", cancellationToken);
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        return await GenerateAsync(bytes, "What is in this image? Answer with one short sentence.", cancellationToken);
    }

    private async Task<string> GenerateAsync(byte[] imageBytes, string prompt, CancellationToken cancellationToken)
    {
        var request = new
        {
            model = _model,
            prompt,
            images = new[] { Convert.ToBase64String(imageBytes) },
            stream = false
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/generate", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Ollama request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: cancellationToken);
        return result?.Response?.Trim() ?? "";
    }

    private static bool ModelNameMatches(string installedName, string requestedName) =>
        installedName.Equals(requestedName, StringComparison.OrdinalIgnoreCase)
        || installedName.Equals($"{requestedName}:latest", StringComparison.OrdinalIgnoreCase);

    private sealed record OllamaTagsResponse([property: JsonPropertyName("models")] IReadOnlyList<OllamaModel>? Models);
    private sealed record OllamaModel([property: JsonPropertyName("name")] string Name);
    private sealed record OllamaGenerateResponse([property: JsonPropertyName("response")] string? Response);
}

internal static class TitleGenerator
{
    private const int MaxImageTitleLength = 120;

    public static string Create(IReadOnlyList<string> tags, string description)
    {
        var tagWords = NormalizeWords(tags.SelectMany(tag => Regex.Split(tag, @"[^\p{L}\p{N}]+")));
        var descriptionWords = NormalizeWords(Regex.Split(description, @"[^\p{L}\p{N}]+"));
        var context = tagWords
            .Concat(descriptionWords)
            .Where(word => !StopWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(ToTitleWord)
            .ToList();

        if (context.Count == 0)
        {
            return "Image";
        }

        var title = string.Join(" / ", context.Take(4));
        if (context.Count > 4)
        {
            title += $" - {string.Join(", ", context.Skip(4).Take(4))}";
        }

        return TrimToMaxLength(title, MaxImageTitleLength);
    }

    private static List<string> NormalizeWords(IEnumerable<string> words) =>
        words
            .Select(word => word.Trim().Trim('_').ToLowerInvariant())
            .Where(word => word.Length >= 3)
            .ToList();

    private static string ToTitleWord(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    private static string TrimToMaxLength(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var trimmed = value[..maxLength].TrimEnd();
        var lastSeparator = trimmed.LastIndexOfAny([' ', ',', '/', '-']);
        if (lastSeparator > 30)
        {
            trimmed = trimmed[..lastSeparator].TrimEnd();
        }

        return trimmed.TrimEnd(',', '/', '-').Trim();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "above", "after", "along", "also", "and", "are", "around", "below", "blue",
        "color", "display", "from", "green", "has", "have", "image", "into", "large", "near",
        "notable", "objects", "photo", "picture", "posing", "setting", "short", "shows", "that",
        "the", "them", "this", "two", "was", "water", "were", "white", "with"
    };
}

internal sealed record ImageMetadata(
    string? Comments,
    string? Title,
    string? ProgramName,
    DateTimeOffset? PixcribeUpdatedAt,
    IReadOnlyList<string> Tags);

internal sealed record ImageMetadataUpdate(
    string? Comments,
    string? Title,
    string ProgramName,
    DateTimeOffset PixcribeUpdatedAt,
    IReadOnlyList<string> Tags);

internal sealed record ImageReportItem(
    string Path,
    string? Title,
    string? Description,
    IReadOnlyList<string> Tags)
{
    public static ImageReportItem FromMetadata(string path, ImageMetadata metadata) =>
        new(path, metadata.Title, metadata.Comments, metadata.Tags);

    public static ImageReportItem FromGenerated(string path, ImageMetadata metadata, string? generatedTitle, string generatedDescription) =>
        new(path, generatedTitle ?? metadata.Title, generatedDescription, metadata.Tags);
}

internal static class HtmlReportWriter
{
    public static HtmlReportResult WritePerFolderReports(string rootFolder, string model, IEnumerable<ImageReportItem> items)
    {
        var groups = GroupByFolder(rootFolder, items);

        var written = 0;
        var failed = 0;
        foreach (var group in groups)
        {
            var folder = group.Key;
            var htmlPath = Path.Combine(folder, $"pixcribe-{SafeFileName(model)}.html");
            try
            {
                File.WriteAllText(htmlPath, BuildHtml(folder, model, group.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)), Encoding.UTF8);
                written++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  Failed to write HTML report: {htmlPath}");
                Console.WriteLine($"    {ex.Message}");
            }
        }

        return new HtmlReportResult(written, failed);
    }

    private static List<IGrouping<string, ImageReportItem>> GroupByFolder(string rootFolder, IEnumerable<ImageReportItem> items) =>
        items
            .GroupBy(item => Path.GetDirectoryName(item.Path) ?? rootFolder, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string BuildHtml(string folder, string model, IEnumerable<ImageReportItem> items)
    {
        var title = $"Pixcribe - {Path.GetFileName(folder)}";
        var rows = string.Join(Environment.NewLine, items.Select(BuildCard));
        return
            $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{Html(title)}}</title>
              <style>
                :root { color-scheme: light; font-family: "Segoe UI", Arial, sans-serif; }
                body { margin: 0; background: #f7f7f4; color: #202124; }
                header { padding: 24px 28px 14px; border-bottom: 1px solid #ddd9cf; background: #ffffff; }
                h1 { margin: 0; font-size: 22px; font-weight: 650; }
                .folder { margin-top: 6px; color: #62625c; font-size: 13px; overflow-wrap: anywhere; }
                main { padding: 24px 28px; display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 18px; }
                article { background: #ffffff; border: 1px solid #ddd9cf; border-radius: 8px; overflow: hidden; }
                img { width: 100%; aspect-ratio: 4 / 3; object-fit: contain; display: block; background: #ece9df; }
                .meta { padding: 14px 14px 16px; display: grid; gap: 10px; }
                .name { font-weight: 650; overflow-wrap: anywhere; }
                dl { margin: 0; display: grid; gap: 8px; }
                dt { color: #66645e; font-size: 12px; font-weight: 650; text-transform: uppercase; }
                dd { margin: 2px 0 0; line-height: 1.35; overflow-wrap: anywhere; }
                .tags { display: flex; flex-wrap: wrap; gap: 6px; }
                .tag { background: #e8f0ee; border: 1px solid #c9d9d4; border-radius: 999px; padding: 3px 8px; font-size: 12px; }
              </style>
            </head>
            <body>
              <header>
                <h1>{{Html(title)}}</h1>
                <div class="folder">{{Html(folder)}}</div>
                <div class="folder">Model: {{Html(model)}}</div>
              </header>
              <main>
            {{rows}}
              </main>
            </body>
            </html>
            """;
    }

    private static string BuildCard(ImageReportItem item)
    {
        var fileName = Path.GetFileName(item.Path);
        var imageSrc = Uri.EscapeDataString(fileName);
        var tags = item.Tags.Count == 0
            ? "<dd></dd>"
            : $"<dd class=\"tags\">{string.Join("", item.Tags.Select(tag => $"<span class=\"tag\">{Html(tag)}</span>"))}</dd>";

        return
            $$"""
                <article>
                  <img src="{{imageSrc}}" alt="{{Html(item.Title ?? item.Description ?? fileName)}}">
                  <div class="meta">
                    <div class="name">{{Html(fileName)}}</div>
                    <dl>
                      <div><dt>Title</dt><dd>{{Html(item.Title ?? "")}}</dd></div>
                      <div><dt>Description</dt><dd>{{Html(item.Description ?? "")}}</dd></div>
                      <div><dt>Tags</dt>{{tags}}</div>
                    </dl>
                  </div>
                </article>
            """;
    }

    private static string Html(string value) => SecurityElement.Escape(value) ?? "";

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c);
        }

        var safe = builder.ToString().Trim('-', '.');
        return string.IsNullOrWhiteSpace(safe) ? "model" : safe;
    }
}

internal sealed record HtmlReportResult(int Written, int Failed);

internal enum FaceExtractStatus
{
    Extracted,
    WouldExtract,
    NoFaceFound
}

internal sealed record FaceExtractResult(FaceExtractStatus Status, string? OutputPath);

internal sealed class FaceExtractor
{
    private const double CropMarginRatio = 0.25;
    private readonly CascadeClassifier _classifier;

    public FaceExtractor()
    {
        var cascadePath = ResolveCascadePath();
        _classifier = new CascadeClassifier(cascadePath);
        if (_classifier.Empty())
        {
            throw new InvalidOperationException($"Could not load face detector cascade: {cascadePath}");
        }
    }

    public FaceExtractResult ExtractLargestFace(string imagePath, bool dryRun)
    {
        using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (image.Empty())
        {
            return new FaceExtractResult(FaceExtractStatus.NoFaceFound, null);
        }

        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);

        var faces = _classifier.DetectMultiScale(
            gray,
            scaleFactor: 1.1,
            minNeighbors: 5,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: new Size(40, 40));

        if (faces.Length == 0)
        {
            return new FaceExtractResult(FaceExtractStatus.NoFaceFound, null);
        }

        var face = faces.OrderByDescending(rect => rect.Width * rect.Height).First();
        var crop = Expand(face, image.Width, image.Height);
        var outputPath = CreateOutputPath(imagePath);
        if (dryRun)
        {
            return new FaceExtractResult(FaceExtractStatus.WouldExtract, outputPath);
        }

        using var faceImage = new Mat(image, crop);
        if (!Cv2.ImWrite(outputPath, faceImage))
        {
            throw new InvalidOperationException($"Could not write face crop: {outputPath}");
        }

        return new FaceExtractResult(FaceExtractStatus.Extracted, outputPath);
    }

    private static Rect Expand(Rect rect, int imageWidth, int imageHeight)
    {
        var marginX = (int)Math.Round(rect.Width * CropMarginRatio);
        var marginY = (int)Math.Round(rect.Height * CropMarginRatio);
        var x = Math.Max(0, rect.X - marginX);
        var y = Math.Max(0, rect.Y - marginY);
        var right = Math.Min(imageWidth, rect.X + rect.Width + marginX);
        var bottom = Math.Min(imageHeight, rect.Y + rect.Height + marginY);
        return new Rect(x, y, right - x, bottom - y);
    }

    private static string CreateOutputPath(string imagePath)
    {
        var directory = Path.GetDirectoryName(imagePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(imagePath);
        var extension = Path.GetExtension(imagePath);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var random = Random.Shared.Next(100000, 999999);
            var candidate = Path.Combine(directory, $"{name}-pixcribe-face-{random}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-pixcribe-face-{Guid.NewGuid():N}{extension}");
    }

    private static string ResolveCascadePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "assets", "haarcascade_frontalface_default.xml"),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", "haarcascade_frontalface_default.xml")
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            throw new FileNotFoundException("Face detector cascade not found.", candidates[0]);
        }

        return path;
    }
}

internal sealed class ImageMetadataService
{
    private static readonly byte[] JpegXmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    public ImageMetadata Read(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return ReadPng(path);
        }

        return ReadJpeg(path);
    }

    public void Write(string path, ImageMetadataUpdate update)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            WritePng(path, update);
            return;
        }

        WriteJpeg(path, update);
    }

    private static ImageMetadata ReadJpeg(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var xmp = GetJpegXmp(bytes);
        if (xmp is null)
        {
            return new ImageMetadata(null, null, null, null, []);
        }

        return ParseXmp(xmp);
    }

    private static void WriteJpeg(string path, ImageMetadataUpdate update)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 2 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            throw new InvalidOperationException("Invalid JPEG file.");
        }

        var existingXmp = GetJpegXmp(bytes);
        var xmp = BuildXmp(existingXmp, update);
        var xmpBytes = Encoding.UTF8.GetBytes(xmp);
        var payload = new byte[JpegXmpHeader.Length + xmpBytes.Length];
        Buffer.BlockCopy(JpegXmpHeader, 0, payload, 0, JpegXmpHeader.Length);
        Buffer.BlockCopy(xmpBytes, 0, payload, JpegXmpHeader.Length, xmpBytes.Length);
        if (payload.Length + 2 > ushort.MaxValue)
        {
            throw new InvalidOperationException("XMP metadata is too large for a JPEG APP1 segment.");
        }

        using var output = new MemoryStream();
        output.Write(bytes, 0, 2);
        WriteJpegApp1(output, BuildExifPayload(update));
        WriteJpegApp1(output, payload);

        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                output.Write(bytes, offset, bytes.Length - offset);
                break;
            }

            var marker = bytes[offset + 1];
            if (marker is 0xDA or 0xD9)
            {
                output.Write(bytes, offset, bytes.Length - offset);
                break;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2, 2));
            var totalLength = segmentLength + 2;
            if (offset + totalLength > bytes.Length)
            {
                throw new InvalidOperationException("Invalid JPEG segment length.");
            }

            var isXmp = marker == 0xE1
                && segmentLength - 2 >= JpegXmpHeader.Length
                && bytes.AsSpan(offset + 4, JpegXmpHeader.Length).SequenceEqual(JpegXmpHeader);
            var isExif = marker == 0xE1
                && segmentLength - 2 >= 6
                && bytes.AsSpan(offset + 4, 6).SequenceEqual("Exif\0\0"u8);

            if (!isXmp && !isExif)
            {
                output.Write(bytes, offset, totalLength);
            }

            offset += totalLength;
        }

        File.WriteAllBytes(path, output.ToArray());
    }

    private static string? GetJpegXmp(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return null;
        }

        var offset = 2;
        while (offset + 4 <= bytes.Length && bytes[offset] == 0xFF)
        {
            var marker = bytes[offset + 1];
            if (marker is 0xDA or 0xD9)
            {
                break;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2, 2));
            var dataLength = segmentLength - 2;
            var dataOffset = offset + 4;
            if (dataOffset + dataLength > bytes.Length)
            {
                break;
            }

            if (marker == 0xE1
                && dataLength >= JpegXmpHeader.Length
                && bytes.AsSpan(dataOffset, JpegXmpHeader.Length).SequenceEqual(JpegXmpHeader))
            {
                return Encoding.UTF8.GetString(bytes, dataOffset + JpegXmpHeader.Length, dataLength - JpegXmpHeader.Length);
            }

            offset += segmentLength + 2;
        }

        return null;
    }

    private static void WriteJpegApp1(Stream output, byte[] payload)
    {
        Span<byte> header = stackalloc byte[4];
        header[0] = 0xFF;
        header[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(payload.Length + 2));
        output.Write(header);
        output.Write(payload);
    }

    private static byte[] BuildExifPayload(ImageMetadataUpdate update)
    {
        using var tiff = new MemoryStream();
        tiff.Write("II"u8);
        WriteUInt16LittleEndian(tiff, 42);
        WriteUInt32LittleEndian(tiff, 8);

        var entries = new List<ExifEntry>();
        var data = new MemoryStream();

        AddAscii(entries, data, 0x010E, update.Comments); // ImageDescription
        AddAscii(entries, data, 0x0131, update.ProgramName); // Software
        AddXpString(entries, data, 0x9C9B, update.Title); // XPTitle
        AddXpString(entries, data, 0x9C9C, update.Comments); // XPComment

        var dataOffsetBase = 8u + 2u + 12u * (uint)entries.Count + 4u;
        WriteUInt16LittleEndian(tiff, (ushort)entries.Count);
        foreach (var entry in entries.OrderBy(entry => entry.Tag))
        {
            WriteUInt16LittleEndian(tiff, entry.Tag);
            WriteUInt16LittleEndian(tiff, entry.Type);
            WriteUInt32LittleEndian(tiff, entry.Count);
            WriteUInt32LittleEndian(tiff, dataOffsetBase + entry.RelativeOffset);
        }

        WriteUInt32LittleEndian(tiff, 0);
        data.Position = 0;
        data.CopyTo(tiff);

        using var payload = new MemoryStream();
        payload.Write("Exif\0\0"u8);
        tiff.Position = 0;
        tiff.CopyTo(payload);
        return payload.ToArray();
    }

    private static void AddAscii(List<ExifEntry> entries, MemoryStream data, ushort tag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var bytes = Encoding.ASCII.GetBytes(ReplaceNonAscii(value) + '\0');
        entries.Add(AddOutOfLineData(data, tag, type: 2, bytes));
    }

    private static void AddXpString(List<ExifEntry> entries, MemoryStream data, ushort tag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(value + '\0');
        entries.Add(AddOutOfLineData(data, tag, type: 1, bytes));
    }

    private static ExifEntry AddOutOfLineData(MemoryStream data, ushort tag, ushort type, byte[] bytes)
    {
        var offset = (uint)data.Length;
        data.Write(bytes);
        if (data.Length % 2 != 0)
        {
            data.WriteByte(0);
        }

        return new ExifEntry(tag, type, (uint)bytes.Length, offset);
    }

    private static string ReplaceNonAscii(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c <= 0x7F ? c : '?');
        }

        return builder.ToString();
    }

    private static void WriteUInt16LittleEndian(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32LittleEndian(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed record ExifEntry(ushort Tag, ushort Type, uint Count, uint RelativeOffset);

    private static ImageMetadata ReadPng(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (!bytes.AsSpan(0, Math.Min(bytes.Length, PngSignature.Length)).SequenceEqual(PngSignature))
        {
            throw new InvalidOperationException("Invalid PNG file.");
        }

        var values = ReadPngTextChunks(bytes);
        values.TryGetValue("Comments", out var comments);
        values.TryGetValue("Description", out var description);
        values.TryGetValue("Title", out var title);
        values.TryGetValue("Software", out var software);
        values.TryGetValue("PixcribeUpdatedAt", out var updatedText);
        values.TryGetValue("Keywords", out var keywords);

        return new ImageMetadata(
            FirstNonBlank(comments, description),
            title,
            software,
            ParseDate(updatedText),
            SplitTags(keywords));
    }

    private static void WritePng(string path, ImageMetadataUpdate update)
    {
        var bytes = File.ReadAllBytes(path);
        if (!bytes.AsSpan(0, Math.Min(bytes.Length, PngSignature.Length)).SequenceEqual(PngSignature))
        {
            throw new InvalidOperationException("Invalid PNG file.");
        }

        using var output = new MemoryStream();
        output.Write(PngSignature);
        var offset = PngSignature.Length;
        var wrotePixcribeChunks = false;

        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var chunkTotal = checked((int)length + 12);
            if (offset + chunkTotal > bytes.Length)
            {
                throw new InvalidOperationException("Invalid PNG chunk length.");
            }

            if (type == "IEND" && !wrotePixcribeChunks)
            {
                WritePngText(output, "Comments", update.Comments);
                WritePngText(output, "Description", update.Comments);
                WritePngText(output, "Title", update.Title);
                WritePngText(output, "Software", update.ProgramName);
                WritePngText(output, "PixcribeUpdatedAt", update.PixcribeUpdatedAt.ToString("O"));
                if (update.Tags.Count > 0)
                {
                    WritePngText(output, "Keywords", string.Join(", ", update.Tags));
                }
                wrotePixcribeChunks = true;
            }

            var isPixcribeText = (type == "tEXt" || type == "iTXt" || type == "zTXt")
                && IsPixcribePngKeyword(bytes.AsSpan(offset + 8, (int)length));
            if (!isPixcribeText)
            {
                output.Write(bytes, offset, chunkTotal);
            }

            offset += chunkTotal;
        }

        File.WriteAllBytes(path, output.ToArray());
    }

    private static Dictionary<string, string> ReadPngTextChunks(byte[] bytes)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = PngSignature.Length;
        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var dataOffset = offset + 8;
            var chunkTotal = checked((int)length + 12);
            if (dataOffset + length > bytes.Length)
            {
                break;
            }

            if (type == "tEXt")
            {
                var data = bytes.AsSpan(dataOffset, (int)length);
                var separator = data.IndexOf((byte)0);
                if (separator > 0)
                {
                    var key = Encoding.UTF8.GetString(data[..separator]);
                    var value = Encoding.UTF8.GetString(data[(separator + 1)..]);
                    values[key] = value;
                }
            }
            else if (type == "iTXt")
            {
                ReadInternationalText(values, bytes.AsSpan(dataOffset, (int)length));
            }
            else if (type == "zTXt")
            {
                ReadCompressedText(values, bytes.AsSpan(dataOffset, (int)length));
            }

            offset += chunkTotal;
        }

        return values;
    }

    private static void ReadInternationalText(Dictionary<string, string> values, ReadOnlySpan<byte> data)
    {
        var keyEnd = data.IndexOf((byte)0);
        if (keyEnd <= 0 || keyEnd + 5 >= data.Length)
        {
            return;
        }

        var key = Encoding.UTF8.GetString(data[..keyEnd]);
        var compressionFlag = data[keyEnd + 1];
        if (compressionFlag != 0)
        {
            return;
        }

        var cursor = keyEnd + 3;
        var languageEnd = data[cursor..].IndexOf((byte)0);
        if (languageEnd < 0)
        {
            return;
        }

        cursor += languageEnd + 1;
        var translatedEnd = data[cursor..].IndexOf((byte)0);
        if (translatedEnd < 0)
        {
            return;
        }

        cursor += translatedEnd + 1;
        values[key] = Encoding.UTF8.GetString(data[cursor..]);
    }

    private static void ReadCompressedText(Dictionary<string, string> values, ReadOnlySpan<byte> data)
    {
        var keyEnd = data.IndexOf((byte)0);
        if (keyEnd <= 0 || keyEnd + 2 >= data.Length || data[keyEnd + 1] != 0)
        {
            return;
        }

        var key = Encoding.UTF8.GetString(data[..keyEnd]);
        using var compressed = new MemoryStream(data[(keyEnd + 2)..].ToArray());
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(zlib, Encoding.UTF8);
        values[key] = reader.ReadToEnd();
    }

    private static bool IsPixcribePngKeyword(ReadOnlySpan<byte> data)
    {
        var separator = data.IndexOf((byte)0);
        if (separator <= 0)
        {
            return false;
        }

        var key = Encoding.UTF8.GetString(data[..separator]);
        return key.Equals("Comments", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Description", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Title", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Software", StringComparison.OrdinalIgnoreCase)
            || key.Equals("PixcribeUpdatedAt", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Keywords", StringComparison.OrdinalIgnoreCase);
    }

    private static void WritePngText(Stream output, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var keyBytes = Encoding.UTF8.GetBytes(key);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var data = new byte[keyBytes.Length + 1 + valueBytes.Length];
        Buffer.BlockCopy(keyBytes, 0, data, 0, keyBytes.Length);
        Buffer.BlockCopy(valueBytes, 0, data, keyBytes.Length + 1, valueBytes.Length);

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        var type = Encoding.ASCII.GetBytes("tEXt");
        output.Write(type);
        output.Write(data);

        var crcInput = new byte[type.Length + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput[type.Length..]);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(crcInput));
        output.Write(crc);
    }

    private static ImageMetadata ParseXmp(string xmp)
    {
        return new ImageMetadata(
            ReadAltText(xmp, "description"),
            ReadAltText(xmp, "title"),
            ReadSimpleElement(xmp, "xmp:CreatorTool"),
            ParseDate(ReadSimpleElement(xmp, "pixcribe:UpdatedAt")),
            ReadBagItems(xmp, "subject"));
    }

    private static string BuildXmp(string? existingXmp, ImageMetadataUpdate update)
    {
        var description = SecurityElement.Escape(update.Comments ?? "");
        var title = SecurityElement.Escape(update.Title ?? "");
        var creatorTool = SecurityElement.Escape(update.ProgramName);
        var timestamp = SecurityElement.Escape(update.PixcribeUpdatedAt.ToString("O"));
        var subjectItems = string.Join("", update.Tags.Select(tag => $"<rdf:li>{SecurityElement.Escape(tag)}</rdf:li>"));

        return
            $"""
            <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about=""
                    xmlns:dc="http://purl.org/dc/elements/1.1/"
                    xmlns:xmp="http://ns.adobe.com/xap/1.0/"
                    xmlns:pixcribe="https://pixcribe.local/ns/1.0/">
                  <dc:description><rdf:Alt><rdf:li xml:lang="x-default">{description}</rdf:li></rdf:Alt></dc:description>
                  <dc:title><rdf:Alt><rdf:li xml:lang="x-default">{title}</rdf:li></rdf:Alt></dc:title>
                  <dc:subject><rdf:Bag>{subjectItems}</rdf:Bag></dc:subject>
                  <xmp:CreatorTool>{creatorTool}</xmp:CreatorTool>
                  <pixcribe:UpdatedAt>{timestamp}</pixcribe:UpdatedAt>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            <?xpacket end="w"?>
            """;
    }

    private static string? ReadAltText(string xmp, string localName)
    {
        var match = Regex.Match(
            xmp,
            $@"<dc:{Regex.Escape(localName)}>\s*<rdf:Alt>\s*<rdf:li[^>]*>(.*?)</rdf:li>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? DecodeXml(match.Groups[1].Value) : null;
    }

    private static string? ReadSimpleElement(string xmp, string elementName)
    {
        var match = Regex.Match(
            xmp,
            $@"<{Regex.Escape(elementName)}[^>]*>(.*?)</{Regex.Escape(elementName)}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? DecodeXml(match.Groups[1].Value) : null;
    }

    private static IReadOnlyList<string> ReadBagItems(string xmp, string localName)
    {
        var match = Regex.Match(
            xmp,
            $@"<dc:{Regex.Escape(localName)}>\s*<rdf:Bag>(.*?)</rdf:Bag>\s*</dc:{Regex.Escape(localName)}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return [];
        }

        return Regex.Matches(match.Groups[1].Value, @"<rdf:li[^>]*>(.*?)</rdf:li>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(item => DecodeXml(item.Groups[1].Value))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static IReadOnlyList<string> SplitTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string DecodeXml(string value) =>
        SecurityElement.FromString($"<x>{value}</x>")?.Text ?? value;
}

internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 1 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
