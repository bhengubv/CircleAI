// MauiDeviceContext.cs
//
// Full MAUI implementation of IDeviceContext.  Uses real platform APIs
// (Battery, Connectivity, DeviceInfo) that are only available in a .NET MAUI
// host — Android, iOS, macOS, or Windows.
//
// GPS (Latitude / Longitude / LocationHint) intentionally returns null.
// Fine-grained location requires an explicit runtime permission request that
// must be initiated by the host app, not a library.  Host apps that need GPS
// context should subclass or wrap MauiDeviceContext and override those three
// members after obtaining permission.
//
// Register via DI in MauiProgram.cs:
//
//   builder.Services.AddSingleton<IDeviceContext>(
//       sp => new MauiDeviceContext("tgn.butler"));
//
//   builder.Services.Configure<AIOptions>(o =>
//       o.DeviceContext = sp.GetRequiredService<IDeviceContext>());

using System;
using System.Globalization;
using CircleAI.Core;
// Essentials on android and Apple. On Windows it would bring the Windows App SDK and
// Win2D, so the WINDOWS branches below read Windows' own APIs - PowerManager and
// NetworkInformation - which is what Essentials calls there anyway.
#if ANDROID || IOS || MACCATALYST
using Microsoft.Maui.Devices;
using Microsoft.Maui.Networking;
// Disambiguate from CircleAI.Core.Connectivity — same pattern as
// `using MauiPermissions = Microsoft.Maui.ApplicationModel.Permissions`
// documented in CLAUDE.md.
using MauiConnectivity = Microsoft.Maui.Networking.Connectivity;
#endif

namespace CircleAI.Maui
{
    /// <summary>
    /// <see cref="IDeviceContext"/> backed by real MAUI platform APIs.
    /// Provides battery level, charging state, and network type without
    /// requiring GPS permissions.
    /// </summary>
    public sealed class MauiDeviceContext : IDeviceContext
    {
        // ------------------------------------------------------------------
        // Private state
        // ------------------------------------------------------------------

        private DateTimeOffset? _lastActiveUtc;

        // ------------------------------------------------------------------
        // Identity / locale
        // ------------------------------------------------------------------

        /// <inheritdoc />
        /// <remarks>
        /// Supplied by the host app at construction (e.g. <c>"tgn.sdpkt"</c>).
        /// </remarks>
        public string? ActiveAppId { get; }

        /// <inheritdoc />
        /// <remarks>Derived from <see cref="CultureInfo.CurrentUICulture"/>.</remarks>
        public string? Locale => CultureInfo.CurrentUICulture.Name;

        /// <inheritdoc />
        /// <remarks>Derived from <see cref="TimeZoneInfo.Local"/>.</remarks>
        public string? TimeZoneId => TimeZoneInfo.Local.Id;

        /// <inheritdoc />
        public DateTimeOffset? LocalTime => DateTimeOffset.Now;

        // ------------------------------------------------------------------
        // Location — not requested here; host app must handle permissions.
        // ------------------------------------------------------------------

        /// <inheritdoc />
        /// <remarks>
        /// Always <c>null</c> — GPS requires an explicit permission request
        /// that must be driven by the host app, not a library.
        /// Override in a subclass after obtaining permission.
        /// </remarks>
        public double? Latitude => null;

        /// <inheritdoc />
        public double? Longitude => null;

        /// <inheritdoc />
        public string? LocationHint => null;

        // ------------------------------------------------------------------
        // Device health — read from MAUI essentials.
        // ------------------------------------------------------------------

        /// <inheritdoc />
        /// <remarks>
        /// MAUI <c>Battery.Default.ChargeLevel</c> returns a <c>double</c> in the
        /// range [0.0, 1.0]; we narrow to <c>float</c> here.  Returns
        /// <c>null</c> on simulators or when the platform throws.
        /// </remarks>
        public float? BatteryLevel
        {
            get
            {
#if ANDROID || IOS || MACCATALYST
                try
                {
                    var level = Battery.Default.ChargeLevel;
                    // ChargeLevel returns -1 when the value is unknown.
                    return level < 0 ? null : (float)level;
                }
                catch
                {
                    return null;
                }
#elif WINDOWS
                try
                {
                    // A desktop with no battery has no level - unknown, not full.
                    if (global::Windows.System.Power.PowerManager.BatteryStatus
                        == global::Windows.System.Power.BatteryStatus.NotPresent)
                        return null;
                    return global::Windows.System.Power.PowerManager.RemainingChargePercent / 100f;
                }
                catch
                {
                    return null;
                }
#else
                return null;
#endif
            }
        }

