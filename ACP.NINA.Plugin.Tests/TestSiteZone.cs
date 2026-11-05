using ACP.NINA.Plugin.Services;
using System;
using System.Runtime.CompilerServices;

namespace ACP.NINA.Plugin.Tests {

    /// The tests cut Activity nights in one made-up site zone, a fixed
    /// UTC+11, so the results do not depend on the zone of the machine
    /// running them. The plugin itself uses the system time zone.
    public static class TestSiteZone {

        public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test site", TimeSpan.FromHours(11), "Test site (UTC+11)", "Test site");

        [ModuleInitializer]
        internal static void Use() {
            ActivityNight.Zone = Zone;
        }
    }
}
