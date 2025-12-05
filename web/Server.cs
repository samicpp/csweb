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
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Security.Authentication;
using System.IO;

public class TcpSocket(NetworkStream stream) : ADualSocket
{
    override protected NetworkStream Stream { get { return stream; } }
    override public bool IsSecure { get { return false; } }
}
public class TlsSocket(SslStream stream) : ADualSocket
{
    override protected SslStream Stream { get { return stream; } }
    override public bool IsSecure { get { return true; } }
}

public class H2CServer(IPEndPoint address)
{
    public IPEndPoint address = address;
    public delegate Task Handler(IDualHttpSocket socket);
    public bool h2c = true;
    public int backlog = 10;

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        listener.Bind(address);
        listener.Listen(backlog);

        while (true)
        {
            var shandler = await listener.AcceptAsync();
            Console.WriteLine($"\e[32m{shandler.RemoteEndPoint}\e[0m");

            var _ = Task.Run(async () =>
            {
                using Http1Socket socket = new(new TcpSocket(new(shandler, ownsSocket: true)), shandler.RemoteEndPoint);

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
                        await h2c.SendPingAsync([104, 101, 97, 114, 98, 101, 97, 116]);

                        var upstream = new Http2Stream(1, h2c);
                        await upstream.ReadClientAsync();
                        var _ = handler(upstream);

                        while (h2c.goaway == null)
                        {
                            Http2Frame frame = await h2c.ReadOneAsync();
                            var sid = await h2c.HandleAsync(frame);

                            if (frame.type == Http2FrameType.Ping && (frame.flags & 0x1) != 0) continue;
                            string dump = $"h2 frame \x1b[36m{frame.type}\x1b[0m [ ";
                            if ((frame.type == Http2FrameType.Data || frame.type == Http2FrameType.Headers || frame.type == Http2FrameType.PushPromise) && frame.raw.Length > 29)
                            {
                                foreach (byte b in frame.raw[..29]) dump += $"0x{b:X}, ";
                                dump += "... ";
                            }
                            else foreach (byte b in frame.raw) dump += $"0x{b:X}, ";
                            dump += "]";
                            Console.WriteLine(dump);

                            if (sid != null)
                            {
                                Http2Stream stream = new((int)sid, h2c);
                                var _d = handler(stream);
                            }
                        }
                    }
                    else
                    {
                        socket.SetHeader("Connection", "close");
                        await handler(socket);
                    }
                }
                catch(Exception e)
                {
                    Console.WriteLine("\x1b[91mserver error occured");
                    Console.WriteLine(e);
                    Console.WriteLine("\e[0m");
                }
            });
        }
    }
}

public class H2Server(IPEndPoint address)
{
    public IPEndPoint address = address;
    public delegate Task Handler(IDualHttpSocket socket);
    public int backlog = 10;

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        listener.Bind(address);
        listener.Listen(backlog);

        while (true)
        {
            var shandler = await listener.AcceptAsync();
            Console.WriteLine($"\e[32m{shandler.RemoteEndPoint}\e[0m");

            var _ = Task.Run(async () =>
            {
                using Http2Session h2 = new(new TcpSocket(new(shandler, ownsSocket: true)), Http2Settings.Default(), shandler.RemoteEndPoint);

                try
                {
                    await h2.InitAsync(); // Console.WriteLine("h2 init");
                    await h2.SendSettingsAsync(Http2Settings.Default()); // Console.WriteLine("h2 settings");
                    await h2.SendPingAsync([104, 101, 97, 114, 98, 101, 97, 116]);

                    while (h2.goaway == null)
                    {
                        try
                        {
                            Http2Frame frame = await h2.ReadOneAsync();
                            var sid = await h2.HandleAsync(frame);

                            if (frame.type == Http2FrameType.Ping && (frame.flags & 0x1) != 0) continue;
                            string dump = $"h2 frame \x1b[36m{frame.type}\x1b[0m [ ";
                            if ((frame.type == Http2FrameType.Data || frame.type == Http2FrameType.Headers || frame.type == Http2FrameType.PushPromise) && frame.raw.Length > 29)
                            {
                                foreach (byte b in frame.raw[..29]) dump += $"0x{b:X}, ";
                                dump += "... ";
                            }
                            else foreach (byte b in frame.raw) dump += $"0x{b:X}, ";
                            dump += "]";
                            Console.WriteLine(dump);

                            if (sid != null)
                            {
                                Http2Stream stream = new((int)sid, h2);
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
                    Console.WriteLine("\e[0m");
                }

            });
        }
    }
}

public class O9Server(IPEndPoint address)
{
    public IPEndPoint address = address;
    public delegate Task Handler(IDualHttpSocket socket);
    public int backlog = 10;

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        listener.Bind(address);
        listener.Listen(backlog);

        while (true)
        {
            var shandler = await listener.AcceptAsync();
            Console.WriteLine($"\e[32m{shandler.RemoteEndPoint}\e[0m");

            var _ = Task.Run(async () =>
            {
                using Http09Socket socket = new(new TcpSocket(new(shandler, ownsSocket: true)), shandler.RemoteEndPoint);

                var client = await socket.ReadClientAsync();

                await handler(socket);
            });
        }
    }
}

public class TlsServer(IPEndPoint address, X509Certificate2 cert)
{
    public IPEndPoint address = address;
    X509Certificate2 cert = cert;
    public delegate Task Handler(IDualHttpSocket socket);
    public int backlog = 10;
    public List<SslApplicationProtocol> alpn = [
        new SslApplicationProtocol("h2"), //SslApplicationProtocol.Http2,
        new SslApplicationProtocol("http/1.1"), //SslApplicationProtocol.Http11,
        new SslApplicationProtocol("http/0.9"),
    ];