        /// <inheritdoc />
        public bool? IsCharging
        {
            get
            {
#if ANDROID || IOS || MACCATALYST
                try
                {
                    return Battery.Default.State switch
                    {
                        BatteryState.Charging => true,
                        BatteryState.Full     => true,  // plugged in and full
                        BatteryState.NotCharging
                            or BatteryState.Discharging => false,
                        _                               => null,
                    };
                }
                catch
                {
                    return null;
                }
#elif WINDOWS
                try
                {
                    return global::Windows.System.Power.PowerManager.BatteryStatus switch
                    {
                        global::Windows.System.Power.BatteryStatus.Charging    => true,
                        // Idle on mains is Essentials' "Full": plugged in, nothing to charge.
                        global::Windows.System.Power.BatteryStatus.Idle        =>
                            global::Windows.System.Power.PowerManager.PowerSupplyStatus
                            != global::Windows.System.Power.PowerSupplyStatus.NotPresent,
                        global::Windows.System.Power.BatteryStatus.Discharging => false,
                        _                                                      => null,
                    };
                }
                catch
                {
                    return null;
                }
#else
                return null;
#endif
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Returns one of <c>"wifi"</c>, <c>"cellular"</c>, <c>"ethernet"</c>,
        /// <c>"bluetooth"</c>, <c>"none"</c>, <c>"constrained"</c>, or
        /// <c>"local"</c>.
        /// </remarks>
        public string? NetworkType
        {
            get
            {
#if WINDOWS
                try
                {
                    var profile = global::Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                    if (profile is null) return "none";

                    switch (profile.GetNetworkConnectivityLevel())
                    {
                        case global::Windows.Networking.Connectivity.NetworkConnectivityLevel.None:
                            return "none";
                        case global::Windows.Networking.Connectivity.NetworkConnectivityLevel.LocalAccess:
                            return "local";
                        case global::Windows.Networking.Connectivity.NetworkConnectivityLevel.ConstrainedInternetAccess:
                            return "constrained";
                    }

                    if (profile.IsWlanConnectionProfile) return "wifi";
                    if (profile.IsWwanConnectionProfile) return "cellular";

                    // IANA interface types: 6 is Ethernet, 71 is 802.11 wireless.
                    return profile.NetworkAdapter?.IanaInterfaceType switch
                    {
                        6u  => "ethernet",
                        71u => "wifi",
                        _   => "internet", // Connected but profile is unrecognised.
                    };
                }
                catch
                {
                    return null;
                }
#elif ANDROID || IOS || MACCATALYST
                try
                {
                    var access = MauiConnectivity.Current.NetworkAccess;

                    if (access == NetworkAccess.None)                return "none";
                    if (access == NetworkAccess.Local)               return "local";
                    if (access == NetworkAccess.ConstrainedInternet) return "constrained";
                    if (access == NetworkAccess.Unknown)             return null;

                    // Internet — inspect the active connection profiles.
                    var profiles = MauiConnectivity.Current.ConnectionProfiles;

                    foreach (var profile in profiles)
                    {
                        if (profile == ConnectionProfile.WiFi)      return "wifi";
                        if (profile == ConnectionProfile.Cellular)  return "cellular";
                        if (profile == ConnectionProfile.Ethernet)  return "ethernet";
                        if (profile == ConnectionProfile.Bluetooth) return "bluetooth";
                    }

                    return "internet"; // Connected but profile is unrecognised.
                }
                catch
                {
                    return null;
                }
#else
                return null;
#endif
            }
        }

        // ------------------------------------------------------------------
        // Diagnostics — not yet wired; override or extend for platform-native
        // CPU / memory / thermal / storage APIs (Android UsageStatsManager,
        // iOS ProcessInfo, HarmonyOS ThermalManager, etc.).
        // ------------------------------------------------------------------

        /// <inheritdoc />
        public float? CpuUsagePercent => null;

        /// <inheritdoc />
        public long? AvailableMemoryBytes => null;

        /// <inheritdoc />
        public CircleAI.Core.ThermalState? ThermalState => null;

        /// <inheritdoc />
        public long? StorageFreeBytes => null;

        // ------------------------------------------------------------------
        // User signals
        // ------------------------------------------------------------------

        /// <inheritdoc />
        public DateTimeOffset? LastActiveUtc => _lastActiveUtc;

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        /// <summary>
        /// Initialises the context for the given app identifier.
        /// </summary>
        /// <param name="activeAppId">
        /// The package / bundle ID of the host app, e.g. <c>"tgn.butler"</c>.
        /// </param>
        public MauiDeviceContext(string? activeAppId = null)
        {
            ActiveAppId   = activeAppId;
            _lastActiveUtc = DateTimeOffset.UtcNow;
        }

        // ------------------------------------------------------------------
        // Interaction tracking
        // ------------------------------------------------------------------

        /// <summary>
        /// Records a user interaction by updating <see cref="LastActiveUtc"/>
        /// to the current UTC time.  Call this from the MAUI Shell's
        /// <c>Navigated</c> event or any significant user action.
        /// </summary>
        public void RecordInteraction() =>
            _lastActiveUtc = DateTimeOffset.UtcNow;
    }
}
