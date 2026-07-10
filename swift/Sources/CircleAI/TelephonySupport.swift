// TelephonySupport.swift
//
// Shared low-level helpers used by the carrier bindings so their wire format
// matches the C# reference byte-for-byte:
//   • TelephonyUri.escapeDataString  — mirrors System.Uri.EscapeDataString.
//   • TelephonyUri.htmlEncode        — mirrors System.Net.WebUtility.HtmlEncode
//                                      (the subset the TwiML/Plivo-XML paths use).
//   • FormUrlEncoded                 — mirrors System.Net.Http.FormUrlEncodedContent.
//   • TelephonyJson                  — thin JSON read helpers over JSONSerialization
//                                      (stand-in for System.Text.Json.JsonDocument),
//                                      incl. the ParseDecimal number/string logic.
//   • String.isBlank                 — mirrors string.IsNullOrWhiteSpace on a value.
//   • TelephonyHttpResponse.ensureSuccess — mirrors EnsureSuccessStatusCode().

import Foundation

// MARK: - String helpers

extension String {
    /// True when the string is empty or all-whitespace. Mirrors
    /// `string.IsNullOrWhiteSpace` applied to a non-nil value.
    var isBlank: Bool {
        trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

// MARK: - TelephonyUri

/// URI/HTML escaping that matches the .NET APIs the carriers call.
public enum TelephonyUri {

    /// Mirror of `System.Uri.EscapeDataString`: percent-encode everything
    /// except the RFC 3986 "unreserved" set `A–Z a–z 0–9 - . _ ~`. Space →
    /// `%20` (NOT `+`). Bytes are UTF-8; hex digits are UPPERCASE.
    public static func escapeDataString(_ s: String) -> String {
        // Unreserved characters that .NET leaves untouched.
        let unreserved = Set("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~".unicodeScalars)
        var out = ""
        for byte in Array(s.utf8) {
            let scalar = Unicode.Scalar(byte)
            if unreserved.contains(scalar) {
                out.unicodeScalars.append(scalar)
            } else {
                out += String(format: "%%%02X", byte)
            }
        }
        return out
    }

    /// Mirror of `System.Net.WebUtility.HtmlEncode` for the characters that can
    /// appear in the encoded values (`&`, `<`, `>`, `"`, `'`). .NET encodes:
    ///   & → &amp;   < → &lt;   > → &gt;   " → &quot;   ' → &#39;
    /// Other characters (including ordinary URL characters like `/`, `:`, `?`,
    /// `=`) pass through unchanged.
    public static func htmlEncode(_ s: String) -> String {
        var out = ""
        out.reserveCapacity(s.count)
        for ch in s {
            switch ch {
            case "&": out += "&amp;"
            case "<": out += "&lt;"
            case ">": out += "&gt;"
            case "\"": out += "&quot;"
            case "'": out += "&#39;"
            default: out.append(ch)
            }
        }
        return out
    }
}

// MARK: - FormUrlEncoded

/// Mirror of `System.Net.Http.FormUrlEncodedContent`: joins name=value pairs
/// with `&`, encoding each name and value with
/// `application/x-www-form-urlencoded` rules — space → `+`, and other reserved
/// characters percent-encoded (UPPERCASE hex). Unreserved `* - . _` and
/// alphanumerics pass through; everything else is escaped.
///
/// .NET's `FormUrlEncodedContent` uses `Uri.EscapeDataString` internally and
/// then replaces `%20` with `+`. It therefore also leaves `-._~` unescaped, and
/// escapes `*` as `%2A`. We reproduce that: escape via `escapeDataString`, then
/// swap `%20`→`+`.
public struct FormUrlEncoded: Sendable, Equatable {
    public let pairs: [Pair]

    public struct Pair: Sendable, Equatable {
        public let name: String
        public let value: String
        public init(_ name: String, _ value: String) {
            self.name = name
            self.value = value
        }
    }

    public init(_ pairs: [(String, String)]) {
        self.pairs = pairs.map { Pair($0.0, $0.1) }
    }

    /// The URL-encoded string body.
    public var encoded: String {
        pairs.map { "\(Self.encode($0.name))=\(Self.encode($0.value))" }
            .joined(separator: "&")
    }

    /// The encoded body as UTF-8 bytes.
    public var data: Data {
        Data(encoded.utf8)
    }

    private static func encode(_ s: String) -> String {
        // EscapeDataString then %20 → + (form semantics).
        TelephonyUri.escapeDataString(s).replacingOccurrences(of: "%20", with: "+")
    }
}

// MARK: - TelephonyJson

/// Thin JSON read helpers over `JSONSerialization`, standing in for
/// `System.Text.Json.JsonDocument`. Objects are `[String: Any]`, arrays are
/// `[Any]`, matching the untyped element traversal the carriers do.
public enum TelephonyJson {

    /// Parse a JSON body into a dictionary. Throws `TelephonyError.invalidOperation`
    /// when the payload is not a JSON object (parallels a failed
    /// `JsonDocument.Parse` / `GetProperty`).
    public static func parse(_ data: Data) throws -> [String: Any] {
        guard !data.isEmpty else {
            throw TelephonyError.invalidOperation("Empty JSON response.")
        }
        let obj = try JSONSerialization.jsonObject(with: data, options: [])
        guard let dict = obj as? [String: Any] else {
            throw TelephonyError.invalidOperation("Expected a JSON object at the response root.")
        }
        return dict
    }

    /// Port of the carriers' `ParseDecimal(JsonElement, property)`:
    ///   • number      → its decimal value
    ///   • string that parses as an invariant-culture decimal → that value
    ///   • otherwise   → nil (property missing or non-numeric).
    public static func parseDecimal(_ obj: [String: Any], _ property: String) -> Decimal? {
        guard let raw = obj[property] else { return nil }
        return decimalFrom(raw)
    }

    /// Port of the Telnyx `ParseMonthlyCost`: reads
    /// `cost_information.monthly_cost` as number-or-string decimal.
    public static func parseNestedDecimal(_ obj: [String: Any], _ outer: String, _ inner: String) -> Decimal? {
        guard let nested = obj[outer] as? [String: Any], let raw = nested[inner] else { return nil }
        return decimalFrom(raw)
    }

    /// number → Decimal; numeric string → Decimal; else nil.
    private static func decimalFrom(_ raw: Any) -> Decimal? {
        switch raw {
        case let n as NSNumber:
            // JSONSerialization surfaces JSON numbers as NSNumber. Booleans are
            // also NSNumber; exclude them (a bool is not a monetary value).
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return nil }
            return Decimal(string: n.stringValue)
        case let s as String:
            // Invariant-culture parse: `.` decimal separator, optional sign.
            return Decimal(string: s, locale: Locale(identifier: "en_US_POSIX"))
        default:
            return nil
        }
    }
}

// MARK: - Response success

extension TelephonyHttpResponse {
    /// Mirror of `HttpResponseMessage.EnsureSuccessStatusCode()`: throw on a
    /// non-2xx status. Carriers that call this expect a throw; those that check
    /// `IsSuccessStatusCode` do not.
    func ensureSuccess() throws {
        if !isSuccessStatusCode {
            throw TelephonyError.invalidOperation(
                "Response status code does not indicate success: \(statusCode).")
        }
    }
}
