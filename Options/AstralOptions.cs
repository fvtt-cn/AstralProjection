﻿namespace AstralProjection.Options
{
    public class AstralOptions
    {
        public string Dir { get; set; }

        /// <summary>
        /// Rewrite URL in manifest, useful when configure CDN.
        /// </summary>
        public string Prefix { get; set; }

        public string Schedule { get; set; } = "30 */12 * * *";

        /// <summary>
        /// Upload timeout in seconds, 180s by default.
        /// </summary>
        public int UploadTimeout { get; set; } = 180;
    }
}
