using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace Niob
{
    public class HttpRequest
    {
        private static readonly Regex RequestLineParser =
            new Regex(
                @"^(?<m>[a-z]+) " +
                @"(?<u>[a-z0-9%!*'();:@&=+$,/?#\[\]~._-]+) " +
                @"HTTP/(?<v>\d\.\d)$",
                RegexOptions.IgnoreCase);

        private readonly ClientState _clientState;

        private Dictionary<string, string> _headers;

        public HttpRequest(ClientState clientState)
        {
            _clientState = clientState;
        }

        public string Method { get; set; }
        public string Uri { get; set; }
        public HttpVersion Version { get; set; }

        public Stream ContentStream { get; set; }
        public string ContentType { get; set; }
        public string ContentCharSet { get; set; }

        public IDictionary<string, string> Headers
        {
            get { return _headers ?? (_headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)); }
        }

        public void ReadHeader(IEnumerable<string> headerLines)
        {
            int i = 0;

            foreach (string headerLine in headerLines)
            {
                if (i == 0)
                {
                    Match match = RequestLineParser.Match(headerLine);

                    if (!match.Success)
                        throw new ProtocolViolationException("Invalid request line");

                    Method = match.Groups["m"].Value;

                    Uri = match.Groups["u"].Value;

                    string version = match.Groups["v"].Value;
                    Version = (version == "1.1") ? HttpVersion.Http11 : HttpVersion.Http10;
                }
                else
                {
                    string[] parts = headerLine.Split(':');
                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    Headers.Add(key, value);
                }

                i++;
            }
        }
    }
}