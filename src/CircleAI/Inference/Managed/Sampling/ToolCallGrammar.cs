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
// WHEN THE TOOLS' SIGNATURES ARE KNOWN, A CALL CAN ONLY BE ONE THE TOOL CAN RUN. The parameter names
// are the tool's own, each at most once; the call cannot close until every required parameter is
// in it; and a parameter that takes a fixed set of values takes one of them, spelled as declared.
// Those are the three ways a well-formed call still came back refused (ToolCallReader.IsRunnable):
// a misspelt argument, a missing one, a value outside its list.
//
// A State is a value: a candidate token is tried against it without changing anything.

namespace CircleAI.Inference.Managed.Sampling;

/// <summary>The XML tool-call form of the Qwen3.5 template, as a matcher over the bytes a model writes.</summary>
public sealed class ToolCallGrammar
{
    /// <summary>What a model writes to open a call; the grammar starts right after it.</summary>
    public const string Opening = "<tool_call>";

    /// <summary>The most parameters of one tool whose use is tracked - which are given, which are required.</summary>
    private const int TrackedParameters = 64;

    private static readonly byte[] OpenFunction = "\n<function="u8.ToArray();
    private static readonly byte[] ParameterTag = "arameter="u8.ToArray(); // after "<p"
    private static readonly byte[] CloseCall = "function>\n</tool_call>"u8.ToArray(); // after "</"
    private static readonly byte[] ValueEnd = "\n</parameter>\n"u8.ToArray();
    private static readonly byte[] NextCall = "\n<tool_call>"u8.ToArray();

