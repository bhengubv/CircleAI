// ThermalState.cs
//
// Device thermal condition surfaced by platform adapters (MAUI, Android, iOS,
// HarmonyOS) through IDeviceContext. B! uses this to back off heavy inference
// tasks before the OS starts throttling the CPU/NPU.

namespace Circle.AI.Core
{
    /// <summary>
    /// Represents the thermal condition of the host device as reported by the
    /// platform layer. Maps to Android <c>PowerManager.ThermalStatus</c>,
    /// iOS/macOS <c>ProcessInfo.thermalState</c>, and HarmonyOS thermal APIs.
    /// </summary>
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
