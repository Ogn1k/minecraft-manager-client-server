using MinecraftManager.App.Services;
using MinecraftManager.App.ViewModels;
using MinecraftManager.Core.Launching;
using MinecraftManager.Core.Manifests;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Profiles;
using MinecraftManager.Core.Services;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.App.Tests;

public sealed class SimpleLauncherViewModelTests
{
    [Fact]
    public void StartsSimpleAndCanToggleAdvancedMode()
    {
        var fixture = new Fixture();
        using var viewModel = fixture.Create();

        Assert.True(viewModel.IsSimpleMode);
        Assert.False(viewModel.IsAdvancedMode);

        viewModel.ToggleAdvancedCommand.Execute(null);

        Assert.True(viewModel.IsAdvancedMode);
        Assert.False(viewModel.IsSimpleMode);
        viewModel.ToggleAdvancedCommand.Execute(null);
        Assert.True(viewModel.IsSimpleMode);
    }

    [Fact]
    public async Task InvalidPlayerNameDoesNotDiscoverOrMutate()
    {
        var fixture = new Fixture();
        using var viewModel = fixture.Create();
        viewModel.PlayerName = "bad name";

        await viewModel.UpdateGameCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.ValidationMessage);
        Assert.Equal(0, fixture.Archives.Calls);
        Assert.Empty(fixture.Instances.Items);
    }

    [Fact]
    public async Task MultipleArchivesRequireSelectionAndUseConfirmedCandidate()
    {
        var fixture = new Fixture();
        var instance = fixture.AddSelectedInstance();
        var first = Candidate("a.7z", "main", "1");
        var second = Candidate("b.7z", "main", "2");
        fixture.Archives.Result = new("C:\\launcher\\updates", [first, second], [], true);
        fixture.Selector.Selection = second;
        using var viewModel = fixture.Create();
        viewModel.PlayerName = "Player";

        await viewModel.UpdateGameCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Selector.Calls);
        var saved = Assert.Single(fixture.Instances.Items, x => x.Id == instance.Id);
        Assert.Equal(second.ArchivePath, Assert.IsType<LocalArchiveSourceSettings>(saved.Pack!.Source).ArchivePath);
        Assert.Equal(1, fixture.Updates.CheckCalls);
        Assert.Equal(1, fixture.Updates.DeclineCalls);
        Assert.Equal(SimpleOperationState.Completed, viewModel.OperationState);
        Assert.Contains("already", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellingArchiveSelectionDoesNotCheckForUpdates()
    {
        var fixture = new Fixture();
        fixture.AddSelectedInstance();
        fixture.Archives.Result = new("C:\\launcher\\updates", [Candidate("a.7z", "main", "1"), Candidate("b.7z", "main", "2")], [], true);
        using var viewModel = fixture.Create();

        await viewModel.UpdateGameCommand.ExecuteAsync(null);

        Assert.Equal(SimpleOperationState.Cancelled, viewModel.OperationState);
        Assert.Equal(0, fixture.Updates.CheckCalls);
        Assert.Empty(fixture.Profiles.Items);
        Assert.DoesNotContain("failed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingVersionMetadataForcesRuntimePreparationForCurrentPack()
    {
        var fixture = new Fixture();
        fixture.AddSelectedInstance();
        fixture.States.Value = new(new("main", "2", "installed-hash", DateTimeOffset.UtcNow), []);
        fixture.Launches.Readiness = LaunchReadiness.From([
            new(LaunchIssueCode.MissingVersionMetadata, LaunchIssueSeverity.Blocking,
                "Minecraft version metadata is missing.", LaunchRemediation.RepairRuntime)]);
        fixture.Archives.Result = new("C:\\launcher\\updates", [Candidate("current.7z", "main", "2")], [], true);
        using var viewModel = fixture.Create();
        await viewModel.LoadAsync();

        await viewModel.UpdateGameCommand.ExecuteAsync(null);

        Assert.Null(fixture.Archives.LastManifestHash);
        Assert.Equal(1, fixture.Updates.InstallCalls);
        Assert.Equal(0, fixture.Updates.DeclineCalls);
    }

    private static LocalArchiveCandidate Candidate(string file, string packId, string version)
    {
        var manifest = new PackManifest
        {
            SchemaVersion = 2, PackId = packId, PackVersion = version, MinecraftVersion = "1.21.1",
            ManagedPaths = [], Files = [], Metadata = new(packId, null)
        };
        return new(Path.GetFullPath(file), file, new(manifest, $"hash-{file}"));
    }

    private sealed class Fixture
    {
        public InstanceService Instances { get; } = new();
        public ProfileService Profiles { get; } = new();
        public ArchiveDiscovery Archives { get; } = new();
        public Selector Selector { get; } = new();
        public Updates Updates { get; } = new();
        public StateStore States { get; } = new();
        public Selection Selection { get; } = new();
        public Processes Processes { get; } = new();
        public Launches Launches { get; } = new();

        public SimpleLauncherViewModel Create() => new(Instances, Profiles, Archives, Selector, Updates, States,
            new Recovery(), Selection, Launches, Processes, new ApplicationSettings());

        public MinecraftInstance AddSelectedInstance()
        {
            var instance = new MinecraftInstance
            {
                Id = Guid.NewGuid(), DisplayName = "Minecraft",
                Location = new(Path.GetTempPath(), InstanceOwnership.ManagedExternal),
                Runtime = new("1.21.1"),
                Pack = new(new LocalArchiveSourceSettings(Path.GetFullPath("old.7z")), PackUpdatePolicy.RequiredBeforeLaunch),
                Launch = LaunchConfiguration.Default
            };
            Instances.Items.Add(instance); Selection.Select(instance.Id); return instance;
        }
    }

    private sealed class InstanceService : IInstanceService
    {
        public List<MinecraftInstance> Items { get; } = [];
        public Task<IReadOnlyList<MinecraftInstance>> GetAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MinecraftInstance>>(Items);
        public Task<MinecraftInstance?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id));
        public Task SaveAsync(MinecraftInstance value, CancellationToken ct) { Items.RemoveAll(x => x.Id == value.Id); Items.Add(value); return Task.CompletedTask; }
        public Task RemoveAsync(Guid id, CancellationToken ct) { Items.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
    }
    private sealed class ProfileService : IOfflineProfileService
    {
        public List<OfflinePlayerProfile> Items { get; } = [];
        public Task<IReadOnlyList<OfflinePlayerProfile>> GetAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<OfflinePlayerProfile>>(Items);
        public Task<OfflinePlayerProfile> AddAsync(string name, CancellationToken ct) { var value = new OfflinePlayerProfile(Guid.NewGuid(), name, OfflineProfileRules.CreateOfflineUuid(name), DateTimeOffset.UtcNow); Items.Add(value); return Task.FromResult(value); }
        public Task<OfflinePlayerProfile> RenameAsync(Guid id, string name, bool confirmed, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<OfflineLaunchIdentity?> GetLaunchIdentityAsync(Guid id, CancellationToken ct) => Task.FromResult<OfflineLaunchIdentity?>(null);
    }
    private sealed class ArchiveDiscovery : ILocalArchiveDiscoveryService
    {
        public int Calls { get; private set; }
        public string? LastManifestHash { get; private set; }
        public LocalArchiveDiscoveryResult Result { get; set; } = new("C:\\launcher\\updates", [], [], true);
        public Task<LocalArchiveDiscoveryResult> DiscoverAsync(string? packId, string? hash, CancellationToken ct) { Calls++; LastManifestHash = hash; return Task.FromResult(Result); }
    }
    private sealed class Selector : IArchiveSelectionService
    {
        public int Calls { get; private set; }
        public LocalArchiveCandidate? Selection { get; set; }
        public Task<LocalArchiveCandidate?> SelectAsync(IReadOnlyList<LocalArchiveCandidate> values, CancellationToken ct) { Calls++; return Task.FromResult(Selection); }
    }
    private sealed class Updates : IUpdateCoordinator
    {
        public int CheckCalls { get; private set; }
        public int DeclineCalls { get; private set; }
        public int InstallCalls { get; private set; }
        public UpdateStage Stage => UpdateStage.Idle;
        public Task<PlannedUpdate> CheckAsync(Guid id, CancellationToken ct)
        {
            CheckCalls++;
            var plan = new UpdatePlan(Guid.NewGuid(), id, "main", "2", "hash", [], [], [], [], [], 0, 0, DateTimeOffset.UtcNow);
            return Task.FromResult(new PlannedUpdate(plan, "archive"));
        }
        public Task<UpdateResult> InstallAsync(Guid id, IProgress<UpdateProgress>? progress, CancellationToken ct) { InstallCalls++; return Task.FromResult(new UpdateResult(true, null)); }
        public void Decline(Guid id) => DeclineCalls++;
    }
    private sealed class StateStore : IInstanceStateStore
    {
        public InstanceState Value { get; set; } = new(null, []);
        public Task<InstanceState> LoadAsync(Guid id, CancellationToken ct) => Task.FromResult(Value);
        public Task SaveAsync(Guid id, InstanceState state, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Recovery : IRecoveryService
    {
        public Task<IReadOnlyList<TransactionJournal>> FindIncompleteAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TransactionJournal>>([]);
        public Task<UpdateResult> RollbackAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Selection : ISelectedInstanceContext
    {
        public Guid? InstanceId { get; private set; }
        public event EventHandler<SelectedInstanceChangedEventArgs>? Changed;
        public void Select(Guid? id) { var old = InstanceId; InstanceId = id; Changed?.Invoke(this, new(old, id)); }
    }
    private sealed class Launches : IGameLaunchService
    {
        public LaunchReadiness Readiness { get; set; } = LaunchReadiness.From([new(LaunchIssueCode.PackNotInstalled, LaunchIssueSeverity.Blocking, "Install first", LaunchRemediation.ReviewUpdate)]);
        public Task<LaunchReadiness> CheckReadinessAsync(Guid instanceId, Guid? profileId, CancellationToken ct) => Task.FromResult(Readiness);
        public Task<LaunchResult> LaunchAsync(Guid instanceId, Guid profileId, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Processes : IGameProcessMonitor
    {
        public event EventHandler<GameProcessSnapshot>? Changed { add { } remove { } }
        public GameProcessSnapshot? Get(Guid id) => null;
        public bool IsRunning(Guid id) => false;
        public Task<RequestStopResult> RequestStopAsync(Guid id, StopConfirmation value, CancellationToken ct) => throw new NotSupportedException();
    }
}
