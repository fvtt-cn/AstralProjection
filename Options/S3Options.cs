namespace AstralProjection.Options
{
    public class S3Options
    {
        public string Bucket { get; set; }

        public string Region { get; set; }

        public string Id { get; set; }

        public string Key { get; set; }

        public string ServiceUrl { get; set; }

        /// <summary>
        ///     In seconds.
        /// </summary>
        public int Timeout { get; set; }
    }
}