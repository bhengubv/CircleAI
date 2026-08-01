# CircleAI on Android

A working phone app built on CircleAI. Everything runs on the device: no account,
no server, no data leaving the handset.

The benchmark is a **Huawei P30 Lite** — a 2019 phone with 3.7 GB of RAM, usually
about 1.3 GB of it free. If something does not work there, it does not work.

## What it does

| Screen | What happens |
|---|---|
| **Speak in 71 languages** | Pick a language, the voice downloads once, the phone says a greeting out loud. Synthesis is entirely local. |
| **What it can do** | Reads the phone's real RAM and storage and reports which models fit. Needs no model and no network, so it answers immediately. |
| **Read an image** | Runs a small vision model on a picture, on the device. |
| **Use the mic** | Wake word → microphone → answer → spoken reply. |
| **Type a message** | Chat with a model chosen to fit this phone. It remembers what you tell it, on the device. |

## Running it

```bash
dotnet build samples/CircleAI.Samples.It.Android/CircleAI.Samples.It.Android.csproj \
  -c Release -f net10.0-android -m:1 \
  -p:ItVoiceOnAndroid=true -p:EmbedAssembliesIntoApk=true
```

Two flags matter and both are easy to forget:

- `-p:ItVoiceOnAndroid=true` — without it the entire voice path is compiled out.
  The app still builds and installs; the voice features are simply absent, with no
  error to tell you why.
- `-p:EmbedAssembliesIntoApk=true` — a Debug APK without it crashes on launch.

Install with `adb install -r` rather than uninstalling first, unless you mean to
delete everything the app has downloaded — uninstalling also removes its files in
`/sdcard/Android/data/`.

## Speech needs a second app

espeak-ng is GPL-3.0 and CircleAI is permissively licensed, so it is never linked
in. Languages whose spelling does not map to sound need it, and it runs in a
**separate app** (`com.bhengubv.espeakng`) that CircleAI talks to across a process
boundary. Without that app installed those languages report why and stay silent;
everything else still speaks.

Most of the 71 do not need it at all — they are driven by graphemes, a shipped
lexicon, or a transliterator, all of which run in-process.

## Where the voices come from

The catalogue points at
[thegeekco/circleai-voices](https://huggingface.co/thegeekco/circleai-voices), one
bucket holding every voice. Each is downloaded on demand and checked against a
SHA-256 recorded in the catalogue, so a truncated or altered file is rejected
rather than played.

None of the voices are ours. They were published by other people, and the bucket's
README credits every one and names its individual licence — several are
non-commercial.

## Reading the code

- `MainActivity.cs` — the chat screen and the feature buttons.
- `LanguagePickerActivity.cs` — the language list, and the one place that shows the
  select → download → verify → speak chain end to end.
- `Ui.cs` — colours, spacing and the button/label helpers.
- `Permissions.cs` — INTERNET and RECORD_AUDIO, declared as assembly attributes.
  There is no `<AndroidPermission>` MSBuild item; writing one adds nothing and
  warns about nothing, which is how this app shipped with no INTERNET permission
  and nobody noticed until the first download was attempted.

The shared, platform-neutral logic lives in `../CircleAI.Samples.It/` and is the
same code the desktop console runs.

## Known limitations

- The chat model does not currently load on the P30 Lite (`MNN model load failed`).
  The voice, capability and vision paths are unaffected. The app says so plainly
  instead of showing a stack trace.
- Two voices carry no upstream licence at all (Cantonese, Japanese) and one has
  contradictory metadata (Indonesian). They are marked in the bucket README rather
  than quietly shipped as though resolved.
- Pashto, Wolof, Tshiluba and Kikongo have no usable model published anywhere yet.
