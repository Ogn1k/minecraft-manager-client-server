using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MinecraftManager.App.ViewModels;

namespace MinecraftManager.App.Views;

public sealed partial class LauncherView : UserControl
{
    public LauncherView() => AvaloniaXamlLoader.Load(this);
    private async void BrowseJava_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LauncherViewModel viewModel) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Java executable", AllowMultiple = false,
            FileTypeFilter = [new("Java executable") { Patterns = OperatingSystem.IsWindows() ? ["java.exe"] : ["java"] }]
        });
        if (result.Count > 0) viewModel.CustomJavaPath = result[0].Path.LocalPath;
    }
}
