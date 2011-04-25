using System;

namespace Niob
{
    public class RequestEventArgs : EventArgs
    {
        private readonly ClientState _clientState;

        public RequestEventArgs(ClientState clientState)
        {
            _clientState = clientState;

            Response = new HttpResponse();
        }

        public HttpResponse Response { get; private set; }
    }
}