namespace Samicpp.Web;

using Samicpp.Http;
using Samicpp.Http.Http1;

using System;
using System.Net;
using System.Text;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;

public class TcpSocket(NetworkStream stream) : ANetSocket
{
    override protected NetworkStream Stream { get { return stream; } }
    override public bool IsSecure { get { return false; } }
}

public class Program
{
    public static async Task Main()
    {
        // creating listener
        IPEndPoint address = new(IPAddress.Parse("0.0.0.0"), 2048);
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        // start listener
        listener.Bind(address);
        listener.Listen(10);

        Console.WriteLine("http echo server listening on http://127.0.0.1:2048");

        // connection loop
        while (true)
        {
            var shandler = await listener.AcceptAsync();
            Console.WriteLine($"\e[32m{shandler.RemoteEndPoint}\e[0m");

            var _ = Task.Run(async () =>
            {
                // first we need to convert it to something we can pass to our class
                using NetworkStream stream = new(shandler, ownsSocket: true);

                // then we use it to construct `Samicpp.Http.Http1.Http1Socket`
                using Http1Socket socket = new(new TcpSocket(stream)); 
                // interface `Samicpp.Http.IDualHttpSocket` can also be used as data type, since the class implements this.
                // individual H2 streams also implement this

                Console.WriteLine("constructed protocol handler");

                // when the client uses `Transfer-Encoding: chunked` each read will only add 1 chunk to the body buffer
                // if `Content-Length: n` was provided the library will only read the full body on the second read invocation 
                // to ensure not enforcing body read
                // this is also usefull for Http2Streams where reading client doesnt block
                var client = await socket.ReadClientAsync();

                // ensures full client has been read
                while (!client.HeadersComplete || !client.BodyComplete) client = await socket.ReadClientAsync();

                // the framework allows for headers to appear multiple times
                if (client.Headers.TryGetValue("accept-encoding", out List<string> encoding))
                {
                    foreach (string s in encoding[0].Split(","))
                    {
                        // setting `Samicpp.Http.IDualSocket.Compression` automatically ensures the appropriate compression type is used
                        // the framework does not verify if client accepts the encoding, this was done on purpose to give the code full control
                        socket.Compression = s switch
                        {
                            "gzip" => Compression.Gzip,
                            "deflate" => Compression.Deflate,
                            "br" => Compression.Brotli,
                            _ => Compression.None,
                        };
                        if (socket.Compression != Compression.None) break;
                    }
                    Console.WriteLine("using compression " + socket.Compression);
                }
                else
                {
                    Console.WriteLine("no compression");
                }

                Console.WriteLine(client);
                Console.WriteLine($"received {client.Body.Count} bytes");

                // the server doesnt decode the client body automatically, it also doesnt decompress it. this is the code's responsibility.
                // for decompression you can use `Samicpp.Http.Compressor.Decompress`
                var text = Encoding.UTF8.GetString([.. client.Body]);

                Console.WriteLine($"received request with body[{text.Length}] \e[36m{text.Trim()}\e[0m");

                // the server does ensure you cannot attempt to send data after connection has been closed
                // nor does it allow you to send headers after
                await socket.CloseAsync(Encoding.UTF8.GetBytes($"<| {text.Trim()} |>\n"));
            });
        }
    }
}
