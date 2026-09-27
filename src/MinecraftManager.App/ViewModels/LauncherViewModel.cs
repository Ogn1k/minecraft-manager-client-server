using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MinecraftManager.Core.Launching;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Profiles;
using MinecraftManager.Core.Runtime;
using MinecraftManager.Core.Services;

namespace MinecraftManager.App.ViewModels;

public sealed partial class LauncherViewModel : ObservableObject, IDisposable
{
    public event EventHandler? ReviewUpdateRequested;
    private readonly ISelectedInstanceContext selection;
    private readonly IInstanceService instances;
    private readonly IOfflineProfileService profiles;
    private readonly IGameLaunchService launches;
    private readonly IMinecraftVersionResolver versions;
    private readonly IRuntimeManager runtime;
    private readonly IGameProcessMonitor processes;
    private readonly IModLoaderInstaller loaderInstaller;
    private readonly MinecraftManager.Core.Persistence.IApplicationPaths paths;
    private readonly IDesktopFolderService folders;
    private long refreshVersion;

    public ObservableCollection<OfflinePlayerProfile> Profiles { get; } = [];
    [ObservableProperty] private MinecraftInstance? instance;
    [ObservableProperty] private OfflinePlayerProfile? selectedProfile;
    [ObservableProperty] private LaunchReadiness readiness = new(false, [new(LaunchIssueCode.RuntimeNotConfigured, LaunchIssueSeverity.Blocking, "Select an instance.", LaunchRemediation.None)]);
    [ObservableProperty] private GameProcessSnapshot? process;
    [ObservableProperty] private string newProfileName = "Player";
    [ObservableProperty] private string minecraftVersion = "1.21.1";
    [ObservableProperty] private string selectedLoader = "Vanilla";
    [ObservableProperty] private string loaderVersion = string.Empty;
    [ObservableProperty] private int minimumMemoryMb = 1024;
    [ObservableProperty] private int maximumMemoryMb = 4096;
    [ObservableProperty] private string customJavaPath = string.Empty;
    [ObservableProperty] private bool useCustomJava;
    [ObservableProperty] private int windowWidth = 1280;
    [ObservableProperty] private int windowHeight = 720;
    [ObservableProperty] private bool fullscreen;
    [ObservableProperty] private string additionalJvmArguments = string.Empty;
    [ObservableProperty] private string status = "Select an instance and an offline profile.";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isRuntimeRepairing;
    [ObservableProperty] private double runtimeProgressPercentage;
    [ObservableProperty] private string runtimeProgressMessage = string.Empty;
    public bool CanPlay => !IsBusy && Readiness.CanLaunch && Instance is not null && SelectedProfile is not null && Process?.State != GameProcessState.Running;
    public string RuntimeSummary => Instance is null ? "No instance selected" : Instance.Runtime.IsConfigured ? $"Minecraft {Instance.Runtime.MinecraftVersion} · {LoaderSummary}" : "Runtime not configured";
    public string LoaderSummary => Instance?.Runtime.ModLoader is { } loader ? $"{loader.Type} {loader.Version}" : "Vanilla";
    public string ReadinessSummary => Readiness.CanLaunch ? "Ready to launch" : Readiness.Issues.FirstOrDefault()?.Message ?? "Not ready";
    public IReadOnlyList<string> LoaderChoices { get; } = ["Vanilla", "Fabric", "Quilt", "Forge", "NeoForge"];

    public LauncherViewModel(ISelectedInstanceContext selection, IInstanceService instances, IOfflineProfileService profiles,
        IGameLaunchService launches, IMinecraftVersionResolver versions, IRuntimeManager runtime, IGameProcessMonitor processes,
        IModLoaderInstaller loaderInstaller, MinecraftManager.Core.Persistence.IApplicationPaths paths, IDesktopFolderService folders)
    {
        this.selection = selection; this.instances = instances; this.profiles = profiles; this.launches = launches; this.versions = versions; this.runtime = runtime; this.processes = processes; this.loaderInstaller = loaderInstaller; this.paths = paths; this.folders = folders;
        selection.Changed += SelectionChanged; processes.Changed += ProcessChanged;
    }

