// VisionServiceCollectionExtensions.cs
//
// The way in. The four on-device vision classes had none.
//
// WHAT WAS WRONG. OnnxFaceDetector, OnnxFaceEmbedder, OnnxPlateRecognizer and
// OnnxImageGenerator were written, tested, committed - and never constructed or
// registered anywhere. A grep for callers on 2026-10-09 returned zero for
// IFaceDetector, IFaceEmbedder and IPlateRecognizer outside the Vision folder,
// and nothing anywhere did `new Onnx*` or AddSingleton<I*>. CLAUDE.md names this
// as the repository's most common defect by a wide margin, and this is it: four
// classes, four interfaces, an ONNX dependency and 1,000 lines of pixel code with
// no door.
//
// WHY LAZY IS NOT A STYLE CHOICE. Every one of those constructors opens an
// InferenceSession and throws FileNotFoundException when the model file is
// absent:
//
//     if (!File.Exists(opts.ModelPath))
//         throw new FileNotFoundException("ONNX model not found", opts.ModelPath);
//
// Models are DOWNLOADED on this product, not shipped - the P30 right now has an
// empty model directory. A straight AddSingleton<IFaceDetector, OnnxFaceDetector>
// would therefore throw on first resolve on every device that has not fetched
// them, and in a service host that resolves eagerly it would take startup down
// over a feature nobody asked for yet. So registration is a factory, and it
// checks the file before it builds the session.
//
// WHY ABSENCE IS REPORTED AND NOT PAPERED OVER. The obvious alternative - return
// a Null implementation that answers "no faces" - is the plausible zero this
// repository has been bitten by all week: a detector that finds nothing looks
// exactly like a photo with nobody in it. TryAdd* is not used either, for the
// same reason: silently losing to an earlier registration is how you end up
// debugging the wrong object. A caller gets either a working detector or a
// resolution failure naming the missing file, and Describe() states the position
// before anything is resolved at all.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Core;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Vision;

/// <summary>Registers the on-device ONNX vision stack.</summary>
public static class VisionServiceCollectionExtensions
{
    /// <summary>Keys for resolving a specific on-device generator.</summary>
    /// <remarks>
    /// Keyed, matching VisionCloudServiceCollectionExtensions.GeneratorIds, so a
    /// host can build a fallback chain that prefers the device and falls back to
    /// a cloud provider. See the note on the two IImageGenerator interfaces below -
    /// that chain cannot actually be built today.
    /// </remarks>
    public static class GeneratorIds
    {
        public const string OnDeviceOnnx = "onnx-ondevice";
    }

    /// <summary>
    /// Registers <see cref="OnnxFaceDetector"/> as <see cref="IFaceDetector"/>.
    /// </summary>
    /// <remarks>
    /// Singleton because an InferenceSession is expensive to build and safe to
    /// share for inference; the session is held for the container's lifetime and
    /// disposed with it, which is what the class's IDisposable expects.
    /// </remarks>
    public static IServiceCollection AddOnnxFaceDetector(
        this IServiceCollection services,
        Func<IServiceProvider, OnnxFaceDetectorOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.AddSingleton<IFaceDetector>(sp =>
        {
            var opts = optionsFactory(sp);
            RequireModel(opts?.ModelPath, nameof(OnnxFaceDetector), "face detection");
            return new OnnxFaceDetector(opts!);
        });
        return services;
    }

    /// <summary>Registers <see cref="OnnxFaceEmbedder"/> as <see cref="IFaceEmbedder"/>.</summary>
    public static IServiceCollection AddOnnxFaceEmbedder(
        this IServiceCollection services,
        Func<IServiceProvider, OnnxFaceEmbedderOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.AddSingleton<IFaceEmbedder>(sp =>
        {
            var opts = optionsFactory(sp);
            RequireModel(opts?.ModelPath, nameof(OnnxFaceEmbedder), "face recognition");
            return new OnnxFaceEmbedder(opts!);
        });
        return services;
    }

