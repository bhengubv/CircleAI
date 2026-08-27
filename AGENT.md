# Universal Local Long-Term Memory

What it is, what it promises, and how any model — remote or local — uses it.

The short version every session needs is [MEMORY.md](MEMORY.md). This is the
reasoning behind it.

---

## The problem it exists for

A session ends and everything it worked out is gone. The next one re-derives
it, or worse, repeats the mistake that produced it. The cost is always paid
twice: once to learn the thing, once to learn it again.

The usual answer is a cloud memory service. That answer is wrong here for two
reasons. It puts what somebody worked out on somebody else's machine, and it
only works for the models that speak its API — which is exactly the set of
models that already have the best memory. A local memory serves the phone in
somebody's hand, and it serves every model equally.

## What it promises

**Recall, not enforcement.** It answers when asked and it never blocks. There
is no gate, no policy check, no refusal. An agent that has to argue with its
memory turns it off, and a memory that is off remembers nothing. Everything
below follows from that: it must be cheap to ask, honest when it knows
nothing, and never in the way.

**Nothing leaves the machine.** No API, no account, no network path. The store
is a text file and a SQLite index.

**Any model, any machine.** The interface is a shell command, because that is
the one interface every model already has. A local llama with tool use reaches
it the same way Claude does.

**It is auditable and it is text.** A person can open the log, read it, fix a
line, and delete one. Nothing is hidden in a binary, and nothing is inferred
that cannot be traced to the line that said it.

## What it is not

- Not a rule engine. It does not decide what you may do.
- Not a vector database. Embeddings improve recall; they must never be what
  enables it, or it stops working on the phone this is for.
- Not a transcript. Raw turns are the episode layer underneath. This layer is
  what those turns settled into.
- Not shared with anybody. Three of your machines, not three people.

---

## What gets remembered

An **atom** is one thing worth remembering, in one of five kinds:

| Kind | What it is | Ranked |
|---|---|---|
| `ruling` | A standing rule. "Never restart a device without asking." | 1.00 |
| `decision` | What was decided about a specific challenge, and how it turned out. | 0.90 |
| `fact` | Something true that can go stale. Carries a `--verify` command. | 0.80 |
| `preference` | How somebody likes things done. | 0.55 |
| `relationship` | How to work with them. Always returned, whatever the subject. | tone |

`decision` is the default and the reason the layer exists: **what was done,
what it cost, and whether it held**. A decision carries the **challenge** that
prompted it and an **outcome** — `resolved`, `open`, or `failed`.

Kinds are open on purpose. A new kind is an enum value, a score, and nothing
else; the log format and the sync do not change.

### The challenge is the searchable half

`--challenge` is what came up. `text` is what was decided. Search runs against
the challenge, because the question anybody actually asks is *"have we been
here before"*, and that is asked about the problem, not the answer.

```bash
memory remember "Use -t:InstallKeepingData when iterating" \
       --about deploy:android \
       --challenge "-t:Install wiped 817 MB of models on every deploy"
```

### A road already closed ranks near the top

An atom marked `failed` is scored **up**, not down. Knowing what was tried and
did not work is worth as much as knowing what worked, and it arrives too late
by default — the whole cost of a repeated mistake is paid before anybody
remembers making it the first time.

An atom that had to be corrected repeatedly also ranks up. Something said four
times is what somebody most needs put in front of them.

### Subjects are situation keys

`--about deploy:android`, `--about language:zu`, `--about device:p30`. Recall
matches the subject of what you are about to do against the subject of the
atom, and rolls up a slash-delimited target: recalling `--doing deploy --to
android/p30` also finds atoms filed under `deploy:android`, and under `deploy`.
Free text is searched too, but the subject is what makes the right atom arrive
before the wrong one. Without the roll-up an atom filed one level up is
invisible to the situation it was written for, which is how a store fills with
things nobody sees again.

---

## Installing it

Two lines, the same on Linux, Windows and the Mac:

```bash
dotnet pack tools/memory -c Release -o .artifacts
dotnet tool install --global --add-source .artifacts CircleAI.Tools.Memory
```

That puts `memory` on PATH. Recall costs about 180 ms, which is inside any
agent's budget for a question it should be asking anyway.

Then point it at a shared folder — a directory inside a git repository, or a
symlink to one:

```bash
export CIRCLEAI_MEMORY=~/CircleMemory        # in the shell profile on each machine
memory where
```

`dotnet tool update --global --add-source .artifacts CircleAI.Tools.Memory`
after a change; `dotnet tool uninstall --global CircleAI.Tools.Memory` to
remove it.

## Using it

```bash
memory recall --doing deploy --to android [--with dotnet] [free text] [--brief]
memory remember "<what>" --about <subject> --challenge "<what came up>" \
                [--kind ruling|decision|fact|preference|relationship] \
                [--outcome resolved|open|failed] [--verify "<command>"]
memory correct <id> "<what it should have said>"
memory failed  <id> "<what went wrong>"
memory list [--kind k] [--about s] [--all]
memory show <id>          # everything about one atom, and what replaced it
memory sync               # rebuild the on-disk index the app reads
memory where              # which folder, which machine, how many lines
```

