// ChatCapability.cs
//
// The capability axis the consumer DECLARES. The SDK then picks the
// concrete model that satisfies it.
//
// Guiding principle: the consumer says WHAT they need (vision, tools,
// long context). The SDK figures out WHICH model can do it on THIS
// device. Hardcoding "Qwen3-30B-A3B-MNN" in AIOptions is the consumer
// pre-empting a decision the SDK is positioned to make better.

namespace CircleAI.Inference;

/// <summary>
/// Capability flags the consumer requests from <see cref="IModelSelector"/>.
/// The selector finds the highest-quality model in the registry that
/// satisfies every requested flag AND fits the device.
/// </summary>
[System.Flags]
public enum ChatCapability
{
    /// <summary>No requirement — selector picks the best-quality default-tier model that fits.</summary>
    None = 0,

    /// <summary>Basic text chat. Every model in the registry satisfies this.</summary>
    Default = 1 << 0,

    /// <summary>Model emits <c>&lt;tool_call&gt;{...}&lt;/tool_call&gt;</c> blocks reliably (Qwen 3+ family).</summary>
    Tools = 1 << 1,

    /// <summary>Model accepts image input via <see cref="VisionInput"/> (Kimi-VL family).</summary>
    Vision = 1 << 2,

    /// <summary>Model supports a context window ≥ 32K tokens.</summary>
    LongContext = 1 << 3,

    /// <summary>Model has an explicit "thinking" mode (Qwen3 reasoning variants).</summary>
    Reasoning = 1 << 4,
}
