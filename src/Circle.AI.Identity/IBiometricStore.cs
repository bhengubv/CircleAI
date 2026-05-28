// IBiometricStore.cs
//
// Persistent store contract for biometric embedding profiles.
// Implementations MUST encrypt EmbeddingVector at rest — biometric templates
// are sensitive personal data regulated by POPIA, GDPR, and equivalent laws.
// Provide a DeleteAsync implementation that satisfies right-to-be-forgotten
// obligations: every byte of the embedding must be irrecoverably destroyed.

using System.Threading;
using System.Threading.Tasks;

namespace Circle.AI.Identity
{
    /// <summary>
    /// Persistent store for <see cref="BiometricProfile"/> records.
    /// </summary>
    /// <remarks>
    /// Implementations must encrypt <see cref="BiometricProfile.EmbeddingVector"/>
    /// at rest using the device's secure enclave or an equivalent key store.
    /// </remarks>
    public interface IBiometricStore
    {
        /// <summary>
        /// Load the biometric profile for <paramref name="identityId"/>.
        /// Returns <c>null</c> if no profile has been enrolled for this identity.
        /// </summary>
        Task<BiometricProfile?> GetAsync(
            string identityId,
            CancellationToken ct = default);

        /// <summary>
        /// Persist (or overwrite) a biometric profile. If a profile already
        /// exists for the identity it is replaced atomically.
        /// </summary>
        Task SaveAsync(
            BiometricProfile profile,
            CancellationToken ct = default);

        /// <summary>
        /// Permanently delete the biometric profile for <paramref name="identityId"/>.
        /// Must irrecoverably destroy the stored embedding to satisfy RTBF obligations.
        /// A no-op if no profile exists.
        /// </summary>
        Task DeleteAsync(
            string identityId,
            CancellationToken ct = default);

        /// <summary>
        /// Returns <c>true</c> if an enrolled biometric profile exists for
        /// <paramref name="identityId"/>.
        /// </summary>
        Task<bool> ExistsAsync(
            string identityId,
            CancellationToken ct = default);
    }
}
