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

public class Handlers(IConfigurationRoot appconfig, string baseDir)
{
    readonly string baseDir = baseDir;
    readonly IConfigurationRoot appconfig = appconfig;
    readonly Regex remove1 = new(@"(\?.*$)|(\#.*$)|(\:.*$)", RegexOptions.Compiled);
    readonly Regex remove2 = new(@"\/\.{1,2}(?=\/|$)", RegexOptions.Compiled);
    readonly Regex collapse = new(@"\/+", RegexOptions.Compiled);
    readonly Regex remove3 = new(@"/$", RegexOptions.Compiled);
    readonly Dictionary<string, (DateTime, string, byte[])> cache = [];
    readonly Dictionary<string, (string, string)> ccache = [];

    DateTime configTime;
    Dictionary<string, Dictionary<string, string>> config = new()
    {
        { "default", new() { { "dir", "." } } }
    };


    string CleanPath(string path)
    {
        var cpath = remove1.Replace(path, "");
        cpath = remove2.Replace(cpath, "");
        cpath = collapse.Replace(cpath, "/");
        cpath = remove3.Replace(cpath, "");
        return cpath;
    }
    
    public async Task Entry(IDualHttpSocket socket)
    {
        if (!socket.Client.IsValid)
        {
            Console.WriteLine("client is not valid");
            await ErrorHandler(socket, "", 400);
            return;
        }

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
                        break;
                };
                if (socket.Compression != Compression.None) break;
            }
            Console.WriteLine("using compression " + socket.Compression);
        }
        else
        {
            Console.WriteLine("no compression");
        }

        string fullhost = $"{(socket.IsHttps ? "https" : "http")}://{socket.Client.Host}{CleanPath(socket.Client.Path)}";

        bool fresh = false;
        string extra = "";
        string router = null;
        FileInfo cinfo = new($"{baseDir}/routes.json");
        if (cinfo.Exists && cinfo.LastWriteTime != configTime)
        {
            configTime = cinfo.LastWriteTime;
            var text = await File.ReadAllBytesAsync($"{baseDir}/routes.json");
            // Console.WriteLine("read routes.json");
            // Console.WriteLine(text);
            try
            {
                config = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(text);
                fresh = true;
            }
            catch (Exception)
            {
                Console.WriteLine("invalid routes config file");
            }
        }

        string fullPath;
        string routerPath;
        if (!fresh && ccache.TryGetValue(fullhost, out var path))
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
                var type = v.GetValueOrDefault("match-type") ?? "host";
                if (
                    (type == "host" && socket.Client.Host == k) ||
                    (type == "start" && fullhost.StartsWith(k, StringComparison.CurrentCultureIgnoreCase)) ||
                    (type == "end" && fullhost.EndsWith(k, StringComparison.CurrentCultureIgnoreCase)) ||
                    (type == "regex" && new Regex(k).IsMatch(fullhost)) ||
                    (type == "path-start" && socket.Client.Path.StartsWith(k, StringComparison.CurrentCultureIgnoreCase)) ||
                    (type == "scheme" && k.Equals(socket.IsHttps ? "https" : "", StringComparison.CurrentCultureIgnoreCase))  ||
                    (type == "protocol" && k.Equals(socket.Client.Version, StringComparison.CurrentCultureIgnoreCase)) 
                )
                {
                    extra = v.GetValueOrDefault("dir") ?? extra;
                    router = v.GetValueOrDefault("router") ?? router;
                    cmatch = true;
                    break;
                }
            }
            if (!cmatch && config.TryGetValue("default", out var def))
            {
                extra = def.GetValueOrDefault("dir") ?? extra;
                router = def.GetValueOrDefault("router") ?? router;
            }

            string rawFullPath = $"{baseDir}/{extra}/{socket.Client.Path.Trim()}";
            routerPath = router == null ? null : $"{baseDir}/{extra}/{router}";
            fullPath = Path.GetFullPath(CleanPath(rawFullPath));
            ccache[fullhost] = (fullPath, routerPath);
        }

        Console.WriteLine($"\x1b[35mfull path = {fullPath}\e[0m");
        if (routerPath != null) Console.WriteLine($"\x1b[35mrouter path = {routerPath}\e[0m");

        // int e = 0;
        // int a = 1 / e;

        if (routerPath != null)
        {
            FileSystemInfo info = new FileInfo(routerPath);
            if (!info.Exists) info = new DirectoryInfo(routerPath);
            if (info.Exists) info = info.ResolveLinkTarget(true) ?? info;

            bool cached = false;
            if (cache.TryGetValue(fullhost, out var tcb))
            {
                var (t, c, b) = tcb;
                if (t == info.LastWriteTime)
                {
                    Console.WriteLine("caching response");
                    socket.SetHeader("Content-Type", c);
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
            if (cache.TryGetValue(fullhost, out var tcb))
            {
                var (t, c, b) = tcb;
                if (t == info.LastWriteTime)
                {
                    Console.WriteLine("caching response");
                    socket.SetHeader("Content-Type", c);
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
        Console.WriteLine($"\x1b[31m{code} error\x1b[0m");

        switch (code)
        {
            case 400:
                socket.Status = 400;
                socket.StatusMessage = "Bad Request";
                await socket.CloseAsync($"fix your client idk\n");
                break;

            case 404:
                socket.Status = 404;
                socket.StatusMessage = "Not Found";
                await socket.CloseAsync($"{socket.Client.Path} Not Found\n");
                break;

            case 409:
                socket.Status = 409;
                socket.StatusMessage = "Conflict";
                await socket.CloseAsync("Something went wrong\n");
                break;

            case 500:
                socket.Status = 500;
                socket.StatusMessage = "Internal Server Error";
                await socket.CloseAsync($"{message}:\n{debug}");
                break;

            case 501:
                socket.Status = 501;
                socket.StatusMessage = "Not Implemented";
                await socket.CloseAsync($"Couldnt handle {socket.Client.Path}\n");
                break;

            default:
                socket.Status = code;
                socket.StatusMessage = status;
                await socket.CloseAsync($"{message}:\n{debug}");
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
            Console.WriteLine($"found file {path}/{found}");
            await FileHandler(socket, $"{path}/{found}", normalPath);
        }
        else
        {
            Console.WriteLine("found no files");
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

        var ext = name.Split(".").Last();
        var dmt = MimeTypes.types.GetValueOrDefault(ext) ?? "application/octet-stream";
        string fullhost = $"{(socket.IsHttps ? "https" : "http")}://{socket.Client.Host}{CleanPath(socket.Client.Path)}";


        if (name.EndsWith(".blank"))
        {
            socket.Status = 204;
            socket.StatusMessage = "No Content";
            await socket.CloseAsync();
        }
        else if (name.EndsWith(".dll"))
        {
            try
            {
                if (plugins.TryGetValue(path, out var plug) && info.LastWriteTime == plug.Item1 && plug.Item2.Alive)
                {
                    await plug.Item2.Handle(socket, normalPath ?? path);
                }
                else
                {
                    Console.WriteLine("loading new plugin " + name);
                    string tname = Path.GetFileNameWithoutExtension(name);
                    byte[] lib = await File.ReadAllBytesAsync(path);
                    Assembly assembly = Assembly.Load(lib);
                    Type type = assembly.GetType(tname);
                    IHttpPlugin plugin = (IHttpPlugin)Activator.CreateInstance(type);
                    await plugin.Init(path);
                    if (plugin.Alive) plugins[path] = (info.LastWriteTime, plugin);
                    await plugin.Handle(socket, normalPath ?? path);
                }
            }
            catch (Exception e)
            {
                await ErrorHandler(socket, path, 500, "", "plugin error", e.StackTrace);
            }
        }
        else if (name.EndsWith(".cs"))
        {
            // await cskernel.SetValueAsync("socket", socket, typeof(IDualHttpSocket));
        }
        else if (name.EndsWith(".redirect") || name.EndsWith(".link") || name.Contains(".var."))
        {
            Console.WriteLine("special file");
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
                { "%BASE_DIR%", baseDir },
            };

            foreach (var (k, v) in vars)
            {
                utext = utext.Replace(k, v);
            }

            if (name.Contains(".var."))
            {
                Console.WriteLine("var file");
                socket.SetHeader("Content-Type", dmt);
                await socket.CloseAsync(utext);
            }
            else if (name.EndsWith(".redirect"))
            {
                Console.WriteLine("redirect file");
                socket.Status = 302;
                socket.StatusMessage = "Found";
                socket.SetHeader("Location", utext.Replace("\n", "").Trim());
                await socket.CloseAsync();
            }
            else if (name.EndsWith(".link"))
            {
                Console.WriteLine("link, passing request to handler");

                FileSystemInfo ninfo = new FileInfo(utext);
                if (!info.Exists) ninfo = new DirectoryInfo(utext);
                if (info.Exists) ninfo = info.ResolveLinkTarget(returnFinalTarget: true) ?? ninfo;

                await Handle(socket, utext.Trim(), ninfo, normalPath);
            }
        }
        else if (name.EndsWith(".br") /*|| name.EndsWith(".gz")*/)
        {
            dmt = MimeTypes.types.GetValueOrDefault(name.Replace(".br", "").Split(".").Last()) ?? "application/octet-stream";
            socket.Compression = Compression.None;
            socket.SetHeader("Content-Encoding", "br");
            socket.SetHeader("Content-Type", dmt);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            await socket.CloseAsync(bytes);
        }
        else
        {
            socket.SetHeader("Content-Type", dmt);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            await socket.CloseAsync(bytes);
            cache[fullhost] = (info.LastWriteTime, dmt, bytes);
        }
    }
}