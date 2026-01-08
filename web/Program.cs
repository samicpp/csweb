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
using System.Security.Cryptography;
using Samicpp.Http.Http2;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

public class AppConfig
{
    [JsonPropertyName("09-address")] [ConfigurationKeyName("09-address")] public string[] O9Address { get; init; } = [];
    [JsonPropertyName("h2c-address")][ConfigurationKeyName("h2c-address")] public string[] H2cAddress { get; init; } = [];
    [JsonPropertyName("h2-address")] [ConfigurationKeyName("h2-address")] public string[] H2Address { get; init; } = [];
    [JsonPropertyName("ssl-address")] [ConfigurationKeyName("ssl-address")] public string[] SslAddress { get; init; } = [];
    [JsonPropertyName("poly-address")] [ConfigurationKeyName("poly-address")] public string[] PolyAddress { get; init; } = [];

    [JsonPropertyName("p12-cert")] [ConfigurationKeyName("p12-cert")] public string P12Cert { get; init; } = null;
    [JsonPropertyName("p12-pass")] [ConfigurationKeyName("p12-pass")] public string P12pass { get; init; } = null;
    [JsonPropertyName("alpn")] [ConfigurationKeyName("alpn")] public string[] Alpn { get; init; } = null;
    // [JsonPropertyName("fallback-alpn")] [ConfigurationKeyName("fallback-alpn")] public string FallbackAlpn { get; init; } = null;
    [JsonPropertyName("fallback")] [ConfigurationKeyName("fallback")] public bool Fallback { get; init; } = true;

    [JsonPropertyName("cwd")] [ConfigurationKeyName("cwd")] public string WorkDir { get; init; } = null;
    [JsonPropertyName("backlog")] [ConfigurationKeyName("backlog")] public int Backlog { get; init; } = 10;
    [JsonPropertyName("dualmode")] [ConfigurationKeyName("dualmode")] public bool DualMode { get; init; } = false;

    [JsonPropertyName("serve-dir")] [ConfigurationKeyName("serve-dir")] public string ServeDir { get; init; } = "./";
    [JsonPropertyName("use-compression")] [ConfigurationKeyName("use-compression")] public bool UseCompression { get; init; } = true;
    [JsonPropertyName("bigfile-threshold")] [ConfigurationKeyName("bigfile-threshold")] public int BigFileThreshold { get; init; } = 16 * 1024 * 1024; // 16mb
    [JsonPropertyName("bigfile-chunk-size")] [ConfigurationKeyName("bigfile-chunk-size")] public int BigFileChunkSize { get; init; } = 16 * 1024 * 1024; // 16mb
    // [JsonPropertyName("stream-bigfiles")] [ConfigurationKeyName("stream-bigfiles")] public bool StreamBigFiles { get; init; } = false;
    [JsonPropertyName("cache-files")] [ConfigurationKeyName("cache-files")] public bool CacheFiles { get; init; } = true;
    [JsonPropertyName("allow-ranged")] [ConfigurationKeyName("allow-ranged")] public bool AllowRanged { get; init; } = true;

    [JsonPropertyName("allow-plugins")] [ConfigurationKeyName("allow-plugins")] public bool AllowPlugins { get; init; } = true;
    [JsonPropertyName("allow-special")] [ConfigurationKeyName("allow-special")] public bool AllowSpecial { get; init; } = true;
    [JsonPropertyName("allow-scripts")] [ConfigurationKeyName("allow-scripts")] public bool AllowScripts { get; init; } = true;

    [JsonPropertyName("loglevel")] [ConfigurationKeyName("loglevel")] public int? Loglevel { get; init; } = (int)(LogLevel.Info | LogLevel.Init | LogLevel.Warning | LogLevel.SoftError | LogLevel.Error | LogLevel.Critical | LogLevel.Fatal | LogLevel.Assert);


    public static AppConfig Default() => new() { H2cAddress = [ "0.0.0.0:8080" ], SslAddress = [ "0.0.0.0:4433" ], Alpn = [ "h2", "http/1.1" ], ServeDir = "./" };
    public static X509Certificate2 SelfSigned()
    {
        // using RSA rsa = RSA.Create(2048);
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        X500DistinguishedName subject = new("CN=localhost");
        CertificateRequest req = new(subject, ecdsa, HashAlgorithmName.SHA256);
        
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        SubjectAlternativeNameBuilder sanBuilder = new();
        sanBuilder.AddDnsName("*.localhost");
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName("127.0.0.1");
        sanBuilder.AddDnsName("::1");
        req.CertificateExtensions.Add(sanBuilder.Build());

        X509Certificate2 cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1)
        );

        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, ""), "");
    }
    public static readonly Dictionary<string,string> SwitchMappings = new()
    {
        { "-l", "loglevel" },
        { "--address", "poly-address" },
        { "--http-address", "h2c-address" },
        { "--https-address", "ssl-address" },
        { "--tls-address", "ssl-address" },
    }; 
}

// #if AOT_BUILD
[JsonSerializable(typeof(AppConfig))]
public partial class AppConfigContext : JsonSerializerContext { }
// #endif

