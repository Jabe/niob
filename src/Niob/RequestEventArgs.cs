using System;

namespace Niob
{
    public class RequestEventArgs : EventArgs
    {
        public RequestEventArgs(ClientState clientState)
        {
            Response = new HttpResponse(clientState);
        }

        public HttpResponse Response { get; private set; }
    }
}