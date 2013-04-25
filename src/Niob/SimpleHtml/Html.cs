using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Niob.SimpleHtml
{
    public static class Html
    {
        static Html()
        {
            NewLine = Environment.NewLine;
            Version = HtmlVersion.Html5;
        }

        public static string NewLine { get; set; }
        public static HtmlVersion Version { get; set; }

        public static string Encode(this string input)
        {
            return (input ?? "")
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#x27;");
        }

        public static void StartHtml(this HttpResponse response, string pageTitle)
        {
            if (Version == HtmlVersion.Html5)
            {
                response.ContentType = "text/html";
                response.ContentCharSet = "utf-8";
            }

            StreamWriter writer = CreateWriter(response);

            if (Version == HtmlVersion.Html5)
            {
                // start with empty line
                writer.WriteLine();

                writer.WriteLine(@"<!DOCTYPE html>");
                writer.WriteLine(@"<html>");
                writer.WriteLine(@"<head>");
                writer.WriteLine(@"<meta charset=""utf-8"" />");

                writer.Write(@"<title>");
                writer.Write(pageTitle.Encode());
                writer.WriteLine(@"</title>");
                writer.WriteLine(@"<body>");
            }

            writer.Flush();
        }

        public static void AppendHtml(this HttpResponse response, string rawHtml, params object[] format)
        {
            var writer = new StreamWriter(response.ContentStream);

            writer.Write(string.Format(rawHtml, format));

            writer.Flush();
        }

        public static void EndHtml(this HttpResponse response)
        {
            var writer = new StreamWriter(response.ContentStream);

            if (Version == HtmlVersion.Html5)
            {
                writer.WriteLine(@"</body>");
                writer.WriteLine(@"</html>");
            }

            writer.Flush();

            response.ContentStream.Seek(0, SeekOrigin.Begin);
        }

        private static StreamWriter CreateWriter(HttpResponse response)
        {
            return new StreamWriter(response.ContentStream) {NewLine = NewLine};
        }
    }
}
