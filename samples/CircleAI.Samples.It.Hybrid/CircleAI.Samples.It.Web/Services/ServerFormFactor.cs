// ServerFormFactor.cs
//
// The server head's answer. Reports the browser as the form factor, because that
// is where the person is - the server is only rendering for them.

namespace CircleAI.Samples.It.Web.Services;

/// <inheritdoc />
public sealed class ServerFormFactor : IFormFactor
{
    /// <inheritdoc />
    public string GetFormFactor() => "Web";

    /// <inheritdoc />
    public string GetPlatform() => Environment.OSVersion.ToString();

    /// <summary>
    /// False. The models are not on the reader's device, so none of the shared
    /// UI's offline claims may be printed here.
    /// </summary>
    public bool IsOnDevice => false;
}
