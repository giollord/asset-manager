namespace AssetManager;

public partial class App : Application
{
    private readonly AssetManager.Core.Platform.FileDropBroker _drops;

    public App(AssetManager.Core.Platform.FileDropBroker drops)
    {
        _drops = drops;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage(_drops)) { Title = "Asset Manager" };
    }
}
