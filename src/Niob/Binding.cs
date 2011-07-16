using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Niob
{
    public class Binding
    {
        public Binding(IPAddress ipAddress, ushort port)
        {
            IpAddress = ipAddress;
            Port = port;
        }

        public Binding(IPAddress ipAddress, ushort port, bool secure, X509Certificate2 certificate)
        {
            IpAddress = ipAddress;
            Port = port;
            Secure = secure;
            Certificate = certificate;
        }

        public IPAddress IpAddress { get; private set; }
        public ushort Port { get; private set; }
        public bool Secure { get; private set; }
        public X509Certificate2 Certificate { get; set; }
    }
}