    /// <summary>Registers <see cref="OnnxPlateRecognizer"/> as <see cref="IPlateRecognizer"/>.</summary>
    public static IServiceCollection AddOnnxPlateRecognizer(
        this IServiceCollection services,
        Func<IServiceProvider, OnnxPlateRecognizerOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.AddSingleton<IPlateRecognizer>(sp =>
        {
            var opts = optionsFactory(sp);
            RequireModel(opts?.ModelPath, nameof(OnnxPlateRecognizer), "plate recognition");
            return new OnnxPlateRecognizer(opts!);
        });
        return services;
    }

    /// <summary>
    /// Registers <see cref="OnnxImageGenerator"/> as <see cref="CircleAI.Core.IImageGenerator"/>.
    /// </summary>
    /// <remarks>
    /// THERE ARE TWO INTERFACES CALLED IImageGenerator AND THIS REGISTERS THE
    /// OTHER ONE. CircleAI.Core.IImageGenerator is what OnnxImageGenerator
    /// implements; CircleAI.Vision.Cloud.IImageGenerator is what
    /// OpenAiImageGenerator and StabilityImageGenerator implement, and what
    /// ImageGeneratorFallbackChain composes. They are different types with the
    /// same name in different namespaces, so the on-device generator can NOT be
    /// put into that chain and a host cannot ask for "an image generator" and get
    /// whichever is available.
    ///
    /// Unifying them changes a published contract that cloud consumers already
    /// implement, so it is not done here. Registering against Core's interface is
    /// the honest half: the class becomes reachable, and the collision stays
    /// visible rather than being hidden behind an adapter that makes it look
    /// solved. Keyed as well as plain, so a host can name it explicitly.
    /// </remarks>
    public static IServiceCollection AddOnnxImageGenerator(
        this IServiceCollection services,
        Func<IServiceProvider, OnnxImageModel> modelFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(modelFactory);

        services.AddSingleton<CircleAI.Core.IImageGenerator>(sp =>
        {
            var model = modelFactory(sp);
            RequireModel(model?.UnetPath, nameof(OnnxImageGenerator), "image generation (UNet)");
            RequireModel(model?.VaeDecoderPath, nameof(OnnxImageGenerator), "image generation (VAE decoder)");
            return new OnnxImageGenerator(model!);
        });

        services.AddKeyedSingleton<CircleAI.Core.IImageGenerator>(
            GeneratorIds.OnDeviceOnnx,
            (sp, _) => sp.GetRequiredService<CircleAI.Core.IImageGenerator>());

        return services;
    }

    /// <summary>
    /// Registers every on-device vision service whose model is present, and
    /// returns what it did.
    /// </summary>
    /// <remarks>
    /// The convenience entry point for a head: one call, and a report rather
    /// than silence. Each service is registered only when its file is on disk at
    /// the time of the call, because a head wiring this at startup can then say
    /// truthfully which abilities exist. Nothing is registered against a missing
    /// file: a resolve that throws later, deep inside a feature, is worse than an
    /// ability the app never offered.
    /// </remarks>
    public static VisionWiringReport AddOnDeviceVision(
        this IServiceCollection services,
        VisionModelPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        var wired = new List<string>();
        var skipped = new List<string>();

        if (Exists(paths.FaceDetectorModel))
        {
            services.AddOnnxFaceDetector(_ => new OnnxFaceDetectorOptions(paths.FaceDetectorModel!));
            wired.Add("face detection");
        }
        else skipped.Add($"face detection (no model at {Describe(paths.FaceDetectorModel)})");

        if (Exists(paths.FaceEmbedderModel))
        {
            services.AddOnnxFaceEmbedder(_ => new OnnxFaceEmbedderOptions(paths.FaceEmbedderModel!));
            wired.Add("face recognition");
        }
        else skipped.Add($"face recognition (no model at {Describe(paths.FaceEmbedderModel)})");

        if (Exists(paths.PlateRecognizerModel))
        {
            services.AddOnnxPlateRecognizer(_ => new OnnxPlateRecognizerOptions(paths.PlateRecognizerModel!));
            wired.Add("plate recognition");
        }
        else skipped.Add($"plate recognition (no model at {Describe(paths.PlateRecognizerModel)})");

        if (Exists(paths.ImageGeneratorUnet) && Exists(paths.ImageGeneratorVaeDecoder))
        {
            services.AddOnnxImageGenerator(_ => new OnnxImageModel(
                paths.ImageGeneratorUnet!,
                paths.ImageGeneratorVaeDecoder!,
                paths.ImageGeneratorTextEncoder));
            wired.Add("image generation");
        }
        else skipped.Add("image generation (needs both a UNet and a VAE decoder)");

        return new VisionWiringReport(wired, skipped, ImageDecoder.IsSupported);
    }

