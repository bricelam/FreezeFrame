using Microsoft.UI.Xaml;

namespace FreezeFrame;

public partial class App : Application
{
    Window _window;

    public App()
        => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
