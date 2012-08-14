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
        private readonly ConcurrentDictionary<Guid, ConnectionHandle> _allClients = new ConcurrentDictionary<Guid, ConnectionHandle>();
        private readonly ConcurrentQueue<ConnectionHandle> _clients = new ConcurrentQueue<ConnectionHandle>();
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

            if (_stopSource != null && !_stopSource.IsCancellationRequested)
            {
                Stop();
            }

            _disposed = true;

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

        internal void EnqueueAndKickWorkers(ConnectionHandle connectionHandle)
        {
            _clients.Enqueue(connectionHandle);
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

                    var clientState = new ConnectionHandle(client, binding, this);
                    clientState.AddState(ClientState.Reading);
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

        private void AddNewClient(ConnectionHandle connectionHandle)
        {
            _allClients.TryAdd(connectionHandle.Id, connectionHandle);
        }

        private void JanitorThread()
        {
            const int wait = 1000;

            while (!_stopSource.Token.WaitHandle.WaitOne(wait))
            {
                long ts = GetTimestamp();
                long rwThreshold = ts - ReadWriteTimeout*10000000;
                long reThreshold = ts - RenderingTimeout*10000000;
                long kaThreshold = ts - KeepAliveDuration*10000000;

                // check sockets for timeouts.
                foreach (var pair in _allClients)
                {
                    ConnectionHandle connectionHandle = pair.Value;

                    if (connectionHandle.Disposed)
                    {
                        RemoveClient(connectionHandle);
                        continue;
                    }

                    if (connectionHandle.HasState(ClientState.KeepingAlive))
                    {
                        if (connectionHandle.LastActivity < kaThreshold)
                        {
                            EndRequest(connectionHandle);
                        }
                    }
                    else if (connectionHandle.HasState(ClientState.Rendering))
                    {
                        if (connectionHandle.LastActivity < reThreshold)
                        {
                            OverrideResponse(connectionHandle);
                            OnRenderTimeout(connectionHandle);
                        }
                    }
                    else if (connectionHandle.LastActivity < rwThreshold)
                    {
                        if (connectionHandle.HasState(ClientState.Reading))
                        {
                            OverrideResponse(connectionHandle).SendError(408);
                        }
                        else
                        {
                            EndRequest(connectionHandle);
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
                ConnectionHandle connectionHandle;

                // got work?
                if (!_clients.TryDequeue(out connectionHandle) || connectionHandle == null)
                {
                    // no work. wait for the "there might be something of interest" signal to fire.
                    _workPending.WaitOne();
                    continue;
                }

                WorkerThreadImpl(connectionHandle);
            }

            // this thread exited. notify the others. this ensures clean shutdown.
            KickWorkers();
        }

        private string WorkerThreadImpl(ConnectionHandle connectionHandle)
        {
            if (!connectionHandle.HasState(ClientState.Ready))
            {
                RecordActivity(connectionHandle);
                connectionHandle.AsyncInitialize(EnqueueAndKickWorkers, x => EndRequest(x, true));

                return "AsyncInitialize";
            }

            if (connectionHandle.HasState(ClientState.Reading))
            {
                RecordActivity(connectionHandle);

                bool continueRead;

                try
                {
                    continueRead = ShouldContinueReading(connectionHandle);
                }
                catch (Exception)
                {
                    OverrideResponse(connectionHandle).SendError(400);
                    return "IsReading -> end.";
                }

                if (continueRead)
                {
                    RecordActivity(connectionHandle);
                    ReadAsync(connectionHandle);
                    return "IsReading -> ReadAsync";
                }

                if (connectionHandle.HasState(ClientState.ExpectingContinue))
                {
                    connectionHandle.RemoveState(ClientState.Reading);
                    EnqueueAndKickWorkers(connectionHandle);
                    return "IsReading -> IsExpectingContinue";
                }

                connectionHandle.RemoveState(ClientState.Reading);
                connectionHandle.AddState(ClientState.Rendering);
                EnqueueAndKickWorkers(connectionHandle);

                return "IsReading -> IsRendering";
            }

            if (connectionHandle.HasState(ClientState.Writing))
            {
                RecordActivity(connectionHandle);

                long bytesToSend = connectionHandle.OutStream.Length;
                long bytesSent = connectionHandle.OutStream.Position;

                if (bytesToSend > bytesSent)
                {
                    RecordActivity(connectionHandle);
                    WriteAsync(connectionHandle);

                    return "IsWriting -> WriteAsync";
                }
                
                if (connectionHandle.HasState(ClientState.PostExpectingContinue))
                {
                    connectionHandle.RemoveState(ClientState.Writing);
                    EnqueueAndKickWorkers(connectionHandle);
                    return "IsWriting -> IsPostExpectingContinue";
                }

                if (connectionHandle.Response != null && connectionHandle.Response.KeepAlive)
                {
                    connectionHandle.RemoveState(ClientState.Writing);
                    connectionHandle.Clear();

                    connectionHandle.AddState(ClientState.KeepingAlive);
                    RecordActivity(connectionHandle);
                    ReadAsync(connectionHandle);

                    return "IsWriting -> ReadAsync (IsKeepingAlive)";
                }

                // end.
                connectionHandle.RemoveState(ClientState.Writing);
                EndRequest(connectionHandle);
                return "IsWriting -> end";
            }

            if (connectionHandle.HasState(ClientState.PostRendering))
            {
                connectionHandle.RemoveState(ClientState.Rendering);
                connectionHandle.RemoveState(ClientState.PostRendering);
                RecordActivity(connectionHandle);

                connectionHandle.OutStream = new MemoryStream();
                connectionHandle.Response.WriteHeaders(connectionHandle.OutStream);

                if (connectionHandle.Response.ContentStream != null)
                {
                    connectionHandle.Response.ContentStream.CopyTo(connectionHandle.OutStream);
                }

                connectionHandle.OutStream.Flush();
                connectionHandle.OutStream.Seek(0, SeekOrigin.Begin);

                connectionHandle.AddState(ClientState.Writing);
                EnqueueAndKickWorkers(connectionHandle);

                return "IsPostRendering -> IsWriting";
            }

            if (connectionHandle.HasState(ClientState.Rendering))
            {
                RecordActivity(connectionHandle);

                // revert content stream
                connectionHandle.Request.ContentStream.Seek(0, SeekOrigin.Begin);
                connectionHandle.Response = new HttpResponse(connectionHandle);

                bool hasHandler = OnRequestAccepted(connectionHandle);

                if (!hasHandler)
                {
                    connectionHandle.Response.SendError(500);
                    return "IsRendering -> ServerError";
                }

                return "IsRendering -> OnRequestAccepted";
            }

            if (connectionHandle.HasState(ClientState.ExpectingContinue))
            {
                connectionHandle.RemoveState(ClientState.ExpectingContinue);
                connectionHandle.OutStream = new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
                connectionHandle.AddState(ClientState.Writing);
                connectionHandle.AddState(ClientState.PostExpectingContinue);
                EnqueueAndKickWorkers(connectionHandle);
                return "IsExpectingContinue -> IsReading|IsPostExpectingContinue";
            }

            if (connectionHandle.HasState(ClientState.PostExpectingContinue))
            {
                connectionHandle.RemoveState(ClientState.PostExpectingContinue);
                connectionHandle.AddState(ClientState.Reading);
                EnqueueAndKickWorkers(connectionHandle);
                return "IsPostExpectingContinue -> IsReading";
            }

            Console.WriteLine("Unknown state.");
            EndRequest(connectionHandle, true);

            return "Unknown -> end";
        }

        private bool OnRequestAccepted(ConnectionHandle connectionHandle)
        {
            EventHandler<RequestEventArgs> handler = RequestAccepted;

            if (handler == null)
            {
                return false;
            }

            var eventArgs = new RequestEventArgs(connectionHandle);

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

        private void ReadAsync(ConnectionHandle connectionHandle)
        {
            try
            {
                connectionHandle.Stream.BeginRead(connectionHandle.Buffer, 0, connectionHandle.Buffer.Length, ReadCallback, connectionHandle);
            }
            catch (Exception)
            {
                // e.g. when clients close their KA socket
                EndRequest(connectionHandle);
                return;
            }
        }

        private void WriteAsync(ConnectionHandle connectionHandle)
        {
            long bytesToSend = connectionHandle.OutStream.Length;
            long bytesSent = connectionHandle.OutStream.Position;

            var size = (int) Math.Min(bytesToSend - bytesSent, ClientBufferSize);

            size = connectionHandle.OutStream.Read(connectionHandle.Buffer, 0, size);

            try
            {
                connectionHandle.Stream.BeginWrite(connectionHandle.Buffer, 0, size, WriteCallback, connectionHandle);
            }
            catch (Exception)
            {
                EndRequest(connectionHandle);
                return;
            }
        }

        private void WriteCallback(IAsyncResult ar)
        {
            var clientState = (ConnectionHandle) ar.AsyncState;

            clientState.RemoveState(ClientState.Writing);

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

            clientState.AddState(ClientState.Writing);
            EnqueueAndKickWorkers(clientState);
        }

        private void ReadCallback(IAsyncResult ar)
        {
            var clientState = (ConnectionHandle) ar.AsyncState;

            clientState.RemoveState(ClientState.Reading);

            if (clientState.Disposed)
                return;

            if (clientState.HasState(ClientState.KeepingAlive))
            {
                clientState.RemoveState(ClientState.KeepingAlive);
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

            clientState.AddState(ClientState.Reading);
            EnqueueAndKickWorkers(clientState);
        }

        private void EndRequest(ConnectionHandle connectionHandle, bool rst = false)
        {
            try
            {
                RemoveClient(connectionHandle);

                if (!connectionHandle.Disposed)
                {
                    if (!rst)
                    {
                        try
                        {
                            connectionHandle.Socket.Shutdown(SocketShutdown.Both);
                        }
                        catch
                        {
                        }
                    }

                    connectionHandle.Socket.Close();
                }

                using (connectionHandle)
                {
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("error while ending a request: " + e);
            }
        }

        private void RemoveClient(ConnectionHandle connectionHandle)
        {
            ConnectionHandle removed;
            _allClients.TryRemove(connectionHandle.Id, out removed);
        }

        private static bool ShouldContinueReading(ConnectionHandle connectionHandle)
        {
            // shortcut
            if (connectionHandle.HeaderLength == -1 && connectionHandle.HeaderStream.Length == 0)
                return true;

            bool continueRead = false;

            // check if we got the complete header
            if (connectionHandle.HeaderLength == -1)
            {
                int headerLength = -1;
                long contentLength = -1;
                int scanStart = connectionHandle.LastHeaderEndOffset;

                // revert 3 bytes
                scanStart -= 3;

                if (scanStart < 0)
                    scanStart = 0;

                // ref to internal buffer
                byte[] buffer = connectionHandle.HeaderStream.GetBuffer();

                int seq = 0;

                for (int i = scanStart; i < connectionHandle.HeaderStream.Length; i++)
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

                connectionHandle.LastHeaderEndOffset = (int) connectionHandle.HeaderStream.Length;

                if (headerLength == -1)
                {
                    // no header end found
                    // check max header length

                    if (connectionHandle.HeaderStream.Length > MaxHeaderSize)
                    {
                        throw new ProtocolViolationException("Header too big.");
                    }

                    continueRead = true;
                }
                else
                {
                    connectionHandle.Request = new HttpRequest(connectionHandle);

                    // header found. read the content-length http header
                    // to determine if we should continue reading.
                    string header = Encoding.ASCII.GetString(buffer, 0, headerLength);

                    // merge continuations
                    header = HeaderLineMerge.Replace(header, " ");

                    string[] lines = header.Split(CrLfArray, StringSplitOptions.RemoveEmptyEntries);

                    connectionHandle.Request.ReadHeader(lines);

                    string contentLengthHeader;

                    if (connectionHandle.Request.Headers.TryGetValue("Content-Length", out contentLengthHeader))
                    {
                        if (!long.TryParse(contentLengthHeader, out contentLength))
                        {
                            contentLength = -1;
                        }
                    }

                    string expectHeader;

                    if (connectionHandle.Request.Headers.TryGetValue("Expect", out expectHeader))
                    {
                        if (StringComparer.OrdinalIgnoreCase.Equals(expectHeader, "100-continue"))
                        {
                            connectionHandle.AddState(ClientState.ExpectingContinue);
                        }
                    }

                    // init content stream
                    if (contentLength > BigFileThreshold)
                    {
                        connectionHandle.ContentStreamFile = Path.GetTempFileName();
                        connectionHandle.ContentStream = File.Create(connectionHandle.ContentStreamFile);
                    }
                    else
                    {
                        connectionHandle.ContentStream = new MemoryStream();
                    }

                    // check if some content got on the wrong stream
                    if (connectionHandle.HeaderStream.Length > headerLength)
                    {
                        // fix it
                        connectionHandle.HeaderStream.Seek(headerLength, SeekOrigin.Begin);
                        connectionHandle.HeaderStream.CopyTo(connectionHandle.ContentStream);
                    }

                    // we are done with the header stream
                    connectionHandle.HeaderStream.Position = 0;
                }

                connectionHandle.HeaderLength = headerLength;
                connectionHandle.ContentLength = contentLength;
            }

            // we got the header...
            if (connectionHandle.HeaderLength >= 0)
            {
                if (!connectionHandle.HasState(ClientState.ExpectingContinue) && connectionHandle.ContentLength >= 0)
                {
                    // we expect a payload ... check if we got all
                    if (connectionHandle.BytesRead < connectionHandle.ContentLength)
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

        private static void RecordActivity(ConnectionHandle connectionHandle)
        {
            connectionHandle.LastActivity = GetTimestamp();
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
                ConnectionHandle state;

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

        private void OnRenderTimeout(ConnectionHandle connectionHandle)
        {
            var handler = RenderTimeout;

            if (handler != null)
            {
                RenderTimeout(this, new RequestEventArgs(connectionHandle));
            }
            else
            {
                EndRequest(connectionHandle);
            }
        }

        private static HttpResponse OverrideResponse(ConnectionHandle connectionHandle)
        {
            if (connectionHandle.Response != null)
                connectionHandle.Response.Vetoed = true;

            return connectionHandle.Response = new HttpResponse(connectionHandle);
        }
    }
}