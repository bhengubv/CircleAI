// ToolCallGrammar.cs
//
// The form Qwen3.5's own chat template writes a tool call in - XML, not JSON - as a matcher over
// bytes, so the sampler can keep a call well formed once the model has opened one:
//
//   <tool_call>
//   <function=NAME>
//   <parameter=NAME>
//   any text, over as many lines as it needs
//   </parameter>
//   ... more parameters ...
//   </function>
//   </tool_call>
//
// then the end of the turn, or "\n<tool_call>" and another call. The model card names malformed and
// looping calls as a known limit of the model; with the logits in our hands, a closing tag cannot
// be skipped, a value cannot swallow its own "</parameter>", and the parameters and the values have
// a ceiling that forces the call shut. Names are identifiers - and, when the tools are known, one
// of the tools' own names.
//
// A State is a value: a candidate token is tried against it without changing anything.

namespace CircleAI.Inference.Managed.Sampling;

/// <summary>The XML tool-call form of the Qwen3.5 template, as a matcher over the bytes a model writes.</summary>
public sealed class ToolCallGrammar
{
    /// <summary>What a model writes to open a call; the grammar starts right after it.</summary>
    public const string Opening = "<tool_call>";

    private static readonly byte[] OpenFunction = "\n<function="u8.ToArray();
    private static readonly byte[] Parameter = "arameter="u8.ToArray();    // after "<p"
    private static readonly byte[] CloseCall = "function>\n</tool_call>"u8.ToArray(); // after "</"
    private static readonly byte[] ValueEnd = "\n</parameter>\n"u8.ToArray();
    private static readonly byte[] NextCall = "\n<tool_call>"u8.ToArray();

    private readonly NameTrie? _names;