public class Program
{
    public static Version Version { get; } = new(2, 8, 3, 2);

    static AppConfig TryConfig()
    {
        string[] cliopt = Environment.GetCommandLineArgs();
        try
        {
            // #if AOT_BUILD
            // FileInfo cinfo = new($"./appsettings.json");
            // if (cinfo.Exists) return JsonSerializer.Deserialize(File.ReadAllBytes("./appsettings.json"), AppConfigContext.Default.AppConfig);
            // else return AppConfig.Default();
            // #else
            return new ConfigurationBuilder().AddJsonFile("appsettings.json", false, false).AddCommandLine(cliopt, AppConfig.SwitchMappings).Build().Get<AppConfig>() ?? AppConfig.Default();
            // #endif
        }
        catch (Exception e)
        {
            Debug.WriteLine((int)LogLevel.Warning, $"\e[4;93;40mappsettings.json was invalid\e[0m \e[38;5;8{e.GetType()}\e[0m");
            Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}", 3);
            return AppConfig.Default();
        }
    }
    
    static readonly AppConfig config = TryConfig();
    public readonly static Handlers hands = new(config);

    public static async Task Main()
    {
        var sw = Stopwatch.StartNew();
        // config = TryConfig(cliopt);
        // var addrs = config["h2c-address"].Split(";");
        // var O9addrs = config["09-address"].Split(";");
        // var h2addrs = config["h2-address"].Split(";");
        // var ssladdrs = config["ssl-address"].Split(";");
        // var p12cert = config["p12-cert"];
        // var p12pass = config["p12-pass"];
        var alpns = config.Alpn?.Select(a => new SslApplicationProtocol(a.Trim())).ToList() ?? [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11];
        if (config.WorkDir != null) Directory.SetCurrentDirectory(config.WorkDir);

        Debug.logLevel = config.Loglevel == 0 ? null : config.Loglevel;

        #if AOT_BUILD
        Debug.WriteColorLine((int)LogLevel.Init, $"csweb v{Version} AOT", (52, 235, 210));
        #else
        Debug.WriteColorLine((int)LogLevel.Init, $"csweb v{Version}", (52, 235, 210));
        #endif

        // string alpndump = "[ "; foreach (var a in alpns) alpndump += $"{a}, "; alpndump += "]";
        // Debug.WriteColorLine((int)LogLevel.Verbose, $"alpns = {alpndump}", 8);
        Debug.WriteColorLine((int)LogLevel.Verbose, $"cwd = {Directory.GetCurrentDirectory()}", 8);
        Debug.WriteColorLine((int)LogLevel.Verbose, $"serve-dir = {config.ServeDir}", 8);
        Debug.WriteLine((int)LogLevel.Init, " ");

        List<Task> tasks = [];

        foreach (var addr in config.PolyAddress)
        {
            X509Certificate2 cert;
            if (config.P12Cert?.Length > 0) cert = X509CertificateLoader.LoadPkcs12FromFile(config.P12Cert, config.P12pass ?? "");
            else cert = AppConfig.SelfSigned();

            if (addr.Length <= 0) continue;
            IPEndPoint address = IPEndPoint.Parse(addr.Trim());
            PolyServer tls = new(address, cert) { backlog = config.Backlog, dualmode = config.DualMode, alpn = alpns, fallback = config.Fallback };
            tasks.Add(tls.Serve(Wrapper));


            Debug.WriteColorLine((int)LogLevel.Init, $"\e[2m- Poly serving on \e[22m\e[1mhttps://{address}", (52, 165, 235));
        }
        foreach (var addr in config.SslAddress)
        {
            X509Certificate2 cert;
            if (config.P12Cert?.Length > 0) cert = X509CertificateLoader.LoadPkcs12FromFile(config.P12Cert, config.P12pass ?? "");
            else cert = AppConfig.SelfSigned();

            if (addr.Length <= 0) continue;
            IPEndPoint address = IPEndPoint.Parse(addr.Trim());
            TlsServer tls = new(address, cert) { backlog = config.Backlog, dualmode = config.DualMode, alpn = alpns, fallback = config.Fallback };
            tasks.Add(tls.Serve(Wrapper));


            Debug.WriteColorLine((int)LogLevel.Init, $"\e[2m- HTTPS serving on \e[22m\e[1mhttps://{address}", (76, 235, 52));
        }
        foreach (var addr in config.H2cAddress)
        {
            if (addr.Length <= 0) continue;
            IPEndPoint address = IPEndPoint.Parse(addr.Trim());
            H2CServer tcp = new(address) { backlog = config.Backlog, dualmode = config.DualMode };
            tasks.Add(tcp.Serve(Wrapper));

            Debug.WriteColorLine((int)LogLevel.Init, $"\e[2m- HTTP/1.1 (h2c) serving on \e[22m\e[1mhttp://{address}", (235, 211, 52));
        }
        foreach (var addr in config.H2Address)
        {
            if (addr.Length <= 0) continue;
            IPEndPoint address = IPEndPoint.Parse(addr.Trim());
            H2Server tcp = new(address) { backlog = config.Backlog, dualmode = config.DualMode };
            tasks.Add(tcp.Serve(Wrapper));

            Debug.WriteColorLine((int)LogLevel.Init, $"\e[2m- HTTP/2 serving on \e[22m\e[1mhttp://{address}", (235, 143, 52));
        }
        foreach (var addr in config.O9Address)
        {
            if (addr.Length <= 0) continue;
            IPEndPoint address = IPEndPoint.Parse(addr.Trim());
            O9Server tcp = new(address) { backlog = config.Backlog, dualmode = config.DualMode };
            tasks.Add(tcp.Serve(Wrapper));

            Debug.WriteColorLine((int)LogLevel.Init, $"\e[2m- HTTP/0.9 serving on \e[22m\e[1mhttp://{address}", (235, 52, 52));
        }
        Debug.WriteLine((int)LogLevel.Init, " ");
        


        // O9Server test = new(IPEndPoint.Parse("0.0.0.0:3000"));
        // tasks.Add(test.Serve(Funni));
        // Console.WriteLine("stopwatch freq " + Stopwatch.Frequency);

        Console.CancelKeyPress += (sender, e) =>
        {
            Debug.WriteColorLine((int)LogLevel.Info, "SIGINT received", 9);
            sw.Stop();
            long nanos = sw.ElapsedTicks * (1_000_000_000 / Stopwatch.Frequency);
            long micros = sw.Elapsed.Microseconds;
            long milis = sw.Elapsed.Milliseconds;
            long secs = sw.Elapsed.Seconds;
            long mins = sw.Elapsed.Minutes;
            long hours = sw.Elapsed.Hours;

            string timestamp = "\x1b[38;2;66;245;245mprogram finished after ";

            if (hours > 0)
            {
                timestamp += $"{hours}h ";
            }
            if (mins > 0)
            {
                timestamp += $"{mins % 60}m ";
            }
            if (secs > 0)
            {
                timestamp += $"{secs % 60}s ";
            }
            if (milis > 0)
            {
                timestamp += $"{milis % 1000}ms ";
            }
            timestamp += $"\x1b[38;2;117;117;117m{micros % 1000}us {nanos % 1000}ns\e[0m";
            Debug.WriteLine((int)LogLevel.Info, timestamp);
            

            Environment.Exit(0);
        };


        // hands = new(config, config["serve-dir"]);
        tasks.Add(Debug.Init(config));

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

        Debug.WriteLine((int)LogLevel.Debug, "waiting untill server end");
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch(Exception e)
            {
                Debug.WriteColorLine((int)LogLevel.Fatal, $"! server exception {e.GetType()}", 9);
                Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}\n", 9);
            }
        }

        Debug.WriteLine((int)LogLevel.Verbose, "server done");
    }

    static ulong counter = 0;
    public static ulong Visits { get => counter; }

    public static async Task Wrapper(IDualHttpSocket conn)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            counter++;
            var client = conn.Client;
            while (!client.HeadersComplete) client = await conn.ReadClientAsync();

            string dump = "\x1b[38;2;52;128;235m";
            // Console.ForegroundColor = ConsoleColor.Cyan;
            dump += "IHttpClient {\n";
            dump += $"   IsValid: {client.IsValid}\n";
            dump += $"   Host: {client.Host}\n";
            dump += $"   Method: {client.Method}\n";
            dump += $"   Path: {client.Path.Trim()}\n";
            dump += $"   Version: {client.Version}\n";
            if (client is Http1Client h1client) dump += $"   ClientSpecifiedVersion: {h1client.ClientVersion}\n";
            dump += $"   HeadersComplete: {client.HeadersComplete}\n";
            dump += $"   BodyComplete: {client.BodyComplete}\n";
            dump += $"   Headers.Count: {client.Headers.Count}\n";
            if (client is Http2Client h2client) dump += $"   Scheme: {h2client.Scheme}\n";
            dump += $"   Secure: {conn.IsHttps}\n";
            dump += "}\e[0m";
            Debug.WriteLine((int)LogLevel.Dump, dump);

            string hdump = "\x1b[38;2;52;177;235m";
            // Console.ForegroundColor = ConsoleColor.DarkCyan;
            if (client.Headers.Count <= 0) hdump += "IHttpClient.Headers {}";
            else
            {
                hdump += "IHttpClient.Headers {\n";
                foreach (var (header, vs) in client.Headers)
                {
                    hdump += $"   {header}: [ ";
                    foreach (var value in vs) hdump += $"{value}, ";
                    hdump += "]\n";
                }

                hdump += "}\n";
            }
            hdump += "\e[0m";
            Debug.WriteLine((int)LogLevel.Dump, hdump);

            // throw new Exception("test");

            await hands.Entry(conn);
        }
        catch (Exception e)
        {
            Debug.WriteColorLine((int)LogLevel.Critical, $"* wrapper error occured {e.GetType()}", 9);
            Debug.WriteColorLine((int)LogLevel.Verbose, $"{e}", 9);
        }
        finally
        {
            sw.Stop();
            Debug.WriteColorLine((int)LogLevel.Log, $"request finished after {sw.ElapsedTicks * (1_000_000.0 / Stopwatch.Frequency) / 1_000}ms", (245, 182, 66));
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
