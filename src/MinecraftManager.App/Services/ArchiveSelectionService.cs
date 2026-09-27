using Avalonia.Controls;
using Avalonia.Layout;
using MinecraftManager.Core.Services;
using System.Globalization;

namespace MinecraftManager.App.Services;

public interface IArchiveSelectionService
{
    Task<LocalArchiveCandidate?> SelectAsync(IReadOnlyList<LocalArchiveCandidate> candidates, CancellationToken cancellationToken);
}

public sealed class ArchiveSelectionService : IArchiveSelectionService
{
    public Window? Owner { get; set; }

    public async Task<LocalArchiveCandidate?> SelectAsync(IReadOnlyList<LocalArchiveCandidate> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        ct.ThrowIfCancellationRequested();

        LocalArchiveCandidate? selected = null;
        var list = new ListBox
        {
            ItemsSource = candidates.Select(x => new ArchiveChoice(x)).ToArray(),
            MinHeight = 180
        };
        var russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);
        var confirm = new Button { Content = russian ? "Использовать выбранный архив" : "Use selected archive", IsEnabled = false };
        var cancel = new Button { Content = russian ? "Отмена" : "Cancel" };
        var dialog = new Window
        {
            Title = russian ? "Выбор архива обновления" : "Choose update archive",
            Width = 620,
            Height = 420,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        list.SelectionChanged += (_, _) => confirm.IsEnabled = list.SelectedItem is ArchiveChoice;
        confirm.Click += (_, _) =>
        {
            selected = (list.SelectedItem as ArchiveChoice)?.Candidate;
            if (selected is not null) dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new Grid
        {
            Margin = new Avalonia.Thickness(24),
            RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
            Children =
            {
                new TextBlock { Text = russian ? "Доступно несколько совместимых архивов. Выберите архив для установки." : "Several compatible archives are available. Choose one to install.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                list.WithGridRow(1),
                new StackPanel
                {
                    [Grid.RowProperty] = 2,
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, confirm }
                }
            }
        };

        using var registration = ct.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Close));
        if (Owner is null) throw new InvalidOperationException("The archive selection dialog has no owner window.");
        await dialog.ShowDialog(Owner);
        return selected;
    }

    private sealed record ArchiveChoice(LocalArchiveCandidate Candidate)
    {
        public override string ToString() => $"{Candidate.DisplayName}  ·  {Candidate.PackId}  ·  {Candidate.PackVersion}  ·  {Candidate.FileName}";
    }
}

internal static class ControlGridExtensions
{
    public static T WithGridRow<T>(this T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        control.Margin = new Avalonia.Thickness(0, 16);
        return control;
    }
}
