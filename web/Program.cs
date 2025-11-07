namespace Samicpp.Web;

using Samicpp.Http;
using Samicpp.Http.Http1;

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Samicpp.Http.Http09;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Linq;
using System.Net.Security;
using Samicpp.Http.Debug;

public class Program
{
    readonly static IConfigurationRoot config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

    static Handlers hands;
    public static async Task Main()
    {
        var sw = Stopwatch.StartNew();
        var addrs = config["h2c-address"].Split(";");
        var O9addrs = config["09-address"].Split(";");
        var h2addrs = config["h2-address"].Split(";");
        var ssladdrs = config["ssl-address"].Split(";");
        var p12cert = config["p12-cert"];
        var p12pass = config["p12-pass"];
        var alpn = config["alpn"].Split(";").Select(a => new SslApplicationProtocol(a.Trim())).ToList();

        Console.WriteLine("\e[38;2;52;235;210mcsweb v2.6.11\e[0m");
        Console.WriteLine($"cwd = {Directory.GetCurrentDirectory()}");

        List<Task> tasks = [];

        foreach (var addr in addrs)
        {
            if (addr.Length <= 0) continue;
            IPEndPoint address = IPEndPoint.Parse(addr.Trim());
            H2CServer tcp = new(address);
            tasks.Add(tcp.Serve(Wrapper));

            Console.WriteLine($"\e[38;2;235;211;52mHTTP/1.1 (h2c) serving on http://{address}\e[0m");
        }
        foreach (var addr in O9addrs)
        {
            if (addr.Length <= 0) continue;
            IPEndPoint address = IPEndPoint.Parse(addr.Trim());
            O9Server tcp = new(address);
            tasks.Add(tcp.Serve(Wrapper));

            Console.WriteLine($"\e[38;2;235;52;52mHTTP/0.9 serving on http://{address}\e[0m");
        }
        foreach (var addr in h2addrs)
        {
            if (addr.Length <= 0) continue;
            IPEndPoint address = IPEndPoint.Parse(addr.Trim());
            H2Server tcp = new(address);
            tasks.Add(tcp.Serve(Wrapper));

            Console.WriteLine($"\e[38;2;235;143;52mHTTP/2 serving on http://{address}\e[0m"); // direct http2 rarely supported, hence the orange color
        }
        foreach (var addr in ssladdrs)
        {
            if (addr.Length <= 0) continue;
            IPEndPoint address = IPEndPoint.Parse(addr.Trim());
            TlsServer tls = new(address, X509CertificateLoader.LoadPkcs12FromFile(p12cert, p12pass));
            tasks.Add(tls.Serve(Wrapper));

            tls.alpn = alpn;

            Console.WriteLine($"\e[38;2;76;235;52mHTTPS serving on https://{address}\e[0m");
        }


        // O9Server test = new(IPEndPoint.Parse("0.0.0.0:3000"));
        // tasks.Add(test.Serve(Funni));
        // Console.WriteLine("stopwatch freq " + Stopwatch.Frequency);

        Console.CancelKeyPress += (sender, e) =>
        {
            Console.WriteLine("SIGINT received");
            sw.Stop();
            long nanos = sw.ElapsedTicks * (1_000_000_000 / Stopwatch.Frequency);
            long micros = sw.Elapsed.Microseconds;
            long milis = sw.Elapsed.Milliseconds;
            long secs = sw.Elapsed.Seconds;
            long mins = sw.Elapsed.Minutes;
            long hours = sw.Elapsed.Hours;

            Console.Write("\x1b[38;2;66;245;245m");
            Console.Write($"program finished after ");

            if (hours > 0)
            {
                Console.Write($"{hours}h ");
            }
            if (mins > 0)
            {
                Console.Write($"{mins % 60}m ");
            }
            if (secs > 0)
            {
                Console.Write($"{secs % 60}s ");
            }
            if (milis > 0)
            {
                Console.Write($"{milis % 1000}ms ");
            }
            Console.WriteLine($"\x1b[38;2;117;117;117m{micros % 1000}us {nanos % 1000}ns\e[0m");
            
            


            

            Environment.Exit(0);
        };


        hands = new(config, config["serve-dir"]);

        // HttpClient testClient = new()
        // {
        //     Host = "localhost",
        //     Method = "GET",
        //     Path = "/",
        // };
        // using FakeHttpSocket test = new(testClient);
        // await Wrapper(test);
        // ScriptNode node = new();
        // await node.Execute("System.Console.WriteLine(\"hello world\");", test, ScriptType.CSharp);

        Console.WriteLine("waiting untill server end");
        foreach (var task in tasks) await task;

        Console.WriteLine("server done");
    }

    public static async Task Wrapper(IDualHttpSocket conn)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var client = conn.Client;
            while (!client.HeadersComplete) client = await conn.ReadClientAsync();

            Console.WriteLine("connection established using " + client.Version);

            Console.WriteLine("\x1b[38;2;52;128;235m");
            // Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("IHttpClient {");
            Console.WriteLine($"   IsValid: {client.IsValid}");
            Console.WriteLine($"   Host: {client.Host}");
            Console.WriteLine($"   Method: {client.Method}");
            Console.WriteLine($"   Path: {client.Path.Trim()}");
            Console.WriteLine($"   Version: {client.Version}");
            Console.WriteLine($"   HeadersComplete: {client.HeadersComplete}");
            Console.WriteLine($"   BodyComplete: {client.BodyComplete}");
            Console.WriteLine($"   Headers.Count: {client.Headers.Count}");
            Console.WriteLine($"   Scheme: {(conn.IsHttps ? "https" : "http")}");
            Console.WriteLine($"   Secure: {conn.IsHttps}");
            Console.WriteLine("}");
            Console.ResetColor();

            Console.WriteLine("\x1b[38;2;52;177;235m");
            // Console.ForegroundColor = ConsoleColor.DarkCyan;
            if (client.Headers.Count <= 0) Console.WriteLine("IHttpClient.Headers {}");
            else
            {
                Console.WriteLine("IHttpClient.Headers {");
                foreach (var (header, vs) in client.Headers)
                {
                    Console.Write($"   {header}: [ ");
                    foreach (var value in vs) Console.Write($"{value}, ");
                    Console.WriteLine("]");
                }
                Console.WriteLine("}");
            }
            Console.ResetColor();

            // throw new Exception("");

            await hands.Entry(conn);
        }
        catch (Exception e)
        {
            Console.WriteLine("\x1b[91mwrapper error occured");
            Console.WriteLine(e);
            Console.ResetColor();
        }
        finally
        {
            sw.Stop();
            Console.Write("\x1b[38;2;245;182;66m");
            Console.WriteLine($"request finished after {sw.ElapsedTicks * (1_000_000.0 / Stopwatch.Frequency) / 1_000}ms\x1b[0m");
            // await conn.DisposeAsync();
        }
    }

    // public static async Task Funni(Http09Socket conn)
    // {
    //     try
    //     {
    //         var client = (Http09Client)conn.Client;
    //         Console.WriteLine("connection established using " + client.version);

    //         Console.WriteLine("Http09Client {");
    //         Console.WriteLine($"   method: {client.method}");
    //         Console.WriteLine($"   path: {client.path.Trim()}");
    //         Console.WriteLine($"   version: {client.version}");
    //         Console.WriteLine("}");

    //         await conn.CloseAsync("angry bord");
    //     }
    //     catch (Exception e)
    //     {
    //         Console.WriteLine(e);
    //     }
    //     finally
    //     {
    //         await conn.DisposeAsync();
    //     }
    // }
}
