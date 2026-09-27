using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MinecraftManager.App.Services;
using MinecraftManager.Core.Launching;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Profiles;
using MinecraftManager.Core.Services;
using MinecraftManager.Core.Updates;

namespace MinecraftManager.App.ViewModels;

public enum SimpleOperationState { Idle, Discovering, SelectingArchive, Checking, Installing, Completed, Cancelled, Failed, Launching }

public sealed partial class SimpleLauncherViewModel : ObservableObject, IDisposable
{
    private readonly IInstanceService instances;
    private readonly IOfflineProfileService profiles;
    private readonly ILocalArchiveDiscoveryService archives;
    private readonly IArchiveSelectionService selector;
    private readonly IUpdateCoordinator updates;
    private readonly IInstanceStateStore states;
    private readonly IRecoveryService recovery;
    private readonly ISelectedInstanceContext selection;
    private readonly IGameLaunchService launches;
    private readonly IGameProcessMonitor processes;
    private readonly bool russian;
    private CancellationTokenSource? operation;
    private Guid? profileId;
    private long refreshVersion;

    public event EventHandler<Guid>? InstanceCreated;
    [ObservableProperty] private string playerName = "Player";
    [ObservableProperty] private string? validationMessage;
    [ObservableProperty] private string statusMessage = "Checking readiness…";
    [ObservableProperty] private string progressMessage = string.Empty;
    [ObservableProperty] private double progressPercentage;
    [ObservableProperty] private bool isProgressIndeterminate;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isAdvancedMode;
    [ObservableProperty] private bool cancellationAvailable;
    [ObservableProperty] private SimpleOperationState operationState;
    [ObservableProperty] private LaunchReadiness readiness = LaunchReadiness.From([
        new(LaunchIssueCode.PackNotInstalled, LaunchIssueSeverity.Blocking, "Install the game first.", LaunchRemediation.ReviewUpdate)]);

    public bool ShowProgress => IsBusy || OperationState is SimpleOperationState.Completed or SimpleOperationState.Failed;
    public bool IsSimpleMode => !IsAdvancedMode;
    public bool CanPlay => !IsBusy && selection.InstanceId is not null && OfflineProfileRules.Validate(PlayerName).IsValid &&
        (Readiness.CanLaunch || Readiness.Issues.All(x => x.Code is LaunchIssueCode.OfflineProfileRequired or LaunchIssueCode.InvalidOfflineProfile || x.Severity != LaunchIssueSeverity.Blocking)) &&
        !processes.IsRunning(selection.InstanceId.Value);
    public string PlayerNameLabel => T("Player name", "Имя игрока");
    public string UpdateGameText => T("UPDATE GAME", "ОБНОВИТЬ ИГРУ");
    public string PlayText => T("PLAY", "ИГРАТЬ");
    public string AdvancedText => IsAdvancedMode ? T("Simple mode", "Простой режим") : T("Advanced settings", "Расширенные настройки");
    public string CancelText => T("Cancel", "Отмена");

    public SimpleLauncherViewModel(IInstanceService instances, IOfflineProfileService profiles,
        ILocalArchiveDiscoveryService archives, IArchiveSelectionService selector, IUpdateCoordinator updates,
        IInstanceStateStore states, IRecoveryService recovery, ISelectedInstanceContext selection,
        IGameLaunchService launches, IGameProcessMonitor processes, ApplicationSettings settings)
    {
        this.instances = instances; this.profiles = profiles; this.archives = archives; this.selector = selector;
        this.updates = updates; this.states = states; this.recovery = recovery; this.selection = selection;
        this.launches = launches; this.processes = processes; russian = settings.Language == LanguagePreference.Russian;
        processes.Changed += ProcessChanged;
        selection.Changed += SelectionChanged;
    }

    public async Task LoadAsync()
    {
        var available = await profiles.GetAllAsync(CancellationToken.None);
        if (available.FirstOrDefault() is { } profile) { profileId = profile.Id; PlayerName = profile.DisplayName; }
        await RefreshReadinessAsync();
    }

