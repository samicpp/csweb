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

// public class TcpSocket(NetworkStream stream) : ADualSocket
// {
//     override protected NetworkStream Stream { get { return stream; } }
//     override public bool IsSecure { get { return false; } }
// }
// public class TlsSocket(SslStream stream) : ADualSocket
// {
//     override protected SslStream Stream { get { return stream; } }
//     override public bool IsSecure { get { return true; } }
// }

public delegate Task Handler(IDualHttpSocket socket);

static class Helper
{
    public static async Task Http2Loop(Http2Session h2, Handler handler, bool init = true)
    {
        // using Http2Session h2 = new(socket, Http2Settings.Default(), end);

        try
        {
            if (init)
            {
                await h2.InitAsync();
                await h2.SendSettingsAsync(new(4096, null, null, 16777215, 16777215, null));
                await h2.SendPingAsync([104, 101, 97, 114, 98, 101, 97, 116]);
            }

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
                    Debug.WriteLine((int)LogLevel.Trace, dump);

                    if (sid != null)
                    {
                        Http2Stream stream = new((int)sid, h2);
                        var _d = handler(stream);
                    }
                }
                catch (HttpException.ConnectionClosed)
                {
                    break;
                }
                catch (IOException ioe) when (ioe.InnerException is SocketException e && (e.SocketErrorCode == SocketError.ConnectionReset || e.SocketErrorCode == SocketError.Shutdown || e.SocketErrorCode == SocketError.ConnectionAborted || e.ErrorCode == 32 /* Broken Pipe */))
                {
                    Debug.WriteColorLine((int)LogLevel.Critical, "* connection ended abruptly", 1);
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Debug.WriteColorLine((int)LogLevel.Critical, $"* server error occured {e.GetType()}", 9);
            Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}\n", 9);
        }
    }
}

public class H2CServer(IPEndPoint address)
{
    public IPEndPoint address = address;
    // public delegate Task Handler(IDualHttpSocket socket);
    public bool h2c = true;
    public int backlog = 10;
    public bool dualmode = false;

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (dualmode) listener.DualMode = true;

        try
        {
            listener.Bind(address);
            listener.Listen(backlog);
        }
        catch (Exception e)
        {
            Debug.WriteColorLine((int)LogLevel.Fatal, $"! HTTP/1.1 server failed to start {e.GetType()}", 9);
            Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}\n", 9);
            return;
        }

        while (true)
        {
            var shandler = await listener.AcceptAsync();
            Debug.WriteColorLine((int)LogLevel.Trace, $"{shandler.RemoteEndPoint}", 8);

            var _ = Task.Run(async () =>
            {
                using Http1Socket socket = new(new TcpSocket(new NetworkStream(shandler, ownsSocket: true)), shandler.RemoteEndPoint);

                var client = socket.Client;

                try
                {
                    while (!client.HeadersComplete) client = await socket.ReadClientAsync();
                }
                catch (Exception e)
                {
                    Debug.WriteColorLine((int)LogLevel.Warning, $"failed to read client ({e.GetType()})", 3);
                }

                try
                {
                    if (h2c && client.Headers.TryGetValue("upgrade", out List<string> up) && up[0] == "h2c")
                    {
                        using var h2c = await socket.H2CAsync();

                        await h2c.InitAsync();
                        await h2c.SendSettingsAsync(new(4096, null, null, 16777215, 16777215, null));
                        await h2c.SendPingAsync([104, 101, 97, 114, 98, 101, 97, 116]);

                        var upstream = new Http2Stream(1, h2c);
                        await upstream.ReadClientAsync();
                        var _ = Task.Run(async () => await handler(upstream));

                        await Helper.Http2Loop(h2c, handler, false);
                    }
                    else
                    {
                        socket.SetHeader("Connection", "close");
                        await handler(socket);
                    }
                }
                catch(Exception e)
                {
                    Debug.WriteColorLine((int)LogLevel.Critical, $"* server error occured {e.GetType()}", 9);
                    Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}\n", 9);
                }
            });
        }
    }
}

public class H2Server(IPEndPoint address)
{
    public IPEndPoint address = address;
    // public delegate Task Handler(IDualHttpSocket socket);
    public int backlog = 10;
    public bool dualmode = false;

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (dualmode) listener.DualMode = true;

