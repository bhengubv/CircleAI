# HarmonyOS parity exclusions

This file is not a note. `harmonyos/tools/parity_ets.py` **reads it** — a name
listed here is counted as present. Adding a line raises the number, so a line is
a claim on the record, and each one carries the reason it was made.

A name must be **module-qualified** (`Module.TypeName`) or a whole-module
wildcard (`Module.*`). An unqualified name would excuse a same-named type in
every other module too, which is how an exclusions file quietly becomes a way of
not porting things.

## What is excluded, and why

Four modules are excluded, and every one of them is a **head for a platform that
is not this one**. The reasons are re-derived here rather than copied from
another port's file, because "the Rust port excluded it" is not a reason for
HarmonyOS to.

**`Maui.*`** — .NET MAUI is a C# UI framework. Every type in this module is a
binding to one of its pages, permission prompts or background services:
`MauiAudioCapture`, `MauiCameraCapture`, `MauiPushSender`, `AlwaysOnService`.
HarmonyOS has its own UI framework, its own permission model and its own
background execution rules, and a MAUI page has no counterpart in any of them.
An ArkTS class called `MauiCameraCapture` would be a name with no behaviour
behind it, which is worse than its absence — it would report as ported.

The *capability* is not excluded. Audio capture, camera capture and push all
belong in the HarmonyOS tree, reached through `@ohos.multimedia.audio`,
`@ohos.multimedia.camera` and `@ohos.pushService`, under names that say so.
What is excluded is the MAUI binding.

**`Device.*`** — Android platform interop: `AndroidDeviceMemory`,
`AndroidMemoryPressure`, `DeviceMemoryProbe`, `ResidentListening`. These call
Android's `ActivityManager` through the Java runtime and register an Android
foreground service. HarmonyOS reads memory through `@ohos.app.ability.appManager`
and keeps work alive with a background task or a `ServiceExtensionAbility` —
different APIs, a different lifecycle, and a different set of things the system
will refuse. The memory *seam* itself is ported and the HarmonyOS probe fills it;
this module is the Android side of it.

**`Desktop.*`** — a Windows/macOS desktop shell (`DesktopCompanionAdapter`,
`DesktopPrimitives`). HarmonyOS runs on phones, tablets and watches, and its
tablet and PC forms are ArkUI applications rather than Win32 or AppKit ones. A
desktop shell here would be a shell for an operating system this package does not
target.

**`WindowsAutomation.*`** — drives the Windows UI Automation COM API to read and
click another application's window tree. HarmonyOS has no COM, and its
accessibility surface (`@ohos.accessibility`) is a permissioned service for
assistive use rather than an automation API — driving another app's UI through it
is not something the platform grants, and building towards it would be building
towards a permission that will not be given.

```excluded
Maui.*
Device.*
Desktop.*
WindowsAutomation.*
```

## Renames

A rename is recorded when a type is genuinely present under a different name —
not as a way of matching two unrelated things.

None are recorded yet. ArkTS is TypeScript for naming purposes, and the ruler
already accepts the conventions this port actually uses without needing a line
here: the dropped `I` prefix on interfaces, `camelCase` for a static class that
became a module of functions, `FooError` for a C# `FooException`, a payload
interface for an `EventArgs`, and `Ability` / `UIAbility` / `ExtensionAbility`
where a C# service or activity arrived as the HarmonyOS component that does the
same job.

```renames
```
