using System;

namespace AstralProjection
{
    public static class Helper
    {
        public static bool IsVersion(this string version) => Version.TryParse(version, out _);

        public static bool VersionGte(this string version, string compare) =>
            Version.TryParse(version, out var ver) && Version.TryParse(compare, out var cpv) && ver >= cpv;

        public static string GetFileExtension(this string platform) => platform.ToUpper() switch
        {
            "WINDOWS" => "exe",
            "MAC" => "dmg",
            "LINUX" => "zip",
            _ => "zip"
        };
    }
}