        try
        {
            listener.Bind(address);
            listener.Listen(backlog);
        }
        catch (Exception e)
        {
            Debug.WriteColorLine((int)LogLevel.Fatal, $"! H2 server failed to start {e.GetType()}", 9);
            Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}\n", 9);
            return;
        }

        while (true)
        {
            var shandler = await listener.AcceptAsync();
            Debug.WriteColorLine((int)LogLevel.Trace, $"{shandler.RemoteEndPoint}", 8);

            var _ = Task.Run(async () =>
            {
                var socket = new TcpSocket(new NetworkStream(shandler, ownsSocket: true));
                using Http2Session h2 = new(socket, Http2Settings.Default(), shandler.RemoteEndPoint);
                await Helper.Http2Loop(h2, handler);
            });
        }
    }
}

public class O9Server(IPEndPoint address)
{
    public IPEndPoint address = address;
    // public delegate Task Handler(IDualHttpSocket socket);
    public int backlog = 10;
    public bool dualmode = false;

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (dualmode) listener.DualMode = true;

        try
        {
            listener.Bind(address);
            listener.Listen(backlog);
        }
        catch (Exception e)
        {
            Debug.WriteColorLine((int)LogLevel.Fatal, $"! 09 server failed to start {e.GetType()}", 9);
            Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}\n", 9);
            return;
        }

        while (true)
        {
            var shandler = await listener.AcceptAsync();
            Debug.WriteColorLine((int)LogLevel.Trace, $"{shandler.RemoteEndPoint}", 8);

            var _ = Task.Run(async () =>
            {
                using Http09Socket socket = new(new TcpSocket(new NetworkStream(shandler, ownsSocket: true)), shandler.RemoteEndPoint);

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
    // public delegate Task Handler(IDualHttpSocket socket);
    public int backlog = 10;
    public bool dualmode = false;
    public List<SslApplicationProtocol> alpn = [
        new SslApplicationProtocol("h2"), //SslApplicationProtocol.Http2,
        new SslApplicationProtocol("http/1.1"), //SslApplicationProtocol.Http11,
        new SslApplicationProtocol("http/1.0"),
        new SslApplicationProtocol("http/0.9"),
    ];

    public string fallback = null;

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (dualmode) listener.DualMode = true;

        try
        {
            listener.Bind(address);
            listener.Listen(backlog);
        }
        catch (Exception e)
        {
            Debug.WriteColorLine((int)LogLevel.Fatal, $"! tls server failed to start {e.GetType()}", 9);
            Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}\n", 9);
            return;
        }

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
            Debug.WriteColorLine((int)LogLevel.Trace, $"{shandler.RemoteEndPoint}", 8);
            NetworkStream stream = new(shandler, true);

            var _ = Task.Run(async () => await TlsUpgrade(handler, stream, opt, shandler.RemoteEndPoint, fallback));
        }
    }
    internal static async Task TlsUpgrade(Handler handler, NetworkStream socket, SslServerAuthenticationOptions opt, EndPoint end, string fallback)
    {
        using var sslStream = new SslStream(socket, false);
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
                Debug.WriteColorLine((int)LogLevel.Critical, $"* tls authentication error occured {e.GetType()}", 9);
                Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}\n", 9);
                return;
            }
            using TlsSocket tls = new(sslStream);

            Debug.WriteLine((int)LogLevel.Debug, $"alpn = \"{alpn}\"");
            if (alpn == "")
            {
                Debug.WriteColorLine((int)LogLevel.Warning, ", alpn not negotiated" + (fallback != null ? $". falling back to {fallback}" : ""), 3);
                if (fallback != null) alpn = fallback;
            } 

            if (alpn == "http/0.9")
            {
                using Http09Socket sock = new(tls, end);
                await handler(sock);
            }
            else if (alpn == "http/1.1")
            {
                using Http1Socket sock = new(tls, end) { Allow09 = false, Allow10 = false, AllowUnknown = false };
                sock.SetHeader("Connection", "close");
                await handler(sock);
            }
            else if (alpn == "h2")
            {
                using Http2Session h2 = new(tls, Http2Settings.Default(), end);
                await Helper.Http2Loop(h2, handler);
            }
            else
            {
                Debug.WriteColorLine((int)LogLevel.Warning, $"couldnt use alpn \"{alpn}\"", 3);

                // using Http1Socket sock = new(tls, end);
                // sock.SetHeader("Connection", "close");
                // await handler(sock);
            }
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset || e.SocketErrorCode == SocketError.Shutdown || e.SocketErrorCode == SocketError.ConnectionAborted)
        {
            Debug.WriteColorLine((int)LogLevel.Critical, "* socket unexpectedly closed", 9);
        }
        catch (Exception e)
        {
            Debug.WriteColorLine((int)LogLevel.Critical, $"* tls error occured {e.GetType()}", 9);
            Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}", 9);
        }
    }
}