    private static bool Exists(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static string Describe(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "no path configured" : path!;

    /// <summary>
    /// Fails the resolve with a sentence that says which file and which ability.
    /// </summary>
    /// <remarks>
    /// The constructors already throw FileNotFoundException, and this adds nothing
    /// to that except the ABILITY the caller lost. "ONNX model not found:
    /// /data/.../x.onnx" sends whoever reads it hunting for a packaging bug;
    /// naming the feature points at a download that has not happened.
    /// </remarks>
    private static void RequireModel(string? path, string type, string ability)
    {
        if (Exists(path)) return;

        throw new InvalidOperationException(
            $"{type} cannot be built: {ability} needs an ONNX model and there is none at "
            + $"{Describe(path)}. Models on this product are downloaded, not shipped - fetch it "
            + $"before resolving this service, or register it only when the file is present "
            + $"(VisionServiceCollectionExtensions.AddOnDeviceVision does that and reports what it wired).");
    }
}

/// <summary>Where the on-device vision models live. Any may be null.</summary>
public sealed class VisionModelPaths
{
    /// <summary>YOLO-style face detector, e.g. yolov8n-face.onnx.</summary>
    public string? FaceDetectorModel { get; init; }

    /// <summary>ArcFace-style embedder. Note: it consumes BGR, not RGB.</summary>
    public string? FaceEmbedderModel { get; init; }

    /// <summary>Licence-plate detector / reader.</summary>
    public string? PlateRecognizerModel { get; init; }

    /// <summary>Diffusion UNet.</summary>
    public string? ImageGeneratorUnet { get; init; }

    /// <summary>Diffusion VAE decoder. Required alongside the UNet.</summary>
    public string? ImageGeneratorVaeDecoder { get; init; }

    /// <summary>Optional text encoder for the generator.</summary>
    public string? ImageGeneratorTextEncoder { get; init; }
}

/// <summary>What <c>AddOnDeviceVision</c> actually wired, and what it could not.</summary>
/// <param name="Wired">Abilities now resolvable.</param>
/// <param name="Skipped">Abilities not registered, each with the reason.</param>
/// <param name="CanDecodeImages">
/// Whether this build can decode encoded image bytes at all. False on a bare
/// net9.0/net10.0 host, where there is no platform codec - every registration
/// above can still succeed there and then fail on the first photo, so a head
/// should read this before offering any of it.
/// </param>
public sealed record VisionWiringReport(
    IReadOnlyList<string> Wired,
    IReadOnlyList<string> Skipped,
    bool CanDecodeImages)
{
    /// <summary>One line for a log or a screen. States counts, never a bare boolean.</summary>
    public override string ToString()
    {
        var head = Wired.Count == 0
            ? "vision: nothing wired"
            : $"vision: {Wired.Count} wired ({string.Join(", ", Wired)})";

        var tail = Skipped.Count == 0 ? "" : $"; {Skipped.Count} skipped - {string.Join("; ", Skipped)}";
        var codec = CanDecodeImages ? "" : "; NO IMAGE CODEC on this platform, so none of it can read a photo";

        return head + tail + codec;
    }
}
