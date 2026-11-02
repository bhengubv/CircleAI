// ISeeingGenerator.cs
//
// WHETHER A BRAIN CAN SEE THE PICTURE IT IS HANDED.
//
// A text brain handed a picture answered anyway. The MNN text generator never reads one, so on the
// P30 on 2026-11-02 (S9) a question about a photo through the app's own path - the link, the queue,
// the inference process - came back as a confident sentence about a picture the brain never got,
// while the phone held SmolVLM as its eyes. A generator that reads pictures says so here, and
// AIService refuses a picture, in words, to any generator that does not.

namespace CircleAI.Inference;

/// <summary>A generator that may see the pictures in its messages.</summary>
public interface ISeeingGenerator
{
    /// <summary>Whether it reads a picture now - the C# engine only with its vision tower beside it.</summary>
    bool CanSee { get; }
}
