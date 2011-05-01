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
        public const int InBufferSize = 0x1000;
        public const int OutBufferSize = 0x1000;
        public const int TcpBackLogSize = 0x20;

        private static readonly object WatchlistLock = new object();
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private static readonly Regex HeaderLineMerge = new Regex(@"\r\n[ \t]+");

        private readonly List<Binding> _bindings = new List<Binding>();
        private readonly ConcurrentQueue<ClientState> _clients = new ConcurrentQueue<ClientState>();
        private readonly ConcurrentBag<Socket> _listeningSockets = new ConcurrentBag<Socket>();

        private readonly ManualResetEvent _stopping = new ManualResetEvent(false);
        private readonly List<ClientState> _timeoutWatch = new List<ClientState>();
        private readonly AutoResetEvent _workPending = new AutoResetEvent(false);

        private readonly List<Thread> _workerThreads = new List<Thread>();

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

                _workerThreads.Add(worker);

                worker.Start();
            }

            foreach (Binding binding in Bindings)
            {
                var listener = new Thread(BindingThread) {IsBackground = true};

                listener.Start(binding);
            }
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

            // get it going initally
            socket.BeginAccept(AcceptCallback, Tuple.Create(socket, binding));

            while (true)
            {
                Thread.Sleep(1000);

                long threshold = GetTimestamp() - Timeout.Ticks;

                var clientsToDrop = new List<ClientState>();

                // check sockets for timeouts.
                lock (WatchlistLock)
                {
                    for (int i = _timeoutWatch.Count - 1; i >= 0; i--)
                    {
                        ClientState clientState = _timeoutWatch[i];

                        if (clientState.LastActivity < threshold)
                        {
                            _timeoutWatch.RemoveAt(i);
                            clientsToDrop.Add(clientState);
                        }
                    }
                }

                foreach (ClientState client in clientsToDrop)
                {
                    DropRequest(client);
                }
            }
        }

        private void AcceptCallback(IAsyncResult ar)
        {
            var tuple = (Tuple<Socket, Binding>) ar.AsyncState;

            try
            {
                // again
                tuple.Item1.BeginAccept(AcceptCallback, tuple);
            }
            catch (ObjectDisposedException)
            {
                // listening socket was closed
                return;
            }

            // the client
            Socket socket = tuple.Item1.EndAccept(ar);

            var clientState = new ClientState(socket, tuple.Item2, this) {IsReading = true};
            EnqueueAndKickWorkers(clientState);
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

                if (!clientState.IsReady)
                {
                    clientState.AsyncInitialize(EnqueueAndKickWorkers);
                }
                else if (clientState.IsReading)
                {
                    clientState.IsReading = false;

                    bool continueRead = false;

                    try
                    {
                        continueRead = ShouldContinueReading(clientState);
                    }
                    catch (ProtocolViolationException e)
                    {
                        Console.WriteLine(e);
                        DropRequest(clientState);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        DropRequest(clientState);
                    }

                    if (continueRead)
                    {
                        ReadAsync(clientState);
                    }
                    else
                    {
                        clientState.IsRendering = true;
                        EnqueueAndKickWorkers(clientState);
                    }
                }
                else if (clientState.IsWriting)
                {
                    clientState.IsWriting = false;

                    long bytesToSend = clientState.OutStream.Length;
                    long bytesSent = clientState.OutStream.Position;

                    if (bytesToSend > bytesSent)
                    {
                        WriteAsync(clientState);
                    }
                    else
                    {
                        if (clientState.KeepAlive)
                        {
                            clientState.Clear();

                            clientState.IsKeepingAlive = true;
                            EnqueueAndKickWorkers(clientState);
                        }
                        else
                        {
                            using (clientState)
                            {
                                // end.
                            }
                        }
                    }
                }
                else if (clientState.IsKeepingAlive)
                {
                    clientState.IsKeepingAlive = false;

                    ReadAsync(clientState);
                }
                else if (clientState.IsRendering)
                {
                    clientState.IsRendering = false;
                    clientState.Response = new HttpResponse(clientState);

                    OnRequestAccepted(clientState);
                }
                else if (clientState.IsPostRendering)
                {
                    clientState.IsPostRendering = false;

                    clientState.OutStream = new MemoryStream();
                    clientState.Response.WriteHeaders(clientState.OutStream);

                    if (clientState.Response.ContentStream != null)
                    {
                        clientState.Response.ContentStream.CopyTo(clientState.OutStream);
                    }

                    clientState.OutStream.Flush();
                    clientState.OutStream.Seek(0, SeekOrigin.Begin);

                    clientState.IsWriting = true;
                    EnqueueAndKickWorkers(clientState);
                }
                else
                {
                    Console.WriteLine("Unknown state.");
                    DropRequest(clientState);
                }
            }
        }

        private void OnRequestAccepted(ClientState clientState)
        {
            EventHandler<RequestEventArgs> handler = RequestAccepted;

            if (handler != null)
            {
                var eventArgs = new RequestEventArgs(clientState);

                handler(this, eventArgs);
            }
        }

        private void ReadAsync(ClientState clientState)
        {
            AddToWatchlist(clientState);

            try
            {
                clientState.Stream.BeginRead(clientState.InBuffer, 0, clientState.InBuffer.Length, ReadCallback, clientState);
            }
            catch (ObjectDisposedException)
            {
                DropRequest(clientState);
            }
        }

        private void WriteAsync(ClientState clientState)
        {
            long bytesToSend = clientState.OutStream.Length;
            long bytesSent = clientState.OutStream.Position;

            var size = (int) Math.Min(bytesToSend - bytesSent, OutBufferSize);

            clientState.OutStream.Read(clientState.OutBuffer, 0, size);

            AddToWatchlist(clientState);

            try
            {
                clientState.Stream.BeginWrite(clientState.OutBuffer, 0, size, WriteCallback, clientState);
            }
            catch (ObjectDisposedException)
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
            catch (IOException)
            {
                DropRequest(clientState);
                return;
            }
            catch (ObjectDisposedException)
            {
                DropRequest(clientState);
                return;
            }

            RemoveFromWatchlist(clientState);

            clientState.IsWriting = true;
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
            catch (IOException)
            {
                DropRequest(clientState);
                return;
            }
            catch (ObjectDisposedException)
            {
                DropRequest(clientState);
                return;
            }

            RemoveFromWatchlist(clientState);

            try
            {
                // copy to stream
                clientState.InStream.Write(clientState.InBuffer, 0, inBytes);
            }
            catch (ObjectDisposedException)
            {
                DropRequest(clientState);
                return;
            }

            clientState.IsReading = true;
            EnqueueAndKickWorkers(clientState);
        }

        private static void DropRequest(ClientState clientState)
        {
            try
            {
                using (clientState)
                {
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("error while dropping a request: " + e);
            }
        }

        private static bool ShouldContinueReading(ClientState clientState)
        {
            bool continueRead = false;

            // check if we got the complete header
            if (clientState.HeaderLength == -1)
            {
                int headerLength = -1;
                int contentLength = -1;
                bool keepAlive = true;

                byte[] headerBytes = clientState.InStream.ToArray();

                if (headerBytes.Length >= 4)
                {
                    for (int i = headerBytes.Length - 4; i >= Math.Max(0, headerBytes.Length - InBufferSize - 4); i--)
                    {
                        if (headerBytes[i + 0] == '\r' && headerBytes[i + 1] == '\n' &&
                            headerBytes[i + 2] == '\r' && headerBytes[i + 3] == '\n')
                        {
                            headerLength = i + 2;
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
                        if (!int.TryParse(contentLengthHeader, out contentLength))
                        {
                            contentLength = -1;
                        }
                    }

                    string connectionHeader;

                    if (clientState.Request.Headers.TryGetValue("connection", out connectionHeader))
                    {
                        if (StringComparer.OrdinalIgnoreCase.Compare(connectionHeader, "keep-alive") != 0)
                        {
                            keepAlive = false;
                        }
                    }
                }

                clientState.HeaderLength = headerLength;
                clientState.ContentLength = contentLength;
                clientState.KeepAlive = keepAlive;
            }

            // we got the header...
            if (clientState.HeaderLength >= 0)
            {
                if (clientState.ContentLength >= 0)
                {
                    // we expect a payload
                    // calculate the payload length already recieved (-2 bytes for the empty line)
                    long currentContentLength = clientState.InStream.Length - clientState.HeaderLength - 2;

                    if (currentContentLength < clientState.ContentLength)
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

        private void AddToWatchlist(ClientState clientState)
        {
            clientState.LastActivity = GetTimestamp();

            lock (WatchlistLock)
            {
                _timeoutWatch.Add(clientState);
            }
        }

        private void RemoveFromWatchlist(ClientState clientState)
        {
            lock (WatchlistLock)
            {
                _timeoutWatch.Remove(clientState);
            }
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

                DropRequest(state);
            }

            lock (WatchlistLock)
            {
                foreach (ClientState client in _timeoutWatch)
                {
                    DropRequest(client);
                }

                _timeoutWatch.Clear();
            }

            foreach (Thread thread in _workerThreads)
            {
                thread.Join();
            }

            _workerThreads.Clear();
        }
    }
}