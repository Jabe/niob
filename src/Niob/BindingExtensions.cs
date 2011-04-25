using System;
using System.Collections.Generic;
using System.Net;

namespace Niob
{
    public static class BindingExtensions
    {
        public static void Add(this List<Binding> self, IPAddress ipAddress, ushort port)
        {
            self.Add(new Binding(ipAddress, port));
        }
    }
}