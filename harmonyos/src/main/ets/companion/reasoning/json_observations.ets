/**
 * companion/reasoning/json_observations
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/reasoning/json_observations.ts
//
// Shared scenario→observations extraction for the world models, ported from
// the private `ExtractObservations` in FrequencyWorldModel / BayesianWorldModel.
//
//   using var doc = JsonDocument.Parse(scenarioJson);
//   if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
//   foreach (var prop in doc.RootElement.EnumerateObject())
//       hits.Add(prop.Name + "=" + prop.Value.ToString());
//
// The one subtle wire detail is `JsonElement.ToString()`, which is NOT the raw
// JSON token. Verified against .NET 10 (System.Text.Json):
//   string  "hello" -> hello          (unescaped, no quotes; empty string -> "")
//   number  5       -> 5              (canonical number text)
//   number  3.14    -> 3.14
//   bool    true    -> True           (capital T)
//   bool    false   -> False          (capital F)
//   null            -> ""             (empty string)
//   object  {"x":1} -> {"x":1}        (raw JSON text)
//   array   [1,2]   -> [1,2]          (raw JSON text)
//
// We reproduce that exactly so the "name=value" observation keys are
// byte-identical to the C# reference, which matters because those keys are the
// dictionary keys the models train and predict on.

/**
 * Parse a scenario JSON object into `"name=value"` observation strings.
 * Returns an empty array when the input is null/blank, not valid JSON, or
 * not a JSON object — mirroring the C# `catch (JsonException)` and the
 * non-object guard.
 */
export function extractObservations(scenarioJson: string | null | undefined): string[] {
  if (scenarioJson == null || scenarioJson.trim().length === 0) return [];
  let root: unknown;
  try {
    root = JSON.parse(scenarioJson);
  } catch {
    return [];
  }
  // JsonValueKind.Object only — arrays, scalars, null all fall through to [].
  if (root === null || typeof root !== "object" || Array.isArray(root)) return [];

  const hits: string[] = [];
  for (const [name, value] of Object.entries(root as Record<string, unknown>)) {
    hits.push(name + "=" + jsonElementToString(value));
  }
  return hits;
}

/**
 * Reproduce `System.Text.Json.JsonElement.ToString()` for a value already
 * parsed by `JSON.parse`. The tricky cases are booleans (`True`/`False`),
 * null (empty string), and nested object/array (raw JSON text via
 * `JSON.stringify`, which — like System.Text.Json — emits compact,
 * key-order-preserving JSON with no spaces).
 */
export function jsonElementToString(value: unknown): string {
  if (value === null || value === undefined) return "";
  switch (typeof value) {
    case "string":
      return value;
    case "boolean":
      return value ? "True" : "False";
    case "number":
      return numberToJsonText(value);
    case "object":
      // Object or array: JsonElement.ToString() returns the raw JSON text.
      return JSON.stringify(value);
    default:
      return String(value);
  }
}

/**
 * Canonical number text matching how System.Text.Json round-trips a number
 * that came in as a JSON token. `JSON.parse`→`JSON.stringify` reproduces the
 * shortest round-trippable form (same as the ECMAScript Number→String
 * algorithm), which matches the numeric text .NET preserves for JSON numbers.
 */
function numberToJsonText(n: number): string {
  // JSON.stringify handles finite numbers; NaN/Infinity can't occur from a
  // parsed JSON number, but guard defensively (JsonElement would never hold
  // them either).
  if (!Number.isFinite(n)) return String(n);
  return JSON.stringify(n);
}
