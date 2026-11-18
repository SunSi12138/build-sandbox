namespace StreetEcoTool.Mobile;

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
        => new(new PhotoLabPage()) { Title = "WebView 照片验证" };
}
