// BrowserFormFactor.cs
//
// What the browser head is.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
public sealed class BrowserFormFactor : IFormFactor
{
    /// <inheritdoc />
    public string GetFormFactor() => "Web";

    /// <inheritdoc />
    public string GetPlatform() => Environment.OSVersion.ToString();

    /// <summary>Always false, and that is the whole point of the property.</summary>
    /// <remarks>
    /// The shared pages print "runs on this phone - nothing leaves the device"
    /// when this is true. In a browser tab that sentence is false: the models are
    /// not here. Getting this wrong does not break a build, it publishes a claim
    /// about privacy that is not true.
    /// </remarks>
    public bool IsOnDevice => false;
}
