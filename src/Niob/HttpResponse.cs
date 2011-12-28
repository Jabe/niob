using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Niob
{
    public class HttpResponse
    {
        private readonly ConnectionHandle _connectionHandle;

        private IDictionary<string, string> _headers;

        public HttpResponse(ConnectionHandle connectionHandle)
        {
            _connectionHandle = connectionHandle;

            if (connectionHandle.Request != null)
            {
                Version = connectionHandle.Request.Version;
            }
            else
            {
                Version = HttpVersion.Http10;
            }
            
            StatusCode = 404;
            StatusText = "Not Found";
        }

        internal bool Vetoed { get; set; }

        public HttpVersion Version { get; set; }
        public ushort StatusCode { get; set; }
        public string StatusText { get; set; }

        public Stream ContentStream { get; set; }
        public string ContentType { get; set; }
        public string ContentCharSet { get; set; }

        public bool KeepAlive
        {
            get { return _connectionHandle.KeepAlive; }
            set { _connectionHandle.KeepAlive = value; }
        }

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

            if (KeepAlive)
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
            if (Vetoed)
            {
                using (ContentStream)
                {
                }

                return;
            }

            if (_connectionHandle.Disposed)
                return;

            // make sure the r/w states arn't set (for error responses)
            _connectionHandle.RemoveState(ClientState.Reading);
            _connectionHandle.RemoveState(ClientState.Writing);

            _connectionHandle.AddState(ClientState.PostRendering);
            _connectionHandle.Server.EnqueueAndKickWorkers(_connectionHandle);
        }
    }
}