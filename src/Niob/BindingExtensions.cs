using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Niob
{
    public static class BindingExtensions
    {
        public static Binding Add(this List<Binding> self, IPAddress ipAddress, ushort port)
        {
            var binding = new Binding(ipAddress, port);
            self.Add(binding);

            return binding;
        }

        public static Binding Add(this List<Binding> self, IPAddress ipAddress, ushort port, bool secure,
                                  X509Certificate2 certificate)
        {
            var binding = new Binding(ipAddress, port, secure, certificate);
            self.Add(binding);

            return binding;
        }
    }
}