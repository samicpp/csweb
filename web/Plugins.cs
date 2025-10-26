namespace Samicpp.Web;

using System.Threading.Tasks;
using Samicpp.Http;

public interface IHttpPlugin
{
    bool Alive { get; }
    Task Handle(IDualHttpSocket socket);
}
