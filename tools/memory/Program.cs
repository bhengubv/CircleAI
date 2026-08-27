// memory
//
// The command that makes the memory universal.
//
// A SHELL IS THE ONE INTERFACE EVERY MODEL ALREADY HAS. Claude, a local llama,
// a script, a person - none of them can link a C# library, and all of them can
// run a command. So this is the surface: remember, recall, correct, and the
// memory works the same from any of them on any of the three machines.
//
//   memory remember "Use -t:InstallKeepingData when iterating" \
//          --about deploy:android --challenge "-t:Install wiped 817 MB of models"
//   memory recall --doing deploy --to android
//   memory correct a1b2c3d4 "Actually the flag is -t:Install with fast deployment off"
//   memory failed  a1b2c3d4 "It still wiped them on MIUI"
//
// IT REPLAYS THE LOGS ON EVERY RUN rather than trusting an index on disk. A
// command that answers from a stale cache after a git pull is worse than one
// that is slow: it looks like it remembered and it did not. Replay of a few
// thousand lines costs milliseconds, and the on-disk index that the app uses
// is rebuilt explicitly by `memory sync`.
//
// WHERE THE MEMORY LIVES is $CIRCLEAI_MEMORY, or ~/.circleai/memory. Point the
// first at a directory inside a git repository and three machines share it.

using System.Globalization;
using CircleAI.Memory;

return await Run(args);

// ----------------------------------------------------------------------
// Dispatch
// ----------------------------------------------------------------------

static async Task<int> Run(string[] argv)
{
    if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
    {
        Usage();
        return argv.Length == 0 ? 2 : 0;
    }

    var command = argv[0].ToLowerInvariant();
    var rest = argv.Skip(1).ToArray();

    try
    {
        return command switch
        {
            "remember" or "record" => await Remember(rest),
            "recall" or "ask"      => await RecallCmd(rest),
            "correct"              => await Correct(rest, failed: false),
            "failed"               => await Correct(rest, failed: true),
            "list"                 => await List(rest),
            "show"                 => await Show(rest),
            "sync"                 => await Sync(rest),
            "where"                => Where(),
            _ => Complain($"Unknown command '{argv[0]}'. Try: memory help"),
        };
    }
    catch (Exception ex)
    {
        // Say what went wrong in a sentence. A stack trace helps nobody who is
        // reading this out of a tool result.
        Console.Error.WriteLine($"memory: {ex.Message}");
        return 1;
    }
}

// ----------------------------------------------------------------------
// Commands
// ----------------------------------------------------------------------

static async Task<int> Remember(string[] argv)
{
    var (text, opts) = Parse(argv);
    if (string.IsNullOrWhiteSpace(text))
        return Complain("Nothing to remember. memory remember \"<what>\" [--about <subject>]");

    var atom = Build(text, opts);

    var (folder, sync) = Open();
    using var store = new SqliteAtomStore("Data Source=:memory:");
    await sync.RebuildAsync(store);
    await sync.RecordAsync(store, atom);

    Console.WriteLine($"remembered  {Short(atom.Id)}  {atom.Kind.ToString().ToLowerInvariant()}" +
                      (atom.Subject is { Length: > 0 } s ? $"  {s}" : "") +
                      $"  ->  {folder.Machine}");
    return 0;
}

static async Task<int> RecallCmd(string[] argv)
{
    var (text, opts) = Parse(argv);

    var situation = new Situation(
        Verb:   opts.GetValueOrDefault("doing"),
        Target: opts.GetValueOrDefault("to"),
        Tool:   opts.GetValueOrDefault("with"),
        Text:   string.IsNullOrWhiteSpace(text) ? null : text);

    if (situation.IsEmpty)
        return Complain("Recall what? memory recall --doing deploy --to android [free text]");

    var budget = new RecallBudget(
        MaxAtoms:      Number(opts, "limit", 5),
        MaxCharacters: Number(opts, "chars", 600));

    var (_, sync) = Open();
    using var store = new SqliteAtomStore("Data Source=:memory:");
    await sync.RebuildAsync(store);

    var result = await new Recall(store).ForAsync(situation, budget);

    // NOTHING KNOWN IS AN ANSWER, and it exits zero. A caller that treats an
    // empty memory as a failure will stop asking, which is the one outcome
    // that makes the whole thing pointless. The tone still comes back: how
    // somebody wants to be worked with does not depend on the subject.
    if (opts.ContainsKey("brief")) Brief(situation, result);
    else                           Full(situation, result);

    return 0;
}

