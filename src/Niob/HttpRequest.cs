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

        private ReadOnlyStream _contentStream;
        private Dictionary<string, string> _headers;

        public HttpRequest(ClientState clientState)
        {
            _clientState = clientState;
            Client = new HttpClient(_clientState);
        }

        public string Host { get; private set; }
        public string Method { get; private set; }
        public Uri Url { get; private set; }
        public HttpVersion Version { get; private set; }

        public bool KeepAlive { get { return _clientState.KeepAlive; } }

        public Stream ContentStream
        {
            get { return _contentStream ?? (_contentStream = new ReadOnlyStream(_clientState.ContentStream)); }
        }

        public string ContentType { get; private set; }
        public string ContentCharSet { get; private set; }

        public IDictionary<string, string> Headers
        {
            get { return _headers ?? (_headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)); }
        }

        public HttpClient Client { get; private set; }

        public void ReadHeader(IEnumerable<string> headerLines)
        {
            int i = 0;

            string uriPath = "/";

            foreach (string headerLine in headerLines)
            {
                if (i == 0)
                {
                    Match match = RequestLineParser.Match(headerLine);

                    if (!match.Success)
                        throw new ProtocolViolationException("Invalid request line");

                    Method = match.Groups["m"].Value;
                    uriPath = match.Groups["u"].Value;

                    string version = match.Groups["v"].Value;
                    Version = (version == "1.1") ? HttpVersion.Http11 : HttpVersion.Http10;
                }
                else
                {
                    string[] parts = headerLine.Split(new[] {':'}, 2);
                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    Headers.Add(key, value);
                }

                i++;
            }

            string contentTypeHeader;

            if (Headers.TryGetValue("Content-Type", out contentTypeHeader))
            {
                string[] parts = contentTypeHeader.Split(new[] {';'}, 2);

                if (parts.Length >= 1)
                {
                    ContentType = parts[0].Trim();
                }

                if (parts.Length >= 2 && parts[1].IndexOf("charset=", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ContentCharSet = parts[1].Replace("charset=", "").Trim();
                }
            }

            string hostHeader;

            if (Headers.TryGetValue("Host", out hostHeader))
            {
                if (!string.IsNullOrEmpty(hostHeader))
                {
                    Host = hostHeader;
                }
            }

            bool keepAlive = _clientState.Server.SupportsKeepAlive &&
                             _clientState.Request.Version != HttpVersion.Http10;

            string connectionHeader;

            if (_clientState.Request.Headers.TryGetValue("Connection", out connectionHeader))
            {
                keepAlive = StringComparer.OrdinalIgnoreCase.Equals(connectionHeader, "keep-alive");
            }

            // set on the connection
            _clientState.KeepAlive = keepAlive;

            Url = ReconstructUri(uriPath);
        }

        private Uri ReconstructUri(string pathAndQuery)
        {
            string scheme = _clientState.Binding.Secure ? "https" : "http";
            string host = (!string.IsNullOrEmpty(Host)) ? Host : _clientState.Binding.IpAddress.ToString();
            int port = _clientState.Binding.Port;

            string[] parts = pathAndQuery.Split(new[] {'?'}, 2);

            var uri = new UriBuilder(scheme, host, port, parts[0]);

            if (parts.Length > 1)
                uri.Query = parts[1];

            return uri.Uri;
        }
    }
}