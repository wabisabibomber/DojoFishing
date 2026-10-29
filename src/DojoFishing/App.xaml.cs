using System.Configuration;
using System.Data;
using System.Windows;

namespace DojoFishing;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
#if CUSTOM_APP_ICON
        Resources["ApplicationIcon"] = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
#endif
        base.OnStartup(e);
    }
}
