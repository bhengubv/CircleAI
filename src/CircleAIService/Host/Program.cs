// Program.cs
//
// CircleAI as a service on a desktop, which is the same promise as on the phone with
// a different front door.
//
// THE ANDROID HEAD IS A BOUND SERVICE AND THAT SHAPE DOES NOT TRAVEL. A binder is
// Android's; Windows has no equivalent an app can attach to by package name. What is
// the same is the thing being offered - one process on this machine owns the models
// and answers for every app that asks - so the transport changes and nothing else
// does.
//
// HttpLoopbackEndpoint IS THAT TRANSPORT AND IT WAS ALREADY WRITTEN. Its own first
// line says "zero ASP.NET Core dependency and stay portable across Linux / macOS /
// Windows / Android", it binds loopback only, and it already carries a shared-secret
// header. It had no host on this platform; this is the host.
//
// LOOPBACK ONLY, DELIBERATELY. The endpoint binds 127.0.0.1 and nothing else: a
// machine-local engine is not a server, and a service that holds somebody's memory
// has no business being reachable from the network it happens to be on.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Hosting;
using CircleAI.Hosting.Endpoints;
using CircleAI.Inference;

namespace CircleAI.Service.Host;

/// <summary>The desktop head: owns the models, answers over loopback.</summary>
public static class Program
{
    /// <summary>Starts the service and runs until it is asked to stop.</summary>
    public static async Task<int> Main(string[] args)
    {
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

        var store = ModelStore();
        Console.WriteLine($"CircleAI service — models at {store}");

        try
        {
            using var registry = new ModelRegistryService();
            using var loader   = new BundleModelLoader(store, registry);

            // WHICH MODEL, AND IS IT ACTUALLY HERE. Resolved and printed before
            // anything can block on it.
            //
            // A BRAIN WITH NO MODEL DOES NOT FAIL, IT HANGS. The core's resolve path
            // blocks and ignores its cancellation token, so a question asked against
            // an absent model never returns - measured here: 180 seconds, 55 MB
            // resident, 2.8 seconds of CPU. Nothing was loading; it was waiting. The
            // Android head grew the same guard for the same reason.
            //
            // AND "PRESENT" IS NOT "THE DIRECTORY EXISTS". This store held a
            // Ternary-Bonsai-2-27B folder of 0.0 GB - an abandoned fetch - which is
            // exactly the shape that gets chosen and then never loads.
            //
            // CIRCLEAI_MODEL PINS ONE. On a phone the selector's answer is the only
            // sane one; on a desktop somebody may have four models and a preference,
            // and the alternative is editing a config file to try the other one.
            var wanted = Environment.GetEnvironmentVariable("CIRCLEAI_MODEL");
            if (string.IsNullOrWhiteSpace(wanted))
                wanted = new DeviceAwareModelSelector(registry)
                    .BestFit(DeviceProbe.Snapshot(), ChatCapability.Default)?.ModelId;

            if (string.IsNullOrWhiteSpace(wanted))
            {
                Console.Error.WriteLine("No model in the catalogue fits this machine.");
                return 2;
            }

            if (!loader.ModelPresent(wanted!))
            {
                Console.Error.WriteLine($"Model '{wanted}' is not on this machine.");
                Console.Error.WriteLine($"Put it under {store}, or set CIRCLEAI_MODEL to one that is.");
                return 2;
            }

            Console.WriteLine($"model: {wanted}");

            var options = new AIOptions
            {
                ModelStorageDirectory = store,
                ModelId               = wanted,

                // NOT WarmOnStart. The phone pays the cold start up front because
                // somebody is watching a notification that says "loading"; a desktop
                // service starts at login with nobody looking, and loading a model
                // nobody has asked for yet is gigabytes held against a maybe.
                WarmOnStart = false,
            };

            var service = new AIService(
                options,
                modelLoader:      loader,
                generatorFactory: null,
                modelSelector:    new DeviceAwareModelSelector(registry),
                modelRegistry:    registry);

            await using var endpoint = new HttpLoopbackEndpoint(options);
            await endpoint.StartAsync(service, stopping.Token).ConfigureAwait(false);

            // THE PORT AND THE TOKEN ARE THE WHOLE INTERFACE, so they are printed
            // rather than left for somebody to find in a log. The endpoint generates
            // the token when none is configured, which means nothing can reach this
            // process without reading this line or being told it.
            Console.WriteLine($"listening on http://127.0.0.1:{endpoint.BoundPort}");
            Console.WriteLine($"X-Butler-Token: {endpoint.Token}");
            Console.WriteLine("Ctrl-C to stop.");

            try { await Task.Delay(Timeout.Infinite, stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* asked to stop */ }

            await endpoint.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            // A service that will not start has to say why on the way out: this runs
            // with no screen and its exit code is all a service manager reports.
            Console.Error.WriteLine("CircleAI service could not start: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Where this machine keeps the models.</summary>
    /// <remarks>
    /// LocalApplicationData, NOT the install directory. The models are gigabytes, they
    /// are per-user, and a service installed under Program Files cannot write beside
    /// itself. CIRCLEAI_MODELS overrides it for a machine that keeps them on another
    /// disk, which on a desktop is a reasonable thing to want and on a phone is not.
    /// </remarks>
    private static string ModelStore()
    {
        var set = Environment.GetEnvironmentVariable("CIRCLEAI_MODELS");
        if (!string.IsNullOrWhiteSpace(set)) return set!;

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CircleAI", "models");
    }
}
