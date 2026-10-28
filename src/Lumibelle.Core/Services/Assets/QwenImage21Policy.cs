using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;

namespace lumibelle.Services.Assets;

public static class QwenImage21Policy
{
    public const int MaximumReferences = 10;
    public const int MaximumResolution = 2048;
    public const int MaximumEncodedSourceBytes = 25 * 1024 * 1024;
    // Allow the small area increase caused by the encoder's 32-pixel rounding.
    public const long MaximumNativePixels = 4_325_376;
    public const long MaximumPreparedBytes = 250L * 1024 * 1024;
    public static QwenImage21Settings Settings(AiSettings settings) => settings.QwenImage21 ?? new();
    public static QwenImage21Options CaptureOptions(AiSettings settings, QwenImage21Options? options) =>
        Settings(settings).Variant == QwenImage21Variant.Turbo ? (options ?? new()) with { Steps = 8 } : options ?? new();
    public static QwenImage21Options Options(AiImageJobRequest request) => request.Create?.QwenImage21 ?? request.Edit?.QwenImage21 ?? new();

    public static void ValidateSettings(QwenImage21Settings? settings)
    {
        if (settings is null) return; // Old settings retain their exact serialized representation.
        static bool InvalidName(string? name) => string.IsNullOrWhiteSpace(name) || name.Length > 2000 ||
            name.IndexOfAny(['\0', '\r', '\n']) >= 0;
        if (!Enum.IsDefined(settings.Variant) || InvalidName(settings.Model) || InvalidName(settings.TextEncoder) || InvalidName(settings.Vae) ||
            settings.CacheDevice is not ("auto" or "gpu" or "cpu" or "off") || settings.CacheDtype is not ("default" or "int8" or "int4"))
            throw new WorkspaceStoreException("Choose the Qwen Image 2.1 model, Qwen3-VL 8B encoder, its own VAE, and valid cache settings.");
        var file = Path.GetFileName(settings.Model.Replace('\\', '/'));
        if (file.StartsWith("qwen_image_2.1_", StringComparison.OrdinalIgnoreCase) &&
            file.Contains("_turbo_", StringComparison.OrdinalIgnoreCase) != (settings.Variant == QwenImage21Variant.Turbo))
            throw new WorkspaceStoreException("Choose the Qwen Base or Turbo variant matching the selected checkpoint file.");
    }

    public static QwenImage21Settings Normalize(QwenImage21Settings settings) => settings with
    { Model = settings.Model.Trim(), TextEncoder = settings.TextEncoder.Trim(), Vae = settings.Vae.Trim() };

    public static void ValidateOptions(QwenImage21Options options, bool editing)
    {
        if (options.Resolution is not (0 or 1024 or 1440 or 2048) || !editing && options.Resolution == 0 || options.Steps is < 1 or > 100)
            throw new AiGenerationException("Qwen Image 2.1 needs 1K (~1 MP), 1440 (~2 MP), or 2K (~4 MP), and 1–100 steps. Original size is edit-only.");
    }