    public string fallback = null;

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        listener.Bind(address);
        listener.Listen(backlog);

        SslServerAuthenticationOptions opt = new()
        {
            ServerCertificate = cert,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            ClientCertificateRequired = false,
            ApplicationProtocols = alpn,
            AllowRenegotiation = false,
            AllowTlsResume = true,
        };

        while (true)
        {
            var shandler = await listener.AcceptAsync();
            Console.WriteLine($"\e[32m{shandler.RemoteEndPoint}\e[0m");
            NetworkStream stream = new(shandler, true);

            var _ = Task.Run(async () => await TlsUpgrade(handler, stream, opt, shandler.RemoteEndPoint));
        }
    }
    async Task TlsUpgrade(Handler handler, NetworkStream socket, SslServerAuthenticationOptions opt, EndPoint end)
    {
        var sslStream = new SslStream(socket, false);
        try
        {
            string alpn = "";
            try
            {
                await sslStream.AuthenticateAsServerAsync(opt);
                alpn = sslStream.NegotiatedApplicationProtocol.ToString();
            }
            catch (Exception e)
            {
                Console.WriteLine("\x1b[91mtls authentication error occured");
                Console.WriteLine(e);
                Console.WriteLine("\e[0m");
            }
            using TlsSocket tls = new(sslStream);

            Console.WriteLine($"alpn = \"{alpn}\"");
            if (alpn == "")
            {
                Console.WriteLine("alpn not negotiated" + fallback != null ? $", falling back to {fallback}" : "");
                if (fallback != null) alpn = fallback;
            } 

            if (alpn == "http/0.9")
            {
                using Http09Socket sock = new(tls, end);
                await handler(sock);
            }
            else if (alpn == "http/1.1")
            {
                using Http1Socket sock = new(tls, end);
                sock.SetHeader("Connection", "close");
                await handler(sock);
            }
            else if (alpn == "h2")
            {
                using Http2Session h2 = new(tls, Http2Settings.Default(), end);
                try
                {
                    await h2.InitAsync();
                    await h2.SendSettingsAsync(Http2Settings.Default());
                    await h2.SendPingAsync([104, 101, 97, 114, 98, 101, 97, 116]);

                    while (h2.goaway == null)
                    {
                        try
                        {
                            
                            Http2Frame frame = await h2.ReadOneAsync();
                            // catch (IOException)
                            // {
                            // }
                            // List<Http2Frame> frames = await h2.ReadAllAsync();
                            var sid = await h2.HandleAsync(frame);

                            if (frame.type == Http2FrameType.Ping && (frame.flags & 0x1) != 0) continue;
                            string dump = $"h2 frame \x1b[36m{frame.type}\x1b[0m [ ";
                            if ((frame.type == Http2FrameType.Data || frame.type == Http2FrameType.Headers || frame.type == Http2FrameType.PushPromise) && frame.raw.Length > 29)
                            {
                                foreach (byte b in frame.raw[..29]) dump += $"0x{b:X}, ";
                                dump += "... ";
                            }
                            else foreach (byte b in frame.raw) dump += $"0x{b:X}, ";
                            dump += "]";
                            Console.WriteLine(dump);

                            if (sid != null)
                            {
                                Http2Stream stream = new((int)sid, h2);
                                var _d = handler(stream);
                            }
                        }
                        catch (HttpException.ConnectionClosed)
                        {
                            // h2.goaway ??= new Http2Frame();
                            break;
                        }
                        catch (IOException ioe) when (ioe.InnerException is SocketException e && (e.SocketErrorCode == SocketError.ConnectionReset || e.SocketErrorCode == SocketError.Shutdown || e.SocketErrorCode == SocketError.ConnectionAborted || e.ErrorCode == 32 /* Broken Pipe */))
                        {
                            Console.WriteLine("\e[31mconnection ended abruptly\e[0m");
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("\x1b[91mserver error occured");
                    Console.WriteLine(e);
                    Console.WriteLine("\e[0m");
                }
            }
            else
            {
                Console.WriteLine($"couldnt handle alpn {alpn}");

                // using Http1Socket sock = new(tls, end);
                // sock.SetHeader("Connection", "close");
                // await handler(sock);
            }
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset || e.SocketErrorCode == SocketError.Shutdown || e.SocketErrorCode == SocketError.ConnectionAborted)
        {
            Console.WriteLine("\x1b[91msocket unexpectedly closed\e[0m");
        }
        catch (Exception e)
        {
            Console.WriteLine("\x1b[91mtls error occured");
            Console.WriteLine(e);
            Console.WriteLine("\e[0m");
        }
    }
}
