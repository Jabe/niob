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
        private readonly NiobServer _server;
        private bool _disposed;
        private byte[] _buffer;
        private MemoryStream _headerStream;
        private NetworkStream _networkStream;
        private SslStream _tlsStream;
        private readonly List<string> _requestHeaderLines = new List<string>();
        private readonly List<KeyValuePair<string, string>> _requestHeaders = new List<KeyValuePair<string, string>>();

        public ClientState(Socket socket, Binding binding, NiobServer server)
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
            get { return _buffer ?? (_buffer = new byte[NiobServer.ClientBufferSize]); }
        }

        public MemoryStream HeaderStream
        {
            get { return _headerStream ?? (_headerStream = new MemoryStream()); }
        }

        public Stream ContentStream { get; set; }

        public bool KeepAlive { get; set; }
        public int HeaderLength { get; set; }
        public long ContentLength { get; set; }
        public long BytesRead { get; set; }
        
        public Stream OutStream { get; set; }

        private readonly object _opLock = new object();
        private int _op;

        public void AddOp(ClientStateOp op)
        {
            lock (_opLock)
            {
                _op |= (int) op;
            }
        }

        public void RemoveOp(ClientStateOp op)
        {
            lock (_opLock)
            {
                _op &= ~(int) op;
            }
        }

        public bool HasOp(ClientStateOp op)
        {
            var o = (int) op;

            lock (_opLock)
            {
                return (_op & o) == o;
            }
        }

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

        public NiobServer Server
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

        public string ContentStreamFile { get; set; }
        public int LastHeaderEndOffset { get; set; }

        #region IDisposable Members

        public void Dispose()
        {
            Clear();

            using (_headerStream)
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
                AddOp(ClientStateOp.Ready);
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

            AddOp(ClientStateOp.Ready);
            onSuccess(this);
        }

        public void Clear()
        {
            HeaderLength = -1;
            ContentLength = -1;
            LastHeaderEndOffset = -1;
            BytesRead = 0;
            RequestHeaders.Clear();
            RequestHeaderLines.Clear();

            if (Request != null)
            {
                using (Request.ContentStream)
                {
                }
            }

            if (Response != null)
            {
                using (Response.ContentStream)
                {
                }
            }

            Request = null;
            Response = null;

            using (_headerStream)
            {
            }
            _headerStream = null;

            using (ContentStream)
            {
            }
            ContentStream = null;

            if (ContentStreamFile != null)
            {
                try
                {
                    File.Delete(ContentStreamFile);
                }
                catch
                {
                }
            }

            ContentStreamFile = null;
        }
    }
}