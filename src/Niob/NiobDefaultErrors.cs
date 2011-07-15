using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Niob.SimpleHtml;

namespace Niob
{
    public static class NiobDefaultErrors
    {
        public static void ServerError(object sender, RequestEventArgs e)
        {
            e.Response.Version = HttpVersion.Http10;
            e.Response.KeepAlive = false;

            e.Response.StatusCode = 500;
            e.Response.StatusText = "Internal Errorz";

            e.Response.ContentStream = new MemoryStream();

            e.Response.StartHtml("Error!");
            e.Response.AppendHtml("<h1>Server Error (500)</h1>");
            e.Response.AppendHtml("<p>The server has a boo-boo.</p>");
            e.Response.EndHtml();

            e.Response.Send();
        }
    }
}