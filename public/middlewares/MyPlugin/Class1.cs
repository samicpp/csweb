namespace MyPlugin;

using Samicpp.Http;
using Samicpp.Web;

public class Class1 : IHttpPlugin
{
    public bool Alive => true;

    public async Task Handle(IDualHttpSocket socket, string path)
    {
        await socket.CloseAsync("helliw world at " + path);
    }

    public Task Init(string self) => Task.CompletedTask;
}
