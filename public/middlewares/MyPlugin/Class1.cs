namespace MyPlugin;

using Samicpp.Http;
using Samicpp.Web;

public class Class1 : IHttpPlugin
{
    public bool Alive => true;

    public async Task Handle(IDualHttpSocket socket)
    {
        await socket.CloseAsync("helliw world");
    }

    public Task Init(string self) => Task.CompletedTask;
}
