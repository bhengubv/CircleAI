# Rust parity exclusions

This file is not a note. `rust/tools/parity_rs.py` **reads it** — a name listed
here is counted as present. Adding a line raises the number, so a line is a
claim on the record, and each one carries the reason it was made.

A name must be **module-qualified** (`Module.TypeName`) or a whole-module
wildcard (`Module.*`). An unqualified name would excuse a same-named type in
every other module too, which is how an exclusions file quietly becomes a way of
not porting things.

## What is excluded, and why

**`Maui.*`** — .NET MAUI is a C# UI framework, and every type in this module is
a binding to one of its pages, permission prompts or background services:
`MauiAudioCapture`, `MauiCameraCapture`, `MauiPushSender`, `AlwaysOnService`.
There is no MAUI on the other side of a Rust FFI boundary. A Rust "port" of
`MauiCameraCapture` would be a struct with the right name and no behaviour,
which is worse than its absence — it would report as ported.

The *capability* is not excluded. Audio capture, camera capture and push all
belong in the Rust tree under names that suit the platform they run on; what is
excluded is the MAUI binding.

**`Device.*`** — Android platform interop: `AndroidDeviceMemory`,
`AndroidMemoryPressure`, `DeviceMemoryProbe`, `ResidentListening`. These call
Android's `ActivityManager` through the Java runtime and register a foreground
service. Rust reaches Android through JNI, which is a different shape of code
belonging in a different crate — and the memory *seam* itself is ported. This
module is the Java side of it.

**`Desktop.*`** — a Windows/macOS desktop shell (`DesktopCompanionAdapter`,
`DesktopPrimitives`). The Rust crate is a library, not a desktop application,
and the desktop head is C#.

**`WindowsAutomation.*`** — drives the Windows UI Automation COM API. Reaching
COM from Rust is possible and pointless here: nothing in this system needs to
drive another Windows application's window tree, and the C# head already does it
where it is wanted.

## The line these four share

Each is a **platform head**, not a capability. What they do is done elsewhere in
the Rust tree under a name that suits the platform it runs on, and a same-named
shell would make the measure read as complete while the work was not.

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
not as a way of matching two unrelated things. None are recorded yet: Rust drops
the `I` prefix on traits, spells modules and functions in `snake_case`, spells
constants in `SCREAMING_SNAKE`, and names an error type `FooError` rather than
`FooException`. The ruler accepts all four conventions directly rather than
needing a line here for each one.

```renames
```
