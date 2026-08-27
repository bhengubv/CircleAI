# CircleAI

## Ask the memory first

There is a local long-term memory on this machine and it already holds what was
worked out here — decisions, standing rules, and the roads that turned out to be
closed. **Ask it before starting something that might have been done before, and
tell it when something is decided, resolved, or fails.**

```bash
memory recall --doing deploy --to android --brief
```

It answers in about 180 ms, it never blocks, and an empty answer exits zero.
[MEMORY.md](MEMORY.md) is the short version; [AGENT.md](AGENT.md) is the
contract. If `memory` is not on PATH, the two install lines are in AGENT.md.

## Building and testing

```bash
dotnet build src/CircleAI.Memory/CircleAI.Memory.csproj
```

```bash
dotnet test tests/CircleAI.Tests/CircleAI.Tests.csproj -f net10.0
```

Everything multi-targets **net9.0 and net10.0**, and the test project runs both
legs. Run one framework while iterating; run both before calling it done — a
green net10 leg has hidden a net9 break before.

The suite is around 2,700 tests and takes about 45 seconds per leg. Serialise
heavy builds rather than running two at once.

## Where things are

| | |
|---|---|
| `src/CircleAI.*` | ~150 libraries; `CircleAI.Core` holds the interfaces the rest build on |
| `src/CircleAI.Memory` | episodes, atoms, recall, the append-only log |
| `src/CircleAI.Memory.Sql` | the same store on PostgreSQL, SQL Server, MySQL, Oracle |
| `tools/` | small runnable programs — `memory`, `voice-audit`, `tts-speak`, `stt-hear` |
| `samples/CircleAI.Samples.It.Hybrid` | the IT! sample: MAUI Blazor hybrid and web off one shared Razor library |
| `tests/CircleAI.Tests` | one project, both frameworks |

`tools/` projects are not in the solution. Run them with
`dotnet run --project tools/<name> -- <args>`.

## Two things that cost a day each

**Deploying to Android wipes the app's data.** With
`EmbedAssembliesIntoApk=true`, `-t:Install` uninstalls first — 817 MB of
downloaded models gone on every deploy, whatever `AndroidPreserveUserData` says.
Use the `InstallKeepingData` target while iterating and `-t:Install` only for a
genuine first run.

**One fact with two owners always ends up with two answers.** The language count,
the model choice, the wake phrase and the app language each lived in three or
four places in the sample, and every one of them disagreed. When adding
something a screen displays, find who else displays it and make them read the
same source.
