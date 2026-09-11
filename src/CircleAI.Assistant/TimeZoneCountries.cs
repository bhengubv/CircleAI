namespace CircleAI.Samples.It;

/// <summary>Which country an IANA timezone is in.</summary>
/// <remarks>
/// THE SIGNAL THAT WORKS ON A PHONE WITH NO SERVICE. Measured on a P30 in Tokyo:
/// the radio was OUT_OF_SERVICE, every network and SIM country came back blank,
/// and <c>persist.sys.timezone</c> said <c>Asia/Tokyo</c> the whole time. It
/// needs no permission, no signal and no request to anybody.
///
/// <para>
/// IT IS NOT PROOF AND IS NOT TREATED AS PROOF. A timezone is set by the phone
/// and can be wrong, stale or deliberately fixed by its owner - so it decides
/// which language to OFFER, never which language somebody speaks, and every
/// screen that uses it says which signal it came from.
/// </para>
/// <para>
/// Only zones whose country the catalogue can actually serve are listed. A zone
/// nobody has checked is left out rather than guessed at, because the fallback
/// is honest and a wrong confident answer is not. Both spellings are kept where
/// Android still ships an old alias - a phone that says Asia/Calcutta or
/// Asia/Saigon is not a phone we get to ignore.
/// </para>
/// </remarks>
public static class TimeZoneCountries
{
    private static readonly Dictionary<string, string> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        // East Asia
        ["Asia/Tokyo"] = "JP",
        ["Asia/Seoul"] = "KR",
        ["Asia/Shanghai"] = "CN",
        ["Asia/Chongqing"] = "CN",
        ["Asia/Urumqi"] = "CN",
        ["Asia/Hong_Kong"] = "HK",
        ["Asia/Taipei"] = "TW",

        // South and South-East Asia
        ["Asia/Kolkata"] = "IN",
        ["Asia/Calcutta"] = "IN",
        ["Asia/Karachi"] = "PK",
        ["Asia/Dhaka"] = "BD",
        ["Asia/Colombo"] = "LK",
        ["Asia/Kathmandu"] = "NP",
        ["Asia/Katmandu"] = "NP",
        ["Asia/Yangon"] = "MM",
        ["Asia/Rangoon"] = "MM",
        ["Asia/Bangkok"] = "TH",
        ["Asia/Ho_Chi_Minh"] = "VN",
        ["Asia/Saigon"] = "VN",
        ["Asia/Jakarta"] = "ID",
        ["Asia/Pontianak"] = "ID",
        ["Asia/Makassar"] = "ID",
        ["Asia/Jayapura"] = "ID",
        ["Asia/Manila"] = "PH",
        ["Asia/Tehran"] = "IR",
        ["Asia/Kabul"] = "AF",

        // Southern Africa
        ["Africa/Johannesburg"] = "ZA",
        ["Africa/Harare"] = "ZW",
        ["Africa/Gaborone"] = "BW",
        ["Africa/Maseru"] = "LS",
        ["Africa/Mbabane"] = "SZ",
        ["Africa/Windhoek"] = "NA",
        ["Africa/Lusaka"] = "ZM",
        ["Africa/Blantyre"] = "MW",
        ["Africa/Maputo"] = "MZ",

        // East Africa
        ["Africa/Nairobi"] = "KE",
        ["Africa/Dar_es_Salaam"] = "TZ",
        ["Africa/Kampala"] = "UG",
        ["Africa/Kigali"] = "RW",
        ["Africa/Bujumbura"] = "BI",
        ["Africa/Addis_Ababa"] = "ET",
        ["Africa/Asmara"] = "ER",
        ["Africa/Mogadishu"] = "SO",
        ["Indian/Antananarivo"] = "MG",

        // West and Central Africa
        ["Africa/Lagos"] = "NG",
        ["Africa/Accra"] = "GH",
        ["Africa/Porto-Novo"] = "BJ",
        ["Africa/Ouagadougou"] = "BF",
        ["Africa/Bamako"] = "ML",
        ["Africa/Dakar"] = "SN",
        ["Africa/Niamey"] = "NE",
        ["Africa/Kinshasa"] = "CD",
        ["Africa/Lubumbashi"] = "CD",
        ["Africa/Brazzaville"] = "CG",
        ["Africa/Bangui"] = "CF",

        // Elsewhere the catalogue reaches
        ["Europe/Moscow"] = "RU",
        ["Europe/Paris"] = "FR",
        ["America/Port-au-Prince"] = "HT",
        ["America/Asuncion"] = "PY",
        ["Pacific/Port_Moresby"] = "PG",
    };

    /// <summary>The country a zone is in, or null when it is not one we checked.</summary>
    public static string? Country(string? zoneId)
        => zoneId is { Length: > 0 } && Zones.TryGetValue(zoneId.Trim(), out var c) ? c : null;

    /// <summary>Every zone this knows, for a test that pins it to the catalogue.</summary>
    public static IReadOnlyDictionary<string, string> All => Zones;
}