static async Task<int> Correct(string[] argv, bool failed)
{
    if (argv.Length == 0)
        return Complain(failed
            ? "memory failed <id> [\"what went wrong\"]"
            : "memory correct <id> \"<the corrected version>\"");

    var prefix = argv[0];
    var (text, opts) = Parse(argv.Skip(1).ToArray());

    var (_, sync) = Open();
    using var store = new SqliteAtomStore("Data Source=:memory:");
    await sync.RebuildAsync(store);

    if (await Resolve(store, prefix) is not { } old)
        return 1;

    // A DECISION THAT FAILED IS NOT AN EDIT, it is a later thing that was
    // found out. So it supersedes rather than overwrites: the original stays
    // readable, and the memory can show that this road was tried.
    //
    // AND MARKING IT FAILED MUST NOT ERASE WHAT IT WAS. On `failed`, the words
    // are the reason it did not hold - the newest thing that came up - so they
    // land in the challenge and the decision itself is kept. A memory that
    // replaced "never restart a device" with "we rebooted merlin anyway" would
    // have forgotten the very rule it was recording a breach of.
    var replacement = new MemoryAtom
    {
        Kind      = old.Kind,
        Text      = !failed && !string.IsNullOrWhiteSpace(text) ? text : old.Text,
        Subject   = opts.GetValueOrDefault("about") ?? old.Subject,
        Challenge = opts.GetValueOrDefault("challenge")
                    ?? (failed && !string.IsNullOrWhiteSpace(text) ? text : null)
                    ?? old.Challenge,
        Outcome   = failed ? DecisionOutcome.Failed : OutcomeOf(opts) ?? old.Outcome,
        Verify    = opts.GetValueOrDefault("verify") ?? old.Verify,
    };

    await sync.RecordAsync(store, replacement, supersedes: old.Id);

    var verb = failed ? "marked failed" : "corrected";
    Console.WriteLine($"{verb}  {Short(old.Id)} -> {Short(replacement.Id)}  {replacement.Text}");
    return 0;
}

static async Task<int> List(string[] argv)
{
    var (text, opts) = Parse(argv);

    var (_, sync) = Open();
    using var store = new SqliteAtomStore("Data Source=:memory:");
    var report = await sync.RebuildAsync(store);

    var limit = Number(opts, "limit", 30);
    List<MemoryAtom> atoms;

    if (opts.GetValueOrDefault("kind") is { Length: > 0 } k)
    {
        if (!Enum.TryParse<AtomKind>(k, ignoreCase: true, out var kind))
            return Complain($"Unknown kind '{k}'. One of: {Kinds()}");
        atoms = (await store.ByKindAsync(kind, limit)).ToList();
    }
    else if (opts.GetValueOrDefault("about") is { Length: > 0 } about ||
             !string.IsNullOrWhiteSpace(text))
    {
        var subject = opts.GetValueOrDefault("about");
        atoms = (await store.MatchAsync(
            new Situation(Verb: subject, Text: text), limit)).ToList();
    }
    else
    {
        atoms = (await store.AllAsync(
            includeSuperseded: opts.ContainsKey("all"), limit: limit)).ToList();
    }

    Console.WriteLine($"{atoms.Count} of {report.Current} remembered" +
                      $"  ({report.Records} lines, {report.Machines} machine{(report.Machines == 1 ? "" : "s")})");
    Console.WriteLine();
    foreach (var atom in atoms) Block(atom);
    return 0;
}

