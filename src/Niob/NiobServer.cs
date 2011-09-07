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
using Niob.SimpleErrors;

namespace Niob
{
    public class NiobServer : IDisposable
    {
        public const int MaxHeaderSize = 0x2000;
        public const int ClientBufferSize = 0x8000;
        public const int TcpBackLogSize = 0x40;
        public const int BigFileThreshold = 0x100000;

        private static readonly string[] CrLfArray = new[] {"\r\n"};
        private static readonly Regex HeaderLineMerge = new Regex(@"\r\n[ \t]+");

        private readonly List<Binding> _bindings = new List<Binding>();
        private readonly ConcurrentDictionary<Guid, ClientState> _allClients = new ConcurrentDictionary<Guid, ClientState>();
        private readonly ConcurrentQueue<ClientState> _clients = new ConcurrentQueue<ClientState>();
        private readonly ConcurrentBag<Socket> _listeningSockets = new ConcurrentBag<Socket>();
        private readonly List<Thread> _threads = new List<Thread>();
        private readonly TimedCounter _dosCounter;

        private bool _disposed;
        private CancellationTokenSource _stopSource;
        private AutoResetEvent _workPending = new AutoResetEvent(false);

        public NiobServer(int dosPeriodInSeconds = 20, int dosThreshold = 100)
        {
            ReadWriteTimeout = 20;
            RenderingTimeout = 600;
            KeepAliveDuration = 100;
            WorkerThreadCount = 2;
            SupportsKeepAlive = true;

            RenderTimeout += (sender, e) => e.Response.SendError(500);

            // dos stuff
            DosThreshold = dosThreshold;
            DosPeriod = dosPeriodInSeconds;

            _dosCounter = new TimedCounter(TimeSpan.FromSeconds(DosPeriod));
        }

        public List<Binding> Bindings
        {
            get { return _bindings; }
        }

        #region IDisposable Members

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (!_stopSource.IsCancellationRequested)
            {
                Stop();
            }

            if (_workPending != null)
            {
                _workPending.Dispose();
                _workPending = null;
            }
        }

        #endregion

        public event EventHandler<RequestEventArgs> RequestAccepted;
        public event EventHandler<RequestEventArgs> RenderTimeout;

