# Python parity exclusions

The Python port is measured by matching C# public type names against the names
the port declares — classes, top-level functions, and module-level constants,
because a C# static class has no single Python spelling.

**The rule this file exists to serve:** a port is done when every C# public type
is either ported or written down here with a reason. No self-chosen percentage
thresholds.

`python/tools/parity_py.py` reads the machine-readable blocks below, so this
document IS the measure's configuration. A type listed here stops being counted
as missing; adding a line is therefore a claim on the record, not a way to make
a number go up quietly.

A `Module.*` line excludes a whole module, which is what a platform head
actually is.

---

## What Python does differently, and why it is not an omission

- **Real packages, so most types keep their name.** Python has namespaces where
  Swift and Go have one flat scope, which is why this port needs far fewer
  renames than those two.
- **Interfaces are ABCs and mostly KEEP the `I`.** `IFarmBoard` is
  `IFarmBoard(ABC)`. Some files drop it; the measure accepts both, and settling
  on one spelling across 166 modules is a rename worth doing deliberately rather
  than as a side effect of measuring.
- **A `...EventArgs` becomes a payload dataclass.** Python has no events, so the
  suffix goes where the name already reads as a noun.
- **An exception stays an exception**, usually keeping its name.
- **snake_case is accepted.** A C# static class that became a module of free
  functions is matched on the snake_case spelling of its name, because that is
  what a Python port of a static helper actually looks like.

---

## Platform heads

Python cannot be the platform head for a .NET or Android surface. These are
whole modules rather than scattered types, and none of them is a decision — they
are bindings to a runtime Python is not running inside.

```excluded
Maui.*                               .NET MAUI bindings — Android services, MAUI capture, MAUI push
Device.*                             Android Binder/AIDL, framework callbacks, a bound foreground service
WindowsAutomation.*                  UI Automation is a Windows COM API
Desktop.*                            binds a Windows desktop shell
```

**Everything else stays in.** Python has HTTP servers, so `Inference.Server`,
`Hosting.Mcp` and `Web` are ordinary work. `Memory.Sql` is ported against
DB-API. `Testing` is ported — golden files and a frozen clock are exactly the
things a Python port should have.

Copying another port's exclusions without re-deriving them is how a port claims
to be finished for reasons that were never true of it.

---

## Renames the measure has to be told about

```renames
```

---

## Still owed

Everything not listed above and not yet ported is real remaining work, and the
measure counts it. Run:

```bash
python3 python/tools/parity_py.py --full
```
