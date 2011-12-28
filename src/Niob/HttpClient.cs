using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Niob
{
    public class HttpClient
    {
        private readonly ConnectionHandle _connectionHandle;
        private string _fingerprint;

        public HttpClient(ConnectionHandle connectionHandle)
        {
            _connectionHandle = connectionHandle;
        }

        public string Fingerprint
        {
            get { return _fingerprint ?? (_fingerprint = GetFingerprint()); }
        }

        public IPEndPoint EndPoint
        {
            get { return (IPEndPoint) _connectionHandle.Socket.RemoteEndPoint; }
        }

        private string GetFingerprint()
        {
            string pool = "niob";

            pool += EndPoint.Address;

            byte[] bytes = Encoding.UTF8.GetBytes(pool);

            using (SHA1 algo = SHA1.Create())
            {
                byte[] hash = algo.ComputeHash(bytes);

                return BitConverter.ToString(hash).ToLowerInvariant().Replace("-", "");
            }
        }
    }
}