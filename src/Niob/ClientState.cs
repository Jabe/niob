using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;

namespace Niob
{
    public class ClientState : IDisposable
    {
        private readonly Socket _socket;
        private readonly Niob _server;
        private bool _disposed;
        private byte[] _buffer;
        private MemoryStream _headerStream;
        private MemoryStream _contentStream;
        private NetworkStream _networkStream;
        private SslStream _tlsStream;
        private readonly List<string> _requestHeaderLines = new List<string>();
        private readonly List<KeyValuePair<string, string>> _requestHeaders = new List<KeyValuePair<string, string>>();

        public ClientState(Socket socket, Binding binding, Niob server)
        {
            _socket = socket;
            _server = server;
            Binding = binding;

            HeaderLength = -1;
            ContentLength = -1;

            Id = Guid.NewGuid();
        }

        public Guid Id { get; set; }

        public Stream Stream
        {
            get { return _tlsStream ?? (Stream) _networkStream; }
        }

        public Binding Binding { get; private set; }

        public byte[] Buffer
        {
            get { return _buffer ?? (_buffer = new byte[Niob.ClientBufferSize]); }
        }

        public MemoryStream HeaderStream
        {
            get { return _headerStream ?? (_headerStream = new MemoryStream()); }
        }

        public MemoryStream ContentStream
        {
            get { return _contentStream ?? (_contentStream = new MemoryStream()); }
        }

        public int HeaderLength { get; set; }
        public int ContentLength { get; set; }
        public bool KeepAlive { get; set; }

        public Stream OutStream { get; set; }

        public bool IsReady { get; set; }
        public bool IsReading { get; set; }
        public bool IsWriting { get; set; }
        public bool IsKeepingAlive { get; set; }
        public bool IsRendering { get; set; }
        public bool IsPostRendering { get; set; }
        public bool IsExpectingContinue { get; set; }
        public bool IsPostExpectingContinue { get; set; }

        private long _lastActivity;

        public bool Disposed
        {
            get { return _disposed; }
        }

        public List<string> RequestHeaderLines
        {
            get { return _requestHeaderLines; }
        }

        public List<KeyValuePair<string, string>> RequestHeaders
        {
            get { return _requestHeaders; }
        }

        public HttpRequest Request { get; set; }
        public HttpResponse Response { get; set; }

        public Niob Server
        {
            get { return _server; }
        }

        public Socket Socket
        {
            get { return _socket; }
        }

        public long LastActivity
        {
            get
            {
                return Interlocked.Read(ref _lastActivity);
            }
            set
            {
                Interlocked.Exchange(ref _lastActivity, value);
            }
        }

        #region IDisposable Members

        public void Dispose()
        {
            using (_headerStream)
            using (_contentStream)
            using (_tlsStream)
            using (_networkStream)
            {
                if (_networkStream != null)
                    _networkStream.Close();
            }

            _disposed = true;
        }

        #endregion

        public void AsyncInitialize(Action<ClientState> onSuccess, Action<ClientState> onFailure)
        {
            try
            {
                _networkStream = new NetworkStream(Socket, true);
            }
            catch (Exception)
            {
                onFailure(this);
                return;
            }

            if (Binding.Secure && Binding.Certificate != null)
            {
                try
                {
                    _tlsStream = new SslStream(_networkStream);
                    _tlsStream.BeginAuthenticateAsServer(Binding.Certificate, false, SslProtocols.Default, false,
                                                         InitializeCallback, onSuccess);
                }
                catch (Exception)
                {
                    onFailure(this);
                    return;
                }
            }
            else
            {
                IsReady = true;
                onSuccess(this);
            }
        }

        private void InitializeCallback(IAsyncResult ar)
        {
            var onSuccess = (Action<ClientState>) ar.AsyncState;

            try
            {
                _tlsStream.EndAuthenticateAsServer(ar);
            }
            catch (Exception e)
            {
                Console.WriteLine("Tls handshake failed: " + e);
                Dispose();
                return;
            }

            IsReady = true;
            onSuccess(this);
        }

        public void Clear()
        {
            HeaderLength = -1;
            ContentLength = -1;
            RequestHeaders.Clear();
            RequestHeaderLines.Clear();
            HeaderStream.SetLength(0);
            ContentStream.SetLength(0);
            Request = null;
            Response = null;
        }
    }
}