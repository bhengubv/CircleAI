// INTERNET, for fetching models from the catalogue. NOT guarded: a chat-only build
// downloads models too, and this is the permission that decides whether the phone
// can reach anything at all.
//
// It was missing entirely until 2026-07-31, and nothing caught it because every
// voice had been pushed over adb — the app had never opened a socket. The first
// real download died as SocketException(13) "Permission denied", which reads like a
// filesystem or server problem and is neither.
//
// The base AndroidManifest.xml claimed this arrived "from MSBuild items". It did
// not, for exactly the reason spelled out below about RECORD_AUDIO: there is no
// <AndroidPermission> build item, so writing one adds nothing and warns about
// nothing. Both permissions now come from attributes, which do work.
[assembly: Android.App.UsesPermission("android.permission.INTERNET")]
[assembly: Android.App.UsesPermission("android.permission.ACCESS_NETWORK_STATE")]

// The resident device service (CircleNeuronService) runs in the FOREGROUND, and
// on Android that is a permission, not a choice:
//
//   FOREGROUND_SERVICE            API 28+. Without it startForegroundService
//                                 throws and the models never load.
//   FOREGROUND_SERVICE_DATA_SYNC  API 34+. From 14 the TYPE must be declared
//                                 separately or the start is refused outright.
//   POST_NOTIFICATIONS            API 33+. A foreground service must show a
//                                 notification; denied, the service can still run
//                                 but the user has no way to see it is holding
//                                 their RAM, which is the thing the notification
//                                 exists to be honest about.
//
// Declared unguarded because the device service is the point of the arrangement —
// a build that cannot host it is not a smaller build, it is a broken one.
[assembly: Android.App.UsesPermission("android.permission.FOREGROUND_SERVICE")]
[assembly: Android.App.UsesPermission("android.permission.FOREGROUND_SERVICE_DATA_SYNC")]
[assembly: Android.App.UsesPermission("android.permission.POST_NOTIFICATIONS")]

#if IT_VOICE_ANDROID

// Permissions.cs
//
// RECORD_AUDIO for the hands-free voice loop (wake word -> mic -> Whisper). Declared
// the real .NET Android way — an assembly-level UsesPermission attribute — because
// the permission MUST be in the manifest for RequestPermissions() to work at runtime:
// without the manifest entry the request is a silent no-op and AudioRecord returns
// nothing but zeroes, so the loop looks broken rather than blocked.
//
// Guarded by IT_VOICE_ANDROID so it is compiled ONLY into the voice build — a
// chat-only APK must never ask for the microphone.
//
// This replaces a `<AndroidPermission Include="android.permission.RECORD_AUDIO" />`
// item in the .csproj, which was a NO-OP: <AndroidPermission> is not a recognized
// .NET Android build item, so the permission never actually reached the manifest.

[assembly: Android.App.UsesPermission("android.permission.RECORD_AUDIO")]

#endif