static async Task<int> Show(string[] argv)
{
    if (argv.Length == 0) return Complain("memory show <id>");

    var (_, sync) = Open();
    using var store = new SqliteAtomStore("Data Source=:memory:");
    await sync.RebuildAsync(store);

    if (await Resolve(store, argv[0]) is not { } atom) return 1;

    Block(atom, full: true);

    // The chain forward, so a superseded decision can be walked to what
    // replaced it. This is what "auditable" means in practice.
    var next = atom.SupersededBy;
    while (next is { } id && await store.GetAsync(id) is { } later)
    {
        Console.WriteLine("  replaced by");
        Block(later, full: true, indent: "    ");
        next = later.SupersededBy;
    }

    return 0;
}

static async Task<int> Sync(string[] argv)
{
    var (_, opts) = Parse(argv);
    var (folder, sync) = Open();
    folder.EnsureGitIgnore();

    // The on-disk index is what the app reads, so this is the one command that
    // writes it. Dropping it first is deliberate: a rebuild that merges into a
    // stale file would keep an atom whose log line somebody deleted by hand.
    if (File.Exists(folder.IndexPath) && !opts.ContainsKey("keep"))
        File.Delete(folder.IndexPath);

    using var store = new SqliteAtomStore(folder.IndexConnectionString);
    var report = await sync.RebuildAsync(store);

    Console.WriteLine($"{report.Current} current of {report.Atoms} atoms" +
                      $"  ({report.Records} lines from {report.Machines} machine{(report.Machines == 1 ? "" : "s")})");
    Console.WriteLine($"index   {folder.IndexPath}" +
                      (store.FullTextAvailable ? "  (full-text search)" : "  (LIKE fallback - no FTS5)"));
    return 0;
}

static int Where()
{
    var folder = Folder();
    Console.WriteLine($"folder   {folder.Path}");
    Console.WriteLine($"machine  {folder.Machine}");
    Console.WriteLine($"writes   {folder.OwnLog}");
    Console.WriteLine($"index    {folder.IndexPath}");
    Console.WriteLine();

    var logs = folder.AllLogs;
    if (logs.Length == 0)
    {
        Console.WriteLine("no logs yet - nothing has been remembered here");
        return 0;
    }

    foreach (var log in logs)
    {
        var lines = File.ReadLines(log).Count(l => !string.IsNullOrWhiteSpace(l));
        var name = Path.GetFileName(log);
        var mine = string.Equals(log, folder.OwnLog, StringComparison.OrdinalIgnoreCase) ? "  <- this machine" : "";
        Console.WriteLine($"  {lines,6} line{(lines == 1 ? " " : "s")}  {name}{mine}");
    }
    return 0;
}

// ----------------------------------------------------------------------
// Printing
// ----------------------------------------------------------------------

static void Full(Situation situation, RecallResult result)
{
    Console.WriteLine(result.Any
        ? $"{situation.Key} - {result.Atoms.Count} of {result.Considered} remembered"
        : $"{situation.Key} - nothing remembered about this yet");
    Console.WriteLine();

    foreach (var atom in result.Atoms) Block(atom);

    if (result.Tone.Count == 0) return;
    Console.WriteLine("how they like to be worked with");
    foreach (var tone in result.Tone) Console.WriteLine($"  - {tone.Text}");
}

static void Brief(Situation situation, RecallResult result)
{
    // The prompt-sized form: one line each, inside the budget, marked so the
    // thing that failed reads as a warning rather than as advice.
    Console.WriteLine(result.Any ? situation.Key : $"{situation.Key} - nothing remembered yet");
    foreach (var atom in result.Atoms)
    {
        var mark = atom.Failed ? "!" : atom.IsStale ? "?" : "-";
        var tally = atom.Corrections > 0 ? $" ({atom.Corrections}x)" : "";
        Console.WriteLine($"{mark} {atom.Kind.ToString().ToLowerInvariant()} {Short(atom.Id)}{tally}: {atom.Text}");
    }
    foreach (var tone in result.Tone) Console.WriteLine($"~ {tone.Text}");
}

