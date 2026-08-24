using System.Windows;
using Velopack;

namespace Chapter.App;

/// <summary>
/// The entry point, which exists for one reason: Velopack has to run before WPF does.
///
/// An installed Chapter is relaunched by its own installer with hook arguments after being
/// installed, updated, or uninstalled, and <see cref="VelopackApp.Run"/> is what services
/// those launches — it does its work and terminates the process without ever returning. If
/// WPF started first, every update would show a window flashing open and shut, and an
/// uninstall would briefly reopen the app it is removing.
///
/// WPF generates a <c>Main</c> of its own from <c>App.xaml</c>; <c>StartupObject</c> in the
/// project file points the compiler at this one instead. The two lines after the hook are
/// exactly what the generated one does.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
