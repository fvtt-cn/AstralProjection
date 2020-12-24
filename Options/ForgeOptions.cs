namespace AstralProjection.Options
{
    public class ForgeOptions
    {
        public string Username { get; set; }

        public string Password { get; set; }

        /// <summary>
        /// For BlobStorage, where program files store.
        /// </summary>
        public string StorageDir { get; set; } = ".";

        /// <summary>
        /// The minimum core version to mirror.
        /// </summary>
        public string Minimum { get; set; } = "0.6.4";

        public string[] Platforms { get; set; } = { "windows", "linux", "mac" };

        // Every even day, 2:30 AM UTC.
        public string Schedule { get; set; } = "30 2 */2 * *";

        /// <summary>
        /// Upload timeout in seconds, 300s by default.
        /// </summary>
        public int UploadTimeout { get; set; } = 300;

        public bool TrimLinuxPackage { get; set; } = true;
    }
}