        public int ReadWriteTimeout { get; set; }
        public int RenderingTimeout { get; set; }
        public int KeepAliveDuration { get; set; }
        public int WorkerThreadCount { get; set; }
        public bool SupportsKeepAlive { get; set; }
        public int DosPeriod { get; private set; }
        public int DosThreshold { get; set; }

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);

            if (_stopSource != null)
                return;

            _stopSource = new CancellationTokenSource();

            for (int i = 0; i < WorkerThreadCount; i++)
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

            var janitor = new Thread(JanitorThread) {IsBackground = true};

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

            var args = new SocketAsyncEventArgs();
            args.Completed += (sender, e) => AcceptCallback(socket, binding, args, true);

            SocketAccept(socket, binding, args);
        }

        private void SocketAccept(Socket socket, Binding binding, SocketAsyncEventArgs e)
        {
            while (true)
            {
                bool async;

                try
                {
                    async = socket.AcceptAsync(e);
                }
                catch
                {
                    // closed
                    return;
                }

                // this pattern prevents stack overflows when we always return sync.
                if (async)
                {
                    break;
                }

                // completed sync
                AcceptCallback(socket, binding, e, false);
            }
        }

        private void AcceptCallback(Socket socket, Binding binding, SocketAsyncEventArgs e, bool async)
        {
            // only one operation support yet, hence the name
            if (e.LastOperation == SocketAsyncOperation.Accept)
            {
                Socket client = e.AcceptSocket;

                if (e.SocketError == SocketError.Success && client != null)
                {
                    // clear state so we can reuse the state object
                    e.AcceptSocket = null;

                    IPEndPoint ipep;

                    try
                    {
                        ipep = (IPEndPoint) client.RemoteEndPoint;
                    }
                    catch (Exception)
                    {
                        // closed
                        return;
                    }

                    if (IsDos(ipep))
                    {
                        try
                        {
                            client.Close();
                            client.Dispose();
                        }
                        catch (Exception)
                        {
                        }

                        return;
                    }

                    client.SendBufferSize = ClientBufferSize;
                    client.ReceiveBufferSize = ClientBufferSize;

                    var clientState = new ClientState(client, binding, this);
                    clientState.AddOp(ClientStateOp.Reading);
                    RecordActivity(clientState);
                    AddNewClient(clientState);
                    EnqueueAndKickWorkers(clientState);
                }

                if (async)
                {
                    // start new accept, but only if we didnt ran sync
                    SocketAccept(socket, binding, e);
                }
            }
        }

        private bool IsDos(IPEndPoint ipep)
        {
            string token = ipep.Address.ToString();
            int count = _dosCounter.GetCount(token);

            if (count > DosThreshold)
            {
                // dos
                return true;
            }

            _dosCounter.Increment(token);

            return false;
        }

        private void AddNewClient(ClientState clientState)
        {
            _allClients.TryAdd(clientState.Id, clientState);
        }

        private void JanitorThread()
        {
            const int wait = 1000;

            while (!_stopSource.Token.WaitHandle.WaitOne(wait))
            {
                long rwThreshold = GetTimestamp() - TimeSpan.FromSeconds(ReadWriteTimeout).Ticks;
                long reThreshold = GetTimestamp() - TimeSpan.FromSeconds(RenderingTimeout).Ticks;
                long kaThreshold = GetTimestamp() - TimeSpan.FromSeconds(KeepAliveDuration).Ticks;

                // check sockets for timeouts.
                foreach (var pair in _allClients)
                {
                    ClientState clientState = pair.Value;

                    if (clientState.Disposed)
                    {
                        RemoveClient(clientState);
                        continue;
                    }

                    if (clientState.HasOp(ClientStateOp.KeepingAlive))
                    {
                        if (clientState.LastActivity < kaThreshold)
                        {
                            EndRequest(clientState);
                        }
                    }
                    else if (clientState.HasOp(ClientStateOp.Rendering))
                    {
                        if (clientState.LastActivity < reThreshold)
                        {
                            OverrideResponse(clientState);
                            OnRenderTimeout(clientState);
                        }
                    }
                    else if (clientState.LastActivity < rwThreshold)
                    {
                        if (clientState.HasOp(ClientStateOp.Reading))
                        {
                            OverrideResponse(clientState).SendError(408);
                        }
                        else
                        {
                            EndRequest(clientState);
                        }
                    }
                }
            }
        }

        private void WorkerThread()
        {
            // check the stopping flag
            while (!_stopSource.IsCancellationRequested)
            {
                ClientState clientState;

                // got work?
                if (!_clients.TryDequeue(out clientState) || clientState == null)
                {
                    // no work. wait for the "there might be something of interest" signal to fire.
                    _workPending.WaitOne();
                    continue;
                }

                WorkerThreadImpl(clientState);
            }

            // this thread exited. notify the others. this ensures clean shutdown.
            KickWorkers();
        }

        private string WorkerThreadImpl(ClientState clientState)
        {
            if (!clientState.HasOp(ClientStateOp.Ready))
            {
                RecordActivity(clientState);
                clientState.AsyncInitialize(EnqueueAndKickWorkers, x => EndRequest(x, true));

                return "AsyncInitialize";
            }

            if (clientState.HasOp(ClientStateOp.Reading))
            {
                RecordActivity(clientState);

                bool continueRead;

                try
                {
                    continueRead = ShouldContinueReading(clientState);
                }
                catch (Exception)
                {
                    OverrideResponse(clientState).SendError(400);
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
                    clientState.RemoveOp(ClientStateOp.Reading);
                    EnqueueAndKickWorkers(clientState);
                    return "IsReading -> IsExpectingContinue";
                }

                clientState.RemoveOp(ClientStateOp.Reading);
                clientState.AddOp(ClientStateOp.Rendering);
                EnqueueAndKickWorkers(clientState);

                return "IsReading -> IsRendering";
            }

            if (clientState.HasOp(ClientStateOp.Writing))
            {
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
                    clientState.RemoveOp(ClientStateOp.Writing);
                    EnqueueAndKickWorkers(clientState);
                    return "IsWriting -> IsPostExpectingContinue";
                }

                if (clientState.Response != null && clientState.Response.KeepAlive)
                {
                    clientState.RemoveOp(ClientStateOp.Writing);
                    clientState.Clear();

                    clientState.AddOp(ClientStateOp.KeepingAlive);
                    RecordActivity(clientState);
                    ReadAsync(clientState);

                    return "IsWriting -> ReadAsync (IsKeepingAlive)";
                }

                // end.
                clientState.RemoveOp(ClientStateOp.Writing);
                EndRequest(clientState);
                return "IsWriting -> end";
            }

            if (clientState.HasOp(ClientStateOp.PostRendering))
            {
                clientState.RemoveOp(ClientStateOp.Rendering);
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

            if (clientState.HasOp(ClientStateOp.Rendering))
            {
                RecordActivity(clientState);

                // revert content stream
                clientState.Request.ContentStream.Seek(0, SeekOrigin.Begin);
                clientState.Response = new HttpResponse(clientState);

                bool hasHandler = OnRequestAccepted(clientState);

                if (!hasHandler)
                {
                    clientState.Response.SendError(500);
                    return "IsRendering -> ServerError";
                }

                return "IsRendering -> OnRequestAccepted";
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
            EndRequest(clientState, true);

            return "Unknown -> end";
        }

        private bool OnRequestAccepted(ClientState clientState)
        {
            EventHandler<RequestEventArgs> handler = RequestAccepted;

            if (handler == null)
            {
                return false;
            }

            var eventArgs = new RequestEventArgs(clientState);

            try
            {
                handler(this, eventArgs);
            }
            catch
            {
                return false;
            }

            return true;
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
                EndRequest(clientState);
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
                EndRequest(clientState);
                return;
            }
        }

        private void WriteCallback(IAsyncResult ar)
        {
            var clientState = (ClientState) ar.AsyncState;

            clientState.RemoveOp(ClientStateOp.Writing);

            if (clientState.Disposed)
                return;

            try
            {
                clientState.Stream.EndWrite(ar);
            }
            catch (Exception)
            {
                EndRequest(clientState);
                return;
            }

            clientState.AddOp(ClientStateOp.Writing);
            EnqueueAndKickWorkers(clientState);
        }

        private void ReadCallback(IAsyncResult ar)
        {
            var clientState = (ClientState) ar.AsyncState;

            clientState.RemoveOp(ClientStateOp.Reading);

            if (clientState.Disposed)
                return;

            if (clientState.HasOp(ClientStateOp.KeepingAlive))
            {
                clientState.RemoveOp(ClientStateOp.KeepingAlive);
            }

            int inBytes;

            try
            {
                inBytes = clientState.Stream.EndRead(ar);
            }
            catch (Exception)
            {
                // e.g. when clients close their KA socket
                EndRequest(clientState);
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
                EndRequest(clientState);
                return;
            }

            clientState.AddOp(ClientStateOp.Reading);
            EnqueueAndKickWorkers(clientState);
        }

        private void EndRequest(ClientState clientState, bool rst = false)
        {
            try
            {
                RemoveClient(clientState);

                if (!clientState.Disposed)
                {
                    if (!rst)
                    {
                        try
                        {
                            clientState.Socket.Shutdown(SocketShutdown.Both);
                        }
                        catch
                        {
                        }
                    }

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

        private void RemoveClient(ClientState clientState)
        {
            ClientState removed;
            _allClients.TryRemove(clientState.Id, out removed);
        }

        private static bool ShouldContinueReading(ClientState clientState)
        {
            // shortcut
            if (clientState.HeaderLength == -1 && clientState.HeaderStream.Length == 0)
                return true;

            bool continueRead = false;

            // check if we got the complete header
            if (clientState.HeaderLength == -1)
            {
                int headerLength = -1;
                long contentLength = -1;
                int scanStart = clientState.LastHeaderEndOffset;

                // revert 3 bytes
                scanStart -= 3;

                if (scanStart < 0)
                    scanStart = 0;

                // ref to internal buffer
                byte[] buffer = clientState.HeaderStream.GetBuffer();

                int seq = 0;

                for (int i = scanStart; i < clientState.HeaderStream.Length; i++)
                {
                    if (seq == 0 && buffer[i] == '\r')
                    {
                        seq++;
                    }
                    else if (seq == 1 && buffer[i] == '\n')
                    {
                        seq++;
                    }
                    else if (seq == 2 && buffer[i] == '\r')
                    {
                        seq++;
                    }
                    else if (seq == 3 && buffer[i] == '\n')
                    {
                        seq++;
                        headerLength = i + 1;
                        break;
                    }
                    else
                    {
                        seq = 0;
                    }
                }

                if (seq != 4)
                {
                    headerLength = -1;
                }

                clientState.LastHeaderEndOffset = (int) clientState.HeaderStream.Length;

                if (headerLength == -1)
                {
                    // no header end found
                    // check max header length

                    if (clientState.HeaderStream.Length > MaxHeaderSize)
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
                    string header = Encoding.ASCII.GetString(buffer, 0, headerLength);

                    // merge continuations
                    header = HeaderLineMerge.Replace(header, " ");

                    string[] lines = header.Split(CrLfArray, StringSplitOptions.RemoveEmptyEntries);

                    clientState.Request.ReadHeader(lines);

                    string contentLengthHeader;

                    if (clientState.Request.Headers.TryGetValue("Content-Length", out contentLengthHeader))
                    {
                        if (!long.TryParse(contentLengthHeader, out contentLength))
                        {
                            contentLength = -1;
                        }
                    }

                    string expectHeader;

                    if (clientState.Request.Headers.TryGetValue("Expect", out expectHeader))
                    {
                        if (StringComparer.OrdinalIgnoreCase.Equals(expectHeader, "100-continue"))
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
                    }

                    // we are done with the header stream
                    clientState.HeaderStream.Position = 0;
                }

                clientState.HeaderLength = headerLength;
                clientState.ContentLength = contentLength;
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
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);

            if (_stopSource == null)
                return;

            // first close the listeners
            while (!_listeningSockets.IsEmpty)
            {
                Socket socket;

                _listeningSockets.TryTake(out socket);

                using (socket)
                {
                    socket.Close();
                }
            }

            // cancel the workers
            _stopSource.Cancel();
            KickWorkers();

            foreach (Thread thread in _threads)
            {
                thread.Join();
            }

            _threads.Clear();

            // clear all queued client states
            while (!_clients.IsEmpty)
            {
                ClientState state;

                _clients.TryDequeue(out state);

                EndRequest(state);
            }

            // clear all client states
            foreach (var kv in _allClients)
            {
                EndRequest(kv.Value);
            }

            _allClients.Clear();

            _stopSource.Dispose();
            _stopSource = null;
        }

        private void OnRenderTimeout(ClientState clientState)
        {
            var handler = RenderTimeout;

            if (handler != null)
            {
                RenderTimeout(this, new RequestEventArgs(clientState));
            }
            else
            {
                EndRequest(clientState);
            }
        }

        private static HttpResponse OverrideResponse(ClientState clientState)
        {
            if (clientState.Response != null)
                clientState.Response.Vetoed = true;

            return clientState.Response = new HttpResponse(clientState);
        }
    }
}