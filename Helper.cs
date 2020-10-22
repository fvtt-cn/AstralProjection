using System;

namespace AstralProjection
{
    public static class Helper
    {
        public static bool IsVersion(this string version) => Version.TryParse(version, out _);

        public static bool VersionGte(this string version, string compare) =>
            Version.TryParse(version, out var ver) && Version.TryParse(compare, out var cpv) && ver >= cpv;
    }
}
