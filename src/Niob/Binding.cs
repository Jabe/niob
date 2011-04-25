using System;
using System.Net;

namespace Niob
{
    public class Binding
    {
        public Binding(IPAddress ipAddress, ushort port)
        {
            IpAddress = ipAddress;
            Port = port;
        }

        public IPAddress IpAddress { get; private set; }
        public ushort Port { get; private set; }
    }
}