    public static (int Width, int Height) Size(int sourceWidth, int sourceHeight, int resolution)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || sourceWidth > 32768 || sourceHeight > 32768 || resolution is not (0 or 1024 or 1440 or 2048))
            throw new AiGenerationException("Invalid Qwen Image 2.1 source dimensions or resolution.");
        static int Round32(double value) => Math.Max(32, checked((int)Math.Round(value / 32, MidpointRounding.ToEven)) * 32);
        var ratio = (double)sourceWidth / sourceHeight;
        var width = Round32(resolution == 0 ? sourceWidth : Math.Sqrt((double)resolution * resolution * ratio));
        var height = Round32(resolution == 0 ? sourceHeight : Math.Sqrt((double)resolution * resolution / ratio));
        if (width > 16384 || height > 16384 || (long)width * height > MaximumNativePixels)
            throw new AiGenerationException("This original image is above the supported Qwen Image 2.1 pixel budget. Choose 1K, ~2 MP or 2K instead, or crop the image.");
        return (width, height);
    }

    public static (int Width, int Height) CreateSize(string aspect, int resolution)
    {
        if (!ComfyReferenceImageGenerator.Sizes.ContainsKey(aspect) || resolution == 0)
            throw new AiGenerationException("Choose a supported aspect ratio and output size.");
        var parts = aspect.Split(':');
        return Size(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), resolution);
    }

    public static (int Width, int Height) PngSize(byte[] png)
    {
        try
        {
            if (png is null || png.Length is 0 or > MaximumEncodedSourceBytes)
                throw new AiGenerationException("A Qwen reference is empty or too large.");
            var info = Image.Identify(png);
            if (info is null || info.Metadata.DecodedImageFormat?.Name != "PNG")
                throw new AiGenerationException("Qwen image requests require prepared PNG references.");
            return (info.Width, info.Height);
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ArgumentException)
        { throw new AiGenerationException("A Qwen reference could not be read as a PNG image."); }
    }

    public static (int Width, int Height) OutputSize(AiImageJobRequest request)
    {
        if (request.Workflow != ImageWorkflow.QwenImage21) return ImageAspectPolicy.Size(request.AspectRatio, request.Inputs.FirstOrDefault()?.Png, request.Create?.Resolution ?? request.Edit!.Resolution);
        var options = Options(request); ValidateOptions(options, request.Edit is not null);
        if (request.Edit is null) return CreateSize(request.AspectRatio, options.Resolution);
        if (request.Inputs.Count == 0) throw new AiGenerationException("Choose the first image to edit.");
        // Regional inputs are already fitted to the captured output canvas. Recomputing
        // an area-based size from the rounded PNG can move its crop/restoration mapping.
        if (request.Regional is { } regional) return (regional.Canvas.CanvasWidth, regional.Canvas.CanvasHeight);
        var size = PngSize(request.Inputs[0].Png);
        return EditSize(request.AspectRatio, size.Width, size.Height, options);
    }

    public static (int Width, int Height) EditSize(string aspect, int sourceWidth, int sourceHeight, QwenImage21Options options)
    {
        if (aspect == ImageAspectPolicy.FromImage1) return Size(sourceWidth, sourceHeight, options.Resolution);
        if (!ComfyReferenceImageGenerator.Sizes.ContainsKey(aspect)) throw new AiGenerationException("Choose an explicit output aspect ratio.");
        if (options.Resolution > 0) return CreateSize(aspect, options.Resolution);
        // Original reference sizes remain unchanged; use Image 1's area for the new canvas.
        var parts = aspect.Split(':');
        var ratio = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
        var width = (int)Math.Round(Math.Sqrt((double)sourceWidth * sourceHeight * ratio));
        var height = (int)Math.Round(Math.Sqrt((double)sourceWidth * sourceHeight / ratio));
        return Size(Math.Max(1, width), Math.Max(1, height), 0);
    }

    public static void ValidateRequest(AiImageJobRequest request)
    {
        if (request.Workflow != ImageWorkflow.QwenImage21)
        {
            if (request.Create?.QwenImage21 is not null || request.Edit?.QwenImage21 is not null)
                throw new AiGenerationException("Qwen Image 2.1 options cannot be applied to another image workflow.");
            return;
        }
        if (request.Settings.QwenImage21 is null || (request.Create?.QwenImage21 ?? request.Edit?.QwenImage21) is null)
            throw new AiGenerationException("The captured Qwen Image 2.1 model settings and output options are missing. Capture a new request; defaults are not substituted during recovery.");
        ValidateSettings(request.Settings.QwenImage21);
        ValidateOptions(Options(request), request.Edit is not null);
        if (request.Settings.QwenImage21.Variant == QwenImage21Variant.Turbo && Options(request).Steps != 8)
            throw new AiGenerationException("Qwen Image 2.1 Turbo requires its fixed eight-step schedule.");
        if (request.Edit?.Regions is { Count: > 0 } && request.Regional is null)
            throw new AiGenerationException("Regional Qwen edits require the captured original and canvas for review.");
        if (request.Regional is { } regional && (regional.Canvas != RegionalImageEdits.Canvas(regional.Selection, request.AspectRatio, qwen: Options(request)) ||
            request.Inputs.Count == 0 || PngSize(request.Inputs[0].Png) != (regional.Canvas.CanvasWidth, regional.Canvas.CanvasHeight)))
            throw new AiGenerationException("The prepared Qwen canvas does not match its captured regional selection.");
        if (request.Inputs.Count > MaximumReferences || request.Edit is not null && request.Inputs.Count == 0 ||
            request.Inputs.Sum(i => (long)i.Png.Length) > MaximumPreparedBytes)
            throw new AiGenerationException("Use one to ten Qwen edit images, with at most 250 MB of prepared PNG data.");
        foreach (var input in request.Inputs)
        {
            var size = PngSize(input.Png);
            // Validate every reference, not only image_1; resolution applies to them all.
            Size(size.Width, size.Height, Options(request).Resolution);
        }
        OutputSize(request);
    }

    public static AssetGenerationMetadata Metadata(AiImageJobRequest request, Guid jobId, AiBatchCandidate candidate)
    {
        var size = OutputSize(request);
        return Metadata(request.Settings, Options(request), request.Prompt, request.AspectRatio, size, request.Edit,
            request.Inputs.Select(i => i.Reference).ToArray(), request.AppliedLoras, candidate.Seed) with
        { AiJobId = jobId, BatchId = request.BatchId, CandidateNumber = candidate.Number, Look = request.Look };
    }

    internal static AssetGenerationMetadata Metadata(AiSettings settings, QwenImage21Options options, string prompt, string aspect,
        (int Width, int Height) size, ReferenceEditRequest? edit, IReadOnlyList<AssetImageReference> references,
        IReadOnlyList<AppliedLora> loras, long seed)
    {
        var qwen = Settings(settings);
        return new()
        {
            Workflow = ImageWorkflow.QwenImage21, Prompt = prompt.Trim(), Seed = seed, AspectRatio = aspect,
            DiffusionModel = qwen.Model, TextEncoder = qwen.TextEncoder, Vae = qwen.Vae, Steps = options.Steps, Loras = loras,
            QwenImage21 = new(options, size.Width, size.Height, edit is null ? "off" : qwen.CacheDevice, edit is null ? "default" : qwen.CacheDtype) { Variant = qwen.Variant },
            Edit = edit is null ? null : new()
            {
                SourceAssetId = edit.SourceAssetId, SourceImageId = edit.SourceImageId, SourceCrop = edit.SourceCrop,
                References = references, ReferenceCrops = edit.ReferenceCrops, ReferenceLooks = edit.ReferenceLooks, Regions = edit.Regions,
                Lora = "", LoraStrength = 0, ReferenceBoost = 0, BaseReferenceBoost = null, GroundingPixels = 0,
                FitMode = aspect == ImageAspectPolicy.FromImage1 ? "qwen-image21-source-latent" : "qwen-image21-output-latent"
            }
        };
    }

    public static bool InvalidMetadata(AssetGenerationMetadata? generation, int width, int height)
    {
        if (generation?.Workflow != ImageWorkflow.QwenImage21) return generation?.QwenImage21 is not null;
        if (generation.QwenImage21 is not { Options: { } options } saved ||
            generation.Edit?.Regional is null && (saved.Width != width || saved.Height != height) ||
            saved.Width < 32 || saved.Height < 32 || saved.Width > 16384 || saved.Height > 16384 || saved.Width % 32 != 0 || saved.Height % 32 != 0 || (long)saved.Width * saved.Height > MaximumNativePixels ||
            !Enum.IsDefined(saved.Variant) || saved.Variant == QwenImage21Variant.Turbo && options.Steps != 8 ||
            generation.Steps != options.Steps || saved.CacheDevice is not ("auto" or "gpu" or "cpu" or "off") ||
            saved.CacheDtype is not ("default" or "int8" or "int4")) return true;
        try
        {
            ValidateOptions(options, generation.Edit is not null);
            if (generation.Edit?.Regional is { } regional &&
                (regional.Selection.Width != width || regional.Selection.Height != height ||
                 regional.Canvas != RegionalImageEdits.Canvas(regional.Selection, generation.AspectRatio, qwen: options) ||
                 (saved.Width, saved.Height) != (regional.Canvas.CanvasWidth, regional.Canvas.CanvasHeight))) return true;
            if (generation.Edit is null && CreateSize(generation.AspectRatio, options.Resolution) != (width, height)) return true;
        }
        catch (AiGenerationException) { return true; }
        return false;
    }

    public static bool InvalidEdit(AssetEditMetadata edit) => edit.References.Count is < 1 or > MaximumReferences ||
        edit.References[0] != new AssetImageReference(edit.SourceAssetId, edit.SourceImageId) ||
        edit.BaseReferenceBoost is not null || edit.Lora != "" || edit.LoraStrength != 0 || edit.ReferenceBoost != 0 ||
        edit.GroundingPixels != 0 || edit.FitMode is not ("qwen-image21-source-latent" or "qwen-image21-output-latent");
}
