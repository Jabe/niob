using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Niob
{
    public static class BindingExtensions
    {
        public static void Add(this List<Binding> self, IPAddress ipAddress, ushort port)
        {
            self.Add(new Binding(ipAddress, port));
        }

        public static void Add(this List<Binding> self, IPAddress ipAddress, ushort port, bool secure,
                               X509Certificate2 certificate)
        {
            self.Add(new Binding(ipAddress, port, secure, certificate));
        }
    }
}