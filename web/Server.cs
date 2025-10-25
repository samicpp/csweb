namespace Samicpp.Web;

using Samicpp.Http;
using Samicpp.Http.Http1;
using Samicpp.Http.Http2;
using Samicpp.Http.Http09;

using System;
using System.Net;
using System.Text;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;


public class TcpSocket(NetworkStream stream) : ANetSocket
{
    override protected NetworkStream Stream { get { return stream; } }
    override public bool IsSecure { get { return false; } }
}

public class H2CServer(IPEndPoint address)
{
    public IPEndPoint address = address;
    public delegate Task Handler(IDualHttpSocket socket);
    public bool h2c = true;

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        listener.Bind(address);
        listener.Listen(10);

        while (true)
        {
            var shandler = await listener.AcceptAsync();
            Console.WriteLine($"\e[32m{shandler.RemoteEndPoint}\e[0m");

            var _ = Task.Run(async () =>
            {
                using Http1Socket socket = new(new TcpSocket(new(shandler, ownsSocket: true)));

                var client = socket.Client;

                try
                {
                    while (!client.HeadersComplete) client = await socket.ReadClientAsync();
                }
                catch (Exception e)
                {
                    Console.WriteLine("failed to read client");
                    Console.WriteLine(e);
                }

                try
                {
                    if (h2c && client.Headers.TryGetValue("upgrade", out List<string> up) && up[0] == "h2c")
                    {
                        using var h2c = await socket.H2CAsync();

                        await h2c.InitAsync();
                        await h2c.SendSettingsAsync(Http2Settings.Default());

                        var upstream = new Http2Stream(1, h2c);
                        await upstream.ReadClientAsync();
                        var _ = handler(upstream);

                        while (h2c.goaway == null)
                        {
                            List<Http2Frame> frames = [await h2c.ReadOneAsync()];
                            var opened = await h2c.HandleAsync(frames);

                            foreach (var frame in frames)
                            {
                                Console.Write($"h2c frame \x1b[36m{frame.type}\x1b[0m [ ");
                                foreach (byte b in frame.raw) Console.Write($"0x{b:X}, ");
                                Console.WriteLine("]");
                            }

                            foreach (var sid in opened)
                            {
                                var stream = new Http2Stream(sid, h2c);

                                var _d = handler(stream);
                            }
                        }
                    }
                    else
                    {
                        await handler(socket);
                    }
                }
                catch(Exception e)
                {
                    Console.WriteLine("\x1b[91mserver error occured");
                    Console.WriteLine(e);
                    Console.ResetColor();
                }
            });
        }
    }
}

public class H2Server(IPEndPoint address)
{
    public IPEndPoint address = address;
    public delegate Task Handler(IDualHttpSocket socket);

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        listener.Bind(address);
        listener.Listen(10);

        while (true)
        {
            var shandler = await listener.AcceptAsync();
            Console.WriteLine($"\e[32m{shandler.RemoteEndPoint}\e[0m");

            var _ = Task.Run(async () =>
            {
                using Http2Session h2 = new(new TcpSocket(new(shandler, ownsSocket: true)), Http2Settings.Default());

                try
                {
                    await h2.InitAsync(); // Console.WriteLine("h2 init");
                    await h2.SendSettingsAsync(Http2Settings.Default()); // Console.WriteLine("h2 settings");

                    while (true)
                    {
                        try
                        {
                            List<Http2Frame> frames = [await h2.ReadOneAsync()];
                            var opened = await h2.HandleAsync(frames);

                            foreach (var frame in frames)
                            {
                                Console.Write($"h2 frame \x1b[36m{frame.type}\x1b[0m [ ");
                                foreach (byte b in frame.raw) Console.Write($"0x{b:X}, ");
                                Console.WriteLine("]");
                            }

                            foreach (var sid in opened)
                            {
                                Http2Stream stream = new(sid, h2);

                                var _d = handler(stream);
                            }
                        }
                        catch(HttpException.ConnectionClosed)
                        {
                            break;
                        }
                    }
                }
                catch(Exception e)
                {
                    Console.WriteLine("\x1b[91mserver error occured");
                    Console.WriteLine(e);
                    Console.ResetColor();
                }

            });
        }
    }
}

public class O9Server(IPEndPoint address)
{
    public IPEndPoint address = address;
    public delegate Task Handler(IDualHttpSocket socket);

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        listener.Bind(address);
        listener.Listen(10);

        while (true)
        {
            var shandler = await listener.AcceptAsync();
            Console.WriteLine($"\e[32m{shandler.RemoteEndPoint}\e[0m");

            var _ = Task.Run(async () =>
            {
                using Http09Socket socket = new(new TcpSocket(new(shandler, ownsSocket: true)));

                var client = await socket.ReadClientAsync();

                await handler(socket);
            });
        }
    }
}