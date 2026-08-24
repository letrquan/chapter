using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Chapter.Core;
using Chapter.Core.Contracts;
using Chapter.Core.Diagnostics;
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

        // The log goes to disk here, unlike in tests: the question it answers — "what did
        // Chapter do to my repository" — is usually asked after a restart.
        var workspace = new WorkspaceService(new GitCli(), new OperationLog(OperationLog.DefaultFilePath));

        _dispatcher = new BridgeDispatcher(workspace, _settings)
        {
            FolderPicker = PickFolderAsync,
            ThemeChanged = ApplyWindowChrome,
            Updater = new VelopackUpdater(),
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
        // Painted from the stored preference rather than waiting for the page to report
        // one, so the caption is already right on the first frame instead of flicking
        // to the correct colour a moment after the window appears.
        ApplyWindowChrome(_settings.Theme);

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

        CheckForUpdatesInBackground();
    }

    /// <summary>
    /// Looks for a newer Chapter once the window is up, and fetches one if it exists.
    ///
    /// Not awaited, and deliberately after <c>Navigate</c>: the page is already being served
    /// by the time this reaches the network, so a slow or unreachable GitHub delays nothing
    /// the user is looking at. The result arrives as an <c>updateStatus</c> event whenever it
    /// arrives, and the front-end draws it only once there is something to say.
    ///
    /// It downloads rather than merely asking, because the alternative puts a prompt in front
    /// of a decision the user has no way to make — "do you want 3MB of deltas?" is a question
    /// about nothing. Nothing is replaced until the app is restarted, and a user who never
    /// restarts has lost only the disk the package sits on.
    /// </summary>
    private void CheckForUpdatesInBackground() =>
        _ = Task.Run(async () =>
        {
            try
            {
                if (_dispatcher.Updater is not null)
                    await _dispatcher.Updater.CheckAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // CheckAsync reports its own failures through the status. Anything reaching
                // here is the updater itself failing, which must not take the window with it.
                System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
            }
        });

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
    // Native window chrome.
    //
    // The caption is the one part of the window the front-end cannot draw, so it is
    // painted here to the same --bg-shell the page uses. Left alone it stays at the
    // system default, which is the single most obvious "unfinished" tell — and worse
    // once the app has a light theme, because a stock dark caption then sits above a
    // white page.
    //
    // Tinting rather than replacing the caption is deliberate: a custom title bar over
    // a WebView2 child window means hand-rolling hit testing, and loses Aero Snap and
    // the Windows 11 snap-layouts flyout with it.
    // -----------------------------------------------------------------------

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed record ChromeTheme(Color Shell, Color Text, Color Dim, Color Border, bool Dark);

    private static readonly ChromeTheme DarkChrome = new(
        Shell: Color.FromRgb(0x05, 0x06, 0x09),
        Text: Color.FromRgb(0xE7, 0xEA, 0xF0),
        Dim: Color.FromRgb(0x8D, 0x96, 0xA8),
        Border: Color.FromRgb(0x1B, 0x1F, 0x29),
        Dark: true);

    private static readonly ChromeTheme LightChrome = new(
        Shell: Color.FromRgb(0xE8, 0xEB, 0xF0),
        Text: Color.FromRgb(0x14, 0x18, 0x1F),
        Dim: Color.FromRgb(0x5A, 0x64, 0x74),
        Border: Color.FromRgb(0xC7, 0xCE, 0xD9),
        Dark: false);

    /// <summary>
    /// Repaints the window — caption, frame and the fallback error panel — for a theme
    /// preference of "dark", "light" or "system".
    /// </summary>
    private void ApplyWindowChrome(string preference)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ApplyWindowChrome(preference));
            return;
        }

        var theme = preference switch
        {
            "dark" => DarkChrome,
            "light" => LightChrome,
            _ => SystemPrefersLight() ? LightChrome : DarkChrome,
        };

        Background = new SolidColorBrush(theme.Shell);
        ErrorPanel.Background = Background;
        ErrorTitle.Foreground = new SolidColorBrush(theme.Text);
        ErrorText.Foreground = new SolidColorBrush(theme.Dim);

        // Governs the flat colour shown before the page's first frame, so a reload does
        // not flash the previous theme.
        WebView.DefaultBackgroundColor =
            System.Drawing.Color.FromArgb(theme.Shell.R, theme.Shell.G, theme.Shell.B);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        // Immersive dark mode still matters alongside an explicit caption colour: it is
        // what darkens the minimise/maximise/close glyphs and their hover states.
        SetChromeAttribute(handle, DwmwaUseImmersiveDarkMode, theme.Dark ? 1 : 0);
        SetChromeAttribute(handle, DwmwaCaptionColor, ColorRef(theme.Shell));
        SetChromeAttribute(handle, DwmwaTextColor, ColorRef(theme.Text));
        SetChromeAttribute(handle, DwmwaBorderColor, ColorRef(theme.Border));
    }

    /// <summary>
    /// DWM takes a COLORREF — 0x00BBGGRR — which is byte-reversed from the way the same
    /// colour is written everywhere else in this codebase.
    /// </summary>
    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static void SetChromeAttribute(IntPtr handle, int attribute, int value)
    {
        try
        {
            // The return value is ignored on purpose. The caption-colour attributes are
            // Windows 11 22000+, and older builds answer E_INVALIDARG — which costs us a
            // default-coloured title bar and nothing else.
            DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Older Windows without dwmapi; the app still works, just with system chrome.
        }
    }

    /// <summary>
    /// Resolves "system" the way the front-end does. Both read the same OS setting —
    /// this one straight from the registry, the page through prefers-color-scheme — so
    /// the caption and the interface cannot disagree.
    /// </summary>
    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            // No key, no permission, or an unexpected value type: dark is the app's own
            // default, so fall back to it rather than to the Windows default of light.
            return false;
        }
    }
}
