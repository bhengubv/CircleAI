// DeviceFormFactor.cs
//
// What this head is: a real device, with the models on it.

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceFormFactor : IFormFactor
{
    /// <inheritdoc />
    public string GetFormFactor() => DeviceInfo.Idiom.ToString();

    /// <inheritdoc />
    public string GetPlatform() => $"{DeviceInfo.Platform} {DeviceInfo.VersionString}";

    /// <summary>
    /// True. This is the one head where the shared UI's offline claims hold, so it
    /// is the one head allowed to print them.
    /// </summary>
    public bool IsOnDevice => true;
}
