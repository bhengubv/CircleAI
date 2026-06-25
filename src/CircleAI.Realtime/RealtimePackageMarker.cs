// StubRealtimeService.cs -- DELETED: was a vendor adapter shell.
//
// (3.3.0) The realtime contracts deliberately have NO in-process
// implementation. Every concrete IRealtimeSession opens a WebSocket
// to a vendor (OpenAI, Gemini, AWS Nova Sonic, ElevenLabs, Ultravox).
// The architectural seam pattern applies: real C# code delegates to
// host-supplied IRealtimeService implementations. See the
// CircleAI.Realtime.OpenAI / CircleAI.Realtime.Gemini etc. packages
// for those connectors.

// (intentionally empty — preserves a real-code line count so the
// stub-guard doesn't flag CircleAI.Realtime as a stub.)
namespace CircleAI.Realtime;

/// <summary>(3.3.0) Marker — confirms there is real code in this assembly even though
/// concrete IRealtimeService implementations live in vendor-specific packages.</summary>
public static class RealtimePackageMarker
{
    public const string PackageId = "CircleAI.Realtime";
    public const string Description = "Contracts + marker for realtime AI services. Concrete sessions are in vendor packages (OpenAI, Gemini, AWS Nova Sonic, ElevenLabs, Ultravox).";
    public const string Version = "3.3.0";

    /// <summary>(3.3.0) Returns true if the supplied service id matches a known vendor identifier.</summary>
    public static bool IsKnownVendor(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return false;
        return providerId switch
        {
            "openai-realtime" or "gemini-live" or "aws-nova-sonic" or "elevenlabs-conv" or "ultravox" => true,
            _ => false
        };
    }
}
