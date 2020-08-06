namespace AstralProjection
{
    public class AstralOptions
    {
        // S3.
        public string Bucket { get; set; }

        public string Region { get; set; }

        public string Id { get; set; }

        public string Key { get; set; }

        public string ServiceUrl { get; set; }


        // Local.
        public string Dir { get; set; }

        public string Prefix { get; set; }
    }
}
