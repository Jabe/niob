using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Niob.Example
{
    internal class Program
    {
        public static string con = @"[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]";

        private static void Main(string[] args)
        {
            var niob = new NiobServer();

            var binding = niob.Bindings.Add(IPAddress.Loopback, 666);
            //niob.Bindings.Add(IPAddress.Loopback, 666, true, new X509Certificate2(@"niob.cer"));

            binding.SupportsKeepAlive = false;

            niob.RequestAccepted += HandleRequestAsync;

            niob.Start();

            Console.WriteLine("started... press enter to stop");
            Console.ReadLine();

            Console.WriteLine("stopping");
            niob.Stop();
            Console.WriteLine("stopped");
        }

        private static void HandleRequestAsync(object sender, RequestEventArgs e)
        {
            // this is just an example, you don't need to do that.
            // however if you don't your worker thread(s) will be clogged.
            Task.Factory.StartNew(HandleRequest, e);
        }

        private static void HandleRequest(object state)
        {
            var e = (RequestEventArgs) state;

            e.Response.StatusCode = 200;
            e.Response.StatusText = "kay";
            e.Response.Version = HttpVersion.Http10;

            e.Response.ContentType = "application/json";
            e.Response.ContentCharSet = "utf-8";
            e.Response.ContentStream = new MemoryStream(Encoding.UTF8.GetBytes(con));

            e.Response.Send();
        }
    }
}