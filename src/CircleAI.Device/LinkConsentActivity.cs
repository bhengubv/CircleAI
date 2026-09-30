// LinkConsentActivity.cs
//
// Where a person approves an app's link — and the only place a biometric can show.
//
// A background service cannot present a system auth sheet, so the client launches
// THIS activity FOR RESULT. Two things fall out of "for result": the client is
// foreground, so showing the sheet is allowed; and the OS fills getCallingPackage
// with the real caller, so this screen knows who is asking without trusting the
// caller to say. It runs transparent — only the system sheet is seen — mints the
// grant into the same store the service reads, and returns Ok/Canceled.

using System;
using Android.App;
using Android.Content;
using Android.OS;
using CircleAI.Aether;
using CircleAI.Linking;

namespace CircleAI.Device;

/// <summary>The link-approval screen, launched for result by a client app.</summary>
[Activity(Name = "ai.circle.LinkConsentActivity", Exported = true,
          Theme = "@android:style/Theme.Translucent.NoTitleBar", ExcludeFromRecents = true)]
[IntentFilter(new[] { LinkIpc.ConsentAction }, Categories = new[] { Intent.CategoryDefault })]
public sealed class LinkConsentActivity : Activity
{
    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetResult(Result.Canceled);   // the default until the person approves

        try
        {
            var caller = CallingPackage;   // the app that started us for result
            var grants = CircleNeuronLinkService.Grants;
            if (string.IsNullOrEmpty(caller) || grants is null)
            {
                // THREE WAYS OUT OF HERE USED TO BE SILENT, and that cost an evening:
                // the sheet appeared and vanished in under a second, no grant was
                // written, and nothing anywhere said which of the three it was. A
                // consent screen that refuses without saying why is unfixable from
                // outside.
                Android.Util.Log.Warn("CircleAI.Link",
                    $"consent: refused - caller={(string.IsNullOrEmpty(caller) ? "unknown" : caller)} " +
                    $"grants={(grants is null ? "not wired" : "ready")}");
                Finish();
                return;
            }

            var signature = CircleNeuronLinkService.SignatureDigestOf(this, caller!);
            if (signature is null)
            {
                // USUALLY PACKAGE VISIBILITY. Reading a caller's signing certificate
                // means getPackageInfo on their package, and on Android 11+ that is
                // filtered unless this app can see them - so it comes back as "not
                // installed" for an app that plainly is.
                Android.Util.Log.Warn("CircleAI.Link",
                    $"consent: cannot read the signature of '{caller}' - package visibility?");
                Finish();
                return;
            }

            Android.Util.Log.Info("CircleAI.Link", $"consent: asking about '{caller}'");

            var scope = (LinkScope)(Intent?.GetIntExtra(LinkIpc.ScopeExtra, (int)LinkScope.Chat)
                                    ?? (int)LinkScope.Chat);

            var gate = new LinkGate(grants, CircleNeuronLinkService.FirstPartySignatures);
            var authorizer = new LinkAuthorizer(grants, gate, CircleNeuronLinkService.GrantLifetime);
            var auth = new AndroidAuthChallenge(() => this);

            // THE REASON WAS THROWN AWAY, and .Succeeded alone cannot tell "they said
            // no" from "the prompt never appeared". AuthChallengeResult has carried a
            // FailureReason all along - no enrolled credential, no foreground screen,
            // a hardware error - and every one of them was reported to the person as
            // if they had declined something they never saw.
            string? why = null;
            var grant = await authorizer.AuthorizeAsync(
                new LinkRequest(caller!, signature, scope),
                async ct =>
                {
                    var outcome = await auth.ChallengeAsync(
                        AuthChallengeReason.AppLinkRequest,
                        AuthMethod.BiometricAndDeviceAdmin,
                        $"Allow {caller} to use your Circle AI?",
                        ct).ConfigureAwait(false);
                    if (!outcome.Succeeded) why = outcome.FailureReason;
                    return outcome.Succeeded;
                },
                DateTimeOffset.UtcNow);

            Android.Util.Log.Info("CircleAI.Link",
                grant is not null
                    ? $"consent: GRANTED to '{caller}'"
                    : $"consent: not granted - {why ?? "the person declined"}");
            SetResult(grant is not null ? Result.Ok : Result.Canceled);
        }
        catch (Exception ex)
        {
            // IT SWALLOWED EVERYTHING. A throw in the authoriser, the auth challenge
            // or the grant store looked exactly like "the person said no".
            Android.Util.Log.Error("CircleAI.Link",
                $"consent: {ex.GetType().Name}: {ex.Message}");
            SetResult(Result.Canceled);
        }
        finally
        {
            Finish();
        }
    }
}
