// Primitives.cs
//
// (2.2.0) Shared shapes used across the vision contract surface.

using System;

namespace CircleAI.Vision;

/// <summary>An axis-aligned rectangle in image-pixel coordinates.</summary>
public readonly record struct BoundingBox(int X, int Y, int Width, int Height);

/// <summary>
/// A 2D point on a detected face — eye centre, mouth corner, etc.
/// Coordinates are image-pixel space.
/// </summary>
public readonly record struct LandmarkPoint(int X, int Y);

/// <summary>One detected face with optional landmark fallback.</summary>
public sealed record DetectedFace(
    BoundingBox                Region,
    float                      Confidence,
    IReadOnlyList<LandmarkPoint>? Landmarks = null);

/// <summary>
/// A face embedding suitable for similarity search. <see cref="Vector"/>
/// is normalised so cosine similarity reduces to a dot product.
/// </summary>
public sealed record FaceEmbedding(
    float[] Vector,
    int     Dimension);

/// <summary>
/// Outcome of liveness detection — is the camera seeing a real human,
/// a printed photo, a screen replay, a 3D mask, …?
/// </summary>
public sealed record LivenessResult(
    bool   IsLive,
    float  Confidence,
    string? FailureReason = null);

/// <summary>One parsed field from an ID document.</summary>
public sealed record DocumentField(string Key, string Value, float Confidence);

/// <summary>Outcome of KYC document verification.</summary>
public sealed record DocumentVerificationResult(
    bool                       IsValid,
    string                     DocumentType,
    string                     IssuingCountry,
    IReadOnlyList<DocumentField> Fields,
    float                      OverallConfidence,
    IReadOnlyList<string>?     Warnings = null);

/// <summary>Outcome of license-plate recognition.</summary>
public sealed record PlateRecognitionResult(
    string      PlateText,
    string?     CountryHint,
    BoundingBox Region,
    float       Confidence);

/// <summary>
/// One observed BLE / RF anomaly. Severity 0-1; higher = more concerning.
/// </summary>
public sealed record BluetoothAnomaly(
    string  Source,
    string  Kind,
    float   Severity,
    string  Description,
    DateTimeOffset ObservedAtUtc);
