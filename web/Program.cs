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

public class Program
{
    readonly static IConfigurationRoot config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

    static Handlers hands;
    public static async Task Main()
    {
        var addrs = config["address"].Split(";");
        var O9addrs = config["09-address"].Split(";");

        Console.WriteLine($"cwd = {Directory.GetCurrentDirectory()}");

        List<Task> tasks = [];

        foreach (var addr in addrs)
        {
            if (addr.Length <= 0) continue;
            IPEndPoint address = IPEndPoint.Parse(addr);
            TcpServer tcp = new(address);
            tasks.Add(tcp.Serve(Wrapper));

            Console.WriteLine($"HTTP/1.1 (h2c) serving on http://{address}");
        }
        foreach (var addr in O9addrs)
        {
            if (addr.Length <= 0) continue;
            IPEndPoint address = IPEndPoint.Parse(addr);
            O9Server tcp = new(address);
            tasks.Add(tcp.Serve(Wrapper));

            Console.WriteLine($"HTTP/0.9 serving on http://{address}");
        }


        // O9Server test = new(IPEndPoint.Parse("0.0.0.0:3000"));
        // tasks.Add(test.Serve(Funni));


        hands = new(config, config["serve-dir"]);

        foreach (var task in tasks) await task;

        Console.WriteLine("server done");
    }

    public static async Task Wrapper(IDualHttpSocket conn)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var client = conn.Client;
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


            await hands.Entry(conn);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
        finally
        {
            sw.Stop();
            Console.Write("\x1b[38;2;245;182;66m");
            Console.WriteLine($"request finished after {((float)sw.Elapsed.Microseconds)/1_000}ms\x1b[0m");
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