    partial void OnPlayerNameChanged(string value)
    {
        var validation = OfflineProfileRules.Validate(value);
        ValidationMessage = validation.IsValid ? null : validation.Message;
        profileId = null;
        _ = MatchExistingProfileAsync(value);
        NotifyCommands();
    }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(ShowProgress)); NotifyCommands(); }
    partial void OnCancellationAvailableChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();
    partial void OnOperationStateChanged(SimpleOperationState value) => OnPropertyChanged(nameof(ShowProgress));
    partial void OnReadinessChanged(LaunchReadiness value) => NotifyCommands();
    partial void OnIsAdvancedModeChanged(bool value) { OnPropertyChanged(nameof(AdvancedText)); OnPropertyChanged(nameof(IsSimpleMode)); }

    [RelayCommand]
    private void ToggleAdvanced()
    {
        IsAdvancedMode = !IsAdvancedMode;
        if (IsAdvancedMode && selection.InstanceId is { } id) InstanceCreated?.Invoke(this, id);
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateGameAsync()
    {
        if (IsBusy || !ValidatePlayerName()) return;
        IsBusy = true;
        StatusMessage = T("Updating game…", "Обновление игры…");
        operation = new CancellationTokenSource();
        try
        {
            if ((await recovery.FindIncompleteAsync(operation.Token)).Count > 0)
                throw new InvalidOperationException(T("An interrupted update must be recovered in Advanced settings first.", "Сначала восстановите прерванное обновление в расширенных настройках."));
            if (selection.InstanceId is { } runningId && processes.IsRunning(runningId))
                throw new InvalidOperationException(T("Close Minecraft before updating the game.", "Закройте Minecraft перед обновлением игры."));

            OperationState = SimpleOperationState.Discovering;
            IsProgressIndeterminate = true;
            ProgressMessage = T("Discovering and validating update archives…", "Поиск и проверка архивов обновления…");
            var existingState = selection.InstanceId is { } instanceId ? await states.LoadAsync(instanceId, operation.Token) : null;
            var runtimePreparationRequired = RequiresRuntimePreparation(Readiness);
            var knownManifest = runtimePreparationRequired ? null : existingState?.InstalledPack?.ManifestSha256;
            var discovered = await archives.DiscoverAsync(existingState?.InstalledPack?.PackId, knownManifest, operation.Token);
            if (!discovered.DirectoryExists)
                throw new DirectoryNotFoundException(T($"The updates directory was not found: {discovered.UpdatesDirectory}", $"Каталог обновлений не найден: {discovered.UpdatesDirectory}"));
            if (discovered.Candidates.Count == 0)
            {
                if (discovered.HasCurrentArchive)
                {
                    ProgressPercentage = 100; IsProgressIndeterminate = false; OperationState = SimpleOperationState.Completed;
                    StatusMessage = T("The game is already up to date.", "Игра уже обновлена.");
                    await RefreshReadinessAsync();
                    return;
                }
                throw new InvalidOperationException(T($"No compatible update archives were found in {discovered.UpdatesDirectory}", $"В каталоге {discovered.UpdatesDirectory} не найдено совместимых архивов обновления"));
            }

            LocalArchiveCandidate? candidate;
            if (discovered.Candidates.Count > 1)
            {
                OperationState = SimpleOperationState.SelectingArchive;
                ProgressMessage = T("Choose an update archive.", "Выберите архив обновления.");
                candidate = await selector.SelectAsync(discovered.Candidates, operation.Token);
                if (candidate is null) { OperationState = SimpleOperationState.Cancelled; StatusMessage = T("Update cancelled.", "Обновление отменено."); return; }
            }
            else candidate = discovered.Candidates[0];

            await ResolveProfileAsync(operation.Token);
            var instance = await EnsureInstanceAsync(candidate, operation.Token);
            OperationState = SimpleOperationState.Checking;
            ProgressMessage = T("Checking installed files and calculating the update…", "Проверка файлов и расчёт обновления…");
            var planned = await updates.CheckAsync(instance.Id, operation.Token);
            if (!runtimePreparationRequired && planned.Plan.FilesToAdd.Count == 0 && planned.Plan.FilesToReplace.Count == 0 && planned.Plan.FilesToDelete.Count == 0)
            {
                updates.Decline(planned.Plan.PlanId);
                ProgressPercentage = 100;
                IsProgressIndeterminate = false;
                OperationState = SimpleOperationState.Completed;
                StatusMessage = T("The game is already up to date.", "Игра уже обновлена.");
                await RefreshReadinessAsync();
                return;
            }

            OperationState = SimpleOperationState.Installing;
            CancellationAvailable = true;
            var operationVersion = Interlocked.Increment(ref refreshVersion);
            var progress = new Progress<UpdateProgress>(p =>
            {
                if (operationVersion != refreshVersion) return;
                IsProgressIndeterminate = p.Percentage is null;
                if (p.Percentage is { } percentage) ProgressPercentage = Math.Clamp(percentage, 0, 100);
                ProgressMessage = p.Message + (p.CurrentRelativePath is null ? "" : $" — {p.CurrentRelativePath}");
                CancellationAvailable = p.Stage is not (UpdateStage.Applying or UpdateStage.RollingBack);
            });
            var result = await updates.InstallAsync(planned.Plan.PlanId, progress, operation.Token);
            if (!result.Succeeded) throw new InvalidOperationException(result.Failure?.SafeMessage ?? T("The update failed.", "Не удалось установить обновление."));
            ProgressPercentage = 100;
            IsProgressIndeterminate = false;
            OperationState = SimpleOperationState.Completed;
            StatusMessage = T($"Version {planned.Plan.TargetVersion} installed successfully.", $"Версия {planned.Plan.TargetVersion} успешно установлена.");
            await RefreshReadinessAsync();
        }
        catch (OperationCanceledException) { OperationState = SimpleOperationState.Cancelled; StatusMessage = T("Update cancelled.", "Обновление отменено."); }
        catch (Exception ex) { OperationState = SimpleOperationState.Failed; StatusMessage = ex.Message; ProgressMessage = ex.Message; }
        finally { Interlocked.Increment(ref refreshVersion); CancellationAvailable = false; IsBusy = false; operation?.Dispose(); operation = null; }
    }

    private bool CanUpdate() => !IsBusy && OfflineProfileRules.Validate(PlayerName).IsValid;

    private static bool RequiresRuntimePreparation(LaunchReadiness value) => value.Issues.Any(x => x.Code is
        LaunchIssueCode.MissingVersionMetadata or LaunchIssueCode.MissingLibraries or LaunchIssueCode.MissingAssets or
        LaunchIssueCode.MissingNatives or LaunchIssueCode.MissingJava or LaunchIssueCode.IncompatibleJava or
        LaunchIssueCode.InvalidModLoader);

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        if (IsBusy || selection.InstanceId is not { } instanceId || !ValidatePlayerName()) return;
        IsBusy = true; OperationState = SimpleOperationState.Launching; StatusMessage = T("Launching…", "Запуск…");
        try
        {
            var profile = await ResolveProfileAsync(CancellationToken.None);
            var readiness = await launches.CheckReadinessAsync(instanceId, profile.Id, CancellationToken.None);
            Readiness = readiness;
            if (!readiness.CanLaunch) { StatusMessage = readiness.Issues.FirstOrDefault(x => x.Severity == LaunchIssueSeverity.Blocking)?.Message ?? T("The game is not ready.", "Игра не готова к запуску."); return; }
            var result = await launches.LaunchAsync(instanceId, profile.Id, CancellationToken.None);
            Readiness = result.Readiness;
            StatusMessage = result.Started ? T("Minecraft is running.", "Minecraft запущен.") : result.Failure?.Message ?? T("Launch failed.", "Не удалось запустить игру.");
        }
        catch (Exception ex) { OperationState = SimpleOperationState.Failed; StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => operation?.Cancel();
    private bool CanCancel() => IsBusy && CancellationAvailable;

    private async Task<MinecraftInstance> EnsureInstanceAsync(LocalArchiveCandidate candidate, CancellationToken ct)
    {
        var existing = selection.InstanceId is { } id ? await instances.GetAsync(id, ct) : null;
        var minecraftVersion = candidate.Manifest.Value.MinecraftVersion ?? existing?.Runtime.MinecraftVersion;
        if (string.IsNullOrWhiteSpace(minecraftVersion)) throw new InvalidDataException(T("The archive does not declare a Minecraft version.", "В архиве не указана версия Minecraft."));
        ModLoaderConfiguration? loader = existing?.Runtime.ModLoader;
        if (candidate.Manifest.Value.ModLoader is { } requirement)
        {
            if (!Enum.TryParse<ModLoaderType>(requirement.Type, true, out var type)) throw new InvalidDataException(T("The archive declares an unsupported mod loader.", "В архиве указан неподдерживаемый загрузчик модов."));
            loader = new(type, requirement.Version);
        }
        if (existing is null)
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var root = Path.Combine(documents, "best_launcher", "Minecraft");
            var existed = Directory.Exists(root);
            Directory.CreateDirectory(root);
            existing = new MinecraftInstance
            {
                Id = Guid.NewGuid(), DisplayName = "Minecraft",
                Location = new(root, existed ? InstanceOwnership.ManagedExternal : InstanceOwnership.ApplicationOwned),
                Runtime = new(minecraftVersion, loader),
                Pack = new(new LocalArchiveSourceSettings(candidate.ArchivePath), PackUpdatePolicy.RequiredBeforeLaunch),
                Launch = LaunchConfiguration.Default
            };
        }
        else
        {
            existing = existing with
            {
                Runtime = existing.Runtime with { MinecraftVersion = minecraftVersion, ModLoader = loader },
                Pack = new(new LocalArchiveSourceSettings(candidate.ArchivePath), existing.Pack?.UpdatePolicy ?? PackUpdatePolicy.RequiredBeforeLaunch)
            };
        }
        await instances.SaveAsync(existing, ct);
        selection.Select(existing.Id);
        InstanceCreated?.Invoke(this, existing.Id);
        return existing;
    }

    private bool ValidatePlayerName()
    {
        var validation = OfflineProfileRules.Validate(PlayerName);
        ValidationMessage = validation.IsValid ? null : validation.Message;
        return validation.IsValid;
    }

    private async Task<OfflinePlayerProfile> ResolveProfileAsync(CancellationToken ct)
    {
        var name = PlayerName.Trim();
        var all = await profiles.GetAllAsync(ct);
        var profile = all.FirstOrDefault(x => x.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? await profiles.AddAsync(name, ct);
        profileId = profile.Id;
        return profile;
    }

    private async Task MatchExistingProfileAsync(string name)
    {
        if (!OfflineProfileRules.Validate(name).IsValid) return;
        try
        {
            var match = (await profiles.GetAllAsync(CancellationToken.None)).FirstOrDefault(x => x.DisplayName.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (PlayerName.Equals(name, StringComparison.Ordinal)) { profileId = match?.Id; await RefreshReadinessAsync(); }
        }
        catch (Exception ex) { if (PlayerName.Equals(name, StringComparison.Ordinal)) StatusMessage = ex.Message; }
    }

    public async Task RefreshReadinessAsync()
    {
        if (selection.InstanceId is not { } id) { NotifyCommands(); return; }
        try
        {
            Readiness = await launches.CheckReadinessAsync(id, profileId, CancellationToken.None);
            if (!IsBusy)
                StatusMessage = Readiness.CanLaunch
                    ? T("Ready to play.", "Готово к запуску.")
                    : Readiness.Issues.FirstOrDefault(x => x.Severity == LaunchIssueSeverity.Blocking)?.Message
                        ?? T("The game is not ready.", "Игра не готова к запуску.");
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private void ProcessChanged(object? sender, GameProcessSnapshot snapshot)
    {
        if (snapshot.InstanceId != selection.InstanceId) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { StatusMessage = snapshot.State == GameProcessState.Failed ? snapshot.Error ?? T("Minecraft stopped unexpectedly.", "Minecraft неожиданно завершился.") : snapshot.State.ToString(); NotifyCommands(); });
    }

    private void SelectionChanged(object? sender, SelectedInstanceChangedEventArgs args)
    {
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => await RefreshReadinessAsync());
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanPlay));
        UpdateGameCommand.NotifyCanExecuteChanged(); PlayCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    private string T(string english, string russianText) => russian ? russianText : english;
    public void Dispose() { processes.Changed -= ProcessChanged; selection.Changed -= SelectionChanged; operation?.Cancel(); operation?.Dispose(); }
}