static void Block(MemoryAtom atom, bool full = false, string indent = "  ")
{
    var flags = new List<string>();
    if (atom.Failed) flags.Add("FAILED");
    if (atom.Outcome == DecisionOutcome.Open) flags.Add("open");
    if (atom.IsStale) flags.Add("did not verify");
    if (atom.Corrections > 0) flags.Add($"corrected {atom.Corrections}x");
    if (!atom.IsCurrent) flags.Add("superseded");

    var head = $"{indent}{atom.Kind.ToString().ToLowerInvariant()}  {Short(atom.Id)}";
    if (atom.Subject is { Length: > 0 } subject) head += $"  {subject}";
    if (flags.Count > 0) head += "  " + string.Join(", ", flags);
    Console.WriteLine(head);

    if (atom.Challenge is { Length: > 0 } challenge)
        Console.WriteLine($"{indent}  came up: {challenge}");
    Console.WriteLine($"{indent}  {atom.Text}");

    var where = atom.Machine is { Length: > 0 } m ? $" on {m}" : "";
    Console.WriteLine($"{indent}  {Ago(atom.RecordedAtUtc)}{where}");

    if (full && atom.Verify is { Length: > 0 } verify)
        Console.WriteLine($"{indent}  check: {verify}");
    if (full)
        Console.WriteLine($"{indent}  id: {atom.Id:N}");

    Console.WriteLine();
}

static string Ago(DateTimeOffset when)
{
    var span = DateTimeOffset.UtcNow - when;
    if (span < TimeSpan.FromMinutes(2)) return "just now";
    if (span < TimeSpan.FromHours(1))   return $"{(int)span.TotalMinutes} minutes ago";
    if (span < TimeSpan.FromDays(1))    return $"{(int)span.TotalHours} hours ago";
    if (span < TimeSpan.FromDays(14))   return $"{(int)span.TotalDays} days ago";
    if (span < TimeSpan.FromDays(60))   return $"{(int)(span.TotalDays / 7)} weeks ago";
    return when.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}

// ----------------------------------------------------------------------
// Plumbing
// ----------------------------------------------------------------------

static MemoryFolder Folder()
{
    var path = Environment.GetEnvironmentVariable("CIRCLEAI_MEMORY");
    if (string.IsNullOrWhiteSpace(path))
        path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".circleai", "memory");

    var machine = Environment.GetEnvironmentVariable("CIRCLEAI_MACHINE");
    return new MemoryFolder(path, string.IsNullOrWhiteSpace(machine) ? null : machine);
}

static (MemoryFolder Folder, MemorySync Sync) Open()
{
    var folder = Folder();
    return (folder, new MemorySync(folder));
}

static MemoryAtom Build(string text, Dictionary<string, string> opts) => new()
{
    Kind      = KindOf(opts),
    Text      = text,
    Subject   = opts.GetValueOrDefault("about"),
    Challenge = opts.GetValueOrDefault("challenge"),
    Outcome   = OutcomeOf(opts) ?? (KindOf(opts) == AtomKind.Decision ? DecisionOutcome.Resolved : null),
    Verify    = opts.GetValueOrDefault("verify"),
};

static AtomKind KindOf(Dictionary<string, string> opts) =>
    opts.GetValueOrDefault("kind") is { Length: > 0 } k &&
    Enum.TryParse<AtomKind>(k, ignoreCase: true, out var kind)
        ? kind
        : AtomKind.Decision;

static DecisionOutcome? OutcomeOf(Dictionary<string, string> opts) =>
    opts.GetValueOrDefault("outcome") is { Length: > 0 } o &&
    Enum.TryParse<DecisionOutcome>(o, ignoreCase: true, out var outcome)
        ? outcome
        : null;

