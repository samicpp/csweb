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


public class Program
{
    readonly static IConfigurationRoot config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

    public static async Task Main()
    {
        var addrs = config["address"].Split(";");

        Console.WriteLine($"cwd = {Directory.GetCurrentDirectory()}");

        List<Task> tasks = [];

        foreach (var addr in addrs)
        {
            IPEndPoint address = IPEndPoint.Parse(addr);
            TcpServer tcp = new(address);
            tasks.Add(tcp.Serve(Handler));

            Console.WriteLine($"serving on {address}");
        }

        foreach (var task in tasks) await task;

        Console.WriteLine("done ig");
    }

    public static async Task Handler(IDualHttpSocket conn)
    {
        try
        {
            var client = conn.Client;
            Console.WriteLine("connection established using " + client.Version);

            Console.WriteLine("IHttpClient {");
            Console.WriteLine($"   Host: {client.Host}");
            Console.WriteLine($"   Method: {client.Method}");
            Console.WriteLine($"   Path: {client.Path}");
            Console.WriteLine($"   Version: {client.Version}");
            Console.WriteLine($"   HeadersComplete: {client.HeadersComplete}");
            Console.WriteLine($"   BodyComplete: {client.BodyComplete}");
            Console.WriteLine("}");

            await conn.CloseAsync("angry bord");
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }
}