    private readonly NameTrie? _names;
    private readonly Signature[]? _signatures;

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
            foreach (var name in names) CheckName(name, nameof(toolNames));
            _names = new NameTrie(names.Select(Bytes));
        }
    }

    /// <summary>
    /// A grammar for calls to <paramref name="functions"/> only, each with its own parameters: their
    /// names, each at most once; every required one before the call closes; a fixed set's values only.
    /// </summary>
    public ToolCallGrammar(IEnumerable<Function> functions, int maxParameters = 32, int maxValueBytes = 8192)
        : this(toolNames: null, maxParameters, maxValueBytes)
    {
        ArgumentNullException.ThrowIfNull(functions);
        var list = functions.Where(f => f is not null && !string.IsNullOrEmpty(f.Name)).ToList();
        foreach (var function in list)
        {
            CheckName(function.Name, nameof(functions));
            foreach (var parameter in function.Parameters) CheckName(parameter.Name, nameof(functions));
        }
        _names = new NameTrie(list.Select(f => Bytes(f.Name)));
        _signatures = new Signature[list.Count];
        for (var i = 0; i < list.Count; i++) _signatures[i] = new Signature(list[i]);
    }

    /// <summary>A tool a call may name: its name and every parameter it takes.</summary>
    /// <param name="Name">The tool's name, as the call must write it.</param>
    /// <param name="Parameters">Its parameters; none means the call carries none.</param>
    public sealed record Function(string Name, IReadOnlyList<Parameter> Parameters);

    /// <summary>One parameter of a <see cref="Function"/>.</summary>
    /// <param name="Name">Its name, as the call must write it.</param>
    /// <param name="Required">The call cannot close without it.</param>
    /// <param name="Values">
    /// The only values it takes, spelled as the call must write them; null takes any text. A set
    /// with a value that holds a line break is not held to (the value's own end is a line break).
    /// </param>
    public sealed record Parameter(string Name, bool Required = false, IReadOnlyList<string>? Values = null);

    /// <summary>Whether a tool or parameter name can be written in this form.</summary>
    public static bool CanCarry(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(c => c < 128 && IsNameByte((byte)c));

    private static void CheckName(string name, string argument)
    {
        if (!CanCarry(name)) throw new ArgumentException($"'{name}' is not a name this form can carry.", argument);
    }

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

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

        /// <summary>A value from the parameter's fixed set, <see cref="State.Node"/> where it is among them.</summary>
        SetValue,

        /// <summary>The rest of "\n&lt;/parameter&gt;\n" after a value from a fixed set, byte <see cref="State.At"/> of it next.</summary>
        SetValueEnd,
    }

    /// <summary>A position in the grammar.</summary>
    /// <param name="Phase">The part being written.</param>
    /// <param name="At">How far into that part.</param>
    /// <param name="Parameters">Parameters this call has opened.</param>
    /// <param name="ValueBytes">Bytes of the current value.</param>
    /// <param name="Node">Where the name (or the fixed-set value) being written is among the known ones.</param>
    /// <param name="Function">The tool this call names, among the known signatures; -1 when none is known.</param>
    /// <param name="Given">The tool's parameters this call has already given, one bit each.</param>
    /// <param name="Param">The parameter being written, in the tool's signature; -1 when none is known.</param>
    public readonly record struct State(Phase Phase, int At, int Parameters, int ValueBytes, int Node,
        int Function = -1, ulong Given = 0, int Param = -1)
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
                    s = s with { Phase = Phase.AfterFunctionName, At = 0, Function = _names?.NameAt(s.Node) ?? -1, Given = 0, Param = -1 };
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
            {
                var signature = SignatureOf(s.Function);
                if (b == (byte)'p' && s.Parameters < MaxParameters && (signature is null || signature.AnyLeft(s.Given)))
                {
                    s = s with { Phase = Phase.OpenParameter, At = 0, Parameters = s.Parameters + 1, Node = 0 };
                    return true;
                }
                // A REQUIRED PARAMETER STILL MISSING KEEPS THE CALL OPEN: closed without it, the
                // call is one the tool refuses.
                if (b == (byte)'/' && (signature is null || signature.HasRequired(s.Given)))
                {
                    s = s with { Phase = Phase.CloseCall, At = 0 };
                    return true;
                }
                return false;
            }

            case Phase.OpenParameter:
                if (b != ParameterTag[s.At]) return false;
                s = s.At + 1 == ParameterTag.Length ? s with { Phase = Phase.ParameterName, At = 0, Node = 0 } : s with { At = s.At + 1 };
                return true;

            case Phase.ParameterName:
            {
                var signature = SignatureOf(s.Function);
                if (b == (byte)'>')
                {
                    if (s.At == 0) return false;
                    if (signature is null)
                    {
                        s = s with { Phase = Phase.AfterParameterName, At = 0, Param = -1 };
                        return true;
                    }
                    var index = signature.Names.NameAt(s.Node);
                    if (index < 0 || signature.WasGiven(s.Given, index)) return false;
                    s = s with { Phase = Phase.AfterParameterName, At = 0, Param = index, Given = signature.Give(s.Given, index) };
                    return true;
                }
                if (!IsNameByte(b)) return false;
                if (signature is not null)
                {
                    // ONLY TOWARDS A NAME NOT YET GIVEN, so the model is never let into a name it
                    // could not finish.
                    var child = signature.Names.Child(s.Node, b);
                    if (child < 0 || !signature.Leads(child, s.Given)) return false;
                    s = s with { Node = child };
                }
                s = s with { At = s.At + 1 };
                return true;
            }

            case Phase.AfterParameterName:
                if (b != (byte)'\n') return false;
                s = SignatureOf(s.Function)?.ValuesOf(s.Param) is not null
                    ? s with { Phase = Phase.SetValue, At = 0, Node = 0, ValueBytes = 0 }
                    : s with { Phase = Phase.Value, At = 0, ValueBytes = 0 };
                return true;

            case Phase.Value:
                return StepValue(ref s, b);

            case Phase.SetValue:
            {
                // One of the declared values, byte for byte; once one is complete, its line break
                // opens the closing tag. A set holding a line break is never held to (Signature).
                var values = SignatureOf(s.Function)!.ValuesOf(s.Param)!;
                if (b == ValueEnd[0] && values.IsName(s.Node))
                {
                    s = s with { Phase = Phase.SetValueEnd, At = 1 };
                    return true;
                }
                var child = values.Child(s.Node, b);
                if (child < 0) return false;
                s = s with { Node = child, ValueBytes = s.ValueBytes + 1 };
                return true;
            }

            case Phase.SetValueEnd:
                if (b != ValueEnd[s.At]) return false;
                s = s.At + 1 == ValueEnd.Length ? s with { Phase = Phase.Choice, At = 0, ValueBytes = 0 } : s with { At = s.At + 1 };
                return true;

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

    private Signature? SignatureOf(int function) =>
        _signatures is { } all && function >= 0 && function < all.Length ? all[function] : null;

    // One tool's signature, compiled: its parameters' names as a trie, which of them are required,
    // and the fixed set of values each one takes, if it takes one.
    private sealed class Signature
    {
        private readonly NameTrie?[] _values;
        private readonly ulong _all;
        private readonly ulong _required;
        private readonly bool _tracked;

        public Signature(Function function)
        {
            var parameters = function.Parameters ?? [];
            Names = new NameTrie(parameters.Select(p => Bytes(p.Name)));
            _values = new NameTrie?[parameters.Count];
            _tracked = parameters.Count <= TrackedParameters;
            for (var i = 0; i < parameters.Count; i++)
            {
                if (_tracked)
                {
                    _all |= 1UL << i;
                    if (parameters[i].Required) _required |= 1UL << i;
                }
                // A VALUE WITH A LINE BREAK IN IT CANNOT BE TOLD FROM ITS OWN END, so such a set is
                // left as free text rather than held to wrongly.
                if (parameters[i].Values is { Count: > 0 } values && !values.Any(v => v is null || v.Contains('\n')))
                    _values[i] = new NameTrie(values.Select(Bytes));
            }
        }

        public NameTrie Names { get; }

        public NameTrie? ValuesOf(int parameter) => parameter >= 0 && parameter < _values.Length ? _values[parameter] : null;

        // Whether a parameter not yet given is left to open; anything goes past the tracked count.
        public bool AnyLeft(ulong given) => !_tracked || (_all & ~given) != 0;

        // Whether every required parameter has been given.
        public bool HasRequired(ulong given) => !_tracked || (_required & ~given) == 0;

        public bool WasGiven(ulong given, int parameter) => _tracked && (given & (1UL << parameter)) != 0;

        public ulong Give(ulong given, int parameter) => _tracked ? given | (1UL << parameter) : given;

        // Whether a name not yet given lies at or below this node.
        public bool Leads(int node, ulong given) => !_tracked || (Names.Reach(node) & ~given) != 0;
    }

    // Names as a trie over their bytes; node 0 is the empty name. Each node knows which name ends
    // there (its index in the order given, the later of two equal names winning) and which of the
    // first 64 names lie at or below it.
    private sealed class NameTrie
    {
        private readonly Dictionary<(int Node, byte Byte), int> _children = [];
        private readonly List<int> _ends = [-1];
        private readonly List<ulong> _reach = [0];

        public NameTrie(IEnumerable<byte[]> names)
        {
            var index = 0;
            foreach (var name in names)
            {
                var bit = index < TrackedParameters ? 1UL << index : 0;
                var node = 0;
                _reach[0] |= bit;
                foreach (var b in name)
                {
                    if (!_children.TryGetValue((node, b), out var child))
                    {
                        child = _ends.Count;
                        _ends.Add(-1);
                        _reach.Add(0);
                        _children[(node, b)] = child;
                    }
                    node = child;
                    _reach[node] |= bit;
                }
                _ends[node] = index++;
            }
        }

        public int Child(int node, byte b) => _children.TryGetValue((node, b), out var child) ? child : -1;

        public bool IsName(int node) => _ends[node] >= 0;

        public int NameAt(int node) => _ends[node];

        public ulong Reach(int node) => _reach[node];
    }
}
