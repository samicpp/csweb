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

using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Samicpp.Http.Http2;
using System.Text;
using Samicpp.Http.Http1;
using System.Security.Cryptography;
using System.Buffers.Text;

public readonly struct BuiltinOpt()
{
    [JsonPropertyName("name")] public string Name { get; init; } = null;

}
public readonly struct RouteConfig()
{
    [JsonPropertyName("match-type")] public string MatchType { get; init; } = "host";
    [JsonPropertyName("dir")] public string Directory { get; init; } = ".";
    [JsonPropertyName("router")] public string Router { get; init; } = null;
    [JsonPropertyName("builtin")] public BuiltinOpt? Builtin { get; init; } = null;

    // [JsonPropertyName("400")] public string E400 { get; init; } = null;
    [JsonPropertyName("404")] public string E404 { get; init; } = null;
    [JsonPropertyName("409")] public string E409 { get; init; } = null;
    // [JsonPropertyName("500")] public string E500 { get; init; } = null;
    // [JsonPropertyName("501")] public string E501 { get; init; } = null;
}
// public class CacheEntry()
// {
//     public DateTime LastModified = DateTime.MinValue;
//     public string ContentType = "application/octet-stream";
//     public byte[] Bytes = [];
//     public CompressionType? Compression = null;
// }


[JsonSerializable(typeof(Dictionary<string, RouteConfig>))]
public partial class RoutesContext : JsonSerializerContext { }

