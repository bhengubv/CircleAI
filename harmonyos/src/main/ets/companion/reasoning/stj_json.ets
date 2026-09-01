/**
 * companion/reasoning/stj_json
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/reasoning/stj_json.ts
//
// A faithful re-implementation of `System.Text.Json.JsonSerializer.Serialize`
// for the exact shape BeliefTrackerTheoryOfMind serialises: a
// `Dictionary<string,double>`. This exists because System.Text.Json's DEFAULT
// output is NOT byte-identical to JavaScript's `JSON.stringify`:
//
//   * String escaping is far more aggressive (the default JavaScriptEncoder
//     escapes '<' '>' '&' '+' '\'' '"' '`' and ALL non-ASCII to \uXXXX with
//     UPPERCASE hex), and uses " for the quote rather than \".
//   * Doubles use .NET's shortest-round-trip with uppercase 'E' and a
//     zero-padded exponent in scientific notation.
//
// The `likelyBeliefJson` field is a wire value the C# reference produces, so we
// reproduce STJ's bytes exactly. The escape table below was captured from
// .NET 10 System.Text.Json for every ASCII code point (see the port notes).

// Short (two-char) escapes STJ uses. Everything else that must be escaped goes
// out as \uXXXX. Captured: \b \t \n \f \r and \\ ; note that '"' does NOT get
// the short \" form — it becomes ".
const SHORT_ESCAPES: Record<number, string> = {
  0x08: "\\b",
  0x09: "\\t",
  0x0a: "\\n",
  0x0c: "\\f",
  0x0d: "\\r",
  0x5c: "\\\\",
};

// ASCII code points (< 0x80) that STJ long-escapes to \uXXXX (in addition to
// all C0 control chars < 0x20, which are always escaped). Captured from the
// default encoder: " & ' + < > ` DEL.
const ASCII_LONG_ESCAPE = new Set<number>([
  0x22, // "
  0x26, // &
  0x27, // '
  0x2b, // +
  0x3c, // <
  0x3e, // >
  0x60, // `
  0x7f, // DEL
]);

function u4(code: number): string {
  // \uXXXX with UPPERCASE hex, matching STJ.
  return "\\u" + code.toString(16).toUpperCase().padStart(4, "0");
}

/**
 * Escape a string body exactly as System.Text.Json's default encoder does
 * (no surrounding quotes). Iterates UTF-16 code units, so astral code points
 * come out as a surrogate pair of \uXXXX escapes — precisely what .NET emits.
 */
export function stjEscape(s: string): string {
  let out = "";
  for (let i = 0; i < s.length; i++) {
    const code = s.charCodeAt(i);
    const short = SHORT_ESCAPES[code];
    if (short !== undefined) {
      out += short;
    } else if (code < 0x20 || code >= 0x80 || ASCII_LONG_ESCAPE.has(code)) {
      // All control chars, all non-ASCII, and the flagged ASCII punctuation.
      out += u4(code);
    } else {
      out += s[i];
    }
  }
  return out;
}

/**
 * Format a double exactly as `JsonSerializer.Serialize(double)` does.
 *
 * For all finite values in normal notation, JavaScript's Number→String
 * (shortest round-trip) equals .NET's, so `JSON.stringify` is reused. The two
 * only diverge in scientific notation, where .NET uses an UPPERCASE 'E' and a
 * sign-prefixed, zero-padded (min 2-digit) exponent — e.g. JS `1e+21` /
 * `1e-7` vs .NET `1E+21` / `1E-07`. We normalise JS output to the .NET form.
 * (BeliefTrackerTheoryOfMind never reaches this range, but keeping the
 * serialiser correct avoids a latent parity bug for other callers.)
 */
export function stjDouble(n: number): string {
  const js = JSON.stringify(n); // shortest round-trip; NaN/Infinity → "null"
  const eIndex = js.indexOf("e");
  if (eIndex < 0) return js; // normal notation — identical to .NET
  const mantissa = js.slice(0, eIndex);
  let exp = js.slice(eIndex + 1); // e.g. "+21", "-7", "21"
  let sign = "+";
  if (exp[0] === "+" || exp[0] === "-") {
    sign = exp[0];
    exp = exp.slice(1);
  }
  exp = exp.padStart(2, "0"); // .NET pads the exponent to at least 2 digits
  return `${mantissa}E${sign}${exp}`;
}

/**
 * Serialise a string→double map exactly as
 * `JsonSerializer.Serialize(Dictionary<string,double>)` does: `{}` when empty,
 * else `{"k":v,...}` in insertion order, keys STJ-escaped, values STJ-formatted,
 * no whitespace.
 */
export function stjSerializeDoubleMap(map: ReadonlyMap<string, number>): string {
  if (map.size === 0) return "{}";
  let out = "{";
  let first = true;
  for (const [key, value] of map) {
    if (!first) out += ",";
    first = false;
    out += '"' + stjEscape(key) + '":' + stjDouble(value);
  }
  out += "}";
  return out;
}
