// Models.swift
// Shared primitive re-exports and type aliases for CircleAI Swift SDK.
// All domain types live in their respective files; this file holds nothing
// that belongs to a single domain but is needed across the package.

import Foundation

// TimeInterval is already Double (seconds) in Foundation — matches C# TimeSpan
// UUID maps to C# Guid
// Date maps to C# DateTimeOffset (UTC)
// Data maps to C# ReadOnlyMemory<byte>
// Float (32-bit) matches C# float
