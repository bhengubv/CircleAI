# TypeScript parity exclusions

This file is not a note. `typescript/tools/parity_ts.py` **reads it** — a name
listed here is counted as present. Adding a line raises the number, so a line is
a claim on the record, and each one carries the reason it was made.

A name must be **module-qualified** (`Module.TypeName`) or a whole-module
wildcard (`Module.*`). An unqualified name would excuse a same-named type in
every other module too, which is how an exclusions file quietly becomes a way of
not porting things.

## What is excluded, and why

**`Maui.*`** — .NET MAUI is a C# UI framework. Every type in it is a bridge to
a MAUI page, a MAUI permission prompt, or a MAUI background service:
`MauiAudioCapture`, `MauiCameraCapture`, `MauiPushSender`, `AlwaysOnService`.
There is no TypeScript on the other side of any of those APIs. A TypeScript
"port" of `MauiCameraCapture` would be a class with the same name that does
nothing, which is worse than its absence — it would report as ported.

The *capability* is not excluded. Audio capture, camera capture and push are
reachable from TypeScript through the browser and Node, and they belong in a
web head under their own names. What is excluded is the MAUI binding.

**`Device.*`** — Android platform interop: `AndroidDeviceMemory`,
`AndroidMemoryPressure`, `DeviceMemoryProbe`, `ResidentListening`. These read
`/proc/meminfo` and Android's `ActivityManager`, and register a foreground
service. TypeScript running in a browser cannot read either, and TypeScript
running in Node on a phone is not how this ships. The memory *seam* exists in
the port; this module is the Android side of it.

**`Desktop.*`** — a Windows/macOS desktop shell (`DesktopCompanionAdapter`,
`DesktopPrimitives`). The TypeScript port's head is the web, not a desktop
window manager.

**`WindowsAutomation.*`** — drives the Windows UI Automation API. There is no
TypeScript binding to UIA and there is no reason to write one: a browser cannot
reach another application's window tree, and it should not be able to.

## The line these four share

Each is a **platform head**, not a capability. The thing they do is done
elsewhere in the TypeScript port under a name that suits the platform it runs
on, and a same-named shell would make the measure read as complete while the
work was not.

Nothing else is excluded. Everything else the C# declares is either ported or
still counted as missing.

```excluded
Maui.*
Device.*
Desktop.*
WindowsAutomation.*
```

## Renames

A rename is recorded when a type is genuinely present under a different name —
not as a way of matching two unrelated things. None are recorded yet: the
TypeScript port drops the `I` prefix on interfaces and camel-cases what became a
function, and the ruler accepts both spellings directly rather than needing a
line here for each one.

```renames
```
