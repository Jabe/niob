using System;
using System.Collections.Generic;
using System.Linq;

namespace Niob
{
    public class Html
    {
        public static string Encode(string input)
        {
            return input
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}