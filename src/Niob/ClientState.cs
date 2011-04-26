using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Niob
{
    public class ClientState : IDisposable
    {
        private readonly Socket _socket;
        private byte[] _inBuffer;
        private MemoryStream _inStream;
        private NetworkStream _networkStream;
        private byte[] _outBuffer;
        private SslStream _tlsStream;

        public ClientState(Socket socket, Binding binding)
        {
            _socket = socket;
            Binding = binding;

            HeaderLength = -1;
            ContentLength = -1;
        }

        public Stream Stream
        {
            get { return _tlsStream ?? (Stream) _networkStream; }
        }

        public Binding Binding { get; private set; }

        public byte[] InBuffer
        {
            get { return _inBuffer ?? (_inBuffer = new byte[Niob.InBufferSize]); }
        }

        public MemoryStream InStream
        {
            get { return _inStream ?? (_inStream = new MemoryStream()); }
        }

        public int HeaderLength { get; set; }
        public int ContentLength { get; set; }
        public bool KeepAlive { get; set; }

        public byte[] OutBuffer
        {
            get { return _outBuffer ?? (_outBuffer = new byte[Niob.OutBufferSize]); }
        }

        public Stream OutStream { get; set; }

        public bool IsReady { get; set; }
        public bool IsReading { get; set; }
        public bool IsWriting { get; set; }
        public bool IsKeepingAlive { get; set; }
        public bool IsRendering { get; set; }

        #region IDisposable Members

        public void Dispose()
        {
            using (_inStream)
            using (_tlsStream)
            using (_networkStream)
            {
                _networkStream.Close();
            }
        }

        #endregion

        public void AsyncInitialize(Action<ClientState> onSuccess)
        {
            _networkStream = new NetworkStream(_socket, true);

            if (Binding.Secure && Binding.Certificate != null)
            {
                _tlsStream = new SslStream(_networkStream);
                _tlsStream.BeginAuthenticateAsServer(Binding.Certificate, false, SslProtocols.Default, false,
                                                     InitializeCallback, onSuccess);
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
    }
}