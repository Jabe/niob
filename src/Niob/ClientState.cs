using System;
using System.IO;
using System.Net.Sockets;

namespace Niob
{
    public class ClientState
    {
        private byte[] _inBuffer;
        private MemoryStream _inStream;

        private byte[] _outBuffer;

        public ClientState(Socket socket, Binding binding)
        {
            Socket = socket;
            Binding = binding;

            HeaderLength = -1;
            ContentLength = -1;
        }

        public Socket Socket { get; private set; }
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
    }
}