    /// <summary>
    /// A grammar for calls to the tools named in <paramref name="toolNames"/>, or to any identifier
    /// when it is null; at most <paramref name="maxParameters"/> parameters a call and
    /// <paramref name="maxValueBytes"/> bytes a value.
    /// </summary>
    public ToolCallGrammar(IEnumerable<string>? toolNames = null, int maxParameters = 32, int maxValueBytes = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParameters, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValueBytes, 1);
        MaxParameters = maxParameters;
        MaxValueBytes = maxValueBytes;
        if (toolNames is not null)
        {
            var names = toolNames.Where(n => !string.IsNullOrEmpty(n)).ToList();
            foreach (var name in names)
            {
                if (!name.All(c => c < 128 && IsNameByte((byte)c)))
                    throw new ArgumentException($"'{name}' is not a name this form can carry.", nameof(toolNames));
            }
            _names = new NameTrie(names);
        }
    }

    /// <summary>Parameters a call may carry.</summary>
    public int MaxParameters { get; }

    /// <summary>Bytes a parameter's value may hold.</summary>
    public int MaxValueBytes { get; }

    /// <summary>Where the grammar is, right after <see cref="Opening"/>.</summary>
    public State Start => new(Phase.OpenFunction, 0, 0, 0, 0);

    /// <summary>The parts of a call.</summary>
    public enum Phase
    {
        /// <summary>"\n&lt;function=", byte <see cref="State.At"/> of it next.</summary>
        OpenFunction,

        /// <summary>The function's name, <see cref="State.At"/> bytes of it so far; "&gt;" ends it.</summary>
        FunctionName,

        /// <summary>The newline after the function's name.</summary>
        AfterFunctionName,

        /// <summary>"&lt;" opening a parameter or the call's close.</summary>
        Choice,

        /// <summary>"p" for a parameter, "/" for the close.</summary>
        ChoiceAfterLess,

        /// <summary>The rest of "&lt;parameter=", byte <see cref="State.At"/> of it next.</summary>
        OpenParameter,

        /// <summary>The parameter's name, <see cref="State.At"/> bytes of it so far; "&gt;" ends it.</summary>
        ParameterName,

        /// <summary>The newline after the parameter's name.</summary>
        AfterParameterName,

        /// <summary>The value, <see cref="State.At"/> bytes of "\n&lt;/parameter&gt;\n" matched at its end.</summary>
        Value,

        /// <summary>The rest of "&lt;/function&gt;\n&lt;/tool_call&gt;", byte <see cref="State.At"/> of it next.</summary>
        CloseCall,

        /// <summary>
        /// A call is complete: the turn may end here, or "\n&lt;tool_call&gt;" opens another
        /// (<see cref="State.At"/> bytes of it so far).
        /// </summary>
        Done,
    }

    /// <summary>A position in the grammar.</summary>
    /// <param name="Phase">The part being written.</param>
    /// <param name="At">How far into that part.</param>
    /// <param name="Parameters">Parameters this call has opened.</param>
    /// <param name="ValueBytes">Bytes of the current value.</param>
    /// <param name="Node">Where the function's name is in the known names.</param>
    public readonly record struct State(Phase Phase, int At, int Parameters, int ValueBytes, int Node)
    {
        /// <summary>
        /// Whether the turn may end here: a call is complete and no other has begun - a newline
        /// after the close is let through, since only a "&lt;" after it begins another call.
        /// </summary>
        public bool MayEnd => Phase == Phase.Done && At <= 1;
    }

    /// <summary>Whether <paramref name="bytes"/> can follow <paramref name="state"/>; <paramref name="next"/> is where they lead.</summary>
    public bool TryAdvance(State state, ReadOnlySpan<byte> bytes, out State next)
    {
        next = state;
        foreach (var b in bytes)
        {
            if (!TryStep(ref next, b)) return false;
        }
        return true;
    }

    /// <summary>Whether <paramref name="bytes"/> can follow <paramref name="state"/>.</summary>
    public bool Accepts(State state, ReadOnlySpan<byte> bytes) => TryAdvance(state, bytes, out _);

    private bool TryStep(ref State s, byte b)
    {
        switch (s.Phase)
        {
            case Phase.OpenFunction:
                if (b != OpenFunction[s.At]) return false;
                s = s.At + 1 == OpenFunction.Length ? s with { Phase = Phase.FunctionName, At = 0, Node = 0 } : s with { At = s.At + 1 };
                return true;

            case Phase.FunctionName:
                if (b == (byte)'>')
                {
                    if (s.At == 0 || (_names is not null && !_names.IsName(s.Node))) return false;
                    s = s with { Phase = Phase.AfterFunctionName, At = 0 };
                    return true;
                }
                if (!IsNameByte(b)) return false;
                if (_names is not null)
                {
                    var child = _names.Child(s.Node, b);
                    if (child < 0) return false;
                    s = s with { Node = child };
                }
                s = s with { At = s.At + 1 };
                return true;

            case Phase.AfterFunctionName:
                if (b != (byte)'\n') return false;
                s = s with { Phase = Phase.Choice, At = 0 };
                return true;

            case Phase.Choice:
                if (b != (byte)'<') return false;
                s = s with { Phase = Phase.ChoiceAfterLess };
                return true;

            case Phase.ChoiceAfterLess:
                if (b == (byte)'p' && s.Parameters < MaxParameters)
                {
                    s = s with { Phase = Phase.OpenParameter, At = 0, Parameters = s.Parameters + 1 };
                    return true;
                }
                if (b == (byte)'/')
                {
                    s = s with { Phase = Phase.CloseCall, At = 0 };
                    return true;
                }
                return false;

            case Phase.OpenParameter:
                if (b != Parameter[s.At]) return false;
                s = s.At + 1 == Parameter.Length ? s with { Phase = Phase.ParameterName, At = 0 } : s with { At = s.At + 1 };
                return true;

            case Phase.ParameterName:
                if (b == (byte)'>')
                {
                    if (s.At == 0) return false;
                    s = s with { Phase = Phase.AfterParameterName, At = 0 };
                    return true;
                }
                if (!IsNameByte(b)) return false;
                s = s with { At = s.At + 1 };
                return true;

            case Phase.AfterParameterName:
                if (b != (byte)'\n') return false;
                s = s with { Phase = Phase.Value, At = 0, ValueBytes = 0 };
                return true;

            case Phase.Value:
                return StepValue(ref s, b);

            case Phase.CloseCall:
                if (b != CloseCall[s.At]) return false;
                s = s.At + 1 == CloseCall.Length ? s with { Phase = Phase.Done, At = 0, Parameters = 0 } : s with { At = s.At + 1 };
                return true;

            case Phase.Done:
                if (b != NextCall[s.At]) return false;
                s = s.At + 1 == NextCall.Length ? Start : s with { At = s.At + 1 };
                return true;

            default:
                return false;
        }
    }

    // A value is any bytes but its own end, "\n</parameter>\n". At counts how much of that end the
    // value's last bytes match; the end's only repeated byte is the newline that opens and closes
    // it, so a mismatch restarts at one when the byte is a newline. Once "\n</parameter>" is all
    // there, nothing but its newline may follow: a value never holds its own closing tag.
    private bool StepValue(ref State s, byte b)
    {
        if (b == ValueEnd[s.At])
        {
            s = s.At + 1 == ValueEnd.Length
                ? s with { Phase = Phase.Choice, At = 0, ValueBytes = 0 }
                : s with { At = s.At + 1 };
            return true;
        }
        if (s.At == ValueEnd.Length - 1) return false;

        // The bytes the end had matched become part of the value, and so does this one - unless it
        // is a newline, which may open the end afresh.
        var opensEnd = b == ValueEnd[0];
        var value = s.ValueBytes + s.At + (opensEnd ? 0 : 1);
        if (value > MaxValueBytes) return false;
        s = s with { At = opensEnd ? 1 : 0, ValueBytes = value };
        return true;
    }

    /// <summary>A token's bytes as the grammar reads them; empty for a token that may never be part of a call.</summary>
    public delegate ReadOnlySpan<byte> PieceOf(int token);

    /// <summary>
    /// Leaves in <paramref name="logits"/> only the tokens that can follow <paramref name="state"/>;
    /// the rest go to negative infinity. The <paramref name="shortlist"/> highest are tried first -
    /// a sampler draws from those - and the whole vocabulary only when none of them fits. An
    /// end-of-turn token fits only where the turn may end. Returns how the model's own first choice
    /// fared: <see cref="Masked.Kept"/>, <see cref="Masked.Overruled"/>, or <see cref="Masked.Nothing"/>
    /// when no token fits at all, the logits then left as they were.
    /// </summary>
    public Masked Mask(Span<float> logits, State state, PieceOf piece, IReadOnlySet<int> endOfTurn, int shortlist = 64)
    {
        ArgumentNullException.ThrowIfNull(piece);
        ArgumentNullException.ThrowIfNull(endOfTurn);

        bool Fits(int token) => endOfTurn.Contains(token)
            ? state.MayEnd
            : piece(token) is { Length: > 0 } bytes && Accepts(state, bytes);

        var top = Highest(logits, Math.Clamp(shortlist, 1, logits.Length));
        var allowed = top.Where(Fits).ToList();
        if (allowed.Count == 0)
        {
            for (var token = 0; token < logits.Length; token++)
            {
                if (Fits(token)) allowed.Add(token);
            }
            if (allowed.Count == 0) return Masked.Nothing;
        }

        var kept = new float[allowed.Count];
        for (var i = 0; i < kept.Length; i++) kept[i] = logits[allowed[i]];
        logits.Fill(float.NegativeInfinity);
        for (var i = 0; i < allowed.Count; i++) logits[allowed[i]] = kept[i];
        return allowed.Contains(top[0]) ? Masked.Kept : Masked.Overruled;
    }

    /// <summary>What <see cref="Mask"/> did to the model's own first choice.</summary>
    public enum Masked
    {
        /// <summary>It fits the form and is still there.</summary>
        Kept,

        /// <summary>It broke the form and was taken away.</summary>
        Overruled,

        /// <summary>No token fits; nothing was masked.</summary>
        Nothing,
    }

    // The tokens with the highest logits, highest first.
    private static int[] Highest(ReadOnlySpan<float> logits, int count)
    {
        var best = new int[count];
        var filled = 0;
        for (var token = 0; token < logits.Length; token++)
        {
            var value = logits[token];
            if (filled == count && !(value > logits[best[count - 1]])) continue;
            var at = filled < count ? filled++ : count - 1;
            while (at > 0 && value > logits[best[at - 1]])
            {
                best[at] = best[at - 1];
                at--;
            }
            best[at] = token;
        }
        return best[..filled];
    }

    private static bool IsNameByte(byte b) =>
        b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
            or (byte)'_' or (byte)'-' or (byte)'.' or (byte)':' or (byte)'/';

    // The tools' names as a trie over their bytes; node 0 is the empty name.
    private sealed class NameTrie
    {
        private readonly Dictionary<(int Node, byte Byte), int> _children = [];
        private readonly List<bool> _ends = [false];

        public NameTrie(IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                var node = 0;
                foreach (var b in System.Text.Encoding.ASCII.GetBytes(name))
                {
                    if (!_children.TryGetValue((node, b), out var child))
                    {
                        child = _ends.Count;
                        _ends.Add(false);
                        _children[(node, b)] = child;
                    }
                    node = child;
                }
                _ends[node] = true;
            }
        }

        public int Child(int node, byte b) => _children.TryGetValue((node, b), out var child) ? child : -1;

        public bool IsName(int node) => _ends[node];
    }
}
