// ThermalState.cs
//
// Device thermal condition surfaced by platform adapters (MAUI, Android, iOS,
// HarmonyOS) through IDeviceContext. B! uses this to back off heavy inference
// tasks before the OS starts throttling the CPU/NPU.

namespace CircleAI.Core
{
    /// <summary>
    /// Represents the thermal condition of the host device as reported by the
    /// platform layer. Maps to Android <c>PowerManager.ThermalStatus</c>,
    /// iOS/macOS <c>ProcessInfo.thermalState</c>, and HarmonyOS thermal APIs.
    /// </summary>
    /// <remarks>
    /// NAME COLLISION — there is also <c>CircleAI.Hosting.ThermalState</c>.
    /// Importing both namespaces yields <c>CS0104: ambiguous reference</c>;
    /// qualify the one you mean. This enum is the raw DEVICE reading surfaced by
    /// <c>IDeviceContext</c>; the Hosting one is the throttling ladder and is
    /// numbered differently (Unknown = 0). They must not be merged without
    /// renumbering every comparison and persisted value.
    /// </remarks>
    public enum ThermalState
    {
        /// <summary>
        /// Temperature is within normal operating range. Heavy inference is safe.
        /// </summary>
        Normal,

        /// <summary>
        /// Device is running warm. B! should consider deferring non-urgent
        /// inference tasks to avoid triggering OS throttling.
        /// </summary>
        Warm,

        /// <summary>
        /// Device is critically hot. The OS may be throttling the CPU/NPU.
        /// Heavy inference must be deferred or aborted until the state returns
        /// to <see cref="Normal"/> or <see cref="Warm"/>.
        /// </summary>
        Critical,
    }
}
