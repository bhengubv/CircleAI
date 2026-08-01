namespace CircleAI.Core;

/// <summary>
/// Where a bundle's files are fetched from. The chat ladder lives on ModelScope;
/// the de-Googled speech models (Piper, Whisper-ggml, Silero-VAD, openWakeWord)
/// live on Hugging Face, which the ModelScope-only downloader could not reach.
/// </summary>
public enum ModelSource
{
    /// <summary>
    /// ModelScope — the default. URLs:
    /// <c>modelscope.cn/api/v1/models/{repo}/repo?...FilePath={file}</c> and the
    /// <c>resolve/master</c> CDN fallback.
    /// </summary>
    ModelScope = 0,

    /// <summary>
    /// Hugging Face. URL: <c>huggingface.co/{repo}/resolve/main/{file}</c>.
    /// LFS files expose their SHA-256 as <c>lfs.oid</c> via the tree API, so
    /// pins are obtainable the same verified way ModelScope pins are.
    /// </summary>
    HuggingFace = 1,

    /// <summary>
    /// A Hugging Face storage bucket — flat object storage, not a git repo.
    /// URL: <c>huggingface.co/buckets/{bucket}/resolve/{file}</c>: no branch
    /// name, because buckets have no branches.
    /// </summary>
    /// <remarks>
    /// This is where our own curated copies live, so a speaker of a small
    /// language depends on one address we control rather than on six strangers
    /// keeping their repositories up. The bucket tree API reports an
    /// <c>xetHash</c>, which is NOT the SHA-256 the downloader verifies against
    /// — pins must still come from the file's own bytes.
    /// </remarks>
    HuggingFaceBucket = 2,
}
