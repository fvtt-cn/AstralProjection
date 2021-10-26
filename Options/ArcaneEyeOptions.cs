namespace AstralProjection.Options
{
    public class ArcaneEyeOptions
    {
        public string Prefix { get; set; }

        public string ListPath { get; set; } = "list.html";

        public string HtmlHeader { get; set; } = "<html><head><meta charset=\"utf-8\"></head><body><ul>";

        public string HtmlFooter { get; set; } = "</ul></body></html>";

        public string LinkTemplate { get; set; } = "<li><a href=\"{0}{1}\">{0}{1}</a></li>";

        public string Schedule { get; set; } = "30 */8 * * *";
    }
}