using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MinecraftManager.App.Views;

public sealed partial class SimpleLauncherView : UserControl
{
    public SimpleLauncherView() => AvaloniaXamlLoader.Load(this);
}
