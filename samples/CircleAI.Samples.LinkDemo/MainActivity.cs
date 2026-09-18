// MainActivity.cs
//
// The proof, in one screen: a second app with no brain of its own borrows Circle
// AI's. Tap the button, approve the link with a fingerprint / PIN the first time,
// and the reply comes back from the ONE shared brain running in Circle AI's
// process — not a model this app ships. The UI is built in code so the demo needs
// no resource files.

using Android.App;
using Android.OS;
using Android.Widget;
using CircleAI.Client;

namespace CircleAI.Samples.LinkDemo;

[Activity(Label = "Circle AI Link Demo", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(48, 96, 48, 48);

        var button = new Button(this) { Text = "Ask Circle AI: my rights at work" };
        var output = new TextView(this) { Text = "This app has no brain. Tap to borrow Circle AI's." };
        output.SetPadding(0, 48, 0, 0);

        layout.AddView(button);
        layout.AddView(output);
        SetContentView(layout);

        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            output.Text = "Linking to Circle AI…";
            try
            {
                using var client = await CircleAiLinkClient.ConnectAsync(this);
                if (client is null)
                {
                    output.Text = "Circle AI is not installed, or the link was refused.";
                    return;
                }

                var reply = await client.AskAsync(
                    "linkdemo", "I have a question about my rights at work.");
                output.Text = reply.Ok ? reply.Reply : ("Failed: " + reply.Error);
            }
            catch (System.Exception ex)
            {
                output.Text = "Error: " + ex.Message;
            }
            finally
            {
                button.Enabled = true;
            }
        };
    }
}
