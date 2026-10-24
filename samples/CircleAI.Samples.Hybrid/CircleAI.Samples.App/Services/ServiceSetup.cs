// ServiceSetup.cs
//
// Setting up means installing CircleAI, not downloading models here.
//
// DeviceSetup planned and ran model downloads into THIS app's storage. A thin app
// downloads nothing: the service holds the models, so "is it ready" collapses to "is
// CircleAI installed and linked", and the work a person has to do is one install
// rather than a queue of gigabytes.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Client;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceSetup(LinkedBrain brain) : ISetup
{
    /// <inheritdoc />
    /// <remarks>Nothing downloads here, so nothing is ever running.</remarks>
    /// <inheritdoc />
    /// <remarks>True while this screen is following a run; the run itself is the service's.</remarks>
    public bool IsRunning => _running;

    private bool _running;

    /// <summary>The service's setup, over the link. The mapping onto the wire is in the product.</summary>
    private readonly LinkedSetup _link = new(brain);

    /// <inheritdoc />
    /// <remarks>
    /// CIRCLEAI'S OWN ANSWER. Whether this phone can hold a conversation is a fact
    /// about the models, and the models are over there.
    /// </remarks>
    public async Task<Readiness> ReadinessAsync(CancellationToken ct = default)
    {
        if (!brain.ServiceInstalled)
            return new Readiness(ReadyStage.NeedsSetup, "CircleAI is not installed",
                                 "It holds the models and does the thinking. Install it to begin.",
                                 false);

        return await _link.ReadinessAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// THE SERVICE'S PLAN, NOT THIS APP'S. It used to return empty with a comment
    /// saying there was nothing to download, and that was true of this app and
    /// useless to the person: CircleAI is the thing with models to fetch, and it has
    /// no screen to offer them on. The rows come back over the link.
    /// </remarks>
    public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
        => _link.PlanAsync(ct);

    /// <inheritdoc />
    /// <remarks>
    /// WHAT CIRCLEAI HOLDS, asked over the link. This used to answer with one row
    /// saying whether CircleAI was installed, which is a true sentence and not a
    /// census: the question a person is asking on that screen is what their phone can
    /// actually DO - can it see, can it hear, can it speak their language - and every
    /// one of those answers is on the other side of the link.
    /// <para>
    /// NOT INSTALLED IS STILL THE FIRST ANSWER. Nothing else is worth saying until
    /// the service is there at all, and the link cannot tell you so.
    /// </para>
    /// </remarks>
    public Task<Census> CensusAsync(CancellationToken ct = default)
    {
        if (!brain.ServiceInstalled)
        {
            var missing = new CensusRow("CircleAI", false, 0, "Not installed.");
            return Task.FromResult(new Census(
                new[] { missing }, 0, 1, "CircleAI is not installed."));
        }

        return _link.CensusAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// THE APP ASKS, THE SERVICE FETCHES. This used to be a no-op whose comment said
    /// "the service downloads its own models" - which was never true of anything: the
    /// service has no screen on which somebody could ask it to, so nothing was ever
    /// downloaded by anybody. The tap happens here, the gigabytes land there.
    /// <para>
    /// The run outlives this call and this screen. Closing the app mid-download and
    /// coming back rejoins the same run rather than starting a second copy of it.
    /// </para>
    /// </remarks>
    public async Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
    {
        _running = true;
        try { await _link.RunAsync(progress, ct).ConfigureAwait(false); }
        finally { _running = false; }
    }

    /// <inheritdoc />
    /// <remarks>
    /// IT TOLD PEOPLE TO INSTALL SOMETHING THEY HAD INSTALLED. One step, returned
    /// unconditionally, on a device where CircleAI was installed, approved, and
    /// downloading models for this app while the card was on screen - and whose
    /// action, "Open CircleAI", names an app with no launcher icon. The same shape as
    /// the wake-phrase and listening rows, left in a fourth place.
    /// <para>
    /// NOTHING TO SAY IS THE RIGHT ANSWER ONCE IT IS WORKING. A tour exists to get
    /// somebody over the first hurdle; after that an empty list is what honesty looks
    /// like, and the setup screen already shows what is still missing.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// AND THE MICROPHONE, WHICH NOTHING ON A FRESH INSTALL EVER ASKED FOR. The wake
    /// word is on by default and could not hear anything until somebody found a switch
    /// that asks; the download wait is the moment the person is here with nothing else
    /// to do. Gone again once Circle AI has it.
    /// <para>
    /// AND GONE FOR SOMEBODY WHO TURNED LISTENING OFF. The step is a button that opens
    /// a microphone dialog, and it was offered on the strength of the permission
    /// alone - so a person who had switched "answer to its name" off was asked for the
    /// microphone it uses, on every download, for a feature they had declined. The
    /// service is the one that knows: NeedsPermission means it wants the microphone
    /// and has not got it, and Off means they said no.
    /// </para>
    /// <para>
    /// The rule itself is MicrophoneAsk.TourFor, in the product, where a test can read
    /// it without a phone.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default)
    {
        var installed = brain.ServiceInstalled;
        var approved  = installed && (await brain.StateAsync(ct).ConfigureAwait(false)).Ready;
        var granted   = installed && ServiceMicrophone.Granted(Android.App.Application.Context);

        return MicrophoneAsk.TourFor(installed, approved, granted,
            ownerWantsListening: approved && await WantsListeningAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Whether the service says the owner has listening on.</summary>
    /// <remarks>
    /// ASKED, NOT GUESSED, AND IT IS THE SERVICE'S FACT. The preference lives in the
    /// service's own storage; this app cannot read it and must not keep a copy. Off is
    /// the one state that means "do not ask them for a microphone", and every other
    /// answer - including a service that will not say - leaves the step offered, which
    /// is the direction a wake word that is on by default should fail in.
    /// </remarks>
    private async Task<bool> WantsListeningAsync(CancellationToken ct)
    {
        try
        {
            // THE STATUS VERB, WHICH READS AND CHANGES NOTHING. Asked through the same
            // type the settings switch uses, so there is one reader of this answer in
            // the app rather than a second copy of the wire format here.
            var status = await _listening.RefreshAsync(ct).ConfigureAwait(false);
            return status.State != ResidentState.Off;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>The service's listening state, read only. See <see cref="WantsListeningAsync"/>.</summary>
    /// <remarks>
    /// No activity is handed to it: this one never asks for anything, and a null
    /// activity is what makes that true by construction rather than by care.
    /// </remarks>
    private readonly LinkedResidentAssistant _listening = new(brain);

    /// <inheritdoc />
    /// <remarks>
    /// THE SERVICE'S MICROPHONE, NOT THIS APP'S. This returned false on the grounds
    /// that this app does not listen - true, and the wrong conclusion: the service
    /// does, it has no screen to ask on, and our own app is approved without one, so
    /// nobody asked at all. Recording for a single spoken question is still this
    /// app's own permission, asked for at the point it happens.
    /// </remarks>
    public Task<bool?> AllowMicrophoneAsync(CancellationToken ct = default)
        => ServiceMicrophone.RequestAsync(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The same screen of Circle AI's, asked quietly: nobody pressed anything.
    /// <para>
    /// ONCE EVER, AND IT USED TO BE ONCE PER LAUNCH WHILE ANYTHING WAS DOWNLOADING.
    /// The loading screen offers this whenever the census is short, and a first run is
    /// hours on a slow link - so a person who tapped Deny met the system dialog again
    /// on every launch until Android itself refused to show it. Repeated prompts for
    /// the same permission are how people are worn into saying yes, and the rule this
    /// code states for itself is "once, at first run". <see cref="OfferedOnce"/> is
    /// what makes it true.
    /// </para>
    /// </remarks>
    public Task<bool?> OfferMicrophoneAsync(CancellationToken ct = default)
    {
        if (ServiceMicrophone.Granted(Android.App.Application.Context)) return Task.FromResult<bool?>(true);
        if (!OfferedOnce.Take()) return Task.FromResult<bool?>(null);

        return ServiceMicrophone.RequestAsync(
            Microsoft.Maui.ApplicationModel.Platform.CurrentActivity, ct, quietly: true);
    }

    /// <inheritdoc />
    /// <remarks>
    /// IT RETURNED FALSE AND THE BUTTON LOOKED BROKEN. "Background work is the
    /// service's; this app has none to exempt" is true and was the wrong conclusion:
    /// the warning above the button is about the SERVICE being killed, so the row
    /// printed "This phone may stop it listening - Fix it" on every launch and Fix it
    /// did nothing, for ever. Seen on a P30 on 2026-10-01.
    /// <para>
    /// THE SETTINGS LIST, NOT THE PER-PACKAGE DIALOG. ACTION_REQUEST_IGNORE_BATTERY_
    /// OPTIMIZATIONS names one package and only shows for the app that holds
    /// REQUEST_IGNORE_BATTERY_OPTIMIZATIONS - and the package needing the exemption
    /// is CircleAI's, which this app cannot ask for on its behalf. The list screen
    /// takes no package, needs no permission at all, and puts the person exactly
    /// where the choice is made. That is also why this head no longer declares that
    /// permission.
    /// </para>
    /// <para>
    /// The return says only that something opened - which is the contract the screen
    /// already documents, because the person may grant, refuse or back out. The
    /// re-read happens on the way back in.
    /// </para>
    /// </remarks>
    public Task<bool> AllowBackgroundAsync(CancellationToken ct = default)
    {
        if (ServiceExempt()) return Task.FromResult(true);

        // THE MAKER'S OWN SCREEN FIRST, because on these phones the stock battery
        // list is necessary and not sufficient - EMUI closes an app left on "Manage
        // automatically" in App launch whatever the battery list says. Somebody who
        // follows a generic instruction, does exactly what it asked, and still loses
        // the assistant overnight concludes the software is broken, and they are
        // being reasonable.
        foreach (var screen in BackgroundFix.Screens(Android.OS.Build.Manufacturer))
        {
            if (Opened(new Android.Content.ComponentName(screen.Package, screen.Activity)))
                return Task.FromResult(true);
        }

        try
        {
            var intent = new Android.Content.Intent(
                Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings);

            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is not null)
            {
                activity.StartActivity(intent);
            }
            else
            {
                // No activity means no task to open into, so it needs its own.
                intent.SetFlags(Android.Content.ActivityFlags.NewTask);
                Android.App.Application.Context.StartActivity(intent);
            }

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            // Vendors move this screen between firmwares; the caller prints a
            // sentence telling somebody where to look instead.
            Android.Util.Log.Warn("CircleAI.Setup", "battery settings did not open: " + ex.Message);
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ASKED ABOUT THE SERVICE'S PACKAGE, which is the one that holds the microphone
    /// and gets hibernated. This answered about THIS app - and hardcoded false at
    /// that - so the warning could never clear however many times somebody granted
    /// the exemption.
    /// </remarks>
    public Task<bool> BackgroundAllowedAsync(CancellationToken ct = default)
        => Task.FromResult(ServiceExempt());

    /// <inheritdoc />
    /// <remarks>
    /// BOTH HALVES, AND THE SECOND ONE IS THE ONE THAT WAS MISSING. Not holding the
    /// exemption is Android's default; it only matters on a phone that will act on
    /// it. Build.Manufacturer is read here because this is the head with the
    /// platform; which makes count is BackgroundRisk's, in the product, where it can
    /// be tested without a phone.
    /// </remarks>
    public async Task<bool> BackgroundAtRiskAsync(CancellationToken ct = default)
    {
        if (await BackgroundAllowedAsync(ct).ConfigureAwait(false)) return false;

        return BackgroundRisk.Known(Android.OS.Build.Manufacturer);
    }

    /// <inheritdoc />
    public Task<string?> BackgroundSettingNameAsync(CancellationToken ct = default)
        => Task.FromResult(BackgroundFix.SettingName(Android.OS.Build.Manufacturer));

    /// <summary>Try one maker screen; true only if it actually came up.</summary>
    /// <remarks>
    /// VENDORS RENAME AND MOVE THESE BETWEEN FIRMWARES, so this is a list of
    /// candidates rather than one name and a hope - and every failure has to be
    /// caught, because a missing component throws ActivityNotFound and a present but
    /// guarded one throws SecurityException. Either way the next candidate gets a
    /// turn and the stock battery screen is still the floor.
    /// </remarks>
    private static bool Opened(Android.Content.ComponentName component)
    {
        try
        {
            var intent = new Android.Content.Intent();
            intent.SetComponent(component);

            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is not null)
            {
                activity.StartActivity(intent);
            }
            else
            {
                intent.SetFlags(Android.Content.ActivityFlags.NewTask);
                Android.App.Application.Context.StartActivity(intent);
            }

            Android.Util.Log.Info("CircleAI.Setup", "opened " + component.FlattenToShortString());
            return true;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Info("CircleAI.Setup",
                component.FlattenToShortString() + " did not open: " + ex.Message);
            return false;
        }
    }

    /// <summary>Whether Android will let CircleAI keep running in the background.</summary>
    /// <remarks>
    /// isIgnoringBatteryOptimizations takes a package name and this one is CircleAI's,
    /// not this app's. LinkIpc.HostPackage rather than a literal: the same constant
    /// the bind uses, so there is one place that says which app is the host.
    /// </remarks>
    private static bool ServiceExempt()
    {
        try
        {
            var power = (Android.OS.PowerManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.PowerService);

            return power?.IsIgnoringBatteryOptimizations(CircleAI.Linking.LinkIpc.HostPackage) == true;
        }
        catch
        {
            // A phone that will not answer is not a phone that has granted it.
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ASKED OVER THE LINK, NOT GUESSED. Capabilities is the cheapest verb that goes
    /// through the same authorisation - its scope is Chat and the manifest is not
    /// private - so a granted link answers it and an ungranted one refuses.
    /// </remarks>
    public async Task<bool> LinkApprovedAsync(CancellationToken ct = default)
    {
        var state = await brain.StateAsync(ct).ConfigureAwait(false);
        return state.Ready;
    }

    /// <inheritdoc />
    /// <remarks>
    /// STARTED FOR RESULT, AND THAT IS LOAD-BEARING. LinkConsentActivity reads
    /// CallingPackage to know who is asking, and the OS only fills that in for an
    /// activity started for result - StartActivity would show the same sheet with no
    /// idea who it was about, and it would grant nothing.
    /// <para>
    /// It needs the ACTIVITY, which is why this cannot live in the shared UI or in the
    /// product library: neither has one. The whole reason ISetup carries this at all
    /// is that the head is the only thing that can do it.
    /// </para>
    /// <para>
    /// THE RESULT IS NOT READ FROM onActivityResult. The consent screen writes the
    /// grant on the SERVICE side, so the honest way to learn the answer is to ask the
    /// service again rather than trust a result code. Polling, because the sheet is a
    /// separate app: this returns when the grant appears, when the person backs out,
    /// or after a minute - whichever comes first.
    /// </para>
    /// </remarks>
    public async Task<bool> ApproveLinkAsync(CancellationToken ct = default)
    {
        if (!brain.ServiceInstalled) return false;
        if (await LinkApprovedAsync(ct).ConfigureAwait(false)) return true;

        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is null) return false;

        // EVERYTHING THIS APP ACTUALLY USES, asked for once. Chat to think, Memory to
        // remember, Skills to look things up, Voice to hear and speak. Asking for them
        // one at a time would mean four sheets for one decision.
        var scope = CircleAI.Linking.LinkScope.Chat
                  | CircleAI.Linking.LinkScope.Memory
                  | CircleAI.Linking.LinkScope.Skills
                  | CircleAI.Linking.LinkScope.Voice;

        try { activity.StartActivityForResult(brain.ConsentIntent(scope), ConsentRequest); }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("CircleAI.Link", "could not open the approval screen: " + ex.Message);
            return false;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (await LinkApprovedAsync(ct).ConfigureAwait(false)) return true;
        }
        return false;
    }

    /// <summary>Request code for the consent sheet. Nothing reads the result; see above.</summary>
    private const int ConsentRequest = 0x11c;
}

/// <summary>The one unasked-for microphone question this app ever raises.</summary>
/// <remarks>
/// "ONCE, AT FIRST RUN" WAS WHAT THE CODE SAID AND NOT WHAT IT DID. The loading
/// screen raises the question whenever the census is short, which on a first run of
/// several gigabytes is every launch for hours - and on a phone whose download keeps
/// failing, every launch for ever. Three of the review's five readers found it
/// independently.
/// <para>
/// IT HAS TO OUTLIVE THE PROCESS, so it is a preference rather than a static: the
/// person who says no and reopens the app is in a new process, and a flag in memory
/// would be exactly as absent as no flag at all.
/// </para>
/// <para>
/// WRITTEN BEFORE THE ANSWER. A question that was raised and whose answer was lost -
/// the app was killed, the result landed nowhere - has still been raised, and raising
/// it again is the behaviour being fixed. Nothing is lost by it: the card's own
/// button still asks, as often as somebody presses it.
/// </para>
/// </remarks>
internal static class OfferedOnce
{
    private const string File = "circleai.microphone.offer";
    private const string Key  = "offered";

    /// <summary>True exactly once in this installation's life, for the first caller.</summary>
    public static bool Take()
    {
        try
        {
            var prefs = (Android.App.Application.Context.ApplicationContext
                         ?? Android.App.Application.Context)
                .GetSharedPreferences(File, Android.Content.FileCreationMode.Private);

            if (prefs is null) return true;
            if (prefs.GetBoolean(Key, false)) return false;

            prefs.Edit()?.PutBoolean(Key, true)?.Apply();
            return true;
        }
        catch
        {
            // A PREFERENCE THAT CANNOT BE WRITTEN FALLS BACK TO ASKING. The wake word
            // is on by default and a phone that never asks is a phone that never
            // hears; a repeated ask is the lesser of the two failures.
            return true;
        }
    }
}