    public async Task LoadAsync()
    {
        Profiles.Clear(); foreach (var profile in await profiles.GetAllAsync(CancellationToken.None)) Profiles.Add(profile);
        SelectedProfile ??= Profiles.FirstOrDefault();
        await LoadInstanceAsync(selection.InstanceId);
    }

    public Task ReloadSelectedInstanceAsync() => LoadInstanceAsync(selection.InstanceId);

    partial void OnSelectedProfileChanged(OfflinePlayerProfile? value) => _ = RefreshAsync();
    partial void OnReadinessChanged(LaunchReadiness value) { OnPropertyChanged(nameof(CanPlay)); OnPropertyChanged(nameof(ReadinessSummary)); }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanPlay));

    [RelayCommand]
    private async Task AddOfflineProfileAsync()
    {
        try { SelectedProfile = await profiles.AddAsync(NewProfileName, CancellationToken.None); Profiles.Clear(); foreach (var item in await profiles.GetAllAsync(CancellationToken.None)) Profiles.Add(item); Status = "Offline profile added."; }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private async Task ConfigureRuntimeAsync()
    {
        if (Instance is null) return;
        if (string.IsNullOrWhiteSpace(MinecraftVersion)) { Status = "Minecraft version is required."; return; }
        ModLoaderConfiguration? loader = null;
        if (SelectedLoader != "Vanilla")
        {
            if (string.IsNullOrWhiteSpace(LoaderVersion) || !Enum.TryParse<ModLoaderType>(SelectedLoader, out var loaderType)) { Status = "A valid loader version is required."; return; }
            loader = new(loaderType, LoaderVersion.Trim());
        }
        Instance = Instance with { Runtime = new RuntimeConfiguration(MinecraftVersion.Trim(), loader) };
        await instances.SaveAsync(Instance, CancellationToken.None);
        if (loader is not null)
        {
            var installed = await loaderInstaller.InstallAsync(new(MinecraftVersion.Trim(), loader, paths.SharedVersionsDirectory), CancellationToken.None);
            if (!installed.Succeeded) { Status = installed.Message ?? "Loader metadata installation failed."; await RefreshAsync(); return; }
        }
        OnPropertyChanged(nameof(RuntimeSummary)); await RefreshAsync();
    }

    [RelayCommand]
    private async Task SaveLaunchSettingsAsync()
    {
        if (Instance is null) return;
        if (MinimumMemoryMb < 256 || MaximumMemoryMb < MinimumMemoryMb || MaximumMemoryMb > 262_144) { Status = "Memory must be at least 256 MB and maximum must not be less than minimum."; return; }
        if (!Fullscreen && (WindowWidth < 320 || WindowHeight < 240)) { Status = "Window size must be at least 320 × 240."; return; }
        var arguments = AdditionalJvmArguments.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try { foreach (var argument in arguments) LaunchPlanner.ValidateUserArgument(argument); }
        catch (ArgumentException ex) { Status = ex.Message; return; }
        var javaSelection = UseCustomJava ? new JavaSelection(JavaSelectionMode.Custom, CustomJavaPath.Trim()) : new JavaSelection();
        var window = Fullscreen ? new WindowSettings(WindowMode.Fullscreen) : new WindowSettings(WindowMode.Custom, WindowWidth, WindowHeight);
        Instance = Instance with { Launch = new(javaSelection, new(MinimumMemoryMb, MaximumMemoryMb), window, arguments) };
        await instances.SaveAsync(Instance, CancellationToken.None); Status = "Launch settings saved."; await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        if (Instance is null || SelectedProfile is null) return;
        IsBusy = true; Status = "Launching…";
        try
        {
            var result = await launches.LaunchAsync(Instance.Id, SelectedProfile.Id, CancellationToken.None);
            Status = result.Started ? $"Minecraft is running (PID {result.Process?.ProcessId})." : result.Failure?.Message ?? "Launch failed.";
            Process = result.Process;
        }
        finally { IsBusy = false; await RefreshAsync(); }
    }

    [RelayCommand]
    private async Task RepairRuntimeAsync()
    {
        if (Instance is null || !Instance.Runtime.IsConfigured) return;
        IsRuntimeRepairing = true;
        RuntimeProgressPercentage = 0;
        RuntimeProgressMessage = "Preparing Minecraft runtime…";
        IsBusy = true; Status = "Repairing Minecraft runtime…";
        try
        {
            var resolved = await versions.ResolveAsync(Instance.Runtime, RuntimePlatform.Current, CancellationToken.None);
            var reporter = new Progress<RuntimeProgress>(p =>
            {
                RuntimeProgressPercentage = p.TotalBytes is > 0
                    ? Math.Clamp(p.CompletedBytes * 100d / p.TotalBytes.Value, 0, 100)
                    : 0;
                var stage = p.Stage == "assets" ? "Downloading assets" : "Downloading runtime files";
                RuntimeProgressMessage = $"{stage} ({p.CompletedBytes}/{p.TotalBytes})" +
                    (p.Artifact is null ? "" : $" — {p.Artifact}");
            });
            var result = await runtime.RepairAsync(resolved, reporter, CancellationToken.None);
            if (result.Succeeded)
            {
                RuntimeProgressPercentage = 100;
                RuntimeProgressMessage = "Runtime repair completed.";
            }
            Status = result.Succeeded ? "Runtime repair completed." : result.Message ?? "Runtime repair failed.";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsRuntimeRepairing = false; IsBusy = false; await RefreshAsync(); }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (Instance is null) return;
        var result = await processes.RequestStopAsync(Instance.Id, new(true), CancellationToken.None); Status = result.Message ?? "Stop requested.";
    }

    [RelayCommand]
    private async Task OpenInstanceFolderAsync()
    {
        if (Instance is null) return;
        try { await folders.OpenAsync(Instance.Location.GameDirectory, CancellationToken.None); }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private async Task OpenLogsAsync()
    {
        try { Directory.CreateDirectory(paths.LogDirectory); await folders.OpenAsync(paths.LogDirectory, CancellationToken.None); }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void ReviewUpdate() => ReviewUpdateRequested?.Invoke(this, EventArgs.Empty);

    private async void SelectionChanged(object? sender, SelectedInstanceChangedEventArgs e) => await LoadInstanceAsync(e.CurrentId);
    private void ProcessChanged(object? sender, GameProcessSnapshot snapshot)
    {
        if (snapshot.InstanceId != Instance?.Id) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { Process = snapshot; Status = snapshot.State == GameProcessState.Failed ? snapshot.Error ?? "Minecraft closed unexpectedly." : snapshot.State.ToString(); OnPropertyChanged(nameof(CanPlay)); });
    }
    private async Task LoadInstanceAsync(Guid? id)
    {
        Instance = id is null ? null : await instances.GetAsync(id.Value, CancellationToken.None);
        if (Instance?.Runtime.MinecraftVersion is { } version) MinecraftVersion = version;
        if (Instance is not null)
        {
            SelectedLoader = Instance.Runtime.ModLoader?.Type.ToString() ?? "Vanilla";
            LoaderVersion = Instance.Runtime.ModLoader?.Version ?? string.Empty;
            MinimumMemoryMb = Instance.Launch.Memory.MinimumMb; MaximumMemoryMb = Instance.Launch.Memory.MaximumMb;
            UseCustomJava = Instance.Launch.Java.Mode == JavaSelectionMode.Custom; CustomJavaPath = Instance.Launch.Java.CustomPath ?? string.Empty;
            Fullscreen = Instance.Launch.Window.Mode == WindowMode.Fullscreen;
            WindowWidth = Instance.Launch.Window.Width ?? 1280; WindowHeight = Instance.Launch.Window.Height ?? 720;
            AdditionalJvmArguments = string.Join(Environment.NewLine, Instance.Launch.AdditionalJvmArguments);
        }
        OnPropertyChanged(nameof(RuntimeSummary)); await RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref refreshVersion);
        if (Instance is null) { Readiness = LaunchReadiness.From([new(LaunchIssueCode.RuntimeNotConfigured, LaunchIssueSeverity.Blocking, "Select an instance.", LaunchRemediation.None)]); return; }
        try
        {
            var result = await launches.CheckReadinessAsync(Instance.Id, SelectedProfile?.Id, CancellationToken.None);
            if (version == refreshVersion) Readiness = result;
        }
        catch (Exception ex) { if (version == refreshVersion) Status = ex.Message; }
    }
    public void Dispose() { selection.Changed -= SelectionChanged; processes.Changed -= ProcessChanged; }
}
