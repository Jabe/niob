using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Niob
{
    public class HttpResponse
    {
        private readonly ClientState _clientState;

        private IDictionary<string, string> _headers;

        public HttpResponse(ClientState clientState)
        {
            _clientState = clientState;

            Version = HttpVersion.Http10;
            StatusCode = 501;
            StatusText = "Not Implemented";
        }

        public HttpVersion Version { get; set; }
        public ushort StatusCode { get; set; }
        public string StatusText { get; set; }

        public Stream ContentStream { get; set; }
        public string ContentType { get; set; }
        public string ContentCharSet { get; set; }

        public IDictionary<string, string> Headers
        {
            get { return _headers ?? (_headers = new Dictionary<string, string>()); }
        }

        public void WriteHeaders(Stream stream)
        {
            var writer = new StreamWriter(stream, Encoding.ASCII);

            if (Version == HttpVersion.Http11)
            {
                writer.Write("HTTP/1.1");
            }
            else
            {
                writer.Write("HTTP/1.0");
            }

            writer.Write(" ");
            writer.Write(StatusCode);
            writer.Write(" ");
            writer.Write(StatusText);
            writer.Write("\r\n");

            long contentLength = (ContentStream == null) ? 0 : ContentStream.Length;

            writer.Write("Content-Length: " + contentLength);
            writer.Write("\r\n");

            if (ContentType != null)
            {
                writer.Write("Content-Type: " + ContentType);

                if (ContentCharSet != null)
                {
                    writer.Write("; charset=" + ContentCharSet);
                }

                writer.Write("\r\n");
            }

            if (_clientState.KeepAlive)
            {
                writer.Write("Connection: keep-alive");
                writer.Write("\r\n");
            }
            else
            {
                writer.Write("Connection: close");
                writer.Write("\r\n");
            }

            foreach (var header in Headers)
            {
                writer.Write(header.Key);
                writer.Write(": ");
                writer.Write(header.Value);
                writer.Write("\r\n");
            }

            writer.Write("\r\n");
            writer.Flush();
        }

        public void Send()
        {
            _clientState.AddOp(ClientStateOp.PostRendering);
            _clientState.Server.EnqueueAndKickWorkers(_clientState);
        }
    }
}