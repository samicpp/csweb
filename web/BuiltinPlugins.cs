namespace Samicpp.Web;

using Samicpp.Http;
using Samicpp.Http.WebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;


public readonly struct BuiltinOpt()
{
    [JsonPropertyName("name")] public string Name { get; init; } = null;
    [JsonPropertyName("headers")] public Dictionary<string, string> Headers { get; init; } = [];

    // auth // why would you use this instead of routes level auth
    [JsonPropertyName("auth-realm")] public string AuthRealm { get; init; } = null;
    [JsonPropertyName("auth-cred")] public string AuthCred { get; init; } = null;
    [JsonPropertyName("auth-file")] public string AuthFile { get; init; } = null;

    // counter
    [JsonPropertyName("count-id")] public string CountId { get; init; } = null;

    // datadump
    [JsonPropertyName("datadump-timeout")] public int? DataDumpTimeout { get; init; } = 5000;
    [JsonPropertyName("datadump-interval")] public int? DataDumpInterval { get; init; } = 100;

    // wsecho

    // globalws
    [JsonPropertyName("globalws-group")] public string GlobalWsGroup { get; init; } = null;

    // sserelay
    [JsonPropertyName("sserelay-group")] public string SseRelayGroup { get; init; } = null;
    [JsonPropertyName("sserelay-retry")] public ulong? SseRelayRetry { get; init; } = 1000;
    [JsonPropertyName("sserelay-timeout")] public int? SseRelayTimeout { get; init; } = 5000;
    [JsonPropertyName("sserelay-interval")] public int? SseRelayInterval { get; init; } = 100;
    [JsonPropertyName("sserelay-updaterate")] public int? SseRelayUpdateRate { get; init; } = 100;
    [JsonPropertyName("sserelay-minsize")] public int? SseRelayMinSize { get; init; } = 1;
    [JsonPropertyName("sserelay-maxsize")] public int? SseRelayMaxSize { get; init; } = 16384;
    [JsonPropertyName("sserelay-sizeerror")] public string SseRelaySizeError { get; init; } = "[false,\"message too short or long\"]";
    [JsonPropertyName("sserelay-success")] public string SseRelaySuccess { get; init; } = "[true]";
    [JsonPropertyName("sserelay-failure")] public string SseRelayfail { get; init; } = "[false]";
}

[JsonSerializable(typeof(BuiltinOpt))]
public partial class BuiltinOptContext : JsonSerializerContext { }

public static class Builtin
{
    public static async Task AutoHandle(IDualHttpSocket http, RouteConfig conf, string path, string normalPath, BuiltinOpt opt)
    {
        if (opt.Headers != null) foreach (var (h, v) in opt.Headers) http.SetHeader(h, v);
        string name = opt.Name.ToLower().Trim();

        switch (name)
        {
            case "auth":
            case "password":
            case "authenticate":
            case "authentication":
                if (await Authenticate(http, opt.AuthRealm ?? "realm", opt.AuthCred ?? "usr:pass"))
                {
                    await Program.hands.FileHandler(http, conf, path, normalPath);
                }
                break;

            case "count":
            case "counter":
                await Counter(http, opt);
                break;

            case "datadump":
                await DataDump(http, opt);
                break;
            
            case "httpdump":
                await HttpDump(http, opt);
                break;

            case "wsecho":
                await WsEcho(http, opt);
                break;
            
            case "globalws":
                await GlobalWs(http, opt);
                break;
            
            case "sserelay":
                await SseRelay(http, opt);
                break;
            
            default:
                await Program.hands.ErrorHandler(http, conf, path, 500, "Internal Server Error", "unknown builtin name", name);
                break;
        }
    }


    public static async Task<bool> Authenticate(IDualHttpSocket dual, string realm, string cred)
    {
        if (dual.Client.Headers.TryGetValue("Authorization", out var authh))
        {
            var auth = authh[0].Split(" ").Last();
            var usrpass = Encoding.UTF8.GetString(Convert.FromBase64String(auth));
            if (usrpass == cred) return true;
        }
        
        dual.Status = 401;
        dual.StatusMessage = "Unauthorized";
        dual.SetHeader("WWW-Authenticate", $"Basic realm=\"{realm}\", charset=\"UTF-8\"");
        await dual.CloseAsync();
        return false;
    }

    static readonly Dictionary<string, ulong> counts = [];
    public static async Task Counter(IDualHttpSocket http, BuiltinOpt opt)
    {
        if (counts.TryGetValue(opt.CountId, out _)) await http.CloseAsync($"{counts[opt.CountId] += 1}");
        else await http.CloseAsync($"{counts[opt.CountId] = 0}");
    }

    public static async Task DataDump(IDualHttpSocket http, BuiltinOpt opt)
    {
        var time = TimeSpan.FromMilliseconds(opt.DataDumpTimeout ?? 5000);
        var start = DateTime.UtcNow;
        var client = http.Client;

        while (!client.BodyComplete)
        {
            if (DateTime.UtcNow - start > time) break;

            client = await http.ReadClientAsync();
            await Task.Delay(opt.DataDumpInterval ?? 100);
        }

        byte[] body = [.. client.Body];
        await http.CloseAsync(body);
    }

    public static async Task HttpDump(IDualHttpSocket http, BuiltinOpt opt)
    {
        var time = TimeSpan.FromMilliseconds(opt.DataDumpTimeout ?? 5000);
        var start = DateTime.UtcNow;
        var client = http.Client;

        while (!client.BodyComplete)
        {
            if (DateTime.UtcNow - start > time) break;

            client = await http.ReadClientAsync();
            await Task.Delay(opt.DataDumpInterval ?? 100);
        }

        string body = Encoding.UTF8.GetString([.. client.Body]);
        string headers = "";
        foreach (var (header, hs) in client.Headers) foreach (var value in hs) headers += $"{header}: {value}\n";
        await http.CloseAsync($"{client.Method} {client.Path} {client.VersionString}\n{headers}\n{body}");
    }