public class PolyServer(IPEndPoint address, X509Certificate2 cert)
{
    public IPEndPoint address = address;
    X509Certificate2 cert = cert;
    public int backlog = 10;
    public bool dualmode = false;
    public List<SslApplicationProtocol> alpn = [
        new SslApplicationProtocol("h2"),
        new SslApplicationProtocol("http/1.1"),
        new SslApplicationProtocol("http/1.0"),
        new SslApplicationProtocol("http/0.9"),
    ];

    public string fallback = null;

    public bool AllowH09 = true;
    public bool AllowH10 = true;
    public bool AllowH11 = true;
    public bool AllowH2c = true;
    public bool AllowH2 = true;
    public bool AllowTls = true;

    public async Task Serve(Handler handler)
    {
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (dualmode) listener.DualMode = true;

        try
        {
            listener.Bind(address);
            listener.Listen(backlog);
        }
        catch (Exception e)
        {
            Debug.WriteColorLine((int)LogLevel.Fatal, $"! poly server failed to start {e.GetType()}", 9);
            Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}\n", 9);
            return;
        }

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
            var socket = await listener.AcceptAsync();
            Debug.WriteColorLine((int)LogLevel.Trace, $"{socket.RemoteEndPoint}", 8);
            
            byte[] snap = new byte[8];
            int r = await socket.ReceiveAsync(snap, SocketFlags.Peek);
            byte[] peek = snap[..r];

            NetworkStream stream = new(socket, true);

            var _ = Task.Run(async () => await Detect(peek, handler, stream, opt, socket.RemoteEndPoint, fallback));
        }
    }

    public async Task Detect(byte[] peek, Handler handler, NetworkStream stream, SslServerAuthenticationOptions opt, EndPoint end, string fallback)
    {
        // string dump = $"peek ({peek.Length})[ ";
        // foreach (byte b in peek) dump += $"{b}, ";
        // dump += "]";

        // Debug.WriteLine((int)LogLevel.Debug, dump);

        // await socket.DisposeAsync();
        if (peek[0] == 22)
        {
            Debug.WriteLine((int)LogLevel.Debug, "poly was tls");
            await TlsServer.TlsUpgrade(handler, stream, opt, end, fallback);
        }
        else if (peek[0] == Http2Session.MAGIC[0])
        {
            var socket = new TcpSocket(stream);
            using Http2Session h2 = new(socket, Http2Settings.Default(), end);
            await Helper.Http2Loop(h2, handler);
        }
        else if (AllowH09 || AllowH10 || AllowH11)
        {
            using Http1Socket socket = new(new TcpSocket(stream), end) { Allow09 = AllowH09, Allow10 = AllowH10, Allow11 = AllowH11 };

            var client = socket.Client;

            try
            {
                while (!client.HeadersComplete) client = await socket.ReadClientAsync();
            }
            catch (Exception e)
            {
                Debug.WriteColorLine((int)LogLevel.Warning, $"failed to read client ({e.GetType()})", 3);
            }

            try
            {
                if (AllowH2c && client.Headers.TryGetValue("upgrade", out List<string> up) && up[0] == "h2c")
                {
                    using var h2c = await socket.H2CAsync();

                    await h2c.InitAsync();
                    await h2c.SendSettingsAsync(new(4096, null, null, 16777215, 16777215, null));
                    await h2c.SendPingAsync([104, 101, 97, 114, 98, 101, 97, 116]);

                    var upstream = new Http2Stream(1, h2c);
                    await upstream.ReadClientAsync();
                    var _ = Task.Run(async () => await handler(upstream));

                    await Helper.Http2Loop(h2c, handler, false);
                }
                else
                {
                    socket.SetHeader("Connection", "close"); // pipelining soon
                    await handler(socket);
                }
            }
            catch(Exception e)
            {
                Debug.WriteColorLine((int)LogLevel.Critical, $"* server error occured {e.GetType()}", 9);
                Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}\n", 9);
            }
        }
    }
}