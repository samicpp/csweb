namespace Samicpp.Web;

using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Samicpp.Http;


public class Handlers(IConfigurationRoot config, string baseDir)
{
    readonly string baseDir = baseDir;
    readonly IConfigurationRoot config = config;
    readonly Regex remove1 = new(@"(\?.*$)|(\#.*$)|(\:.*$)", RegexOptions.Compiled);
    readonly Regex remove2 = new(@"\/\.{1,2}(?=\/|$)", RegexOptions.Compiled);
    readonly Regex collapse = new(@"\/+", RegexOptions.Compiled);

    string CleanPath(string path)
    {
        var cpath = remove1.Replace(path, "");
        cpath = remove2.Replace(cpath, "");
        cpath = collapse.Replace(cpath, "/");
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
        var fullPath = CleanPath(rawFullPath);

        FileSystemInfo info = new FileInfo(fullPath);
        if (!info.Exists) info = new DirectoryInfo(fullPath);
        if (info.Exists) info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;

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
            await FileHandler(socket, fi, fullPath);
        }
        else if (info is DirectoryInfo di)
        {
            await DirectoryHandler(socket, di, fullPath);
        }
        else
        {
            await ErrorHandler(socket, fullPath, 501);
        }
    }

    public async Task ErrorHandler(IDualHttpSocket socket, string path, int code, string message = "", string debug = "")
    {
        switch (code)
        {
            case 404:
                socket.Status = 404;
                socket.StatusMessage = "Not Found";
                socket.SetHeader("Content-Type", "text/plain");
                await socket.CloseAsync(socket.Client.Path + " Not Found");
                break;

            case 501:
                socket.Status = 501;
                socket.StatusMessage = "Not Implemented";
                socket.SetHeader("Content-Type", "text/plain");
                await socket.CloseAsync("Couldnt handle " + socket.Client.Path);
                break;

            default:
                socket.Status = code;
                socket.StatusMessage = message;
                socket.SetHeader("Content-Type", "text/plain");
                await socket.CloseAsync(debug);
                break;
        }
    }
    public async Task FileHandler(IDualHttpSocket socket, FileInfo info, string path)
    {
        // TODO: files over 500mb need to be streamed

        // byte[] bytes = File.ReadAllBytes(path);
    }
    public async Task DirectoryHandler(IDualHttpSocket socket, DirectoryInfo info, string path)
    {
        // string[] files = Directory.GetFiles(path);
    }
}