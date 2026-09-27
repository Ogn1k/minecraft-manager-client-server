using MinecraftManager.App.Services;
using MinecraftManager.App.ViewModels;
using MinecraftManager.Core.Diagnostics;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Security;
using MinecraftManager.Core.Services;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task AddsUserSelectedInstanceWithoutAssumingDefaultPath()
    {
        using var minecraft = new TemporaryDirectory();
        using var source = new TemporaryDirectory();
        var fixture = new Fixture();
        var viewModel = fixture.Create();
        viewModel.NewInstanceName = "Fabric";
        viewModel.MinecraftRoot = minecraft.Path;
        viewModel.LocalSourceRoot = source.Path;

        await viewModel.AddInstanceCommand.ExecuteAsync(null);

        var saved = Assert.Single(fixture.Instances.Items);
        Assert.Equal(Path.GetFullPath(minecraft.Path), saved.Location.GameDirectory);
        Assert.IsType<LocalFolderSourceSettings>(saved.Pack!.Source);
    }

    [Fact]
    public async Task AddsInstanceUsingSelected7zArchive()
    {
        using var minecraft = new TemporaryDirectory();
        using var source = new TemporaryDirectory();
        var archive = Path.Combine(source.Path, "pack.7z");
        await File.WriteAllBytesAsync(archive, []);
        var fixture = new Fixture();
        var viewModel = fixture.Create();
        viewModel.NewInstanceName = "Archive pack";
        viewModel.MinecraftRoot = minecraft.Path;
        viewModel.SelectedSourceType = "7z archive";
        viewModel.LocalSourceRoot = archive;

        await viewModel.AddInstanceCommand.ExecuteAsync(null);

        var saved = Assert.Single(fixture.Instances.Items);
        Assert.Equal(Path.GetFullPath(archive), Assert.IsType<LocalArchiveSourceSettings>(saved.Pack!.Source).ArchivePath);
    }

    [Fact]
    public async Task StartupSurfacesInterruptedTransaction()
    {
        var fixture = new Fixture { RecoveryJournal = new TransactionJournal(1, Guid.NewGuid(), Guid.NewGuid(), "root", "hash", "source", TransactionPhase.Applying, [], DateTimeOffset.UtcNow) };
        var viewModel = fixture.Create();

        await viewModel.LoadAsync();

        Assert.True(viewModel.HasRecovery);
        Assert.Contains("interrupted", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SavesSelectedThemeWithoutRequiringRestart()
    {
        var fixture = new Fixture();
        var viewModel = fixture.Create();
        viewModel.SelectedTheme = "Light";

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(ThemePreference.Light, fixture.Configuration.Value.Settings.Theme);
        Assert.Equal("Settings saved.", viewModel.StatusMessage);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task SwitchesToRussianImmediatelyAndPersistsLanguage()
    {
        var fixture = new Fixture();
        var viewModel = fixture.Create();

        viewModel.SelectedLanguage = "Русский";

        Assert.Equal("Настройки и диагностика", viewModel.SettingsText);
        Assert.Equal("Системная", viewModel.SelectedTheme);
        Assert.Contains("Локальная папка", viewModel.SourceTypes);

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(LanguagePreference.Russian, fixture.Configuration.Value.Settings.Language);
        Assert.Equal("Настройки сохранены.", viewModel.StatusMessage);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task DeletesSelectedInstanceWithoutDeletingItsFolder()
    {
        using var minecraft = new TemporaryDirectory();
        using var source = new TemporaryDirectory();
        var fixture = new Fixture();
        var viewModel = fixture.Create();
        viewModel.MinecraftRoot = minecraft.Path;
        viewModel.LocalSourceRoot = source.Path;
        await viewModel.AddInstanceCommand.ExecuteAsync(null);

        await viewModel.DeleteSelectedInstanceCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Instances.Items);
        Assert.Empty(viewModel.Instances);
        Assert.Null(viewModel.SelectedInstance);
        Assert.True(Directory.Exists(minecraft.Path));
        Assert.Contains("files were not deleted", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture
    {
        public MemoryInstanceService Instances { get; } = new();
        public TransactionJournal? RecoveryJournal { get; init; }
        public ConfigurationStore Configuration { get; } = new();
        public MainWindowViewModel Create() => new(Instances, new NoopUpdateCoordinator(), new Recovery(RecoveryJournal), new StateStore(), new HistoryStore(), Configuration, new Registration(), new Diagnostics(), new Connections(), new PortraitBackgroundService(new TestPaths()), Configuration.Value.Settings);
    }

    private sealed class MemoryInstanceService : IInstanceService
    {
        public List<MinecraftInstance> Items { get; } = [];
        public Task<IReadOnlyList<MinecraftInstance>> GetAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MinecraftInstance>>(Items);
        public Task<MinecraftInstance?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id));
        public Task SaveAsync(MinecraftInstance instance, CancellationToken ct) { Items.Add(instance); return Task.CompletedTask; }
        public Task RemoveAsync(Guid id, CancellationToken ct) { Items.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
    }
    private sealed class NoopUpdateCoordinator : IUpdateCoordinator
    {
        public UpdateStage Stage => UpdateStage.Idle;
        public Task<PlannedUpdate> CheckAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<UpdateResult> InstallAsync(Guid id, IProgress<UpdateProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
        public void Decline(Guid id) { }
    }
    private sealed class Recovery(TransactionJournal? journal) : IRecoveryService
    {
        public Task<IReadOnlyList<TransactionJournal>> FindIncompleteAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TransactionJournal>>(journal is null ? [] : [journal]);
        public Task<UpdateResult> RollbackAsync(Guid id, CancellationToken ct) => Task.FromResult(new UpdateResult(true, null, id));
    }
    private sealed class StateStore : IInstanceStateStore
    {
        public Task<InstanceState> LoadAsync(Guid id, CancellationToken ct) => Task.FromResult(new InstanceState(null, []));
        public Task SaveAsync(Guid id, InstanceState state, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class HistoryStore : IHistoryStore
    {
        public Task AppendAsync(UpdateHistoryEntry entry, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<UpdateHistoryEntry>> GetAsync(Guid id, int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<UpdateHistoryEntry>>([]);
    }
    private sealed class ConfigurationStore : IConfigurationStore
    {
        public ClientConfiguration Value { get; private set; } = ClientConfiguration.Empty;
        public Task<ClientConfiguration> LoadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(ClientConfiguration value, CancellationToken ct) { Value = value; return Task.CompletedTask; }
    }
    private sealed class TestPaths : IApplicationPaths
    {
        public string DataDirectory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mm-paths-" + Guid.NewGuid().ToString("N"));
        public string ConfigurationFile => System.IO.Path.Combine(DataDirectory, "config.json");
        public string InstanceStateDirectory => System.IO.Path.Combine(DataDirectory, "state", "instances");
        public string TransactionDirectory => System.IO.Path.Combine(DataDirectory, "transactions");
        public string HistoryDirectory => System.IO.Path.Combine(DataDirectory, "history");
        public string LogDirectory => System.IO.Path.Combine(DataDirectory, "logs");
        public string CacheDirectory => System.IO.Path.Combine(DataDirectory, "cache");
        public string SharedAssetsDirectory => System.IO.Path.Combine(DataDirectory, "shared", "assets");
        public string SharedLibrariesDirectory => System.IO.Path.Combine(DataDirectory, "shared", "libraries");
        public string SharedVersionsDirectory => System.IO.Path.Combine(DataDirectory, "shared", "versions");
        public string RuntimeStateDirectory => System.IO.Path.Combine(DataDirectory, "runtime-state");
        public string NativeWorkDirectory => System.IO.Path.Combine(DataDirectory, "natives");
        public string OfflineProfilesFile => System.IO.Path.Combine(DataDirectory, "profiles", "offline-profiles.json");
    }
    private sealed class Registration : IClientRegistrationService
    {
        public Task<Guid> RegisterAsync(Guid id, string code, string name, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
        public Task RevokeAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Diagnostics : IDiagnosticsService { public Task<string> ExportAsync(CancellationToken ct) => Task.FromResult("diagnostics.zip"); }
    private sealed class Connections : IServerConnectionService
    {
        public event EventHandler<Guid>? RefreshRequested { add { } remove { } }
        public IReadOnlyDictionary<Guid, ServerConnectionStatus> Statuses => new Dictionary<Guid, ServerConnectionStatus>();
        public Task ConnectAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mm-app-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
    public string Path { get; }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
