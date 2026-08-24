// IFormFactor.cs
//
// Which head is rendering, and on what.
//
// The pattern's own sample carries this interface, and it earns its place for a
// reason beyond showing platform detection: the shared UI makes claims about
// where work happens ("runs on this phone", "nothing leaves the device"), and
// those claims are TRUE ON ONE HEAD AND FALSE ON ANOTHER. A page that cannot
// tell which head it is in will print the wrong one.

namespace CircleAI.Samples.It;

/// <summary>The host this UI is currently rendering in.</summary>
public interface IFormFactor
{
    /// <summary>"Phone", "Desktop", "Web" - the device class.</summary>
    string GetFormFactor();

    /// <summary>The platform string, for the diagnostics screen.</summary>
    string GetPlatform();

    /// <summary>
    /// True when this head runs on the user's own device with the models local to
    /// it - the only case where the sample's offline claims hold.
    /// </summary>
    bool IsOnDevice { get; }
}
