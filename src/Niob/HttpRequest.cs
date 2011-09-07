using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace Niob
{
    public class HttpRequest
    {
        private const string CharSetPrefix = "charset=";

        private static readonly char[] SpaceArray = new[] {' '};
        private static readonly char[] ColonArray = new[] {':'};
        private static readonly char[] SemicolonArray = new[] {';'};

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
        public string Url { get; private set; }
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
                    string[] parts = headerLine.Split(SpaceArray, 3);

                    if (parts.Length != 3)
                        FailProtocol();

                    Method = parts[0].Trim();
                    uriPath = parts[1].Trim();

                    string version = parts[2].Trim();

                    Version = (version.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase))
                                  ? HttpVersion.Http11
                                  : HttpVersion.Http10;
                }
                else
                {
                    string[] parts = headerLine.Split(ColonArray, 2);
                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    Headers.Add(key, value);
                }

                i++;
            }

            string contentTypeHeader;

            if (Headers.TryGetValue("Content-Type", out contentTypeHeader))
            {
                string[] parts = contentTypeHeader.Split(SemicolonArray, 2);

                if (parts.Length >= 1)
                {
                    ContentType = parts[0].Trim();
                }

                if (parts.Length >= 2)
                {
                    var part = parts[1].Trim();

                    if (part.StartsWith(CharSetPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        ContentCharSet = part.Substring(CharSetPrefix.Length);
                    }
                }
            }

            string hostHeader;

            if (Headers.TryGetValue("Host", out hostHeader))
            {
                Host = hostHeader.Trim();
            }

            bool keepAlive = false;

            if (_clientState.Server.SupportsKeepAlive)
            {
                // default by protocol
                keepAlive = (_clientState.Request.Version != HttpVersion.Http10);

                string connectionHeader;

                // overrideable by explicit header. backported to http10
                if (_clientState.Request.Headers.TryGetValue("Connection", out connectionHeader))
                {
                    keepAlive = connectionHeader.Trim().Equals("keep-alive", StringComparison.OrdinalIgnoreCase);
                }
            }

            // set on the connection
            _clientState.KeepAlive = keepAlive;

            Url = ReconstructUri(uriPath);
        }

        private string ReconstructUri(string pathAndQuery)
        {
            string scheme = _clientState.Binding.Secure ? "https" : "http";
            int port = _clientState.Binding.Port;

            string host = string.IsNullOrEmpty(Host)
                              ? _clientState.Binding.IpAddress.ToString()
                              : Host;

            // remove colon from host header
            int colon = host.IndexOf(':');

            if (colon > 0)
                host = host.Substring(0, colon);

            string uri = scheme + "://" + host;

            if (!(scheme == "http" && port == 80 || scheme == "https" && port == 443))
            {
                uri += ":" + port;
            }

            uri += pathAndQuery;

            return uri;
        }

        private static void FailProtocol()
        {
            throw new ProtocolViolationException("Invalid request line");
        }
    }
}