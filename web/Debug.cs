namespace Samicpp.Web;

using System;
using System.Threading.Tasks;


public enum LogLevel: int
{
    Debug = 1,
    Verbose = 2,
    Log = 4,
    Info = 8,
    Dump = 16,
    Trace = 32,
    Init = 64,
    Warning = 128,
    SoftError = 256,
    Error = 512,
    Critical = 1024,
    Fatal = 2048,
    Assert = 4096,
}

public class Debug
{
    static AppConfig config;

    public static async Task Timer()
    {
        while (true)
        {
            await Task.Delay(3600_000);
            WriteLine((int)LogLevel.Debug, "an hour has passed");
        }
    }

    static ulong last = 0; 
    public static async Task Visits()
    {
        while (true)
        {
            await Task.Delay(120_000);
            if (Program.Visits != last)
            {
                WriteLine((int)LogLevel.Info, $"total of {Program.Visits} visits \e[38;5;8m{DateTime.Now:H:mm:ss yy-M-d}\e[0m");
                last = Program.Visits;
            }
        }
    }

    public static async Task Init(AppConfig conf)
    {
        config = conf;

        var _ = Timer();
        _ = Visits();
        await Task.CompletedTask;
    }



    public static int? logLevel = null; 
    public static void Write<T>(int level, T value)
    {
        if (logLevel == null || (logLevel & level) != 0) Console.Write(value);
    }
    public static void WriteLine<T>(int level, T value)
    {
        if (logLevel == null || (logLevel & level) != 0) Console.WriteLine(value);
    }
    public static void WriteLines<T>(int level, params T[] values) // seperate for explicit invocation
    {
        if (logLevel == null || (logLevel & level) != 0) foreach (T value in values) Console.WriteLine(value);
    }
    public static void WriteLines(int level, params object[] values) // seperate for explicit invocation
    {
        if (logLevel == null || (logLevel & level) != 0) foreach (object value in values) Console.WriteLine(value);
    }
    public static void WriteColorLine(int level, string text, (byte,byte,byte)? foreground = null, (byte,byte,byte)? background = null, bool reset = true)
    {
        if (foreground != null)
        {
            var (r,g,b) = ((byte,byte,byte))foreground;
            text = $"\e[38;2;{r};{g};{b}m" + text;
        }
        if (background != null)
        {
            var (r,g,b) = ((byte,byte,byte))background;
            text = $"\e[48;2;{r};{g};{b}m" + text;
        }
        if (reset) text += "\e[0m";

        WriteLine(level, text);
    }
    public static void WriteColorLine(int level, string text, byte? foreground = null, byte? background = null, bool reset = true)
    {
        if (foreground != null) text = $"\e[38;5;{foreground}m" + text;
        if (background != null) text = $"\e[48;5;{background}m" + text;
        if (reset) text += "\e[0m";

        WriteLine(level, text);
    }
    public static void WriteColorLine(int level, string text, int? foreground = null, int? background = null, bool reset = true)
    {
        (byte,byte,byte)? fore = null;
        (byte,byte,byte)? back = null;

        if (foreground != null) fore = ((byte)((foreground & 0xff0000) >> 16), (byte)((foreground & 0x00ff00) >> 8), (byte)(foreground & 0x0000ff));
        if (background != null) back = ((byte)((background & 0xff0000) >> 16), (byte)((background & 0x00ff00) >> 8), (byte)(background & 0x0000ff));

        WriteColorLine(level, text, fore, back, reset);
    }
    public static void AutoFormatWriteLine(LogLevel level, string text)
    {
        string message = level switch
        {
            LogLevel.Debug => $"\x1b[38;5;4m[{level}] {text}\e[0m",                 // blue
            LogLevel.Verbose => $"\x1b[38;5;6m[{level}] {text}\e[0m",               // cyan
            LogLevel.Log => $"[{level}] {text}\e[0m",                               // 
            LogLevel.Info => $"\x1b[38;5;2m[{level}] {text}\e[0m",                  // green
            LogLevel.Dump => $"\x1b[38;5;8m[{level}] {text}\e[0m",                  // gray
            LogLevel.Trace => $"\x1b[38;5;8m[{level}] {text}\e[0m",                 // grey
            LogLevel.Init => $"\x1b[38;5;5m[{level}] {text}\e[0m",                  // magenta
            LogLevel.Warning => $"\x1b[38;5;3m[{level}] {text}\e[0m",               // yellow
            LogLevel.SoftError => $"\x1b[38;5;1m[{level}] {text}\e[0m",             // red
            LogLevel.Error => $"\x1b[38;5;1m[{level}] {text}\e[0m",                 // red
            LogLevel.Critical => $"\x1b[38;5;9m[{level}] {text}\e[0m",              // bright red
            LogLevel.Fatal => $"\x1b[38;5;0m\x1b[48;5;9m[{level}] {text}\e[0m",     // black, bright red background
            LogLevel.Assert => $"\x1b[38;5;9m\x1b[48;5;0m[{level}] {text}\e[0m",    // bright red, black background
            _ => $"[{level}] {text}\e[0m",                                          // 
        };
        WriteLine((int)level, message);
    }
}