    public static async Task WsEcho(IDualHttpSocket http, BuiltinOpt opt)
    {
        if (http.Client.Headers.TryGetValue("upgrade", out List<string> upgrade) && upgrade[0] == "websocket")
        {
            using WebSocket ws = await http.WebSocketAsync();
            bool done = false;

            while (!done)
            {
                List<WebSocketFrame> frames = await ws.IncomingAsync();

                foreach (var frame in frames)
                {
                    switch (frame.type)
                    {
                        case WebSocketFrameType.Text:
                        case WebSocketFrameType.Binary:
                        case WebSocketFrameType.Continuation:
                            await ws.SendTextAsync(frame.GetPayload());
                            break;
                        
                        case WebSocketFrameType.Ping:
                            await ws.SendPongAsync(frame.GetPayload());
                            break;
                        
                        case WebSocketFrameType.ConnectionClose:
                            await ws.SendCloseConnectionAsync(frame.GetPayload());
                            done = true;
                            break;
                    }
                }
            }
        }
        else
        {
            http.Status = 426;
            http.StatusMessage = "Upggrade Required";
            http.Close("websocket enpoint");
        }
    }

    static readonly ConcurrentDictionary<string, Dictionary<long, WebSocket>> clients = [];
    static readonly SemaphoreSlim locker = new(1,1);
    public static async Task GlobalWs(IDualHttpSocket http, BuiltinOpt opt)
    {
        string room = opt.GlobalWsGroup;

        if (!clients.ContainsKey(room)) clients[room] = [];

        if (http.Client.Headers.TryGetValue("upgrade", out List<string> upgrade) && upgrade[0] == "websocket")
        {
            using WebSocket ws = await http.WebSocketAsync();
            await locker.WaitAsync();
            long oid = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            clients[room][oid] = ws;
            locker.Release();
            
            bool done = false;

            while (!done)
            {
                List<WebSocketFrame> frames = await ws.IncomingAsync();

                foreach (var frame in frames)
                {
                    switch (frame.type)
                    {
                        case WebSocketFrameType.Text:
                        case WebSocketFrameType.Binary:
                        case WebSocketFrameType.Continuation:
                            await locker.WaitAsync();
                            foreach (var (id,client) in clients[room])
                            {
                                if (id != oid) await client.SendTextAsync(frame.GetPayload());
                            }
                            locker.Release();
                            break;
                        
                        case WebSocketFrameType.Ping:
                            await ws.SendPongAsync(frame.GetPayload());
                            break;
                        
                        case WebSocketFrameType.ConnectionClose:
                            await ws.SendCloseConnectionAsync(frame.GetPayload());
                            await locker.WaitAsync();
                            clients[room].Remove(oid);
                            locker.Release();
                            done = true;
                            break;
                    }
                }
            }
        }
        else
        {
            http.Status = 426;
            http.StatusMessage = "Upggrade Required";
            http.Close("websocket enpoint");
        }
    }

    static readonly ConcurrentDictionary<string, ImmutableList<string>> msgs = [];
    static readonly SemaphoreSlim rlock = new(1, 1);
    public static async Task SseRelay(IDualHttpSocket http, BuiltinOpt opt)
    {
        var client = http.Client;
        string room = opt.SseRelayGroup;

        if (!clients.ContainsKey(room)) msgs[room] = [];
        while (!client.HeadersComplete) client = await http.ReadClientAsync();

        if (client.Headers.TryGetValue("accept", out var accept) && accept[0] == "text/event-stream")
        {
            http.SetHeader("Content-Type", "text/event-stream");
            int last = 0;
            if (client.Headers.TryGetValue("last-event-id", out var l)) last = Convert.ToInt32(l[0]) + 1;
            http.Write("retry: 1000\n\n");

            while (true)
            {
                await Task.Delay(opt.SseRelayUpdateRate ?? 100);

                // Log("message length " + msgs.Count);
                if (last < msgs[room].Count)
                {
                    http.Write($"id: {last}\nevent: message\ndata: {msgs[room][last]}\n\n");
                    last++;
                }
            }
        }
        else if (client.Method != "GET")
        {
            var time = TimeSpan.FromSeconds(opt.SseRelayTimeout ?? 5000);
            var start = DateTime.UtcNow;

            while (!client.BodyComplete)
            {
                if (DateTime.UtcNow - start > time)
                {
                    break;
                }

                client = await http.ReadClientAsync();
                await Task.Delay(opt.SseRelayInterval ?? 100);
            }

            byte[] body = [.. client.Body];
            if (body.Length < (opt.SseRelayMinSize ?? 1))
            {
                await http.CloseAsync(opt.SseRelaySizeError ?? "[false,\"message too short or long\"]");
            }
            else if (body.Length > (opt.SseRelayMaxSize ?? 16384))
            {
                await http.CloseAsync(opt.SseRelaySizeError ?? "[false,\"message too short or long\"]");
            }
            else
            {
                await rlock.WaitAsync();
                try
                {
                    var enc = Convert.ToBase64String(body);
                    msgs[room] = msgs[room].Add(enc);
                    await http.CloseAsync(opt.SseRelaySuccess ?? "[true]");
                }
                finally
                {
                    rlock.Release();
                }

                // Log("new length " + msgs.Count);
            }
        }
        else
        {
            await http.CloseAsync(opt.SseRelayfail ?? "[false]");
        }
    }
}