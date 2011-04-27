using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Niob
{
    public class Niob
    {
        public const int MaxHeaderSize = 0x2000;
        public const int InBufferSize = 0x1000;
        public const int OutBufferSize = 0x1000;
        public const int TcpBackLogSize = 0x20;

        private static readonly object WatchlistLock = new object();
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private readonly List<Binding> _bindings = new List<Binding>();
        private readonly ConcurrentQueue<ClientState> _clients = new ConcurrentQueue<ClientState>();

        private readonly AutoResetEvent _event = new AutoResetEvent(false);
        private readonly List<ClientState> _timeoutWatch = new List<ClientState>();

        public List<Binding> Bindings
        {
            get { return _bindings; }
        }

        public event EventHandler<RequestEventArgs> RequestAccepted;

        public void Start()
        {
            int count = 1;

            for (int i = 0; i < count; i++)
            {
                var worker = new Thread(WorkerThread) {IsBackground = true};

                worker.Start();
            }

            foreach (Binding binding in Bindings)
            {
                var listener = new Thread(BindingThread) {IsBackground = true};

                listener.Start(binding);
            }
        }

        private void EnqueueAndKickWorkers(ClientState clientState)
        {
            _clients.Enqueue(clientState);
            KickWorkers();
        }

        private void KickWorkers()
        {
            _event.Set();
        }

        private void BindingThread(object state)
        {
            var binding = (Binding) state;

            var socket = new Socket(binding.IpAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

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

            // again
            tuple.Item1.BeginAccept(AcceptCallback, tuple);

            Socket socket = tuple.Item1.EndAccept(ar);

            var clientState = new ClientState(socket, tuple.Item2) {IsReading = true};
            EnqueueAndKickWorkers(clientState);
        }

        private void WorkerThread()
        {
            // Scan queue
            while (true)
            {
                ClientState clientState;

                _clients.TryDequeue(out clientState);

                if (clientState == null)
                {
                    const int maxIdleTime = 10000;

                    _event.WaitOne(maxIdleTime);
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
                            clientState.HeaderLength = -1;
                            clientState.ContentLength = -1;
                            clientState.InStream.Position = 0;
                            clientState.InStream.SetLength(0);

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

                    HttpResponse response = OnRequestAccepted(clientState);

                    if (response == null)
                    {
                        // no handler
                        response = new HttpResponse(clientState);
                    }

                    clientState.OutStream = new MemoryStream();

                    response.WriteHeaders(clientState.OutStream);

                    if (response.ContentStream != null)
                    {
                        response.ContentStream.CopyTo(clientState.OutStream);
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

        private HttpResponse OnRequestAccepted(ClientState clientState)
        {
            EventHandler<RequestEventArgs> handler = RequestAccepted;

            if (handler != null)
            {
                var eventArgs = new RequestEventArgs(clientState);

                handler(this, eventArgs);

                return eventArgs.Response;
            }

            return null;
        }

        private void ReadAsync(ClientState clientState)
        {
            AddToWatchlist(clientState);
            clientState.Stream.BeginRead(clientState.InBuffer, 0, clientState.InBuffer.Length, ReadCallback, clientState);
        }

        private void WriteAsync(ClientState clientState)
        {
            long bytesToSend = clientState.OutStream.Length;
            long bytesSent = clientState.OutStream.Position;

            var size = (int) Math.Min(bytesToSend - bytesSent, OutBufferSize);

            clientState.OutStream.Read(clientState.OutBuffer, 0, size);

            AddToWatchlist(clientState);
            clientState.Stream.BeginWrite(clientState.OutBuffer, 0, size, WriteCallback, clientState);
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
            catch (IOException e)
            {
                Console.WriteLine(e);
                DropRequest(clientState);
                return;
            }
            catch (ObjectDisposedException e)
            {
                Console.WriteLine(e);
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
            catch (IOException e)
            {
                Console.WriteLine(e);
                DropRequest(clientState);
                return;
            }
            catch (ObjectDisposedException e)
            {
                Console.WriteLine(e);
                return;
            }

            RemoveFromWatchlist(clientState);

            try
            {
                // copy to stream
                clientState.InStream.Write(clientState.InBuffer, 0, inBytes);
            }
            catch (ObjectDisposedException e)
            {
                Console.WriteLine(e);
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
                // get current header
                byte[] headerBytes = clientState.InStream.ToArray();

                int headerLength = -1;
                int contentLength = -1;
                bool keepAlive = true;

                for (int i = headerBytes.Length - 4; i >= 0; i--)
                {
                    if (headerBytes[i + 0] == '\r' && headerBytes[i + 1] == '\n' &&
                        headerBytes[i + 2] == '\r' && headerBytes[i + 3] == '\n')
                    {
                        headerLength = i + 2;
                        break;
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
                    // TODO better parsing

                    // header found. read the content-length http header
                    // to determine if we should continue reading.
                    string header = Encoding.ASCII.GetString(headerBytes, 0, headerLength);

                    // TODO: this sucks
                    if (header.Contains("HTTP/1.0\r\n"))
                    {
                        keepAlive = false;
                    }

                    const string clMatch = "\r\nContent-Length: ";

                    int clIndex = header.IndexOf(clMatch, StringComparison.OrdinalIgnoreCase);

                    if (clIndex >= 0)
                    {
                        // cl header found.

                        int crIndex = header.IndexOf('\r', clIndex + clMatch.Length);
                        int startIndex = clIndex + clMatch.Length;
                        int length = crIndex - startIndex;

                        string number = header.Substring(startIndex, length);

                        if (!int.TryParse(number, out contentLength))
                        {
                            contentLength = -1;
                        }
                    }

                    const string coMatch = "\r\nConnection: ";

                    int coIndex = header.IndexOf(coMatch, StringComparison.OrdinalIgnoreCase);

                    if (coIndex >= 0)
                    {
                        // connection header found.

                        int crIndex = header.IndexOf('\r', coIndex + coMatch.Length);
                        int startIndex = coIndex + coMatch.Length;
                        int length = crIndex - startIndex;

                        string mode = header.Substring(startIndex, length);

                        if (StringComparer.OrdinalIgnoreCase.Compare(mode, "keep-alive") != 0)
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
    }
}