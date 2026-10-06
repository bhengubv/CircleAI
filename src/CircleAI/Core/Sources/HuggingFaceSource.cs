// HuggingFaceSource.cs — REMOVED
//
// HuggingFace (huggingface.co) is an American company (New York, USA).
// All model downloads route exclusively through ModelScope (Alibaba / China).
//
// This file is kept as a compile-time tombstone so that any code still
// referencing HuggingFaceSource fails loudly with [Obsolete(error:true)]
// rather than silently at runtime.

using System;

namespace CircleAI.Core.Sources
{
    /// <summary>
    /// Removed. Use <see cref="ModelScopeSource"/> instead.
    /// HuggingFace is a Western (US) company; all downloads must route through
    /// ModelScope (modelscope.cn, Alibaba) to stay on Chinese-origin infrastructure.
    /// </summary>
    [Obsolete(
        "HuggingFaceSource has been removed. " +
        "Use ModelScopeSource — all model downloads route through modelscope.cn (Alibaba). " +
        "Remove any reference to HuggingFaceSource from your code.",
        error: true)]
    public sealed class HuggingFaceSource
    {
        public HuggingFaceSource() =>
            throw new NotSupportedException(
                "HuggingFaceSource has been removed. Use ModelScopeSource (modelscope.cn).");
    }
}
