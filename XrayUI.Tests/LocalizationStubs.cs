namespace XrayUI.Helpers
{
    // Test-side stand-ins for the WinAppSDK ResourceLoader-backed localization
    // facade (Helpers/L.cs, Helpers/Loc.cs). Only the members that linked
    // production sources actually touch are stubbed; add members here when a
    // newly linked file references more of L.*.
    public static class L
    {
        public static string ServerDetail_Timeout => "Timeout";
        public static string Subscription_NeverUpdated => "Never updated";
        public static string Subscription_NoParsed => "No servers parsed";
        public static string Subscription_CheckUrl => "Check subscription URL";
        public static string Subscription_JustNow => "Just now";
    }

    public static class Loc
    {
        public static string GetString(string key) => key switch
        {
            "Traffic_UnitBytes" => "B",
            "Traffic_UnitKiB" => "KiB",
            "Traffic_UnitMiB" => "MiB",
            "Traffic_UnitGiB" => "GiB",
            "Traffic_UnitTiB" => "TiB",
            "Traffic_PerSecond" => "/s",
            _ => key
        };
        public static string Format(string key, params object?[] args) =>
            $"{key}({string.Join(",", args)})";
    }
}
