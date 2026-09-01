# Go parity exclusions

The Go port is measured by matching C# public type names against the types the
port declares.

**The rule this file exists to serve:** a port is done when every C# public type
is either ported or written down here with a reason. No self-chosen percentage
thresholds.

`go/tools/parity_go.py` reads the machine-readable blocks below, so this document
IS the measure's configuration. A type listed here stops being counted as
missing; adding a line is therefore a claim on the record, not a way to make a
number go up quietly.

A `Module.*` line excludes a whole module, which is what a platform head
actually is.

---

## What Go does differently, and why it is not an omission

Go has one package here where C# has 166 projects, no exceptions, no
inheritance, and no generics beyond type parameters. Four consequences run
through the port:

- **One namespace, so collisions rename.** Two modules cannot both keep a
  same-named type. The loser takes its module as a prefix, and the module word
  is not repeated when it is already inside the name: `PiperVoiceConfig` is
  `VoicePiperConfig`, never `VoicePiperVoiceConfig`.
- **Interfaces KEEP the `I`.** Not idiomatic Go, and deliberate: renaming 166
  modules' worth of interfaces to the `-er` convention by hand is 166 chances to
  rename two of them to the same thing, in a package where that is a compile
  error rather than a shrug. The measure accepts both spellings.
- **An exception becomes an error value.** Every `...Exception` is an error
  type or a sentinel, so the word "Exception" does not survive.
- **A `...EventArgs` becomes a payload struct.** Go has no events; the fields
  are what a callback receives, so the suffix goes.

---

## Platform heads

Go cannot be the platform head for a .NET or Android surface. These are whole
modules rather than scattered types, and none of them is a decision — they are
bindings to a runtime Go is not running inside.

```excluded
Maui.*                               .NET MAUI bindings — Android services, MAUI capture, MAUI push
Device.*                             Android Binder/AIDL, framework callbacks, a bound foreground service
WindowsAutomation.UIAutomationDriver UI Automation is a Windows COM API
```

**Everything else stays in.** Go has `net/http`, so `Inference.Server`,
`Hosting.Mcp` and `Web` are ordinary work rather than exclusions — the C port
excluded them because C has no managed web host, and that reasoning does not
transfer. `Desktop` is likewise ported: C excluded it as a Windows shell
binding, but what the module actually contains is data structures and a board,
which Go has no trouble with. `Memory.Sql` is ported against `database/sql`.

Copying another port's exclusions without re-deriving them is how a port claims
to be finished for reasons that were never true of it.

---

## Renames the measure has to be told about

The measure knows the ordinary conventions — the `I` prefix, the module prefix,
`EventArgs`, `Exception`. A handful of names need telling explicitly.

```renames
```

---

## Still owed

Everything not listed above and not yet ported is real remaining work, and the
measure counts it. Run:

```bash
python3 go/tools/parity_go.py --full
```