Ids are any unique prefix — eight characters is plenty.

`--brief` is the prompt-sized form: one line per atom, `!` for a road that
failed, `?` for a fact that did not verify, `~` for tone. It respects the
budget (5 atoms, 600 characters by default).

**Correcting never deletes.** A correction is a new atom that supersedes the
old one; the old one stops being an answer and stays readable. `memory failed`
keeps what was decided and records why it did not hold — marking a rule
breached must not erase the rule.

---

## Three machines, one memory

Linux, Windows and a Mac all see the same store, and it travels by git. That
decides the layout, not taste:

```
$CIRCLEAI_MEMORY/          # or ~/.circleai/memory
  atoms.linux-box.jsonl      one writer per file - never a conflict
  atoms.windows-desk.jsonl
  atoms.mac-build.jsonl
  index.{machine}.db         derived, gitignored, thrown away without loss
```

**A SQLite file cannot be committed.** Git cannot merge a binary blob, and the
only resolutions it offers — keep mine, keep theirs — each destroy half the
memory. So the durable half is an append-only text log, and there is one per
machine: a file with a single writer can never conflict, which is a stronger
guarantee than any merge strategy.

**Append-only changes the model.** A row can be `UPDATE`d to say it was
superseded; a line already written cannot. So a correction is a new line naming
what it **supersedes** — pointing backwards — and replay derives the forward
pointer by walking every machine's lines in time order. That is also what makes
a correction made on the Mac apply to a decision made on Windows: two lines in
one ordered stream, not two databases arguing.

**The index is disposable.** It is rebuilt from the logs, so a corrupt index, a
schema change, or a machine that has never seen the folder all cost the same
thing: a rebuild. The CLI replays on every run rather than trusting a cache —
a command that answers from a stale index after a `git pull` looks like it
remembered and did not.

**Set it up** by pointing `$CIRCLEAI_MEMORY` at a directory inside a git
repository — a symlink works — on each machine. `$CIRCLEAI_MACHINE` overrides
the machine name if two boxes would otherwise collide.

---

## Beyond SQLite

SQLite is the default and the only one that matters first: no server, ships
inside the app, and the only option on a phone. PostgreSQL, SQL Server, MySQL
and Oracle are the shared case — a team, or a machine somebody already runs.

`CircleAI.Memory.Sql` is that store, and it **references no driver**. The caller
hands in an open `DbConnection`, which keeps Oracle's client out of a phone
build, keeps Npgsql out of a SQL Server deployment, and makes an engine nobody
here anticipated a `SqlDialect` rather than a package we have to ship.

```csharp
await using var conn = new NpgsqlConnection(connectionString);
var store = new AdoAtomStore(conn, SqlDialect.PostgreSql);
```

| Engine | Keyword search |
|---|---|
| PostgreSQL | a generated `tsvector` column and a GIN index, both in the schema |
| MySQL / MariaDB | a `FULLTEXT` index on InnoDB |
| SQL Server | `LIKE` — `CONTAINS` needs the Full-Text feature installed on the instance |
| Oracle | `LIKE` — Oracle Text needs a CTXSYS index and a privilege a memory should not ask for |

`LIKE` is the floor, not an excuse: it is worse at ranking and perfectly capable
of finding things. A store that refused to start because an optional index
would not build would be the worse outcome. `AdoAtomStore.FullTextAvailable`
says which one ran.

**Status, plainly.** The shared implementation is run end to end against a real
engine — SQLite through the same ADO path, the same `DbConnection`,
`DbCommand` and `DbDataReader` the other four use. What each dialect emits is
checked as SQL. Neither is a live PostgreSQL, SQL Server, MySQL or Oracle
server: until one of those has been pointed at a real instance, treat the four
as **written and unproven**. Nothing else changes when they are — the log is
still the durable half, and any engine is still an index over it.

---

## Where the code is

| | |
|---|---|
| `src/CircleAI.Memory/MemoryAtom.cs` | the kinds and what an atom carries |
| `src/CircleAI.Memory/Situation.cs` | subject keys, roll-up, recall budget |
| `src/CircleAI.Memory/IAtomStore.cs` | the seam every engine implements |
| `src/CircleAI.Memory/SqliteAtomStore.cs` | FTS5, with a LIKE fallback that is not an excuse |
| `src/CircleAI.Memory/Recall.cs` | what ranks, and why |
| `src/CircleAI.Memory/MemoryFolder.cs` | paths, machine identity, gitignore |
| `src/CircleAI.Memory/AtomLog.cs` | the line format — this outlives the code |
| `src/CircleAI.Memory/MemorySync.cs` | log-then-index, and replay |
| `tools/memory/` | the command |
| `tests/CircleAI.Tests/RecallTests.cs` | what recall owes |
| `tests/CircleAI.Tests/MemorySyncTests.cs` | what three machines owe |
