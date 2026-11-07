// IReadingGenerator.cs
//
// WHAT A PROMPT WILL COST A BRAIN TO READ, BEFORE IT STARTS.
//
// The queue gives an answer two minutes and sends the session's history with every question, up to
// 1,200 characters on a 4 GB phone - sized at ~29 ms a token, which is MNN on the P30. The C# engine
// takes about 450 ms a token there, so a full history is about 170 s of reading: on the P30 on
// 2026-11-05 questions as small as "Say hello in one word." stopped at the deadline with no answer.
// A generator that measures its own reading says so here, and AIService hands it only what it can
// read in the turn's time (GenerationOptions.ReadWithin).
//
// AND WHAT NO TURN HAS THE TIME TO READ IS READ BEFORE ONE ASKS (PrimeAsync). The template's tools
// block is about 540 tokens, four minutes on the P30, and the same every time: a turn that had to
// read it never reached its question. Read once in the gaps and kept on disk, it costs a turn nothing,
// and neither does changing it: AIService offers such a brain what each turn asks for, and no more.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Tools;

namespace CircleAI.Inference;

/// <summary>A generator that knows how long reading a prompt will take it.</summary>
public interface IReadingGenerator
{
    /// <summary>How long one prompt token took to read when last measured; null until one was.</summary>
    TimeSpan? ReadCost { get; }

    /// <summary>The tokens of this conversation it would have to read, leaving out what it already holds.</summary>
    int Unread(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools);

    /// <summary>
    /// The same, for a turn going on <paramref name="conversation"/> (<see cref="GenerationOptions.Conversation"/>):
    /// a brain keeping it has only the new turn to read, whatever the messages hold.
    /// </summary>
    int Unread(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, string? conversation) => Unread(messages, tools);

    /// <summary>
    /// Reads the system turn of <paramref name="messages"/>, <paramref name="tools"/> in it, and keeps it
    /// on disk, so a turn that opens the same way starts after it. It reads in the gaps: a turn takes the
    /// engine whenever it asks, and this goes on when the turn is done.
    /// </summary>
    /// <returns>True once that start is on disk; false when this brain keeps none.</returns>
    Task<bool> PrimeAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, CancellationToken ct = default);
}
