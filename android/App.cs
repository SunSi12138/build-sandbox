namespace StreetEcoTool.Mobile;

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
        => new(new ServerPage()) { Title = "环保排查" };
}
