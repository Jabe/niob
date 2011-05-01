using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Niob
{
    public class ReadOnlyStream : Stream
    {
        private readonly Stream _innerStream;

        public ReadOnlyStream(Stream innerStream)
        {
            _innerStream = innerStream;
        }

        public override bool CanRead
        {
            get { return _innerStream.CanRead; }
        }

        public override bool CanSeek
        {
            get { return _innerStream.CanSeek; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override long Length
        {
            get { return _innerStream.Length; }
        }

        public override long Position
        {
            get { return _innerStream.Position; }
            set { _innerStream.Position = value; }
        }

        public override void Flush()
        {
            ThrowReadOnly();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _innerStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            ThrowReadOnly();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _innerStream.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowReadOnly();
        }

        private static void ThrowReadOnly()
        {
            throw new NotSupportedException("Stream is read-only.");
        }
    }
}