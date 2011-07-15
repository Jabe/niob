using System;

namespace Niob
{
    public class RequestEventArgs : EventArgs
    {
        private readonly ClientState _clientState;

        public RequestEventArgs(ClientState clientState)
        {
            _clientState = clientState;

            // copy the refs
            Request = _clientState.Request;
            Response = _clientState.Response;
        }

        public HttpRequest Request { get; private set; }
        public HttpResponse Response { get; private set; }
    }
}