using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Niob
{
    public class Niob : IDisposable
    {
        public const int MaxHeaderSize = 0x2000;
        public const int ClientBufferSize = 0x1000;
        public const int TcpBackLogSize = 0x20;
        public const int BigFileThreshold = 0x100000;

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private static readonly Regex HeaderLineMerge = new Regex(@"\r\n[ \t]+");

        private readonly List<Binding> _bindings = new List<Binding>();
        private readonly ConcurrentDictionary<Guid, ClientState> _allClients = new ConcurrentDictionary<Guid, ClientState>();
        private readonly ConcurrentQueue<ClientState> _clients = new ConcurrentQueue<ClientState>();
        private readonly ConcurrentBag<Socket> _listeningSockets = new ConcurrentBag<Socket>();

        private readonly ManualResetEvent _stopping = new ManualResetEvent(false);
        private readonly AutoResetEvent _workPending = new AutoResetEvent(false);

        private readonly List<Thread> _threads = new List<Thread>();

        public List<Binding> Bindings
        {
            get { return _bindings; }
        }

        #region IDisposable Members

        public void Dispose()
        {
            if (!_stopping.WaitOne(0))
            {
                Stop();
            }
        }

        #endregion

        public event EventHandler<RequestEventArgs> RequestAccepted;

        public void Start()
        {
            int count = 1;

            for (int i = 0; i < count; i++)
            {
                var worker = new Thread(WorkerThread) {IsBackground = true};

                _threads.Add(worker);
                worker.Start();
            }

            foreach (Binding binding in Bindings)
            {
                var listener = new Thread(BindingThread) {IsBackground = true};

                _threads.Add(listener);
                listener.Start(binding);
            }

            var janitor = new Thread(HandleKeepAlive) {IsBackground = true};

            _threads.Add(janitor);
            janitor.Start();
        }

        internal void EnqueueAndKickWorkers(ClientState clientState)
        {
            _clients.Enqueue(clientState);
            KickWorkers();
        }

        private void KickWorkers()
        {
            _workPending.Set();
        }

        private void BindingThread(object state)
        {
            var binding = (Binding) state;

            var socket = new Socket(binding.IpAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            _listeningSockets.Add(socket);

            socket.Bind(new IPEndPoint(binding.IpAddress, binding.Port));
            socket.Listen(TcpBackLogSize);

            while (!_stopping.WaitOne(0))
            {
                Socket client;

                try
                {
                    client = socket.Accept();
                }
                catch (Exception)
                {
                    // closed
                    break;
                }

                var clientState = new ClientState(client, binding, this);
                clientState.AddOp(ClientStateOp.Reading);
                RecordActivity(clientState);
                AddNewClient(clientState);
                EnqueueAndKickWorkers(clientState);
            }
        }

        private void AddNewClient(ClientState clientState)
        {
            _allClients.TryAdd(clientState.Id, clientState);
        }

        private void HandleKeepAlive()
        {
            const int wait = 1000;

            while (!_stopping.WaitOne(wait))
            {
                long threshold = GetTimestamp() - Timeout.Ticks;

                // check sockets for timeouts.
                foreach (var pair in _allClients)
                {
                    ClientState clientState = pair.Value;

                    if (clientState.Disposed)
                    {
                        RemoveClient(clientState);
                    }

                    if (clientState.LastActivity < threshold)
                    {
                        EndRequest(clientState);
                    }
                }
            }
        }

        private void WorkerThread()
        {
            // Scan queue
            while (!_stopping.WaitOne(0))
            {
                ClientState clientState;

                _clients.TryDequeue(out clientState);

                if (clientState == null)
                {
                    const int maxIdleTime = 1000;

                    _workPending.WaitOne(maxIdleTime);
                    continue;
                }

                WorkerThreadImpl(clientState);
            }
        }

        private string WorkerThreadImpl(ClientState clientState)
        {
            if (!clientState.HasOp(ClientStateOp.Ready))
            {
                RecordActivity(clientState);
                clientState.AsyncInitialize(EnqueueAndKickWorkers, DropRequest);

                return "AsyncInitialize";
            }

            if (clientState.HasOp(ClientStateOp.Reading))
            {
                clientState.RemoveOp(ClientStateOp.Reading);
                RecordActivity(clientState);

                bool continueRead;

                try
                {
                    continueRead = ShouldContinueReading(clientState);
                }
                catch (Exception)
                {
                    EndRequest(clientState);
                    return "IsReading -> end.";
                }

                if (continueRead)
                {
                    RecordActivity(clientState);
                    ReadAsync(clientState);
                    return "IsReading -> ReadAsync";
                }

                if (clientState.HasOp(ClientStateOp.ExpectingContinue))
                {
                    EnqueueAndKickWorkers(clientState);
                    return "IsReading -> IsExpectingContinue";
                }

                clientState.AddOp(ClientStateOp.Rendering);
                EnqueueAndKickWorkers(clientState);

                return "IsReading -> IsRendering";
            }

            if (clientState.HasOp(ClientStateOp.Writing))
            {
                clientState.RemoveOp(ClientStateOp.Writing);
                RecordActivity(clientState);

                long bytesToSend = clientState.OutStream.Length;
                long bytesSent = clientState.OutStream.Position;

                if (bytesToSend > bytesSent)
                {
                    RecordActivity(clientState);
                    WriteAsync(clientState);

                    return "IsWriting -> WriteAsync";
                }
                
                if (clientState.HasOp(ClientStateOp.PostExpectingContinue))
                {
                    EnqueueAndKickWorkers(clientState);
                    return "IsWriting -> IsPostExpectingContinue";
                }

                if (clientState.KeepAlive)
                {
                    clientState.Clear();

                    RecordActivity(clientState);
                    clientState.AddOp(ClientStateOp.KeepingAlive);

                    EnqueueAndKickWorkers(clientState);

                    return "IsWriting -> IsKeepingAlive";
                }

                // end.
                EndRequest(clientState);
                return "IsWriting -> end";
            }

            if (clientState.HasOp(ClientStateOp.KeepingAlive))
            {
                clientState.RemoveOp(ClientStateOp.KeepingAlive);
                RecordActivity(clientState);

                ReadAsync(clientState);

                return "IsKeepingAlive -> ReadAsync";
            }

            if (clientState.HasOp(ClientStateOp.Rendering))
            {
                clientState.RemoveOp(ClientStateOp.Rendering);
                RecordActivity(clientState);

                // revert content stream
                clientState.Request.ContentStream.Seek(0, SeekOrigin.Begin);

                clientState.Response = new HttpResponse(clientState);

                bool hasHandler = OnRequestAccepted(clientState);

                if (!hasHandler)
                {
                    RecordActivity(clientState);
                    clientState.Response.Send();
                }

                return "IsRendering -> OnRequestAccepted";
            }

            if (clientState.HasOp(ClientStateOp.PostRendering))
            {
                clientState.RemoveOp(ClientStateOp.PostRendering);
                RecordActivity(clientState);

                clientState.OutStream = new MemoryStream();
                clientState.Response.WriteHeaders(clientState.OutStream);

                if (clientState.Response.ContentStream != null)
                {
                    clientState.Response.ContentStream.CopyTo(clientState.OutStream);
                }

                clientState.OutStream.Flush();
                clientState.OutStream.Seek(0, SeekOrigin.Begin);

                clientState.AddOp(ClientStateOp.Writing);
                EnqueueAndKickWorkers(clientState);

                return "IsPostRendering -> IsWriting";
            }

            if (clientState.HasOp(ClientStateOp.ExpectingContinue))
            {
                clientState.RemoveOp(ClientStateOp.ExpectingContinue);
                clientState.OutStream = new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
                clientState.AddOp(ClientStateOp.Writing);
                clientState.AddOp(ClientStateOp.PostExpectingContinue);
                EnqueueAndKickWorkers(clientState);
                return "IsExpectingContinue -> IsReading|IsPostExpectingContinue";
            }

            if (clientState.HasOp(ClientStateOp.PostExpectingContinue))
            {
                clientState.RemoveOp(ClientStateOp.PostExpectingContinue);
                clientState.AddOp(ClientStateOp.Reading);
                EnqueueAndKickWorkers(clientState);
                return "IsPostExpectingContinue -> IsReading";
            }

            Console.WriteLine("Unknown state.");
            DropRequest(clientState);

            return "Unknown -> end";
        }

        private bool OnRequestAccepted(ClientState clientState)
        {
            EventHandler<RequestEventArgs> handler = RequestAccepted;

            if (handler != null)
            {
                var eventArgs = new RequestEventArgs(clientState);

                handler(this, eventArgs);

                return true;
            }

            return false;
        }

        private void ReadAsync(ClientState clientState)
        {
            try
            {
                clientState.Stream.BeginRead(clientState.Buffer, 0, clientState.Buffer.Length, ReadCallback, clientState);
            }
            catch (Exception)
            {
                // e.g. when clients close their KA socket
                DropRequest(clientState);
                return;
            }
        }

        private void WriteAsync(ClientState clientState)
        {
            long bytesToSend = clientState.OutStream.Length;
            long bytesSent = clientState.OutStream.Position;

            var size = (int) Math.Min(bytesToSend - bytesSent, ClientBufferSize);

            size = clientState.OutStream.Read(clientState.Buffer, 0, size);

            try
            {
                clientState.Stream.BeginWrite(clientState.Buffer, 0, size, WriteCallback, clientState);
            }
            catch (Exception)
            {
                DropRequest(clientState);
                return;
            }
        }

        private void WriteCallback(IAsyncResult ar)
        {
            var clientState = (ClientState) ar.AsyncState;

            if (clientState.Disposed)
                return;

            try
            {
                clientState.Stream.EndWrite(ar);
            }
            catch (Exception)
            {
                DropRequest(clientState);
                return;
            }

            clientState.AddOp(ClientStateOp.Writing);
            EnqueueAndKickWorkers(clientState);
        }

        private void ReadCallback(IAsyncResult ar)
        {
            var clientState = (ClientState) ar.AsyncState;

            if (clientState.Disposed)
                return;

            int inBytes;

            try
            {
                inBytes = clientState.Stream.EndRead(ar);
            }
            catch (Exception)
            {
                // e.g. when clients close their KA socket
                DropRequest(clientState);
                return;
            }

            if (inBytes == 0)
            {
                // eos
                EndRequest(clientState);
                return;
            }

            try
            {
                // copy to current stream
                Stream stream = (clientState.HeaderLength == -1)
                                    ? clientState.HeaderStream
                                    : clientState.ContentStream;

                stream.Write(clientState.Buffer, 0, inBytes);

                clientState.BytesRead += inBytes;
            }
            catch (ObjectDisposedException)
            {
                DropRequest(clientState);
                return;
            }

            clientState.AddOp(ClientStateOp.Reading);
            EnqueueAndKickWorkers(clientState);
        }

        private void EndRequest(ClientState clientState)
        {
            try
            {
                RemoveClient(clientState);

                if (!clientState.Disposed)
                {
                    clientState.Socket.Shutdown(SocketShutdown.Both);
                    clientState.Socket.Close();
                }

                using (clientState)
                {
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("error while ending a request: " + e);
            }
        }

        private void DropRequest(ClientState clientState)
        {
            try
            {
                RemoveClient(clientState);

                if (!clientState.Disposed)
                {
                    clientState.Socket.Close();
                }

                using (clientState)
                {
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("error while dropping a request: " + e);
            }
        }

        private void RemoveClient(ClientState clientState)
        {
            ClientState removed;
            _allClients.TryRemove(clientState.Id, out removed);
        }

        private static bool ShouldContinueReading(ClientState clientState)
        {
            bool continueRead = false;

            // check if we got the complete header
            if (clientState.HeaderLength == -1)
            {
                int headerLength = -1;
                long contentLength = -1;
                bool keepAlive = clientState.Binding.SupportsKeepAlive;

                byte[] headerBytes = clientState.HeaderStream.ToArray();

                if (headerBytes.Length >= 4)
                {
                    for (int i = headerBytes.Length - 4; i >= Math.Max(0, headerBytes.Length - ClientBufferSize - 4); i--)
                    {
                        if (headerBytes[i + 0] == '\r' && headerBytes[i + 1] == '\n' &&
                            headerBytes[i + 2] == '\r' && headerBytes[i + 3] == '\n')
                        {
                            headerLength = i + 4;
                            break;
                        }
                    }
                }

                if (headerLength == -1)
                {
                    // no header end found
                    // check max header length

                    if (headerBytes.Length > MaxHeaderSize)
                    {
                        throw new ProtocolViolationException("Header too big.");
                    }

                    continueRead = true;
                }
                else
                {
                    clientState.Request = new HttpRequest(clientState);

                    // header found. read the content-length http header
                    // to determine if we should continue reading.
                    string header = Encoding.ASCII.GetString(headerBytes, 0, headerLength);

                    // merge continuations
                    header = HeaderLineMerge.Replace(header, " ");

                    string[] lines = header.Split(new[] {"\r\n"}, StringSplitOptions.RemoveEmptyEntries);

                    clientState.Request.ReadHeader(lines);

                    string contentLengthHeader;

                    if (clientState.Request.Headers.TryGetValue("Content-Length", out contentLengthHeader))
                    {
                        if (!long.TryParse(contentLengthHeader, out contentLength))
                        {
                            contentLength = -1;
                        }
                    }

                    if (clientState.Binding.SupportsKeepAlive)
                    {
                        string connectionHeader;

                        if (clientState.Request.Headers.TryGetValue("Connection", out connectionHeader))
                        {
                            if (StringComparer.OrdinalIgnoreCase.Compare(connectionHeader, "keep-alive") < 0)
                            {
                                keepAlive = false;
                            }
                        }
                        else
                        {
                            // decide by version
                            keepAlive = (clientState.Request.Version != HttpVersion.Http10);
                        }
                    }

                    string expectHeader;

                    if (clientState.Request.Headers.TryGetValue("Expect", out expectHeader))
                    {
                        if (StringComparer.OrdinalIgnoreCase.Compare(expectHeader, "100-continue") >= 0)
                        {
                            clientState.AddOp(ClientStateOp.ExpectingContinue);
                        }
                    }

                    // init content stream
                    if (contentLength > BigFileThreshold)
                    {
                        clientState.ContentStreamFile = Path.GetTempFileName();
                        clientState.ContentStream = File.Create(clientState.ContentStreamFile);
                    }
                    else
                    {
                        clientState.ContentStream = new MemoryStream();
                    }

                    // check if some content got on the wrong stream
                    if (clientState.HeaderStream.Length > headerLength)
                    {
                        // fix it
                        clientState.HeaderStream.Seek(headerLength, SeekOrigin.Begin);
                        clientState.HeaderStream.CopyTo(clientState.ContentStream);
                        clientState.HeaderStream.SetLength(headerLength);
                    }

                    // we are done with the header stream
                    clientState.HeaderStream.SetLength(0);
                }

                clientState.HeaderLength = headerLength;
                clientState.ContentLength = contentLength;
                clientState.KeepAlive = keepAlive;
            }

            // we got the header...
            if (clientState.HeaderLength >= 0)
            {
                if (!clientState.HasOp(ClientStateOp.ExpectingContinue) && clientState.ContentLength >= 0)
                {
                    // we expect a payload ... check if we got all
                    if (clientState.BytesRead < clientState.ContentLength)
                    {
                        continueRead = true;
                    }
                }
                else
                {
                    // no payload
                    continueRead = false;
                }
            }

            return continueRead;
        }

        private static long GetTimestamp()
        {
            return DateTimeOffset.UtcNow.Ticks;
        }

        private static void RecordActivity(ClientState clientState)
        {
            clientState.LastActivity = GetTimestamp();
        }

        public void Stop()
        {
            _stopping.Set();

            while (!_listeningSockets.IsEmpty)
            {
                Socket socket;

                _listeningSockets.TryTake(out socket);

                using (socket)
                {
                    socket.Close();
                }
            }

            while (!_clients.IsEmpty)
            {
                ClientState state;

                _clients.TryDequeue(out state);

                EndRequest(state);
            }

            foreach (var kv in _allClients)
            {
                EndRequest(kv.Value);
            }

            _allClients.Clear();

            foreach (Thread thread in _threads)
            {
                thread.Join();
            }

            _threads.Clear();
        }
    }
}