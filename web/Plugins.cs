namespace Samicpp.Web;

using System;
using System.Reflection;
using System.Threading.Tasks;
using Samicpp.Http;

public interface IHttpPlugin
{
    bool Alive { get; }
    Task Handle(IDualHttpSocket socket);
    Task Init(string selfPath);
}

// public static class HttpPlugins
// {
//     public static IHttpPlugin LoadPlugin(string name, byte[] bytes)
//     {
//         Assembly assembly = Assembly.Load(bytes);
//         Type type = assembly.GetType(name);
//         return (IHttpPlugin)Activator.CreateInstance(type);
//     }
// }