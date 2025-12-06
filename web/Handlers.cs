namespace Samicpp.Web;

using Compression = Samicpp.Http.CompressionType;

using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Samicpp.Http;
using System.Linq;
using System.Text.Json;
using System.Net;
using System.Reflection;

using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Commands;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

public readonly struct RouteConfig()
{
    [JsonPropertyName("match-type")] public string MatchType { get; init; } = "host";
    [JsonPropertyName("dir")] public string Directory { get; init; } = ".";
    [JsonPropertyName("router")] public string Router { get; init; } = null;
}

[JsonSerializable(typeof(Dictionary<string, RouteConfig>))]
public partial class RoutesContext : JsonSerializerContext { }

[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class HeadersContext : JsonSerializerContext { }

public class Handlers(AppConfig appconfig)
{
    string BaseDir { get => appconfig.ServeDir; }
    readonly AppConfig appconfig = appconfig;
    readonly Regex remove1 = new(@"(\?.*$)|(\#.*$)|(\:.*$)", RegexOptions.Compiled);
    readonly Regex remove2 = new(@"\/\.{1,2}(?=\/|$)", RegexOptions.Compiled);
    readonly Regex collapse = new(@"\/+", RegexOptions.Compiled);
    readonly Regex remove3 = new(@"/$", RegexOptions.Compiled);
    readonly Dictionary<string, (DateTime, string, byte[], Compression?)> cache = [];
    readonly Dictionary<string, (string, string)> ccache = [];

    DateTime configTime;
    Dictionary<string, RouteConfig> config = new()
    {
        { "default", new() { Directory = "." } }
    };
    DateTime eheadersTime;
    Dictionary<string, string> eheaders = [];


    string CleanPath(string path)
    {
        var cpath = remove1.Replace(path, "");
        cpath = remove2.Replace(cpath, "");
        cpath = collapse.Replace(cpath, "/");
        cpath = remove3.Replace(cpath, "");
        return cpath;
    }
    string CleanPath(string baseDir, string path)
    {
        var cpath = "/" + remove1.Replace(path, "");
        cpath = remove2.Replace(baseDir + cpath, "");
        cpath = collapse.Replace(cpath, "/");
        cpath = remove3.Replace(cpath, "");
        return cpath;
    }
    
    public async Task Entry(IDualHttpSocket socket)
    {
        if (!socket.Client.IsValid)
        {
            Debug.WriteLine((int)LogLevel.Verbose, "client is not valid");
            await ErrorHandler(socket, "", 400);
            return;
        }

        Debug.WriteLine((int)LogLevel.Debug, "connection established using " + socket.Client.Version);

        if (socket.Client.Headers.TryGetValue("accept-encoding", out List<string> encoding))
        {
            foreach (string s in encoding[0].Split(","))
            {
                switch(s)
                {
                    case "gzip":
                        socket.Compression = Compression.Gzip;
                        socket.SetHeader("Content-Encoding", "gzip");
                        break;
                    
                    case "deflate":
                        socket.Compression = Compression.Deflate;
                        socket.SetHeader("Content-Encoding", "deflate");
                        break;

                    case "br":
                        socket.Compression = Compression.Brotli;
                        socket.SetHeader("Content-Encoding", "br");
                        break;

                    default:
                        socket.Compression = Compression.None;
                        socket.SetHeader("Content-Encoding", "identity");
                        break;
                };
                if (socket.Compression != Compression.None) break;
            }
            Debug.WriteLine((int)LogLevel.Debug, "using compression " + socket.Compression);
        }
        else
        {
            Debug.WriteLine((int)LogLevel.Debug, "no compression");
        }

        string fullhost = $"{(socket.IsHttps ? "https" : "http")}://{socket.Client.Host}{CleanPath("/" + socket.Client.Path)}";

        // bool fresh = false;
        string extra = "";
        string router = null;
        FileInfo cinfo = new($"{BaseDir}/routes.json");
        if (cinfo.Exists && cinfo.LastWriteTime != configTime)
        {
            configTime = cinfo.LastWriteTime;
            var text = await File.ReadAllBytesAsync($"{BaseDir}/routes.json");
            // Console.WriteLine("read routes.json");
            // Console.WriteLine(text);
            try
            {
                RoutesContext context = new();
                config = JsonSerializer.Deserialize(text, RoutesContext.Default.DictionaryStringRouteConfig);
                ccache.Clear();
                // fresh = true;
            }
            catch (Exception)
            {
                Debug.WriteLine((int)LogLevel.Warning, "invalid routes config file");
            }
        }

        FileInfo hinfo = new($"{BaseDir}/headers.json");
        if (hinfo.Exists && hinfo.LastWriteTime != eheadersTime)
        {
            eheadersTime = hinfo.LastWriteTime;
            var text = await File.ReadAllBytesAsync($"{BaseDir}/headers.json");
            try
            {
                eheaders = JsonSerializer.Deserialize(text, HeadersContext.Default.DictionaryStringString);
            }
            catch (Exception)
            {
                Debug.WriteLine((int)LogLevel.Warning, "invalid extra headers file");
            }
        }

        string fullPath;
        string routerPath;
        if (ccache.TryGetValue(fullhost, out var path))
        {
            fullPath = path.Item1;
            routerPath = path.Item2;
        }
        else
        {
            bool cmatch = false;
            foreach (var (k, v) in config)
            {
                if (k == "default") continue;
                var type = v.MatchType;
                if (
                    (type == "host" && socket.Client.Host == k) ||
                    (type == "start" && fullhost.StartsWith(k, StringComparison.CurrentCultureIgnoreCase)) ||
                    (type == "end" && fullhost.EndsWith(k, StringComparison.CurrentCultureIgnoreCase)) ||
                    (type == "regex" && new Regex(k).IsMatch(fullhost)) ||
                    (type == "path-start" && socket.Client.Path.StartsWith(k, StringComparison.CurrentCultureIgnoreCase)) ||
                    (type == "scheme" && k.Equals(socket.IsHttps ? "https" : "http", StringComparison.CurrentCultureIgnoreCase))  ||
                    (type == "protocol" && k.Equals(socket.Client.Version, StringComparison.CurrentCultureIgnoreCase)) 
                )
                {
                    extra = v.Directory;
                    router = v.Router;
                    cmatch = true;
                    break;
                }
            }
            if (!cmatch && config.TryGetValue("default", out var def))
            {
                extra = def.Directory;
                router = def.Router;
            }

            // string rawFullPath = $"{BaseDir}/{extra}/{socket.Client.Path.Trim()}";
            routerPath = router == null ? null : Path.GetFullPath($"{BaseDir}/{extra}/{router}");
            fullPath = Path.GetFullPath(CleanPath($"{BaseDir}/{extra}/", socket.Client.Path.Trim()));
            ccache[fullhost] = (fullPath, routerPath);
            Debug.WriteLine((int)LogLevel.Debug, $"routes path '{extra}' -> '{routerPath}' '{fullPath}'");
        }

        Debug.WriteColorLine((int)LogLevel.Info, $"↓ {socket.Client.Method} '{fullhost}'", 8);
        Debug.WriteColorLine((int)LogLevel.Log, $"full path = {fullPath}", 5);
        if (routerPath != null) Debug.WriteColorLine((int)LogLevel.Log, $"router path = {routerPath}", 5);

        foreach (var (k,v) in eheaders)
        {
            socket.SetHeader(k, v);
        }
        // int e = 0;
        // int a = 1 / e;

        if (routerPath != null)
        {
            FileSystemInfo info = new FileInfo(routerPath);
            if (!info.Exists) info = new DirectoryInfo(routerPath);
            if (info.Exists) info = info.ResolveLinkTarget(true) ?? info;

            bool cached = false;
            if (cache.TryGetValue(fullhost, out var tcbe))
            {
                var (t, c, b, e) = tcbe;
                if (t == info.LastWriteTime)
                {
                    Debug.WriteLine((int)LogLevel.Verbose, "caching response");
                    socket.SetHeader("Content-Type", c);
                    if (e != null)
                    {
                        var compression = e switch
                        {
                            Compression.None => "identity",
                            Compression.Gzip => "gzip",
                            Compression.Deflate => "deflate",
                            Compression.Brotli => "br",
                            _ => "identity",
                        };
                        socket.SetHeader("Content-Encoding", compression);
                        socket.Compression = Compression.None;
                    }

                    await socket.CloseAsync(b);
                    cached = true;
                }
            }

            if (!cached) await Handle(socket, routerPath, info, fullPath);
        }
        else
        {
            FileSystemInfo info = new FileInfo(fullPath);
            if (!info.Exists) info = new DirectoryInfo(fullPath);
            if (info.Exists) info = info.ResolveLinkTarget(true) ?? info;

            bool cached = false;
            if (cache.TryGetValue(fullhost, out var tcbe))
            {
                var (t, c, b, e) = tcbe;
                if (t == info.LastWriteTime)
                {
                    Debug.WriteLine((int)LogLevel.Verbose, "caching response");
                    socket.SetHeader("Content-Type", c);
                    if (e != null)
                    {
                        var compression = e switch
                        {
                            Compression.None => "identity",
                            Compression.Gzip => "gzip",
                            Compression.Deflate => "deflate",
                            Compression.Brotli => "br",
                            _ => "identity",
                        };
                        socket.SetHeader("Content-Encoding", compression);
                        socket.Compression = Compression.None;
                    }

                    await socket.CloseAsync(b);
                    cached = true;
                }
            }

            if (!cached) await Handle(socket, fullPath, info, null);
        }
    }

    public async Task Handle(IDualHttpSocket socket, string fullPath, FileSystemInfo info, string normalPath)
    {
        // FileSystemInfo info = new FileInfo(fullPath);
        // if (!info.Exists) info = new DirectoryInfo(fullPath);
        // if (info.Exists) info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;

        // if (!info.Exists) Console.WriteLine($"{fullPath} doesnt exist");
        // else if (info is FileInfo) Console.WriteLine($"file {info.FullName}");
        // else if (info is DirectoryInfo) Console.WriteLine($"directory {info.FullName}");
        // else Console.WriteLine($"unknown type {info.FullName}");

        if (!info.Exists)
        {
            // socket.Status = 404;
            // socket.StatusMessage = "Not Found";
            // socket.SetHeader("Content-Type", "text/plain");
            // await socket.CloseAsync("404 Not Found");
            await ErrorHandler(socket, fullPath, 404);
        }
        else if (info is FileInfo fi)
        {
            await FileHandler(socket, fullPath, normalPath);
        }
        else if (info is DirectoryInfo di)
        {
            await DirectoryHandler(socket, fullPath, normalPath);
        }
        else
        {
            await ErrorHandler(socket, fullPath, 501);
        }
    }

    public async Task ErrorHandler(IDualHttpSocket socket, string path, int code, string status = "", string message = "", string debug = "")
    {
        socket.SetHeader("Content-Type", "text/plain");
        // Debug.WriteColorLine((int)LogLevel.Error, $"{code} error", 1);

        switch (code)
        {
            case 400:
                socket.Status = 400;
                socket.StatusMessage = "Bad Request";
                await socket.CloseAsync($"fix your client idk\n");
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} invalid client", (255, 119, 0));
                break;

            case 404:
                socket.Status = 404;
                socket.StatusMessage = "Not Found";
                await socket.CloseAsync($"{socket.Client.Path} Not Found\n");
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} {path}", (255, 119, 0));
                break;

            case 409:
                socket.Status = 409;
                socket.StatusMessage = "Conflict";
                await socket.CloseAsync("Something went wrong\n");
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} no index", (255, 119, 0));
                break;

            case 500:
                socket.Status = 500;
                socket.StatusMessage = "Internal Server Error";
                await socket.CloseAsync($"{message}:\n{debug}");
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} {message}", (255, 119, 0));
                break;

            case 501:
                socket.Status = 501;
                socket.StatusMessage = "Not Implemented";
                await socket.CloseAsync($"Couldnt handle {socket.Client.Path}\n");
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} not implemented", (255, 119, 0));
                break;

            default:
                socket.Status = code;
                socket.StatusMessage = status;
                await socket.CloseAsync($"{message}:\n{debug}");
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} '{message}'", (255, 119, 0));
                break;
        }
    }
    
    public async Task DirectoryHandler(IDualHttpSocket socket, string path, string normalPath)
    {
        // await socket.CloseAsync("directory");
        // Console.WriteLine("directory " + path);
        string last = path.Replace("\\", "/").Split("/").Last().ToLower();
        var files = Directory.GetFiles(path).Select(f => { /*Console.WriteLine($"direntry {f}");*/ return f.Replace("\\", "/").Split('/').Last(); });

        string found = null;
        found = files.FirstOrDefault(f => f.StartsWith(last, StringComparison.CurrentCultureIgnoreCase));
        found ??= files.FirstOrDefault(f => f.StartsWith("index", StringComparison.CurrentCultureIgnoreCase));


        if (found != null)
        {
            Debug.WriteLine((int)LogLevel.Log, $"found file {path}/{found}");
            await FileHandler(socket, $"{path}/{found}", normalPath);
        }
        else
        {
            Debug.WriteLine((int)LogLevel.Log, "found no files");
            await ErrorHandler(socket, path, 409);
        }
    }

    Dictionary<string, (DateTime, IHttpPlugin)> plugins = [];
    public async Task FileHandler(IDualHttpSocket socket, string path, string normalPath)
    {
        // await socket.CloseAsync("file");
        // TODO: files over 500mb need to be streamed
        var info = new FileInfo(path);
        var str = info.OpenRead();
        var name = info.Name;

        var dot = name.Split(".");
        var ext = dot.Last();
        var dmt = MimeTypes.types.GetValueOrDefault(ext) ?? "application/octet-stream";
        string fullhost = $"{(socket.IsHttps ? "https" : "http")}://{socket.Client.Host}{CleanPath(socket.Client.Path)}";


        if (name.EndsWith(".blank"))
        {
            socket.Status = 204;
            socket.StatusMessage = "No Content";
            await socket.CloseAsync();
            Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ 204 blank", 2);
        }
        #if !AOT_BUILD
        else if (name.EndsWith(".dll"))
        {
            try
            {
                if (plugins.TryGetValue(path, out var plug) && info.LastWriteTime == plug.Item1 && plug.Item2.Alive)
                {
                    Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ 0 handed over control", 5);
                    await plug.Item2.Handle(socket, normalPath ?? path);
                }
                else
                {
                    Debug.WriteColorLine((int)LogLevel.Info, "\e[2mloading new plugin " + path, (255, 173, 173)); // dangerous, hence elevated log level
                    
                    string tname = Path.GetFileNameWithoutExtension(name);
                    byte[] lib = await File.ReadAllBytesAsync(path);
                    Assembly assembly = Assembly.Load(lib);
                    Type type = assembly.GetType(tname);

                    IHttpPlugin plugin = (IHttpPlugin)Activator.CreateInstance(type);
                    await plugin.Init(path);
                    if (plugin.Alive) plugins[path] = (info.LastWriteTime, plugin);

                    Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ 0 handed over control", 5);
                    await plugin.Handle(socket, normalPath ?? path);
                }
            }
            catch (Exception e)
            {
                await ErrorHandler(socket, path, 500, "", "plugin error", e.ToString());
            }
        }
        #else
        else if (name.EndsWith(".dll"))
        {
            await ErrorHandler(socket, path, 501);
        }
        #endif
        else if (false && name.EndsWith(".cs"))
        {
            // await cskernel.SetValueAsync("socket", socket, typeof(IDualHttpSocket));
        }
        else if (name.EndsWith(".redirect") || name.EndsWith(".link") || name.Contains(".var."))
        {
            Debug.WriteLine((int)LogLevel.Verbose, "special file");
            string utext = await File.ReadAllTextAsync(path);
            EndPoint ip = socket.EndPoint ?? IPEndPoint.Parse("[::]:0");
            string addr = ip.ToString();

            if (ip is IPEndPoint end)
            {
                addr = end.Address.ToString();
            }

            Dictionary<string, string> vars = new()
            {
                { "%IP%", addr },
                { "%FULL_IP%", ip.ToString() },
                { "%PATH%", socket.Client.Path },
                { "%HOST%", socket.Client.Host },
                { "%SCHEME%", socket.IsHttps ? "https" : "http" },
                { "%BASE_DIR%", BaseDir },
                { "%USER_AGENT%", socket.Client.Headers.GetValueOrDefault("user-agent")?[0] ?? "null" }
            };

            foreach (var (k, v) in vars)
            {
                utext = utext.Replace(k, v);
            }

            if (name.Contains(".var."))
            {
                Debug.WriteLine((int)LogLevel.Debug, "var file");
                socket.SetHeader("Content-Type", dmt);
                await socket.CloseAsync(utext);
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ 200 '{path}' ({utext.Length})", 2);
            }
            else if (name.EndsWith(".redirect"))
            {
                Debug.WriteLine((int)LogLevel.Debug, "redirect file");

                string location = utext.Replace("\n", "").Trim();
                int code = 302;
                if (dot.Length >= 2 && int.TryParse(dot[^2], out int scode)) code = scode;

                // TODO: cache these
                socket.Status = code;
                socket.StatusMessage = "Found";
                socket.SetHeader("Location", location);

                await socket.CloseAsync();
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} {location}", 6);
            }
            else if (name.EndsWith(".link"))
            {
                Debug.WriteLine((int)LogLevel.Debug, "link, passing request to handler");

                FileSystemInfo ninfo = new FileInfo(utext);
                if (!info.Exists) ninfo = new DirectoryInfo(utext);
                if (info.Exists) ninfo = info.ResolveLinkTarget(returnFinalTarget: true) ?? ninfo;

                await Handle(socket, utext.Trim(), ninfo, normalPath);
            }
        }
        else if (name.EndsWith(".br") || name.EndsWith(".gz"))
        {
            var dts = name.Split(".");
            var last = dts.ElementAtOrDefault(dts.Length - 2) ?? "";
            var enc = dts.Last();

            dmt = MimeTypes.types.GetValueOrDefault(last) ?? "application/octet-stream";
            socket.Compression = Compression.None;
            socket.SetHeader("Content-Encoding", enc == "br" ? "br" : "gzip");

            socket.SetHeader("Content-Type", dmt);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            await socket.CloseAsync(bytes);
            cache[fullhost] = (info.LastWriteTime, dmt, bytes, enc == "br" ? Compression.Brotli : Compression.Gzip);
            Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ 200 '{path}' ({bytes.Length})", 2);
        }
        else
        {
            socket.SetHeader("Content-Type", dmt);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            await socket.CloseAsync(bytes);
            cache[fullhost] = (info.LastWriteTime, dmt, bytes, null);
            Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ 200 '{path}' ({bytes.Length})", 2);
        }
    }
}