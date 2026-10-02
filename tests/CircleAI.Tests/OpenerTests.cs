// OpenerTests.cs
//
// "Hey B" went to a language model and cost 38.5 seconds to say hello.
//
// The numbers from the P30, 2026-10-03: 25.6 s of audio transcribed to the seven
// characters "Hey, B."; three SaaS skills matched on the substring "hey"; the
// prompt reached 573 tokens; prefill 26,256 ms, decode 1,018 ms.
//
// Both halves are fixed - the substring in SqliteSkillStore, the greeting here -
// and this file is the half that must not over-fire. A phone that stops answering
// real questions because they open with "hi" is worse than the bug.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class OpenerTests
{
    [Theory]
    [InlineData("hi")]
    [InlineData("Hello")]
    [InlineData("HEY")]
    [InlineData("hi there")]
    [InlineData("Good morning")]
    [InlineData("good evening")]
    [InlineData("howzit")]
    [InlineData("sawubona")]
    public void A_hello_on_its_own_is_not_a_question(string said)
        => Assert.True(Opener.IsNothingButHello(said));

    [Theory]
    [InlineData("Hey, B.")]
    [InlineData("hey b")]
    [InlineData("Hey B!")]
    [InlineData("Hey B, hello")]
    public void The_wake_phrase_on_its_own_is_not_a_question(string said)
    {
        // THE TRANSCRIPT THAT STARTED THIS. Whisper punctuates - "Hey, B." has a
        // comma and a full stop nobody spoke - so this compares words, not strings.
        Assert.True(Opener.IsNothingButHello(said, "HEY B"));
    }

    [Theory]
    [InlineData("Hey B, what is the weather")]
    [InlineData("hello, how do I plant maize")]
    [InlineData("hi, tell me a joke")]
    [InlineData("good morning, what is 12 times 8")]
    public void A_hello_with_a_question_after_it_is_a_question(string said)
    {
        // THE FAILURE TO AVOID, and the reason this is not an intent classifier.
        // Most people greet an assistant and then ask in the same breath.
        Assert.False(Opener.IsNothingButHello(said, "HEY B"));
    }

    [Theory]
    [InlineData("what is the capital of Kenya")]
    [InlineData("tell me a joke")]
    [InlineData("highlight the main points")]
    [InlineData("hire a plumber")]
    [InlineData("history of Soweto")]
    public void A_real_question_is_never_a_greeting(string said)
    {
        // "highlight", "hire" and "history" all START with "hi". Words, not
        // substrings - which is the same mistake that matched "hey" to HeyGen.
        Assert.False(Opener.IsNothingButHello(said, "HEY B"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_said_is_not_a_greeting(string? said)
    {
        // A quiet room is its own case and the turn already handles it. Calling it
        // a greeting would answer "Hi." at a microphone nobody spoke into.
        Assert.False(Opener.IsNothingButHello(said, "HEY B"));
    }

    [Fact]
    public void Without_a_wake_phrase_it_still_knows_a_plain_hello()
    {
        // The listener has not started yet, or the device was woken some other way.
        Assert.True(Opener.IsNothingButHello("hello"));
        Assert.False(Opener.IsNothingButHello("Hey B"));   // "B" is not a hello
    }

    [Fact]
    public void A_changed_wake_phrase_is_still_recognised()
    {
        // ASKED OF THE LISTENER, NOT COPIED. Somebody can change what the phone
        // answers to; a hard-coded "hey b" would stop recognising that same day.
        Assert.True(Opener.IsNothingButHello("Okay Circle.", "OKAY CIRCLE"));
        Assert.False(Opener.IsNothingButHello("Okay Circle, what time is it", "OKAY CIRCLE"));
    }
}
