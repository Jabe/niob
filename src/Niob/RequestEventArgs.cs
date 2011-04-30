using System;

namespace Niob
{
    public class RequestEventArgs : EventArgs
    {
        private readonly ClientState _clientState;

        public RequestEventArgs(ClientState clientState)
        {
            _clientState = clientState;
        }

        public HttpRequest Request
        {
            get { return _clientState.Request; }
        }

        public HttpResponse Response
        {
            get { return _clientState.Response; }
        }
    }
}