using System.Net.WebSockets;
using System.Text;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
using Chapter.Core.Git;
using Microsoft.Extensions.FileProviders;

// Linux stand-in for src/Chapter.App/MainWindow.xaml.cs.
//
// The WPF shell does three things the front-end depends on: map a folder onto a virtual
// host, pump web messages through BridgeDispatcher.HandleAsync, and forward EventRaised
// back to the page. WPF and WebView2 are Windows-only; none of those three are. This
// serves the same dist folder over HTTP and swaps the WebView2 message pipe for a
// WebSocket, so the real dispatcher drives the real front-end against real git.

var webRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("web root required");
var repoPath = args.Length > 1 ? Path.GetFullPath(args[1]) : "";
var shimRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot-shim");

// Shared with the shell, which passes it through so both ends agree on the origin the
// page's Content-Security-Policy has to allow.
var port = Environment.GetEnvironmentVariable("CHAPTER_PORT") is { Length: > 0 } configured ? configured : "5099";
var origin = $"http://127.0.0.1:{port}";

if (!Directory.Exists(webRoot))
    throw new DirectoryNotFoundException($"No front-end at {webRoot}. Run 'npm run build' in src/Chapter.Web.");

var settings = AppSettings.Load();
if (repoPath.Length > 0 && Directory.Exists(repoPath))
{
    settings.RecordRepo(repoPath);
    settings.Save();
}

var workspace = new WorkspaceService(new GitCli(), new OperationLog(OperationLog.DefaultFilePath));
var dispatcher = new BridgeDispatcher(workspace, settings)
{
    // Stands in for Microsoft.Win32.OpenFolderDialog. zenity is the GTK folder chooser
    // the rest of this desktop uses, so "Add repository" opens the dialog a GNOME user
    // expects rather than doing nothing.
    FolderPicker = PickFolderAsync,
    ThemeChanged = theme => Console.WriteLine($"[host] theme -> {theme}"),
};
dispatcher.StartWatching();

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls(origin);

var app = builder.Build();
app.UseWebSockets();

// index.html, with two edits. The shim must define window.chrome.webview before app.js
// evaluates — a classic script does, since the module script is deferred — and the page's
// CSP says connect-src 'self', which predates there being a socket to connect to.
app.MapGet("/", ServeIndex);
app.MapGet("/index.html", ServeIndex);

async Task ServeIndex(HttpContext context)
{
    var html = await File.ReadAllTextAsync(Path.Combine(webRoot, "index.html"));

    html = html.Replace("connect-src 'self';", $"connect-src 'self' ws://127.0.0.1:{port};");
    html = html.Replace(
        "<script type=\"module\" src=\"/app.js\"></script>",
        "<script src=\"/__host/webview.js\"></script>\n    <script type=\"module\" src=\"/app.js\"></script>");

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(html);
}

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(shimRoot),
    RequestPath = "/__host",
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(webRoot),
    RequestPath = "",
    ServeUnknownFileTypes = true,   // .woff2 and Monaco's codicon .ttf
});

app.Map("/bridge", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    Console.WriteLine("[host] front-end connected");

    // WebSocket forbids overlapping sends, and the dispatcher answers requests on the
    // thread pool while the watcher raises events from its own.
    var sendLock = new SemaphoreSlim(1, 1);

    async Task SendAsync(string json)
    {
        await sendLock.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The page navigated away mid-flight; the message has nowhere to go.
        }
        finally
        {
            sendLock.Release();
        }
    }

    void OnBackendEvent(BridgeEvent backendEvent) => _ = SendAsync(BridgeJson.Serialize(backendEvent));

    dispatcher.EventRaised += OnBackendEvent;

    try
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            // A diff or a file's contents easily exceeds one frame, so accumulate until
            // the message is complete rather than parsing a truncated fragment.
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var requestJson = Encoding.UTF8.GetString(message.ToArray());
            var responseJson = await dispatcher.HandleAsync(requestJson);
            await SendAsync(responseJson);
        }
    }
    catch (WebSocketException)
    {
        Console.WriteLine("[host] front-end disconnected");
    }
    finally
    {
        dispatcher.EventRaised -= OnBackendEvent;
    }
});

static async Task<string?> PickFolderAsync()
{
    var startInfo = new System.Diagnostics.ProcessStartInfo("zenity")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    startInfo.ArgumentList.Add("--file-selection");
    startInfo.ArgumentList.Add("--directory");
    startInfo.ArgumentList.Add("--title=Select a git repository or worktree");

    try
    {
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null) return null;

        var chosen = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();

        // Exit code 1 is the user pressing Cancel, which is not an error.
        return process.ExitCode == 0 && chosen.Length > 0 ? chosen : null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[host] folder picker unavailable: {ex.Message}");
        return null;
    }
}

Console.WriteLine($"[host] serving {webRoot}");
Console.WriteLine($"[host] repo   {(repoPath.Length > 0 ? repoPath : "(none on argv)")}");
Console.WriteLine($"[host] {origin}/");

app.Run();
