# CircleAI.Identity

Identity primitives — `CircleIdentity`, `RegisteredDevice`, biometric
profile storage (`IBiometricStore`), and the identity tiering used by
KYC-aware features (no-KYC / basic / verified).

```bash
dotnet add package CircleAI.Identity
```

```csharp
using CircleAI.Identity;

IBiometricStore store = new FileSystemBiometricStore("./biometrics");
await store.SaveAsync(new BiometricProfile(identityId, kind: "face", payload: bytes), ct);
var profile = await store.GetAsync(identityId, ct);
```

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
