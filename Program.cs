using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PhoneSpeaker;
using QRCoder;

int port = 8080;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) port = p;

Console.OutputEncoding = Encoding.UTF8;

var hub = new AudioHub();
var source = new LoopbackSource();
source.Frame += hub.Broadcast;
var mute = new PcMute();
WasapiOut? tone = null;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();
app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.Map("/ws", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.ServeAsync(ws, ctx.RequestAborted);
});

void Cleanup()
{
    mute.Restore();
    tone?.Dispose();
    source.Dispose();
}

Console.CancelKeyPress += (_, e) =>
{
    Cleanup();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

try
{
    source.Start();
    await app.StartAsync();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Could not start: {ex.Message}");
    if (ex is IOException) Console.WriteLine($"Port {port} may be in use. Try: --port 8090");
    Console.ResetColor();
    Cleanup();
    return 1;
}

var ips = LanAddresses();
Console.WriteLine();
Console.WriteLine("  PhoneSpeaker: PC audio -> phone");
Console.WriteLine($"  Capturing: {source.DeviceName}");
Console.WriteLine();
if (ips.Count == 0)
{
    Console.WriteLine($"  No network found. Connect to Wi-Fi, then open http://<this-pc-ip>:{port}");
}
else
{
    var url = $"http://{ips[0]}:{port}";
    Console.WriteLine("  Scan with your iPhone camera (same Wi-Fi), or open in Safari:");
    Console.WriteLine();
    PrintQr(url);
    Console.WriteLine($"  {url}");
    foreach (var other in ips.Skip(1)) Console.WriteLine($"  http://{other}:{port}");
}
Console.WriteLine();
Console.WriteLine("  [M] mute/unmute PC speakers   [T] test tone   [Q] quit");
Console.WriteLine();

var running = true;
while (running)
{
    if (!Console.IsInputRedirected)
    {
        while (Console.KeyAvailable)
        {
            switch (Console.ReadKey(true).Key)
            {
                case ConsoleKey.M:
                    try { mute.Toggle(); } catch (Exception ex) { Status($"Mute failed: {ex.Message}"); }
                    break;
                case ConsoleKey.T:
                    if (tone == null)
                    {
                        tone = new WasapiOut();
                        tone.Init(new SignalGenerator(LoopbackSource.SampleRate, 2)
                        {
                            Frequency = 440,
                            Gain = 0.2,
                        });
                        tone.Play();
                    }
                    else
                    {
                        tone.Dispose();
                        tone = null;
                    }
                    break;
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    running = false;
                    break;
            }
        }
    }

    var level = Math.Min(1f, source.TakePeak());
    var bars = (int)Math.Round(level * 20);
    Status($"Phones: {hub.Count}  Level: [{new string('#', bars)}{new string('-', 20 - bars)}]  " +
           $"PC speakers: {(mute.Active ? "MUTED" : "on")}  Tone: {(tone != null ? "on" : "off")}");
    await Task.Delay(200);
}

Console.WriteLine();
Cleanup();
await app.StopAsync();
return 0;

static void Status(string text)
{
    var width = Console.IsOutputRedirected ? 100 : Math.Max(20, Console.WindowWidth - 1);
    var line = "  " + text;
    Console.Write("\r" + (line.Length > width ? line[..width] : line.PadRight(width)));
}

static void PrintQr(string url)
{
    using var generator = new QRCodeGenerator();
    using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
    // Blocks are drawn for the light modules, so the code reads as dark-on-light in a dark terminal.
    var art = new AsciiQRCode(data).GetGraphicSmall(drawQuietZones: true, invert: false);
    foreach (var line in art.Split('\n')) Console.WriteLine("  " + line);
}

static List<string> LanAddresses() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        // Interfaces with a gateway (real Wi-Fi/Ethernet) first, virtual adapters last.
        .OrderByDescending(n => n.GetIPProperties().GatewayAddresses
            .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                      !g.Address.Equals(IPAddress.Any)))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString())
        .Distinct()
        .ToList();
