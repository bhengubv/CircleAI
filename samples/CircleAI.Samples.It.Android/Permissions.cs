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
