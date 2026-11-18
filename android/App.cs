namespace StreetEcoTool.Mobile;

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
        => new(new OfflineHomePage()) { Title = "环保排查" };
}
