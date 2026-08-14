using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Git;
using Microsoft.Web.WebView2.Core;

namespace Chapter.App;

public partial class MainWindow : Window
{
    /// <summary>
    /// Reserved TLD, so this can never resolve to a real host. The front-end is served
    /// from a folder mapped onto it — no local web server, no ports, works offline.
    /// </summary>
    private const string VirtualHost = "chapter.invalid";

    private readonly BridgeDispatcher _dispatcher;
    private readonly AppSettings _settings;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _dispatcher = new BridgeDispatcher(new WorkspaceService(new GitCli()), _settings)
        {
            FolderPicker = PickFolderAsync,
        };
        _dispatcher.EventRaised += OnBackendEvent;
        _dispatcher.StartWatching();

        RegisterCommandLineRepo();

        Loaded += OnLoaded;
        Closed += (_, _) => _dispatcher.Dispose();
    }

    /// <summary>
    /// Supports <c>chapter.exe &lt;path&gt;</c> so the app can be launched from a terminal
    /// sitting in the repository you want to review.
    /// </summary>
    private void RegisterCommandLineRepo()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length < 2) return;

        var path = args[1];
        if (Directory.Exists(path)) _settings.RecordRepo(Path.GetFullPath(path));
    }

    /// <summary>Folder picker for "Add repository". Must run on the UI thread.</summary>
    private Task<string?> PickFolderAsync()
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(PickFolderAsync).Task.Unwrap();

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a git repository or worktree",
            Multiselect = false,
        };

        return Task.FromResult(dialog.ShowDialog(this) == true ? dialog.FolderName : null);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDarkTitleBar();

        try
        {
            await InitialiseWebViewAsync();
        }
        catch (Exception ex)
        {
            ShowError(
                $"The embedded browser failed to initialise.\n\n{ex.Message}\n\n" +
                "Chapter needs the Microsoft Edge WebView2 runtime, which ships with Windows 11. " +
                "If it has been removed, reinstall it from the Microsoft Edge WebView2 download page.");
        }
    }

    private async Task InitialiseWebViewAsync()
    {
        // The default user-data folder sits beside the executable, which fails whenever the
        // app lives somewhere read-only. Keep browser state with the app's own settings.
        var userDataFolder = Path.Combine(AppSettings.DirectoryPath, "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder);

        await WebView.EnsureCoreWebView2Async(environment);

        var core = WebView.CoreWebView2;
        var webRoot = ResolveWebRoot();

        if (!Directory.Exists(webRoot))
        {
            ShowError(
                $"The user interface files are missing.\n\nExpected them at:\n{webRoot}\n\n" +
                "Run 'npm install' then 'npm run build' in src/Chapter.Web to produce them.");
            return;
        }

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, webRoot, CoreWebView2HostResourceAccessKind.Allow);

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif

        core.WebMessageReceived += OnWebMessageReceived;

        // Anything outside the mapped folder opens in the user's real browser rather than
        // replacing the app's own UI.
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternally(args.Uri);
        };

        // Same rule for same-window navigation. A link in a rendered Markdown document is
        // content an agent wrote; without this, clicking one replaces the entire UI with
        // a web page and there is no way back.
        core.NavigationStarting += (_, args) =>
        {
            if (args.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase)) return;

            args.Cancel = true;
            OpenExternally(args.Uri);
        };

        core.Navigate($"https://{VirtualHost}/index.html");
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // The front-end posts an object, not a string. TryGetWebMessageAsString throws for
        // anything that is not a JS string, so reading the JSON form is the only option
        // that works here — getting this wrong drops every request silently.
        string requestJson;
        try
        {
            requestJson = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unreadable web message: {ex.Message}");
            return;
        }

        var responseJson = await _dispatcher.HandleAsync(requestJson);

        // The dispatcher runs work on the thread pool, so we may no longer be on the UI
        // thread — and CoreWebView2 is thread-affine.
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => PostToWeb(responseJson));
            return;
        }

        PostToWeb(responseJson);
    }

    private void OnBackendEvent(BridgeEvent backendEvent)
    {
        var json = BridgeJson.Serialize(backendEvent);

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => PostToWeb(json));
            return;
        }

        PostToWeb(json);
    }

    private void PostToWeb(string json)
    {
        try
        {
            WebView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (ObjectDisposedException)
        {
            // The window is closing; the message has nowhere to go.
        }
        catch (InvalidOperationException)
        {
            // WebView2 was torn down mid-flight.
        }
    }

    /// <summary>
    /// Locates the built front-end. In a debug build the source output is preferred so
    /// front-end changes need only a page reload rather than a full rebuild.
    /// </summary>
    private static string ResolveWebRoot()
    {
#if DEBUG
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Chapter.Web", "dist");
            if (Directory.Exists(candidate)) return candidate;
        }
#endif
        return Path.Combine(AppContext.BaseDirectory, "wwwroot");
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        WebView.Visibility = Visibility.Collapsed;
    }

    private static void OpenExternally(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // No handler registered for the scheme. Silently ignoring beats crashing.
        }
    }

    // -----------------------------------------------------------------------
    // Windows 11 dark title bar. Without this the chrome stays light against a
    // dark app, which is the single most obvious "unfinished" tell.
    // -----------------------------------------------------------------------

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            var enabled = 1;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Older Windows without dwmapi; the app still works, just with light chrome.
        }
    }
}
