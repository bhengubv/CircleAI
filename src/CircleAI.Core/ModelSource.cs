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

    /// <summary>
    /// A GitHub release. URL:
    /// <c>github.com/{owner}/{name}/releases/download/{tag}/{asset}</c>, where
    /// the tag rides on the repo as <c>owner/name@tag</c> —
    /// <c>bhengubv/circleai-voices@voices-v1</c>.
    /// </summary>
    /// <remarks>
    /// THE TAG IS NOT PART OF THE FILE NAME, though it was at first. A bundle
    /// file's name is the path it unpacks to, and Open JTalk's dictionary has to
    /// land where the phonemiser looks; spelling the tag as a leading directory
    /// built a correct URL and then put 103 MB somewhere nothing reads. Release
    /// assets are flat, so only the last segment of the name is the asset.
    /// </remarks>
    /// <remarks>
    /// THE STORE WE CAN ACTUALLY WRITE TO. The Hugging Face bucket needs a
    /// credential that does not exist on any machine here, and the cost of that
    /// showed: 45 of the small files the catalogue named had quietly stopped
    /// existing, so those languages downloaded a 114 MB model and then failed on
    /// 2 KB of settings. `github.com/bhengubv` is the account's canonical
    /// storage and we hold its token, so a voice can be published the same day
    /// it is proven instead of waiting on a key.
    ///
    /// Release assets are FLAT — no directories — which is why the tag stands in
    /// for one. Attribution for every asset lives in that repository's README;
    /// each licence here permits redistribution and every one requires the
    /// credit, so removing that file breaks compliance.
    /// </remarks>
    GitHubRelease = 3,
}
