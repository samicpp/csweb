namespace Samicpp.Web;

using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Samicpp.Http;
using System.Linq;

public class Handlers(IConfigurationRoot config, string baseDir)
{
    readonly string baseDir = baseDir;
    readonly IConfigurationRoot config = config;
    readonly Regex remove1 = new(@"(\?.*$)|(\#.*$)|(\:.*$)", RegexOptions.Compiled);
    readonly Regex remove2 = new(@"\/\.{1,2}(?=\/|$)", RegexOptions.Compiled);
    readonly Regex collapse = new(@"\/+", RegexOptions.Compiled);
    readonly Regex remove3 = new(@"/$", RegexOptions.Compiled);

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

        if (socket.Client.Headers.TryGetValue("accept-encoding", out List<string> encoding))
        {
            foreach (string s in encoding[0].Split(","))
            {
                socket.Compression = s switch
                {
                    "gzip" => Compression.Gzip,
                    "deflate" => Compression.Deflate,
                    "br" => Compression.Brotli,
                    _ => Compression.None,
                };
                if (socket.Compression != Compression.None) break;
            }
            Console.WriteLine("using compression " + socket.Compression);
        }
        else
        {
            Console.WriteLine("no compression");
        }

        string extra = "";
        string rawFullPath = $"{baseDir}/{extra}/{socket.Client.Path}";
        var fullPath = Path.GetFullPath(CleanPath(rawFullPath));

        FileSystemInfo info = new FileInfo(fullPath);
        if (!info.Exists) info = new DirectoryInfo(fullPath);
        if (info.Exists) info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;

        // if (!info.Exists) Console.WriteLine($"{fullPath} doesnt exist");
        // else if (info is FileInfo) Console.WriteLine($"file {info.FullName}");
        // else if (info is DirectoryInfo) Console.WriteLine($"directory {info.FullName}");
        // else Console.WriteLine($"unknown type {info.FullName}");
        Console.WriteLine($"\x1b[35mfull path = {fullPath}\e[0m");

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
            await FileHandler(socket, fullPath);
        }
        else if (info is DirectoryInfo di)
        {
            await DirectoryHandler(socket, fullPath);
        }
        else
        {
            await ErrorHandler(socket, fullPath, 501);
        }
    }

    public async Task ErrorHandler(IDualHttpSocket socket, string path, int code, string message = "", string debug = "")
    {
        socket.SetHeader("Content-Type", "text/plain");
        Console.WriteLine($"\x1b[31m{code} error\x1b[0m");

        switch (code)
        {
            case 404:
                socket.Status = 404;
                socket.StatusMessage = "Not Found";
                await socket.CloseAsync(socket.Client.Path + " Not Found");
                break;

            case 409:
                socket.Status = 409;
                socket.StatusMessage = "Conflict";
                await socket.CloseAsync("Something went wrong");
                break;

            case 501:
                socket.Status = 501;
                socket.StatusMessage = "Not Implemented";
                await socket.CloseAsync("Couldnt handle " + socket.Client.Path);
                break;

            default:
                socket.Status = code;
                socket.StatusMessage = message;
                await socket.CloseAsync(debug);
                break;
        }
    }
    public async Task FileHandler(IDualHttpSocket socket, string path)
    {
        // await socket.CloseAsync("file");
        // TODO: files over 500mb need to be streamed
        var info = new FileInfo(path);
        var str = info.OpenRead();

        var big = new byte[(int)(0.5 * Math.Pow(1024, 2))];
        await socket.CloseAsync(big);
        await Task.Delay(100);
        // byte[] bytes = File.ReadAllBytes(path);
    }
    public async Task DirectoryHandler(IDualHttpSocket socket, string path)
    {
        // await socket.CloseAsync("directory");
        string last = path.Split("/").Last().ToLower();
        var files = Directory.GetFiles(path).Select(f => f.Split("/").Last());

        string found = null;
        found = files.FirstOrDefault(f => f.StartsWith(last, StringComparison.CurrentCultureIgnoreCase));
        found ??= files.FirstOrDefault(f => f.StartsWith("index", StringComparison.CurrentCultureIgnoreCase));
        

        if (found != null)
        {
            Console.WriteLine($"found file {path}/{found}");
            await FileHandler(socket, $"{path}/{found}");
        }
        else
        {
            Console.WriteLine("found no files");
            await ErrorHandler(socket, path, 409);
        }
    }
}