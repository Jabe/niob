using System;
using System.IO;
using Niob.SimpleHtml;

namespace Niob.SimpleErrors
{
    public static class ErrorExtensions
    {
        public static void SendError(this HttpResponse response, ushort code)
        {
            if (response == null) throw new ArgumentNullException("response");

            switch (code)
            {
                case 400:
                    response.SendError(code, "Bad Request", "The request could not be understood due to malformed syntax.");
                    break;
                case 404:
                    response.SendError(code, "Not Found", "The requested resource could not be found.");
                    break;
                case 408:
                    response.SendError(code, "Request Timeout", "The server timed out waiting for your request.");
                    break;
                case 500:
                    response.SendError(code, "Internal Server Error", "The server has a boo-boo.");
                    break;
            }
        }

        public static void SendError(this HttpResponse response, ushort statusCode, string statusText, string desc)
        {
            if (response == null) throw new ArgumentNullException("response");

            response.StatusCode = statusCode;
            response.StatusText = statusText;

            response.ContentStream = new MemoryStream();

            response.StartHtml("Error!");
            response.AppendHtml("<h1>{0} ({1})</h1>", statusText, statusCode);
            response.AppendHtml("<p>{0}</p>", desc);
            response.EndHtml();

            response.Send();
        }
    }
}