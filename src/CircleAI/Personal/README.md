# CircleAI.Personal

Personal-data layer — consent, opt-in scoping, and the typed surfaces
the Companion uses when answering questions about the user's own life
(calendar, contacts, location history, health metrics, etc.).

```bash
dotnet add package CircleAI.Personal
```

```csharp
using CircleAI.Personal;

IPersonalConsentStore consent = new InMemoryPersonalConsentStore();
await consent.GrantAsync(identityId, scope: PersonalScope.Health, ct);
var hasConsent = await consent.HasConsentAsync(identityId, PersonalScope.Health, ct);
```

See [docs/ARCHITECTURE.md](https://github.com/bhengubv/CircleAI/blob/master/docs/ARCHITECTURE.md).
