using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Niob
{
    public class HttpClient
    {
        private readonly ClientState _clientState;
        private string _fingerprint;

        public HttpClient(ClientState clientState)
        {
            _clientState = clientState;
        }

        public string Fingerprint
        {
            get { return _fingerprint ?? (_fingerprint = GetFingerprint()); }
        }

        public IPEndPoint EndPoint
        {
            get { return (IPEndPoint) _clientState.Socket.RemoteEndPoint; }
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