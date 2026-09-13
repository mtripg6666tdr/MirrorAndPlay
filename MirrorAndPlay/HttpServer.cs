using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MirrorAndPlay
{
    public class HttpServer(int port, IFFmpegProcess process) : IDisposable
    {
        public int Port { get; } = port;
        private readonly TcpListener _listener = new(IPAddress.Any, port);
        private readonly CancellationTokenSource _cts = new();
        private readonly IFFmpegProcess _process = process;
        private TcpClient? _activeClient;

        public void Start()
        {
            this.StartServer();
            this.StartPiping();
        }

        private async void StartServer()
        {
            var token = this._cts.Token;
            this._listener.Start();

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    this._listener.Stop();
                    break;
                }

                var client = await this._listener.AcceptTcpClientAsync(token);

                var stream = client.GetStream();

                if (this._activeClient != null)
                {
                    await stream.ReadAsync(new byte[1024], token);
                    var busyResp = Encoding.UTF8.GetBytes(@"HTTP/1.1 423 Locked
Content-Type: text/plain; charset=UTF-8
Connection: close
Server: MirrorAndPlay Http Server 0.0.0

Server is currently busy. Try again later.");
                    await stream.WriteAsync(busyResp, token);

                    client.Close();
                    client.Dispose();

                    continue;
                }

                var requestIsOk = true;
                using var reader = new StreamReader(stream, leaveOpen: true);

                string? line;
                var count = 0;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token)))
                {
                    if (count == 0)
                    {
                        if (!line.StartsWith("GET /live"))
                        {
                            requestIsOk = false;
                        }
                    }

                    count++;
                }

                if (!requestIsOk)
                {
                    var badResp = Encoding.UTF8.GetBytes(@"HTTP/1.1 400 Bad Request

");
                    await stream.WriteAsync(badResp, token);

                    client.Close();
                    client.Dispose();

                    continue;
                }

                var okResp = Encoding.UTF8.GetBytes(@"HTTP/1.1 200 OK
Content-Type: video/mp2t
Connection: close

");
                await stream.WriteAsync(okResp, token);
                await stream.FlushAsync(token);

                client.NoDelay = true;
                this._activeClient = client;
            }
        }

        private void StartPiping()
        {
            var token = this._cts.Token;

            _ = Task.Run(() =>
            {
                var bufferSize = 188 * 348; // MPEG-TS packet size * number of packets
                var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

                try
                {
                    while (true)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        var size = this._process.StandardOutputStream.Read(buffer.AsSpan(0, bufferSize));

                        if (size <= 0)
                        {
                            // No more data to read, exit the loop (which means the process is going to exit)
                            break;
                        }

                        if (this._activeClient != null)
                        {
                            try
                            {
                                var stream = this._activeClient.GetStream();
                                stream.Write(buffer.AsSpan(0, size));
                                stream.Flush();
                            }
                            catch
                            {
                                this._activeClient.Close();
                                this._activeClient.Dispose();
                                this._activeClient = null;
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            });
        }

        public void Dispose()
        {
            this.Dispose(disposing: true);

            GC.SuppressFinalize(this);
        }

        public void Dispose(bool disposing)
        {
            if (disposing)
            {
                this._cts.Cancel();
                this._listener.Stop();
                this._activeClient?.Close();
                this._activeClient?.Dispose();
            }
        }
    }
}
