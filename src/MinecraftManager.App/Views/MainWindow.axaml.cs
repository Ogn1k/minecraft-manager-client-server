using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MinecraftManager.App.Services;
using MinecraftManager.App.ViewModels;

namespace MinecraftManager.App.Views;

public sealed partial class MainWindow : Window
{
    private MainWindowViewModel? viewModel;
    public MainWindow() => AvaloniaXamlLoader.Load(this);
    public MainWindow(MainWindowViewModel viewModel, LauncherViewModel launcher, SimpleLauncherViewModel simple, ArchiveSelectionService archiveSelection)
    {
        AvaloniaXamlLoader.Load(this); this.viewModel = viewModel; viewModel.AttachLauncher(launcher); viewModel.AttachSimple(simple); DataContext = viewModel;
        archiveSelection.Owner = this;
        Opened += async (_, _) => { await viewModel.LoadAsync(); await launcher.LoadAsync(); await simple.LoadAsync(); };
        Closing += (_, args) => { args.Cancel = !viewModel.CanClose(); if (!args.Cancel) simple.Dispose(); };
    }
    private void DefaultMinecraft_Click(object? sender, RoutedEventArgs e) => viewModel?.SetDefaultMinecraftRoot();

    private async void BrowseMinecraft_Click(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new() { Title = viewModel?.T("Choose Minecraft folder", "Выберите папку Minecraft") ?? "Choose Minecraft folder", AllowMultiple = false });
        if (result.Count > 0 && viewModel is not null) viewModel.SetMinecraftRoot(result[0].Path.LocalPath);
    }
    private async void BrowseSource_Click(object? sender, RoutedEventArgs e)
    {
        if (viewModel is null) return;
        if (viewModel.IsArchiveSource)
        {
            var result = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = viewModel.T("Choose local pack 7z archive", "Выберите архив 7z локального пака"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("7z archive") { Patterns = ["*.7z"] }]
            });
            if (result.Count > 0) viewModel.LocalSourceRoot = result[0].Path.LocalPath;
        }
        else
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new() { Title = viewModel.T("Choose local pack folder", "Выберите папку локального пака"), AllowMultiple = false });
            if (result.Count > 0) viewModel.LocalSourceRoot = result[0].Path.LocalPath;
        }
    }

    private async void DeleteInstance_Click(object? sender, RoutedEventArgs e)
    {
        if (viewModel?.SelectedInstance is null) return;

        var confirmed = false;
        var dialog = new Window
        {
            Title = viewModel.DeleteInstanceTitle,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var deleteButton = new Button { Content = viewModel.DeleteText };
        var cancelButton = new Button { Content = viewModel.KeepText };
        deleteButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancelButton.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = viewModel.DeleteInstanceWarning, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancelButton, deleteButton }
                }
            }
        };

        await dialog.ShowDialog(this);
        if (confirmed) await viewModel.DeleteSelectedInstanceCommand.ExecuteAsync(null);
    }
}
