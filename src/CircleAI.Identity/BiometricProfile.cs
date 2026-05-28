// BiometricProfile.cs
//
// An encrypted biometric embedding template linked to a CircleIdentity.
//
// Architecture notes:
//   - EmbeddingVector is a float[] produced by the facex feature extractor,
//     L2-normalised before storage. It is NOT a cryptographic hash.
//     A hash cannot be fuzzy-matched; an embedding vector can.
//   - Matching is cosine similarity (dot product of L2-normalised vectors)
//     against a configurable threshold — see BiometricMatcher.
//   - This type is deliberately separate from EpisodicMemoryEntry
//     (which stores conversational events) and CircleIdentity
//     (which stores device/display identity).
//   - Implementations of IBiometricStore MUST encrypt EmbeddingVector at rest.
//     Biometric templates are sensitive personal data under POPIA and GDPR.
//     On deletion or right-to-be-forgotten requests, call IBiometricStore.DeleteAsync.

using System;

namespace CircleAI.Identity
{
    /// <summary>
    /// Biometric embedding template for a Circle AI identity.
    /// The <see cref="EmbeddingVector"/> is the L2-normalised float array
    /// output of the facex feature extractor for an enrolled face.
    /// Matching is performed by <see cref="BiometricMatcher.IsMatch"/>.
    /// </summary>
    public sealed record BiometricProfile
    {
        /// <summary>
        /// The <see cref="CircleIdentity.IdentityId"/> this profile belongs to.
        /// </summary>
        public required string IdentityId { get; init; }

        /// <summary>
        /// L2-normalised face embedding vector from the facex pipeline.
        /// Typical dimensions: 128 (lightweight model) or 256 (full model).
        /// Must not be stored in plaintext — IBiometricStore implementations
        /// must encrypt this array at rest.
        /// </summary>
        public required float[] EmbeddingVector { get; init; }

        /// <summary>Dimension of <see cref="EmbeddingVector"/>.</summary>
        public int EmbeddingDimension => EmbeddingVector.Length;

        /// <summary>
        /// Cosine similarity threshold at or above which a live embedding is
        /// considered a positive match. Range [0.0, 1.0]. Default 0.85.
        /// Lower values (e.g. 0.75) tolerate more variation across lighting
        /// and expression changes; higher values (e.g. 0.92) are more strict.
        /// </summary>
        public float MatchThreshold { get; init; } = 0.85f;

        /// <summary>UTC timestamp when this template was enrolled.</summary>
        public DateTimeOffset EnrolledAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// UTC timestamp of the most recent successful match, or <c>null</c>
        /// if the profile has never been matched since enrollment.
        /// </summary>
        public DateTimeOffset? LastMatchAt { get; set; }
    }
}
