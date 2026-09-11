// AndroidMediaPlayer.cs
//
// Handing "play Coldplay" to whatever this phone already plays music with.
//
// ANDROID'S OWN SEARCH INTENT, NOT AN APP NAME. INTENT_ACTION_MEDIA_PLAY_FROM_SEARCH
// is the platform's standard "play this" request: every music app registers for
// it, the phone's chooser decides who gets it, and whatever somebody already
// uses is what opens. Naming Spotify in code would make the feature depend on
// one company's app being installed - and on this product, hard-coding a
// store-delivered app is worse than useless.
//
// THE QUERY GOES THROUGH UNPARSED. A player's own search understands its own
// catalogue; splitting artist from album here is the guess that turns "play
// Adele 30" into a search for the number thirty. EXTRA_MEDIA_FOCUS says only
// "this is about music" - the vaguest focus on purpose, because claiming it is
// an artist when it might be an album is the same guess in a different place.

using Android.Content;
using Android.Provider;

namespace CircleAI.Assistant.Device;

/// <inheritdoc />
public sealed class AndroidMediaPlayer : IPlaysMedia
{
    // NO ANNOUNCEMENT HERE. It belongs one level up, in PlayMediaCapability,
    // where the STEP is known - this class only knows how to fire an intent, and
    // a capability that plays music after adding a calendar entry has two steps
    // to narrate, not one. See IAnnounces.

    /// <inheritdoc />
    /// <remarks>
    /// AN EMPTY QUERY IS A PROBE, NOT A REQUEST. ReadyAsync asks whether this
    /// phone can play anything at all, and answering that by starting a player
    /// would mean opening Spotify to find out whether Spotify exists. So an
    /// empty string resolves the intent and starts nothing.
    /// </remarks>
    public Task<PlayResult> PlayAsync(string what, CancellationToken ct = default)
    {
        try
        {
            var context = global::Android.App.Application.Context;

            var intent = new Intent(MediaStore.IntentActionMediaPlayFromSearch);
            // Fully qualified: SearchManager lives in Android.App, which this file
            // deliberately does not import - Application.Context is qualified for
            // the same reason, because an unqualified Application collides with
            // MAUI's own.
            intent.PutExtra(global::Android.App.SearchManager.Query, what ?? string.Empty);
            intent.PutExtra(MediaStore.ExtraMediaFocus, "vnd.android.cursor.item/*");
            intent.SetFlags(ActivityFlags.NewTask);

            // WHETHER ANYTHING CAN TAKE IT, BEFORE TRYING. Unlike the Huawei and
            // MIUI battery screens - where ResolveActivity lies because of
            // package visibility - this is a PUBLIC platform action that any
            // music app registers for, so resolution here is meaningful. The
            // manifest still needs a <queries> entry for it on API 30+, which is
            // why a null result is reported as "no player" rather than swallowed.
            if (intent.ResolveActivity(context.PackageManager!) is null)
                return Task.FromResult(PlayResult.NoPlayer);

            // The probe stops here: something can play music, and nothing was
            // asked for yet.
            if (string.IsNullOrWhiteSpace(what))
                return Task.FromResult(PlayResult.Playing);

            context.StartActivity(intent);
            Android.Util.Log.Info("CircleAI.Turn", $"play: handed \"{what}\" to the phone's player");
            return Task.FromResult(PlayResult.Playing);
        }
        catch (ActivityNotFoundException)
        {
            return Task.FromResult(PlayResult.NoPlayer);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("CircleAI.Turn", "play failed: " + ex.Message);
            return Task.FromResult(PlayResult.NoPlayer);
        }
    }
}
