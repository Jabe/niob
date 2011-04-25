using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

        private readonly List<Binding> _bindings = new List<Binding>();
        private readonly ConcurrentQueue<ClientState> _clients = new ConcurrentQueue<ClientState>();

        private readonly AutoResetEvent _event = new AutoResetEvent(false);
        public event EventHandler<RequestEventArgs> RequestAccepted;

        public List<Binding> Bindings
        {
            get { return _bindings; }
        }

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
                var listener = new Thread(ListenerThread) {IsBackground = true};

                listener.Start(binding);
            }
        }

        private void KickWorkers()
        {
            _event.Set();
        }

        private void ListenerThread(object state)
        {
            var binding = (Binding) state;

            var socket = new Socket(binding.IpAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            socket.Bind(new IPEndPoint(binding.IpAddress, binding.Port));
            socket.Listen(TcpBackLogSize);

            while (true)
            {
                Socket client = socket.Accept();

                Stopwatch sw = Stopwatch.StartNew();

                _clients.Enqueue(new ClientState(client, binding));
                KickWorkers();

                sw.Stop();
                //Console.WriteLine("Accept client: {0}", sw.Elapsed.ToStringAuto());
            }
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

                if (clientState.HeaderLength == -1)
                {
                    ReadAsync(clientState);
                }
                else
                {
                    HttpResponse response = OnRequestAccepted(clientState);

                    if (response == null)
                    {
                        // no handler
                        response = new HttpResponse();
                    }

                    clientState.OutStream = new MemoryStream();

                    response.WriteHeaders(clientState.OutStream);

                    if (response.ContentStream != null)
                    {
                        response.ContentStream.CopyTo(clientState.OutStream);
                    }

                    clientState.OutStream.Flush();
                    clientState.OutStream.Seek(0, SeekOrigin.Begin);

                    WriteAsync(clientState);
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
            clientState.Socket.BeginReceive(clientState.InBuffer, 0, clientState.InBuffer.Length, SocketFlags.None,
                                            ReadCallback, clientState);
        }

        private void WriteAsync(ClientState clientState)
        {
            long bytesToSend = clientState.OutStream.Length;
            long bytesSent = clientState.OutStream.Position;

            var size = (int) Math.Min(bytesToSend - bytesSent, OutBufferSize);

            clientState.OutStream.Read(clientState.OutBuffer, 0, size);
            clientState.Socket.BeginSend(clientState.OutBuffer, 0, size, SocketFlags.None, WriteCallback, clientState);
        }

        private void WriteCallback(IAsyncResult ar)
        {
            var clientState = (ClientState) ar.AsyncState;
            Socket client = clientState.Socket;

            client.EndSend(ar);

            long bytesToSend = clientState.OutStream.Length;
            long bytesSent = clientState.OutStream.Position;

            if (bytesToSend > bytesSent)
            {
                WriteAsync(clientState);
            }
            else
            {
                using (clientState.Socket)
                {
                    clientState.Socket.Shutdown(SocketShutdown.Both);
                    clientState.Socket.Close();
                }
            }
        }

        private void ReadCallback(IAsyncResult ar)
        {
            var clientState = (ClientState) ar.AsyncState;
            Socket client = clientState.Socket;

            int inBytes = client.EndReceive(ar);

            // copy to stream
            clientState.InStream.Write(clientState.InBuffer, 0, inBytes);

            bool continueRead;

            try
            {
                continueRead = ShouldContinueReading(clientState);
            }
            catch (ProtocolViolationException e)
            {
                Console.WriteLine(e);

                // stop.
                return;
            }

            if (continueRead)
            {
                ReadAsync(clientState);
            }
            else
            {
                // put back in queue
                _clients.Enqueue(clientState);

                KickWorkers();
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
                        using (clientState.Socket)
                        {
                            clientState.Socket.Close();
                        }

                        throw new ProtocolViolationException("Header too big. Dropping request.");
                    }

                    continueRead = true;
                }
                else
                {
                    // header found. read the content-length http header
                    // to determine if we should continue reading.
                    string header = Encoding.ASCII.GetString(headerBytes, 0, headerLength);

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
                }

                clientState.HeaderLength = headerLength;
                clientState.ContentLength = contentLength;
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
    }
}