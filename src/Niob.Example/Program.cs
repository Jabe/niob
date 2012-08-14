using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Niob.SimpleHtml;
using Niob.SimpleErrors;

namespace Niob.Example
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            var niob = new NiobServer();

            niob.Bindings.Add(IPAddress.Loopback, 666);
            //niob.Bindings.Add(IPAddress.Loopback, 666, true, new X509Certificate2(@"niob.cer"));

            niob.SupportsKeepAlive = true;
            niob.WorkerThreadCount = 2;
            niob.RequestAccepted += HandleRequestAsync;

            niob.DosThreshold = 100000;

            niob.Start();

            Console.WriteLine("started... press enter to stop");
            Console.ReadLine();

            Console.WriteLine("stopping");
            niob.Stop();
            Console.WriteLine("stopped");

            niob.Dispose();
        }

        private static void HandleRequestAsync(object sender, RequestEventArgs e)
        {
            // this is just an example, you don't need to do that.
            // however if you don't your worker thread(s) will be clogged.
            Task.Factory.StartNew(HandleRequestSafe, e);
        }

        private static void HandleRequestSafe(object state)
        {
            var e = (RequestEventArgs) state;

            // you should really avoid crashing niobs worker thread or
            // a thread pool thread.
            try
            {
                HandleRequest(e);
            }
            catch
            {
                e.Response.SendError(500);
            }
        }

        private static void HandleRequest(RequestEventArgs e)
        {
            e.Response.StatusCode = 200;
            e.Response.StatusText = "kay";

            // cache for easier benchmarking against iis (caches files < 256 KB).
            if (_cache == null)
            {
                e.Response.ContentStream = new MemoryStream();

                e.Response.StartHtml("Niob");
                e.Response.AppendHtml("<h1>Niob</h1>");
                e.Response.AppendHtml("<p>Small and efficient webserver for embedding in your application.</p>");
                e.Response.AppendHtml("<p>This page is 128 KiB big.</p>");
                e.Response.AppendHtml("\n<!--|");
                e.Response.AppendHtml("".PadLeft(128*1024 - 235, ' '));
                e.Response.AppendHtml("|");
                e.Response.AppendHtml("-->\n");
                e.Response.EndHtml();

                _cache = ((MemoryStream) e.Response.ContentStream).ToArray();
            }
            else
            {
                e.Response.ContentStream = new MemoryStream(_cache);
            }

            e.Response.Send();
        }

        private static byte[] _cache;
    }
}