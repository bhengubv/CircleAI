// TurnHold.cs
//
// A SPOKEN TURN NEEDS THE BIG CORES WITH NOTHING OF OURS ON THE SCREEN, AND THE PHONE WILL NOT GIVE
// THEM TO A FOREGROUND SERVICE.
//
// EMUI ranks a foreground service as background work. Measured on the P30 on 2026-11-06, with none of
// our apps on screen every thread of the service and the brain sat in the group that holds cores 2, 3
// and 7 - and by the phone's own setting 0 to 3, all little, with the screen off - where a question that
// takes 3 s with the app open took 17,3 s. Moving a thread is not open to an app: the group's files are
// the system's, and whatever a thread is called the phone moves it back.
//
// WHAT THE PHONE DOES HONOUR IS WHO HOLDS A PROCESS. Every process the system or the screen was bound
// to had all eight cores, Google's assistant among them. MidiService binds an app's service on that
// app's own say-so, with no permission and no setting, and for exactly as long as the app keeps the
// device open. So the device here is private to this app, carries no music, and is open for the length
// of a turn: the brain's process is bound by this one and rises with it. The same questions then took
// 2,8 to 3,6 s with the launcher in front.
//
// AND THE SCREEN COMES ON, BECAUSE THE PHONE'S CLOCKS ARE TIED TO IT. With the display off the P30 runs
// its big cores at 1,04 GHz at most and its little ones at 1,33 GHz, whatever group a thread is in:
// measured on 2026-11-06, eight turns took 4,6 to 7,2 s in the dark and the same questions 3 s on a
// lit lock screen, the phone still locked. No app can ask for the clocks; any app can ask for the screen.
// The owner's word, 2026-11-06: light it. It is dim, because nobody asked to look at anything.

using Android.App;
using Android.Content;
using Android.Media.Midi;
using Android.OS;

namespace CircleAI.Service.Droid.Assistant;

/// <summary>The service the system binds for the length of a spoken turn. It does nothing else.</summary>
[Service(Name = "ai.circle.TurnHoldService", Permission = "android.permission.BIND_MIDI_DEVICE_SERVICE", Exported = true)]
[IntentFilter(new[] { "android.media.midi.MidiDeviceService" })]
[MetaData("android.media.midi.MidiDeviceService", Resource = "@xml/circle_turn_hold")]
public sealed class TurnHoldService : MidiDeviceService
{
    /// <inheritdoc />
    public override MidiReceiver[] OnGetInputPortReceivers() => [new Nothing()];

    // A port has to exist for the device to; nobody can reach it but this app, and nothing is sent.
    private sealed class Nothing : MidiReceiver
    {
        public override void OnSend(byte[]? msg, int offset, int count, long timestamp) { }
    }
}

/// <summary>Keeps this process on all the cores, at full speed, until disposed.</summary>
public static class TurnHold
{
    private const string Tag = "CircleAI.TurnHold";
    private const string Product = "Circle AI turn hold";

    // A turn that never says it is over must not leave the screen on.
    private static readonly TimeSpan LongestLit = TimeSpan.FromMinutes(2);

    /// <summary>Lights the screen and asks the system to hold this process, as far as this phone can.</summary>
    public static IDisposable Begin(Context context)
    {
        var hold = new Hold(LightTheScreen(context));
        try
        {
            if (context.GetSystemService(Context.MidiService) is MidiManager midi)
            {
                var device = midi.GetDevices()?.FirstOrDefault(d => d.Properties?.GetString(MidiDeviceInfo.PropertyProduct) == Product);
                if (device is null) global::Android.Util.Log.Warn(Tag, "the system does not list the turn's device");
                else midi.OpenDevice(device, new Opened(hold), null);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            global::Android.Util.Log.Warn(Tag, "could not hold the process: " + ex.GetType().Name);
        }
        return hold;
    }

    // Already on, this changes nothing anybody can see.
    private static PowerManager.WakeLock? LightTheScreen(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.PowerService) is not PowerManager power) return null;
            var lit = power.NewWakeLock(WakeLockFlags.ScreenDim | WakeLockFlags.AcquireCausesWakeup, "CircleAI:turn-screen");
            lit?.SetReferenceCounted(false);
            lit?.Acquire((long)LongestLit.TotalMilliseconds);
            return lit;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            global::Android.Util.Log.Warn(Tag, "could not light the screen: " + ex.GetType().Name);
            return null;
        }
    }

    // Opening is answered later, and a short turn can be over by then.
    private sealed class Hold(PowerManager.WakeLock? lit) : IDisposable
    {
        private readonly object _gate = new();
        private MidiDevice? _device;
        private bool _over;

        public void Took(MidiDevice? device)
        {
            lock (_gate)
            {
                if (!_over)
                {
                    _device = device;
                    return;
                }
            }
            device?.Close();
        }

        public void Dispose()
        {
            MidiDevice? device;
            lock (_gate)
            {
                _over = true;
                device = _device;
                _device = null;
            }
            device?.Close();
            lit?.Release();
        }
    }

    private sealed class Opened(Hold hold) : Java.Lang.Object, MidiManager.IOnDeviceOpenedListener
    {
        public void OnDeviceOpened(MidiDevice? device) => hold.Took(device);
    }
}
