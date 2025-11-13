namespace Samicpp.Web;

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

public class Debug
{
    static IConfigurationRoot config;

    public static async Task Timer()
    {
        while (true)
        {
            await Task.Delay(3600_000);
            Console.WriteLine("an hour has passed");
        }
    }
    public static async Task Visits()
    {
        while (true)
        {
            await Task.Delay(120_000);
            Console.WriteLine($"total of {Program.Visits} visits");
        }
    }

    public static async Task Init(IConfigurationRoot conf)
    {
        config = conf;

        var _ = Timer();
        _ = Visits();

    }
}