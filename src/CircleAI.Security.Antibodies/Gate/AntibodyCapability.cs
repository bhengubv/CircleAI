// AntibodyCapability.cs
//
// The closed set of defensive threat-AWARENESS capabilities that may sit behind
// the authorized-use gate. Every value names a capability whose subject is the
// USER'S OWN SAFETY — never a third party. There is deliberately no "scan target",
// "probe host", or "profile person" capability: those are out of scope by the
// boundary in docs/SECURITY_AUTHORIZED_USE.md and stay absent.

namespace CircleAI.Security.Antibodies.Gate;

/// <summary>
/// Names a single defensive threat-awareness capability that the
/// <see cref="IAuthorizedUseGate"/> can authorize. Each capability answers a
/// question about a threat to the user, and only warns — it never acts on a
/// third party. The set is intentionally closed and small.
/// </summary>
public enum AntibodyCapability
{
    /// <summary>
    /// "Is a file the user is about to open known-bad?" Assesses a file by its
    /// hash against the device's local indicator corpus and warns before the user
    /// opens or runs it. Reference shape: malware-awareness (malwoverview),
    /// reframed as a pre-open warning for the user's own downloads.
    /// </summary>
    FileReputationAwareness,

    /// <summary>
    /// "Is a URL / IP / domain the user is about to trust known-bad?" Assesses a
    /// network indicator the user is about to connect to and warns before they do.
    /// Reference shape: indicator/blocklist intel (deepdarkCTI, ipblocklist),
    /// reframed as a pre-connect warning for the user.
    /// </summary>
    NetworkIndicatorAwareness,

    /// <summary>
    /// "Has the user's OWN identity (their email / username / phone) turned up in a
    /// breach corpus?" Hashes the user's identity and checks the local breach set so
    /// they can rotate an exposed credential. Reference shape: breach/identity intel
    /// (findme), reframed to protect the user's own identity only.
    /// </summary>
    BreachExposureAwareness,
}
