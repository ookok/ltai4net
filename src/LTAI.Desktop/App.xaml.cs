namespace LTAI.Desktop;

public partial class App : Application
{
    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell())
        {
            Title = "LTAI Console",
            Width = 1280, Height = 800,
            MinimumWidth = 900, MinimumHeight = 600
        };
    }
}
