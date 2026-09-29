// ServiceApplication.cs
//
// The standalone CircleAI service, bootstrapped.
//
// EVERYTHING HERE WAS IN THE HYBRID SAMPLE'S MauiProgram, which is the thing this
// app exists to undo. LinkIpc.HostPackage named the sample, so "the shared brain on
// this device" was something a person got by installing a demo — and any second app
// that wanted it had to hope the demo was still there. The host is now an app of its
// own, and the sample is one of its clients.
//
// WHAT AN APPLICATION CLASS IS FOR HERE: the link service can be bound by another app
// before anybody opens this one, and OnBind runs with whatever statics were set at
// process start. Wiring in an Activity would mean the first bind after a cold start
// found no grant store and no memory — so it happens here, before any component runs.

using Android.App;
using Android.Runtime;
using CircleAI.Core;
using CircleAI.Device;
using CircleAI.Linking;

namespace CircleAIService;

// NO ICON SET, which is a gap and not a choice worth defending: this ships with
// Android's default launcher icon. A store listing needs a real one, and inventing
// artwork here would be worse than leaving the hole visible.
[Application(Label = "CircleAI")]
public sealed class ServiceApplication : Application
{
    public ServiceApplication(IntPtr handle, JniHandleOwnership transfer)
        : base(handle, transfer) { }

    public override void OnCreate()
    {
        // MEASURE THE DEVICE BEFORE ANYTHING ASKS WHAT FITS. DeviceProbe reads the
        // GC heap limit on Android — about 100 MB inside the sandbox — unless a head
        // installs the real hook, so every model-fit decision would be made against a
        // fiction. It has to run before base.OnCreate, because base can start
        // components that probe.
        AndroidDeviceMemory.Install(this);

        base.OnCreate();

        // WHERE STANDING GRANTS LIVE. A killed service process must not forget who
        // the person already approved: an in-memory store is safe but asks again on
        // every restart, which trains people to tap through consent without reading.
        var grants = System.IO.Path.Combine(FilesDir!.AbsolutePath, "link-grants.json");
        CircleNeuronLinkService.Grants = new FileLinkGrantStore(grants);

        // NOBODY IS TRUSTED BY DEFAULT. An empty first-party set means every client,
        // including our own sample, is approved once by the person with device auth
        // rather than waved through on a signature. Populating this is a deliberate
        // decision about which packages ship as "ours", not a convenience.
        CircleNeuronLinkService.FirstPartySignatures = new HashSet<string>(StringComparer.Ordinal);

        // No auth gate on the SERVICE: a background service cannot raise a biometric
        // sheet. Approval happens in LinkConsentActivity, launched FOR RESULT by the
        // foreground client, which is also how the OS tells the consent screen which
        // package is really asking.
    }
}
