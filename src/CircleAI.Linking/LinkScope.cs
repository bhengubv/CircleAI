// LinkScope.cs
//
// What a linked app is allowed to reach inside CircleAI.
//
// CHAT IS THE FLOOR, MEMORY IS NOT. The default an app gets by linking is the
// brain and nothing else — text in, text out. A person's long-term memory and
// the full skill library are separate, higher grants a person opts into per app,
// because "let this app use the assistant" must never silently mean "let this app
// read everything the assistant knows about me." Same privacy stance as the
// pooled-inference offload, which sends plain questions only.

namespace CircleAI.Linking;

/// <summary>The capabilities a linked app may reach. Combine with <c>|</c>.</summary>
[Flags]
public enum LinkScope
{
    /// <summary>Nothing. Not a usable grant.</summary>
    None = 0,

    /// <summary>Ask the brain and get a reply. The default a link grants.</summary>
    Chat = 1,

    /// <summary>Read and write the person's long-term memory. A higher grant.</summary>
    Memory = 2,

    /// <summary>The full skill library beyond the caller's own domain. A higher grant.</summary>
    Skills = 4,
}
