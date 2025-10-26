// File: Hello.Plugin.cs
using System;
using System.Threading.Tasks;
using Samicpp.Web;
using Samicpp.Http;

public class Plugin : IHttpPlugin
{
    public bool Alive => false;

    public async ValueTask Handle(IDualHttpSocket socket)
    {
        await socket.CloseAsync("hello workd");
    }
}