// An atom from the front of its id.
//
// NOBODY TYPES A GUID, and a model that has to echo one back spends thirty
// characters to say a thing it could say in eight. So any unique prefix works,
// and an ambiguous one is told which ones it matched rather than picking for
// itself.
static async Task<MemoryAtom?> Resolve(IAtomStore store, string prefix)
{
    var cleaned = prefix.Replace("-", "").Trim().ToLowerInvariant();

    if (Guid.TryParse(prefix, out var exact) &&
        await store.GetAsync(exact) is { } found)
        return found;

    // Superseded ones included: correcting a correction is normal, and so is
    // asking to see one that has already been replaced.
    var matches = (await store.AllAsync(includeSuperseded: true, limit: 5000))
        .Where(a => a.Id.ToString("N").StartsWith(cleaned, StringComparison.OrdinalIgnoreCase))
        .ToList();

    switch (matches.Count)
    {
        case 1: return matches[0];
        case 0:
            Console.Error.WriteLine($"memory: nothing here starts with '{prefix}'");
            return null;
        default:
            Console.Error.WriteLine($"memory: '{prefix}' matches {matches.Count} atoms:");
            foreach (var m in matches.Take(10))
                Console.Error.WriteLine($"  {Short(m.Id)}  {m.Text}");
            return null;
    }
}

static string Short(Guid id) => id.ToString("N")[..8];

static string Kinds() =>
    string.Join(", ", Enum.GetNames<AtomKind>().Select(n => n.ToLowerInvariant()));

static int Number(Dictionary<string, string> opts, string name, int fallback) =>
    opts.GetValueOrDefault(name) is { Length: > 0 } raw &&
    int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
        ? n
        : fallback;

// Free text and --options, in whichever order they arrived. Flags with no
// value (--brief) map to themselves so a plain ContainsKey reads as the
// question being asked.
static (string Text, Dictionary<string, string> Options) Parse(string[] argv)
{
    var words = new List<string>();
    var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < argv.Length; i++)
    {
        if (!argv[i].StartsWith("--", StringComparison.Ordinal))
        {
            words.Add(argv[i]);
            continue;
        }

        var name = argv[i][2..];
        if (name.IndexOf('=') is > 0 and var eq)
        {
            opts[name[..eq]] = name[(eq + 1)..];
        }
        else if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            opts[name] = argv[++i];
        }
        else
        {
            opts[name] = name;
        }
    }

    return (string.Join(" ", words), opts);
}

static int Complain(string message)
{
    Console.Error.WriteLine($"memory: {message}");
    return 2;
}

static void Usage() => Console.WriteLine("""
    memory - what this machine and the others already worked out

      remember <what>            record a decision, ruling, fact, preference or relationship
        --about <subject>          the situation key: deploy:android, language:count
        --challenge <what came up> what prompted it; this is what search searches
        --kind <kind>              decision (default), ruling, fact, preference, relationship
        --outcome <outcome>        resolved (default for a decision), open, failed
        --verify <command>         how to re-check a fact later

      recall                     what bears on what you are about to do
        --doing <verb> --to <target> --with <tool>   the situation
        <free text>                anything else about it
        --brief                    one line each, for a prompt
        --limit <n> --chars <n>    the budget (5 atoms, 600 characters)

      correct <id> <what>        supersede an atom; the original stays readable
      failed  <id> [why]         record that a decision did not hold
      list    [--kind k] [--about s] [--limit n]
      show    <id>               everything about one atom, and what replaced it
      sync                       rebuild the on-disk index the app reads
      where                      which folder, which machine, how many lines

    Ids are any unique prefix - eight characters is plenty.

    The memory lives in $CIRCLEAI_MEMORY, or ~/.circleai/memory. Point that at a
    directory inside a git repository and three machines share one memory: each
    writes only its own log, so there is never a merge to resolve.
    """);