[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class HeadersContext : JsonSerializerContext { }

public class Handlers(AppConfig app)
{
    string BaseDir { get => app.ServeDir; }
    readonly AppConfig app = app;
    static readonly Regex remove1 = new(@"(\?.*$)|(\#.*$)|(\:.*$)", RegexOptions.Compiled);
    static readonly Regex remove2 = new(@"\/\.{1,2}(?=\/|$)", RegexOptions.Compiled);
    static readonly Regex remove3 = new(@"/$", RegexOptions.Compiled);
    static readonly Regex collapse = new(@"\/+", RegexOptions.Compiled);
    static readonly Regex domain = new(@"([a-z|0-9|\-]+\.)?([a-z|0-9|\-]+)(?=:|$)", RegexOptions.Compiled);
    readonly Dictionary<string, (DateTime, string, byte[], Compression?)> cache = [];
    readonly Dictionary<string, (string, string, RouteConfig)> ccache = [];

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
            Debug.WriteLine((int)LogLevel.SoftError, "# client is not valid");
            await ErrorHandler(socket, new(), "", 400);
            return;
        }

        Debug.WriteLine((int)LogLevel.Debug, "connection established using " + socket.Client.Version);

        socket.Compression = Compression.None;
        socket.SetHeader("Content-Encoding", "identity");

        if (app.UseCompression && socket.Client.Headers.TryGetValue("accept-encoding", out List<string> encoding))
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
                };
            }
            Debug.WriteLine((int)LogLevel.Debug, "using compression " + socket.Compression);
        }
        else
        {
            Debug.WriteLine((int)LogLevel.Debug, "no compression");
        }

        string mfullhost = $"{(socket.IsHttps ? "https" : "http")}://{socket.Client.Host}{CleanPath("/" + socket.Client.Path)}";
        string fullhost = $"[{socket.Client.VersionString}]{mfullhost}";

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
        RouteConfig conf = new();
        if (ccache.TryGetValue(fullhost, out var path))
        {
            fullPath = path.Item1;
            routerPath = path.Item2;
            conf = path.Item3;
        }
        else
        {
            bool cmatch = false;
            Match dmm = domain.Match(socket.Client.Host);
            string dm = socket.Client.Host;
            if (dmm.Success) dm = dmm.Value;

            foreach (var (k, v) in config)
            {
                if (k == "default") continue;
                var type = v.MatchType;
                if (
                    (type == "host" && k.Equals(socket.Client.Host, StringComparison.CurrentCultureIgnoreCase)) ||
                    (type == "start" && mfullhost.StartsWith(k, StringComparison.CurrentCultureIgnoreCase)) ||
                    (type == "end" && mfullhost.EndsWith(k, StringComparison.CurrentCultureIgnoreCase)) ||
                    (type == "regex" && new Regex(k).IsMatch(mfullhost)) ||
                    (type == "path-start" && socket.Client.Path.StartsWith(k, StringComparison.CurrentCultureIgnoreCase)) ||
                    (type == "scheme" && k.Equals(socket.IsHttps ? "https" : "http", StringComparison.CurrentCultureIgnoreCase))  ||
                    (type == "protocol" && k.Equals(socket.Client.VersionString, StringComparison.CurrentCultureIgnoreCase)) ||
                    (type == "domain" && k.Equals(dm, StringComparison.CurrentCultureIgnoreCase))
                )
                {
                    extra = v.Directory;
                    router = v.Router;
                    cmatch = true;
                    conf = v;
                    break;
                }
            }
            if (!cmatch && config.TryGetValue("default", out var def))
            {
                extra = def.Directory;
                router = def.Router;
                conf = def;
            }
            // else if (!cmatch)
            // {
            //     conf = new() { };
            // }

            // string rawFullPath = $"{BaseDir}/{extra}/{socket.Client.Path.Trim()}";
            routerPath = router == null ? null : Path.GetFullPath($"{BaseDir}/{extra}/{router}");
            fullPath = Path.GetFullPath(CleanPath($"{BaseDir}/{extra}/", socket.Client.Path.Trim()));
            ccache[fullhost] = (fullPath, routerPath, conf);
            Debug.WriteLine((int)LogLevel.Debug, $"routes path '{conf.Directory}' -> '{routerPath}' '{fullPath}'");
            Debug.WriteLine((int)LogLevel.Debug, $"error files 404:'{conf.E404}' 409:'{conf.E409}'");
        }

        Debug.WriteColorLine((int)LogLevel.Info, $"↓ {socket.Client.Method} '{fullhost}' {socket.EndPoint}", 8);
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

            if (!cached) await Handle(socket, conf, routerPath, info, fullPath);
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

            if (!cached) await Handle(socket, conf, fullPath, info, null);
        }
    }

    public async Task Handle(IDualHttpSocket socket, RouteConfig conf, string fullPath, FileSystemInfo info, string normalPath)
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
            await ErrorHandler(socket, conf, fullPath, 404);
        }
        else if (info is FileInfo fi)
        {
            await FileHandler(socket, conf, fullPath, normalPath);
        }
        else if (info is DirectoryInfo di)
        {
            await DirectoryHandler(socket, conf, fullPath, normalPath);
        }
        else
        {
            await ErrorHandler(socket, conf, fullPath, 501);
        }
    }

    public async Task ErrorHandler(IDualHttpSocket socket, RouteConfig conf, string path, int code, string status = "", string message = "", string debug = "")
    {
        socket.SetHeader("Content-Type", "text/plain");
        // Debug.WriteColorLine((int)LogLevel.Error, $"{code} error", 1);

        string e404 = null;
        if (conf.E404 != null )
        {
            string u404 = Path.GetFullPath($"{BaseDir}/{conf.Directory}/{conf.E404}");
            if (new FileInfo(u404).Exists) e404 = u404;
            Debug.WriteLine((int)LogLevel.Debug, $"404 error file present {u404}");
        }

        string e409 = null;
        if (conf.E409 != null )
        {
            string u409 = Path.GetFullPath($"{BaseDir}/{conf.Directory}/{conf.E409}");
            if (new FileInfo(u409).Exists) e409 = u409;
            Debug.WriteLine((int)LogLevel.Debug, $"409 error file present {u409}");
        }

        switch (code)
        {
            case 400:
                socket.Status = 400;
                socket.StatusMessage = "Bad Request";
                await socket.CloseAsync($"broken request\n");
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} invalid client", (255, 119, 0));
                break;

            case 404:
                socket.Status = 404;
                socket.StatusMessage = "Not Found";
                if (e404 != null)
                {
                    await FileHandler(socket, conf, e404, socket.Client.Path);
                    Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m, {code} '{path}' error file used", (255, 119, 0));
                }
                else
                {
                    await socket.CloseAsync($"{socket.Client.Path} Not Found\n");
                    Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} '{path}'", (255, 119, 0));
                }
                break;

            case 409:
                socket.Status = 409;
                socket.StatusMessage = "Conflict";
                if (e409 != null)
                {
                    await FileHandler(socket, conf, e409, socket.Client.Path);
                    Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m, {code} 'no index' error file used", (255, 119, 0));
                }
                else
                {
                    await socket.CloseAsync("Something went wrong\n");
                    Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} 'no index'", (255, 119, 0));
                }
                break;

            case 500:
                socket.Status = 500;
                socket.StatusMessage = "Internal Server Error";
                await socket.CloseAsync($"{message}:\n{debug}");
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} '{message}'", (255, 119, 0));
                break;

            case 501:
                socket.Status = 501;
                socket.StatusMessage = "Not Implemented";
                await socket.CloseAsync($"Couldnt handle {socket.Client.Path}\n");
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} 'not implemented'", (255, 119, 0));
                break;

            default:
                socket.Status = code;
                socket.StatusMessage = status;
                await socket.CloseAsync($"{message}:\n{debug}");
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {code} '{message}'", (255, 119, 0));
                break;
        }
    }
    
    public async Task DirectoryHandler(IDualHttpSocket socket, RouteConfig conf, string path, string normalPath)
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
            await FileHandler(socket, conf, $"{path}/{found}", normalPath);
        }
        else
        {
            Debug.WriteLine((int)LogLevel.Log, "found no files");
            await ErrorHandler(socket, conf, path, 409);
        }
    }

    readonly Dictionary<string, (DateTime, IHttpPlugin)> plugins = [];
    static readonly Regex netPluginExt = new(@"\.net(\.dll)?$", RegexOptions.Compiled);

    public async Task FileHandler(IDualHttpSocket socket, RouteConfig conf, string path, string normalPath)
    {
        // await socket.CloseAsync("file");
        // TODO: files over 500mb need to be streamed
        var info = new FileInfo(path);
        using FileStream file = File.OpenRead(path);
        // using var str = info.OpenRead();
        var name = info.Name;

        var dot = name.Split(".");
        var ext = dot.Last();
        var dmt = MimeTypes.types.GetValueOrDefault(ext) ?? "application/octet-stream";
        // string fullhost = $"{(socket.IsHttps ? "https" : "http")}://{socket.Client.Host}{CleanPath(socket.Client.Path)}";
        string fullhost = $"[{socket.Client.VersionString}]{(socket.IsHttps ? "https" : "http")}://{socket.Client.Host}{CleanPath("/" + socket.Client.Path)}";
        long length = info.Length;


        if (name.EndsWith(".blank"))
        {
            socket.Status = 204;
            socket.StatusMessage = "No Content";
            await socket.CloseAsync();
            Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {socket.Status} blank", 2);
        }
        #if !AOT_BUILD
        else if (app.AllowPlugins && netPluginExt.IsMatch(name))
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
                    
                    string tname = netPluginExt.Replace(name, "");
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
                await ErrorHandler(socket, conf, path, 500, "", "plugin error", e.ToString());
            }
        }
        #else
        else if (netPluginExt.IsMatch(name))
        {
            await ErrorHandler(socket, conf, path, 501);
        }
        #endif
        else if (false && app.AllowPlugins && name.EndsWith(".cs"))
        {
            // await cskernel.SetValueAsync("socket", socket, typeof(IDualHttpSocket));
        }
        else if (app.AllowSpecial && (name.EndsWith(".redirect") || name.EndsWith(".link") || name.Contains(".var.")))
        {
            Debug.WriteLine((int)LogLevel.Verbose, "special file");
            string utext = await File.ReadAllTextAsync(path);
            EndPoint ip = socket.EndPoint ?? IPEndPoint.Parse("[::]:0");
            string addr = ip.ToString();
            Match dmm = domain.Match(socket.Client.Host);
            string dm = socket.Client.Host;

            if (dmm.Success) dm = dmm.Value;

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
                { "%USER_AGENT%", socket.Client.Headers.GetValueOrDefault("user-agent")?[0] ?? "null" },
                { "%DOMAIN%", dm },
                { "%VERSION%", socket.Client.VersionString },
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
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {socket.Status} '{path}' ({utext.Length})", 2);
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

                await Handle(socket, conf, utext.Trim(), ninfo, normalPath);
            }
        }
        else
        {
            socket.SetHeader("Content-Type", dmt);
            socket.SetHeader("Last-Modified", $"{info.LastWriteTimeUtc}");
            socket.SetHeader("Accept-Ranges", "bytes");
            
            var etag = SHA256.HashData(Encoding.UTF8.GetBytes($"{info}@{info.LastWriteTimeUtc}"));
            socket.SetHeader("ETag", Convert.ToBase64String(etag));

            List<(long,long)> rsend = [];
            bool invalid = false;

            Compression? precompressed = null;
            if (name.EndsWith(".br") || name.EndsWith(".gz"))
            {
                var dts = name.Split(".");
                var last = dts.ElementAtOrDefault(dts.Length - 2) ?? "";
                var enc = dts.Last();

                dmt = MimeTypes.types.GetValueOrDefault(last) ?? "application/octet-stream";
                socket.Compression = Compression.None;
                socket.SetHeader("Content-Encoding", enc == "br" ? "br" : "gzip");

                socket.SetHeader("Content-Type", dmt);
                precompressed = enc == "br" ? Compression.Brotli : Compression.Gzip;
            }

            if (app.AllowRanged && socket.Client.Headers.TryGetValue("range", out List<string> rangehs))
            {
                foreach(string ranges in rangehs) foreach(string range in ranges.Split(','))
                {
                    if (string.IsNullOrWhiteSpace(range)) break;

                    long start = 0;
                    long end = 0;
                    bool succ = true;
                    
                    string[] r = range.Replace("bytes=", "").Split('-', 2);

                    if (r[0] != "" && r[1] == "")
                    {
                        // Debug.WriteLine((int)LogLevel.Debug,"first");
                        succ &= long.TryParse(r[0], out start);
                        end = length - 1;
                    }
                    else if (r[0] == "" && r[1] != "")
                    {
                        // Debug.WriteLine((int)LogLevel.Debug,"second");
                        succ &= long.TryParse(r[1], out long suff);
                        start = length - suff;
                        end = length - 1;
                    }
                    else
                    {
                        // Debug.WriteLine((int)LogLevel.Debug,"both");
                        succ &= long.TryParse(r[0], out start);
                        succ &= long.TryParse(r[1], out end);
                    }

                    succ &= start <= end;
                    succ &= end < length;
                    // succ &= (end - start + 1) < int.MaxValue;

                    // Debug.WriteLine((int)LogLevel.Debug, $"range = {range}\n{start}..{end} for [{length}] {succ}");

                    if (!succ) { invalid = true; break; }

                    rsend.Add((start, end));
                }

                if (invalid)
                {
                    socket.SetHeader("Content-Range", $"*/{length}");
                    await ErrorHandler(socket, conf, path, 416, "Range Not Satisfiable", "invalid range", "");
                }
                else
                {
                    socket.Status = 206;
                    socket.StatusMessage = "Partial Content";
                    
                    if (rsend.Count == 1)
                    {
                        Debug.WriteColorLine((int)LogLevel.Debug, $", sending ranged", 8);

                        var (start, end) = rsend[0];
                        socket.SetHeader("Content-Range", $"bytes {start}-{end}/{length}");
                        
                        file.Position = start;
                        long delta = end - start + 1;
                        byte[] chunk = new byte[app.BigFileChunkSize];

                       if (socket is Http2Stream) socket.SetHeader("Content-Length", delta.ToString());

                        Debug.WriteLines((int)LogLevel.Debug, $"range = {start}..{end} ({delta})");

                        if (delta > app.BigFileChunkSize)
                        {
                            int read = 0;
                            while ((read = await file.ReadAsync(chunk)) > 0)
                            {
                                await socket.WriteAsync(chunk[..read]);
                            }
                        }
                        else
                        {
                            int read = await file.ReadAsync(chunk);
                            await socket.WriteAsync(chunk[..read]);
                        }
                        
                        await socket.CloseAsync([13, 10]);

                        Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {socket.Status} '{path}' [{start}..{end}] ({delta})", 2);
                    }
                    else
                    {
                        Debug.WriteColorLine((int)LogLevel.Debug, $", sending {rsend.Count} ranges", 8);

                        string boundary = "aGVsbG8gIHlvdSEh"; // 3d6b69b2ad18
                        // byte[] bytebound = Encoding.UTF8.GetBytes($"--{boundary}");
                        socket.SetHeader("Content-Type", "multipart/byteranges; boundary=" + boundary);
                        int i = 0;
                        foreach (var (start, end) in rsend)
                        {
                            byte[] header = Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Type: {dmt}\r\nContent-Range: bytes {start}-{end}/{length}\r\n\r\n");
                            await socket.WriteAsync(header);

                            file.Position = start;
                            long delta = end - start + 1;
                            byte[] chunk = new byte[app.BigFileChunkSize];

                            Debug.WriteLines((int)LogLevel.Debug, $"ranges[{i++}] = {start}..{end} ({delta})");

                            if (delta > app.BigFileChunkSize)
                            {
                                int read = 0;
                                while ((read = await file.ReadAsync(chunk)) > 0)
                                {
                                    await socket.WriteAsync(chunk[..read]);
                                }
                            }
                            else
                            {
                                int read = await file.ReadAsync(chunk);
                                await socket.WriteAsync(chunk[..read]);
                            }
                            
                            await socket.WriteAsync([13, 10]);
                        }

                        await socket.CloseAsync(Encoding.UTF8.GetBytes($"--{boundary}--\r\n"));
                        Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {socket.Status} '{path}' {rsend.Count} ranges", 2);
                    }
                }
            }
            else if (info.Length < app.BigFileThreshold)
            {
                Debug.WriteColorLine((int)LogLevel.Debug, $", sending in one go", 8);
                byte[] bytes = new byte[(int)length];
                await file.ReadExactlyAsync(bytes);
                await socket.CloseAsync(bytes);
                if (app.CacheFiles) cache[fullhost] = (info.LastWriteTime, dmt, bytes, precompressed);
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {socket.Status} '{path}' ({length})", 2);
            }
            else
            {
                // if (socket is Http2Stream) socket.SetHeader("Content-Length", info.Length.ToString());

                Debug.WriteColorLine((int)LogLevel.Debug, $", streaming bigfile", 8);
                if (socket is Http2Stream) socket.SetHeader("Content-Length", info.Length.ToString());
                byte[] buff = new byte[app.BigFileChunkSize];
                int read;
                while ((read = await file.ReadAsync(buff)) != 0)
                {
                    await socket.WriteAsync(buff.AsMemory(0, read));
                }
                await socket.CloseAsync();
                Debug.WriteColorLine((int)LogLevel.Info, $"\e[2m↑ {socket.Status} '{path}' ({info.Length})", 2);
            }
        }
    }
}