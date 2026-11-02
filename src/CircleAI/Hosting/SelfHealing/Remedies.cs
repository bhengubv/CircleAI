// Remedies.cs
//
// THE NAMES OF EVERYTHING WOLVERINE MAY DO ON ITS OWN (S6), IN ONE PLACE.
//
// A remedy's name is a string four halves have to agree on: the rule that picks it, the safe fix
// that carries it out, the brain that is offered the list for a fault no rule knows, and the
// report that tells WolverineAPI what was done. Each of those writing its own literal is this
// repository's most repeated defect, so they are constants, and the list the brain chooses from
// is built from them - it can choose nothing that is not here.

namespace CircleAI.Hosting.SelfHealing;

/// <summary>The safe fixes Wolverine runs by itself, by name.</summary>
public static class Remedies
{
    /// <summary>Step down to the next brain this device holds, for SelfHeal's window.</summary>
    public const string SmallerBrain = "smaller-brain";

    /// <summary>Run the brain on fewer threads.</summary>
    public const string FewerThreads = "fewer-threads";

    /// <summary>Hold working memory (the attention cache) at fewer bits a value.</summary>
    public const string FewerMemoryBits = "fewer-memory-bits";

    /// <summary>Hold a shorter context.</summary>
    public const string ShorterContext = "shorter-context";

    /// <summary>Close the reasoning sooner, so an answer always follows it.</summary>
    public const string TighterThinking = "tighter-thinking";

    /// <summary>Kernels without vector instructions, after an illegal instruction.</summary>
    public const string ScalarKernels = "scalar-kernels";

    /// <summary>Fetch a model file again after it failed its hash.</summary>
    public const string Redownload = "redownload";

    /// <summary>Resume a download that stalled, starting from its other address.</summary>
    public const string ResumeDownload = "resume-download";

    /// <summary>Retry a tool call that could not be read, held to the template's form. Done where the call is.</summary>
    public const string ToolCallRetry = "tool-call-retry";

    /// <summary>Free the regenerable caches under memory pressure (MemoryManagerSafeFix).</summary>
    public const string Memory = "memory";

    /// <summary>Trim the regenerable caches (MemoryManagerSafeFix).</summary>
    public const string Cache = "cache";

    /// <summary>What each remedy does, in one line - the menu the brain is offered for a fault no rule knows.</summary>
    public static string Describe(string remedy) => remedy switch
    {
        SmallerBrain    => "use a smaller brain for a while",
        FewerThreads    => "run the brain on fewer threads",
        FewerMemoryBits => "keep the brain's working memory at fewer bits",
        ShorterContext  => "give the brain a shorter context",
        TighterThinking => "let the brain think for fewer tokens before answering",
        ScalarKernels   => "run the brain without vector instructions",
        Redownload      => "download a damaged model file again",
        ResumeDownload  => "resume a stalled download from another address",
        ToolCallRetry   => "retry a tool call that could not be read",
        Memory          => "free the caches that can be rebuilt",
        Cache           => "trim the caches that can be rebuilt",
        _               => remedy,
    };
}
