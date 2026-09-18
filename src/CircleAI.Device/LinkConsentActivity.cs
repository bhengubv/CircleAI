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
            if (string.IsNullOrEmpty(caller) || grants is null) { Finish(); return; }

            var signature = CircleNeuronLinkService.SignatureDigestOf(this, caller!);
            if (signature is null) { Finish(); return; }

            var scope = (LinkScope)(Intent?.GetIntExtra(LinkIpc.ScopeExtra, (int)LinkScope.Chat)
                                    ?? (int)LinkScope.Chat);

            var gate = new LinkGate(grants, CircleNeuronLinkService.FirstPartySignatures);
            var authorizer = new LinkAuthorizer(grants, gate, CircleNeuronLinkService.GrantLifetime);
            var auth = new AndroidAuthChallenge(() => this);

            var grant = await authorizer.AuthorizeAsync(
                new LinkRequest(caller!, signature, scope),
                async ct => (await auth.ChallengeAsync(
                    AuthChallengeReason.AppLinkRequest,
                    AuthMethod.BiometricAndDeviceAdmin,
                    $"Allow {caller} to use your Circle AI?",
                    ct)).Succeeded,
                DateTimeOffset.UtcNow);

            SetResult(grant is not null ? Result.Ok : Result.Canceled);
        }
        catch
        {
            SetResult(Result.Canceled);
        }
        finally
        {
            Finish();
        }
    }
}
