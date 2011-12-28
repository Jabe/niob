using System;

namespace Niob
{
    public class RequestEventArgs : EventArgs
    {
        private readonly ConnectionHandle _connectionHandle;

        public RequestEventArgs(ConnectionHandle connectionHandle)
        {
            _connectionHandle = connectionHandle;

            // copy the refs
            Request = _connectionHandle.Request;
            Response = _connectionHandle.Response;
        }

        public HttpRequest Request { get; private set; }
        public HttpResponse Response { get; private set; }
    }
}