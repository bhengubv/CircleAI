// MainActivity.cs
//
// The proof, in one screen: a second app with no brain of its own borrows Circle
// AI's. Tap the button → approve the link with a fingerprint / PIN the first time
// (in Circle AI's consent screen, launched for result) → the reply comes back
// from the ONE shared brain in Circle AI's process. The UI is built in code so
// the demo needs no resource files.

using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using CircleAI.Client;

namespace CircleAI.Samples.LinkDemo;

[Activity(Label = "Circle AI Link Demo", MainLauncher = true)]
public class MainActivity : Activity
{
    private const int ReqConsent = 1;
    private TextView _output = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(48, 96, 48, 48);

        var button = new Button(this) { Text = "Ask Circle AI: my rights at work" };
        _output = new TextView(this) { Text = "This app has no brain. Tap to borrow Circle AI's." };
        _output.SetPadding(0, 48, 0, 0);

        layout.AddView(button);
        layout.AddView(_output);
        SetContentView(layout);

        // Approve the link first — a foreground screen is required to show the
        // biometric sheet — then ask. On a repeat the grant already exists and
        // consent returns immediately.
        button.Click += (_, _) =>
        {
            _output.Text = "Approving the link…";
            StartActivityForResult(CircleAiLinkClient.ConsentIntent(), ReqConsent);
        };
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != ReqConsent) return;
        if (resultCode != Result.Ok) { _output.Text = "Link not approved."; return; }
        _ = AskAsync();
    }

    private async System.Threading.Tasks.Task AskAsync()
    {
        _output.Text = "Asking Circle AI…";
        try
        {
            using var client = await CircleAiLinkClient.ConnectAsync(this);
            if (client is null)
            {
                _output.Text = "Circle AI is not installed.";
                return;
            }

            var reply = await client.AskAsync(
                "linkdemo", "I have a question about my rights at work.");
            _output.Text = reply.Ok ? reply.Reply : ("Failed: " + reply.Error);
        }
        catch (System.Exception ex)
        {
            _output.Text = "Error: " + ex.Message;
        }
    }
}
