// StatusActivity.cs
//
// The service's only screen, and it is deliberately almost nothing.
//
// A background service still needs a face, for three reasons that are not
// decoration: a store listing needs something to launch, Android gives a person no
// other way to start or stop it, and "is the shared brain actually running" is a
// question somebody will ask when a client says it cannot reach it. This answers
// that and nothing else — every task a person wants to DO happens in a client.
//
// IT SHOWS WHAT THE DEVICE CAN DO, not what the product can do. DeviceCapability
// computes that from the phone in hand rather than a feature list, so a P30 owner is
// told plainly what their handset will and will not manage. That is the same report
// a client would show, from the same source, so the two cannot disagree.

using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Device;
using CircleAI.Inference;

namespace CircleAIService;

[Activity(Label = "CircleAI", MainLauncher = true, Exported = true)]
public sealed class StatusActivity : Activity
{
    private TextView? _state;
    private TextView? _capability;
    private Button? _toggle;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(48, 64, 48, 48);

        root.AddView(new TextView(this) { Text = "CircleAI", TextSize = 28f });
        root.AddView(new TextView(this)
        {
            Text = "The shared brain for this device. Other apps ask it; "
                 + "you do not have to open it.",
            TextSize = 14f,
        });

        _state = new TextView(this) { TextSize = 16f };
        _state.SetPadding(0, 48, 0, 16);
        root.AddView(_state);

        _toggle = new Button(this);
        _toggle.Click += (_, _) => Toggle();
        root.AddView(_toggle);

        _capability = new TextView(this) { TextSize = 14f };
        _capability.SetPadding(0, 48, 0, 0);
        root.AddView(_capability);

        SetContentView(root);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Refresh();
    }

    private void Toggle()
    {
        // The person's own device, so they may stop it. Starting is the common case
        // and is what a client's "install CircleAI" flow lands on.
        if (CircleNeuronService.State == CircleNeuronService.ServiceState.Idle)
            CircleNeuronService.Start(this);
        else
            CircleNeuronService.Stop(this);

        Refresh();
    }

    private void Refresh()
    {
        var running = CircleNeuronService.State != CircleNeuronService.ServiceState.Idle;
        _state!.Text = $"{CircleNeuronService.State} — {CircleNeuronService.Status}";
        _toggle!.Text = running ? "Stop" : "Start";

        try
        {
            // COMPUTED FROM THIS PHONE, not asserted. The same report a client shows,
            // from the same source, so a person cannot be told two different stories
            // about what their device can do.
            var probe = DeviceProbe.Snapshot();
            using var registry = new ModelRegistryService();
            var report = DeviceCapability.For(
                new DeviceAwareModelSelector(registry), new SpeechModelSelector(registry), probe);

            _capability!.Text = DeviceCapability.Disclaimer(report)
                + $"\n\nModels may use up to {report.BudgetBytes / 1e9:0.#} GB of this device.";
        }
        catch (Exception ex)
        {
            // A status screen that crashes is worse than one that says it could not
            // work something out.
            _capability!.Text = $"Could not read this device: {ex.Message}";
        }
    }
}
