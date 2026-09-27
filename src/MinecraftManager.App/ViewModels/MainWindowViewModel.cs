using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MinecraftManager.App.Services;
using MinecraftManager.Core.Models;
using MinecraftManager.Core.Persistence;
using MinecraftManager.Core.Services;
using MinecraftManager.Core.Updates;
using MinecraftManager.Core.Diagnostics;
using MinecraftManager.Core.Security;
using Avalonia.Threading;
using System.Globalization;

namespace MinecraftManager.App.ViewModels;

public sealed partial class MainWindowViewModel(
    IInstanceService instances,
    IUpdateCoordinator updates,
    IRecoveryService recovery,
    IInstanceStateStore states,
    IHistoryStore history,
    IConfigurationStore configuration,
    MinecraftManager.Core.Security.IClientRegistrationService registration,
    IDiagnosticsService diagnostics,
    IServerConnectionService connections,
    PortraitBackgroundService portraits,
    ApplicationSettings bootstrapSettings,
    ISelectedInstanceContext? selectedInstanceContext = null) : ObservableObject
{
    private readonly ISelectedInstanceContext selection = selectedInstanceContext ?? new SelectedInstanceContext();
    public LauncherViewModel? Launcher { get; internal set; }
    public SimpleLauncherViewModel? Simple { get; private set; }
    [ObservableProperty] private int selectedNavigationIndex;
    internal void AttachLauncher(LauncherViewModel launcher)
    {
        if (Launcher is not null) Launcher.ReviewUpdateRequested -= OnReviewUpdateRequested;
        Launcher = launcher;
        launcher.ReviewUpdateRequested += OnReviewUpdateRequested;
        OnPropertyChanged(nameof(Launcher));
    }
    internal void AttachSimple(SimpleLauncherViewModel simple)
    {
        if (Simple is not null) Simple.InstanceCreated -= OnSimpleInstanceCreated;
        Simple = simple;
        simple.InstanceCreated += OnSimpleInstanceCreated;
        OnPropertyChanged(nameof(Simple));
    }
    private async void OnSimpleInstanceCreated(object? sender, Guid instanceId)
    {
        try
        {
            Instances.Clear();
            foreach (var item in await instances.GetAllAsync(CancellationToken.None)) Instances.Add(item);
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == instanceId);
            OnPropertyChanged(nameof(HasInstances));
            if (Launcher is not null) await Launcher.ReloadSelectedInstanceAsync();
        }
        catch (Exception ex) { ShowError(T("Unable to refresh instances.", "Не удалось обновить список установок."), ex); }
    }
    private void OnReviewUpdateRequested(object? sender, EventArgs e) => SelectedNavigationIndex = 1;
    public ObservableCollection<MinecraftInstance> Instances { get; } = [];
    public ObservableCollection<string> PlannedChanges { get; } = [];
    public ObservableCollection<UpdateHistoryEntry> History { get; } = [];
    public ObservableCollection<ServerProfile> ServerProfiles { get; } = [];
    public IReadOnlyList<string> SourceTypes => ParseLanguage(SelectedLanguage) == LanguagePreference.Russian
        ? ["Локальная папка", "Архив 7z", "Статический HTTP", "Управляемый сервер"]
        : ["Local folder", "7z archive", "Static HTTP", "Managed server"];

    [ObservableProperty] private MinecraftInstance? selectedInstance;
    [ObservableProperty] private string newInstanceName = "Minecraft";
    [ObservableProperty] private string minecraftRoot = string.Empty;
    [ObservableProperty] private string localSourceRoot = string.Empty;
    [ObservableProperty] private string selectedSourceType = bootstrapSettings.Language == LanguagePreference.Russian ? "Локальная папка" : "Local folder";
    [ObservableProperty] private string remoteHttpUrl = "https://example.com/minecraft-pack/";
    [ObservableProperty] private bool allowInsecureLanHttp;
    [ObservableProperty] private ServerProfile? selectedServerProfile;
    [ObservableProperty] private string serverDisplayName = string.Empty;
    [ObservableProperty] private string serverUrl = "https://";
    [ObservableProperty] private string registrationCode = string.Empty;
    [ObservableProperty] private string assignedPackId = string.Empty;
    [ObservableProperty] private string selectedTheme = ThemeName(bootstrapSettings.Theme, bootstrapSettings.Language);
    [ObservableProperty] private string selectedLanguage = LanguageName(bootstrapSettings.Language);
    [ObservableProperty] private int maximumConcurrentDownloads = bootstrapSettings.MaxConcurrentDownloads;
    public IReadOnlyList<string> ThemeChoices => ParseLanguage(SelectedLanguage) == LanguagePreference.Russian
        ? ["Системная", "Светлая", "Тёмная"]
        : ["System", "Light", "Dark"];
    public IReadOnlyList<string> LanguageChoices { get; } = ["English", "Русский"];
    [ObservableProperty] private string statusMessage = bootstrapSettings.Language == LanguagePreference.Russian ? "Готово" : "Ready";
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? technicalDetails;
    [ObservableProperty] private UpdatePlan? currentPlan;
    [ObservableProperty] private double progressPercentage;
    [ObservableProperty] private string progressMessage = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private Guid? recoveryTransactionId;
    [ObservableProperty] private bool cancellationAvailable;
    [ObservableProperty] private Bitmap? backgroundPortrait;
    private CancellationTokenSource? activeOperation;
    private string? autoRoot;

    public bool HasPlan => CurrentPlan is not null;
    public bool HasError => ErrorMessage is not null;
    public bool HasRecovery => RecoveryTransactionId is not null;
    public bool HasInstances => Instances.Count > 0;
    public string SelectedSummary => SelectedInstance is null ? T("Select or add an instance", "Выберите или добавьте установку") : $"{SelectedInstance.DisplayName} — {SelectedInstance.Location.GameDirectory}";

    partial void OnSelectedInstanceChanged(MinecraftInstance? value)
    {
        OnPropertyChanged(nameof(SelectedSummary));
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        DeleteSelectedInstanceCommand.NotifyCanExecuteChanged();
        selection.Select(value?.Id);
        _ = PersistSelectionAsync(value?.Id);
        _ = LoadHistoryAsync(value);
    }
    partial void OnCurrentPlanChanged(UpdatePlan? value) { OnPropertyChanged(nameof(HasPlan)); InstallUpdateCommand.NotifyCanExecuteChanged(); }
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnRecoveryTransactionIdChanged(Guid? value) => OnPropertyChanged(nameof(HasRecovery));
    partial void OnIsBusyChanged(bool value) { CheckForUpdatesCommand.NotifyCanExecuteChanged(); InstallUpdateCommand.NotifyCanExecuteChanged(); AddInstanceCommand.NotifyCanExecuteChanged(); DeleteSelectedInstanceCommand.NotifyCanExecuteChanged(); }
    partial void OnNewInstanceNameChanged(string value)
    {
        AddInstanceCommand.NotifyCanExecuteChanged();
        if (string.IsNullOrWhiteSpace(value)) return;
        if (string.IsNullOrWhiteSpace(MinecraftRoot) || (autoRoot is not null && MinecraftRoot == autoRoot))
        {
            MinecraftRoot = DefaultMinecraftRoot(value);
            autoRoot = MinecraftRoot;
        }
    }
    partial void OnMinecraftRootChanged(string value) => AddInstanceCommand.NotifyCanExecuteChanged();
    partial void OnLocalSourceRootChanged(string value) => AddInstanceCommand.NotifyCanExecuteChanged();
    partial void OnSelectedSourceTypeChanged(string value)
    {
        AddInstanceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(LocalPackFolderText));
        OnPropertyChanged(nameof(IsArchiveSource));
    }
    partial void OnRemoteHttpUrlChanged(string value) => AddInstanceCommand.NotifyCanExecuteChanged();
    partial void OnSelectedServerProfileChanged(ServerProfile? value) => AddInstanceCommand.NotifyCanExecuteChanged();
    partial void OnSelectedThemeChanged(string value)
    {
        // ComboBox selection can briefly be null while its items are being rebuilt.
        // Applying only known values prevents a binding transition from reaching
        // Avalonia's theme machinery with an invalid preference.
        if (TryParseTheme(value, out var theme))
            App.ApplyTheme(theme);
    }
    partial void OnSelectedLanguageChanged(string value)
    {
        var theme = TryParseTheme(SelectedTheme, out var currentTheme) ? currentTheme : ThemePreference.System;
        var source = ParseSource(SelectedSourceType);
        var language = ParseLanguage(value);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language == LanguagePreference.Russian ? "ru-RU" : "en-US");
        foreach (var propertyName in LocalizedPropertyNames) OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(ThemeChoices));
        OnPropertyChanged(nameof(SourceTypes));
        SelectedTheme = ThemeName(theme);
        SelectedSourceType = SourceName(source);
        StatusMessage = T("Ready", "Готово");
    }

    public async Task LoadAsync()
    {
        try
        {
            LoadBackground();
            Instances.Clear();
            foreach (var item in await instances.GetAllAsync(CancellationToken.None)) Instances.Add(item);
            var config = await configuration.LoadAsync(CancellationToken.None);
            SelectedLanguage = LanguageName(config.Settings.Language);
            SelectedTheme = ThemeName(config.Settings.Theme); MaximumConcurrentDownloads = config.Settings.MaxConcurrentDownloads;
            ServerProfiles.Clear();
            foreach (var profile in config.ServerProfiles) ServerProfiles.Add(profile);
            connections.RefreshRequested -= OnServerRefreshRequested;
            connections.RefreshRequested += OnServerRefreshRequested;
            foreach (var profile in config.ServerProfiles.Where(x => x.DeviceId is not null)) _ = ConnectProfileAsync(profile.Id);
            SelectedInstance = Instances.FirstOrDefault(x => x.Id == config.SelectedInstanceId) ?? Instances.FirstOrDefault();
            OnPropertyChanged(nameof(HasInstances));
            var incomplete = await recovery.FindIncompleteAsync(CancellationToken.None);
            if (incomplete.FirstOrDefault() is { } journal)
            {
                RecoveryTransactionId = journal.TransactionId;
                StatusMessage = T("An interrupted update was detected. Rollback is recommended before installing another update.", "Обнаружено прерванное обновление. Перед установкой следующего обновления рекомендуется выполнить откат.");
            }
        }
        catch (Exception ex) { ShowError(T("Unable to load local configuration.", "Не удалось загрузить локальную конфигурацию."), ex); }
    }

    private async Task PersistSelectionAsync(Guid? instanceId)
    {
        try
        {
            var value = await configuration.LoadAsync(CancellationToken.None);
            if (value.SelectedInstanceId != instanceId) await configuration.SaveAsync(value with { SelectedInstanceId = instanceId }, CancellationToken.None);
        }
        catch { /* Selection persistence is best effort; normal saves surface errors. */ }
    }

    [RelayCommand(CanExecute = nameof(CanAddInstance))]
    private async Task AddInstanceAsync()
    {
        try
        {
            var root = Path.GetFullPath(MinecraftRoot);
            Directory.CreateDirectory(root);
            var instanceId = Guid.NewGuid();
            UpdateSourceSettings source = SelectedSourceType switch
            {
                var selected when ParseSource(selected) == SourceChoice.LocalFolder && Directory.Exists(LocalSourceRoot) => new LocalFolderSourceSettings(Path.GetFullPath(LocalSourceRoot)),
                var selected when ParseSource(selected) == SourceChoice.LocalArchive && File.Exists(LocalSourceRoot) && Path.GetExtension(LocalSourceRoot).Equals(".7z", StringComparison.OrdinalIgnoreCase) => new LocalArchiveSourceSettings(Path.GetFullPath(LocalSourceRoot)),
                var selected when ParseSource(selected) == SourceChoice.StaticHttp && Uri.TryCreate(RemoteHttpUrl, UriKind.Absolute, out var uri) => new StaticHttpSourceSettings(uri, AllowInsecureLanHttp),
                var selected when ParseSource(selected) == SourceChoice.ManagedServer && SelectedServerProfile is not null && !string.IsNullOrWhiteSpace(AssignedPackId) => new ManagedServerSourceSettings(SelectedServerProfile.Id, AssignedPackId.Trim(), instanceId),
                _ => throw new InvalidOperationException(T("Complete the selected update source configuration.", "Заполните настройки выбранного источника обновлений."))
            };
            var instance = new MinecraftInstance
            {
                Id = instanceId,
                DisplayName = NewInstanceName.Trim(),
                Location = new(root, InstanceOwnership.ManagedExternal),
                Runtime = new(),
                Pack = new(source),
                Launch = LaunchConfiguration.Default
            };
            await instances.SaveAsync(instance, CancellationToken.None);
            Instances.Add(instance); SelectedInstance = instance; OnPropertyChanged(nameof(HasInstances));
            StatusMessage = T("Instance saved. Check for updates when ready.", "Установка сохранена. Теперь можно проверить обновления."); ErrorMessage = null;
        }
        catch (Exception ex) { ShowError(T("Unable to add the instance. Check both folders.", "Не удалось добавить установку. Проверьте обе папки."), ex); }
    }
    private bool CanAddInstance() => !IsBusy && !string.IsNullOrWhiteSpace(NewInstanceName) && !string.IsNullOrWhiteSpace(MinecraftRoot);

    public void SetMinecraftRoot(string path) { autoRoot = null; MinecraftRoot = path; }

    public void SetDefaultMinecraftRoot()
    {
        MinecraftRoot = DefaultMinecraftRoot(NewInstanceName);
        autoRoot = MinecraftRoot;
    }

    private static string DefaultMinecraftRoot(string name)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var folder = string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars())).Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 64) folder = "instance";
        return Path.Combine(documents, "best_launcher", folder);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedInstance))]
    private async Task DeleteSelectedInstanceAsync()
    {
        if (SelectedInstance is not { } instance) return;
        await RunBusyAsync(async () =>
        {
            await instances.RemoveAsync(instance.Id, CancellationToken.None);
            Instances.Remove(instance);
            SelectedInstance = Instances.FirstOrDefault();
            CurrentPlan = null;
            PlannedChanges.Clear();
            OnPropertyChanged(nameof(HasInstances));
            StatusMessage = T($"Instance '{instance.DisplayName}' removed. Minecraft files were not deleted.", $"Установка «{instance.DisplayName}» удалена из менеджера. Файлы Minecraft не удалялись.");
        }, T("Unable to remove the instance.", "Не удалось удалить установку."));
    }
    private bool CanDeleteSelectedInstance() => SelectedInstance is not null && !IsBusy;

    [RelayCommand]
    private async Task SaveServerProfileAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || string.IsNullOrWhiteSpace(ServerDisplayName))
                throw new InvalidOperationException(T("Server name and an HTTPS URL without embedded credentials are required.", "Необходимы имя сервера и HTTPS-адрес без встроенных учётных данных."));
            var profile = new ServerProfile(Guid.NewGuid(), ServerDisplayName.Trim(), uri);
            var config = await configuration.LoadAsync(CancellationToken.None);
            await configuration.SaveAsync(config with { ServerProfiles = config.ServerProfiles.Append(profile).ToArray() }, CancellationToken.None);
            ServerProfiles.Add(profile); SelectedServerProfile = profile;
            if (!string.IsNullOrWhiteSpace(RegistrationCode))
            {
                var machineId = await registration.RegisterAsync(profile.Id, RegistrationCode, Environment.MachineName, CancellationToken.None);
                var refreshed = await configuration.LoadAsync(CancellationToken.None);
                await configuration.SaveAsync(refreshed with { ServerProfiles = refreshed.ServerProfiles.Select(x => x.Id == profile.Id ? x with { DeviceId = machineId } : x).ToArray() }, CancellationToken.None);
                ServerProfiles.Remove(profile); profile = profile with { DeviceId = machineId }; ServerProfiles.Add(profile); SelectedServerProfile = profile;
            }
            RegistrationCode = string.Empty; StatusMessage = T("Server profile saved securely.", "Профиль сервера безопасно сохранён.");
        }, T("Unable to save or register the server profile.", "Не удалось сохранить или зарегистрировать профиль сервера."));
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (!TryParseTheme(SelectedTheme, out var theme)) throw new InvalidOperationException(T("Theme setting is invalid.", "Выбрана недопустимая тема."));
            var config = await configuration.LoadAsync(CancellationToken.None);
            var settings = config.Settings with { Theme = theme, Language = ParseLanguage(SelectedLanguage), MaxConcurrentDownloads = Math.Clamp(MaximumConcurrentDownloads, 1, 8) };
            await configuration.SaveAsync(config with { Settings = settings }, CancellationToken.None);
            App.ApplyTheme(theme);
            StatusMessage = T("Settings saved.", "Настройки сохранены.");
        }, T("Unable to save settings.", "Не удалось сохранить настройки."));
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        await RunBusyAsync(async () => StatusMessage = T("Diagnostics exported to ", "Диагностика экспортирована в ") + await diagnostics.ExportAsync(CancellationToken.None), T("Unable to export diagnostics.", "Не удалось экспортировать диагностику."));
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckForUpdatesAsync()
    {
        if (SelectedInstance is null) return;
        await RunBusyAsync(async () =>
        {
            StatusMessage = T("Checking and calculating the update plan…", "Проверка и расчёт плана обновления…");
            activeOperation = new CancellationTokenSource(); CancellationAvailable = true;
            var result = await updates.CheckAsync(SelectedInstance.Id, activeOperation.Token);
            CurrentPlan = result.Plan; PlannedChanges.Clear();
            foreach (var item in result.Plan.FilesToAdd) PlannedChanges.Add(T($"+ Add      {item.Path}", $"+ Добавить   {item.Path}"));
            foreach (var item in result.Plan.FilesToReplace) PlannedChanges.Add(T($"~ Replace  {item.Path}", $"~ Заменить   {item.Path}"));
            foreach (var item in result.Plan.FilesToDelete) PlannedChanges.Add(T($"− Delete   {item.Path}", $"− Удалить    {item.Path}"));
            StatusMessage = T($"Review {PlannedChanges.Count} changes for {result.Plan.TargetVersion}. Nothing has been modified.", $"Проверьте изменения ({PlannedChanges.Count}) для версии {result.Plan.TargetVersion}. Файлы ещё не изменены.");
        }, T("Unable to create an update plan.", "Не удалось создать план обновления."));
    }
    private bool CanCheck() => SelectedInstance is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallUpdateAsync()
    {
        if (CurrentPlan is null) return;
        var planId = CurrentPlan.PlanId;
        await RunBusyAsync(async () =>
        {
            activeOperation = new CancellationTokenSource(); CancellationAvailable = true;
            var reporter = new Progress<UpdateProgress>(p => { ProgressPercentage = p.Percentage ?? 0; ProgressMessage = p.Message + (p.CurrentRelativePath is null ? "" : $" — {p.CurrentRelativePath}"); if (p.Stage is UpdateStage.Applying or UpdateStage.RollingBack) CancellationAvailable = false; });
            var result = await updates.InstallAsync(planId, reporter, activeOperation.Token);
            if (!result.Succeeded) throw new UpdateUiException(result.Failure!);
            StatusMessage = T($"Version {CurrentPlan.TargetVersion} installed successfully.", $"Версия {CurrentPlan.TargetVersion} успешно установлена.");
            CurrentPlan = null; PlannedChanges.Clear(); await LoadHistoryAsync(SelectedInstance);
        }, T("The update could not be completed.", "Не удалось завершить обновление."));
    }
    private bool CanInstall() => CurrentPlan is not null && !IsBusy;

    [RelayCommand]
    private void DismissError() { ErrorMessage = null; TechnicalDetails = null; }

    [RelayCommand]
    private void CancelOperation() { if (CancellationAvailable) activeOperation?.Cancel(); }

    [RelayCommand]
    private async Task RecoverAsync()
    {
        if (RecoveryTransactionId is not { } transactionId) return;
        await RunBusyAsync(async () =>
        {
            var result = await recovery.RollbackAsync(transactionId, CancellationToken.None);
            if (!result.Succeeded) throw new UpdateUiException(result.Failure!);
            RecoveryTransactionId = null;
            StatusMessage = T("The interrupted update was rolled back successfully.", "Прерванное обновление успешно отменено.");
        }, T("The interrupted update could not be recovered automatically.", "Не удалось автоматически восстановиться после прерванного обновления."));
    }

    private async Task RunBusyAsync(Func<Task> action, string friendly)
    {
        IsBusy = true; ErrorMessage = null; TechnicalDetails = null;
        try { await action(); }
        catch (UpdateUiException ex) { ErrorMessage = ex.Failure.SafeMessage; TechnicalDetails = $"{ex.Failure.Code}\nPath: {ex.Failure.RelativePath ?? "n/a"}"; }
        catch (Exception ex) { ShowError(friendly, ex); }
        finally { CancellationAvailable = false; activeOperation?.Dispose(); activeOperation = null; IsBusy = false; }
    }
    private async Task LoadHistoryAsync(MinecraftInstance? instance)
    {
        History.Clear(); if (instance is null) return;
        foreach (var entry in await history.GetAsync(instance.Id, 20, CancellationToken.None)) History.Add(entry);
        _ = await states.LoadAsync(instance.Id, CancellationToken.None);
    }
    private void LoadBackground()
    {
        try { BackgroundPortrait = new Bitmap(portraits.EnsurePortraitCached(Random.Shared)); }
        catch { /* Background artwork is decorative; missing assets must not prevent startup. */ }
    }

    private void ShowError(string friendly, Exception ex) { ErrorMessage = friendly; TechnicalDetails = $"{ex.GetType().Name}: {ex.Message}"; StatusMessage = T("Action failed", "Ошибка выполнения"); }
    private void OnServerRefreshRequested(object? sender, Guid profileId)
    {
        if (SelectedInstance?.Pack?.Source is ManagedServerSourceSettings managed && managed.ServerProfileId == profileId && !IsBusy)
            Dispatcher.UIThread.Post(() => CheckForUpdatesCommand.Execute(null));
    }
    private async Task ConnectProfileAsync(Guid profileId)
    {
        try { await connections.ConnectAsync(profileId, CancellationToken.None); }
        catch { Dispatcher.UIThread.Post(() => StatusMessage = T("Managed server is offline. Local features remain available.", "Управляемый сервер недоступен. Локальные функции продолжают работать.")); }
    }
    public bool CanClose()
    {
        if (!IsBusy) return true;
        if (CancellationAvailable) activeOperation?.Cancel();
        StatusMessage = CancellationAvailable
            ? T("Cancelling safely. Close again after the operation stops.", "Выполняется безопасная отмена. Закройте приложение после остановки операции.")
            : T("Files are being applied or recovered. The application can close after a safe checkpoint.", "Выполняется применение или восстановление файлов. Приложение можно закрыть после безопасной контрольной точки.");
        return false;
    }

    public string T(string english, string russian) => ParseLanguage(SelectedLanguage) == LanguagePreference.Russian ? russian : english;
    private static LanguagePreference ParseLanguage(string? value) => value == "Русский" ? LanguagePreference.Russian : LanguagePreference.English;
    private static string LanguageName(LanguagePreference value) => value == LanguagePreference.Russian ? "Русский" : "English";
    private string ThemeName(ThemePreference value) => value switch
    {
        ThemePreference.Light => T("Light", "Светлая"),
        ThemePreference.Dark => T("Dark", "Тёмная"),
        _ => T("System", "Системная")
    };
    private static string ThemeName(ThemePreference value, LanguagePreference language) => (value, language) switch
    {
        (ThemePreference.Light, LanguagePreference.Russian) => "Светлая",
        (ThemePreference.Dark, LanguagePreference.Russian) => "Тёмная",
        (ThemePreference.System, LanguagePreference.Russian) => "Системная",
        (ThemePreference.Light, _) => "Light",
        (ThemePreference.Dark, _) => "Dark",
        _ => "System"
    };
    private static bool TryParseTheme(string? value, out ThemePreference theme)
    {
        theme = value switch
        {
            "Light" or "Светлая" => ThemePreference.Light,
            "Dark" or "Тёмная" or "Темная" => ThemePreference.Dark,
            "System" or "Системная" => ThemePreference.System,
            _ => (ThemePreference)(-1)
        };
        return Enum.IsDefined(theme);
    }
    private SourceChoice ParseSource(string? value) => value switch
    {
        "7z archive" or "Архив 7z" => SourceChoice.LocalArchive,
        "Static HTTP" or "Статический HTTP" => SourceChoice.StaticHttp,
        "Managed server" or "Управляемый сервер" => SourceChoice.ManagedServer,
        _ => SourceChoice.LocalFolder
    };
    private string SourceName(SourceChoice value) => value switch
    {
        SourceChoice.LocalArchive => T("7z archive", "Архив 7z"),
        SourceChoice.StaticHttp => T("Static HTTP", "Статический HTTP"),
        SourceChoice.ManagedServer => T("Managed server", "Управляемый сервер"),
        _ => T("Local folder", "Локальная папка")
    };
    private enum SourceChoice { LocalFolder, LocalArchive, StaticHttp, ManagedServer }
    public bool IsArchiveSource => ParseSource(SelectedSourceType) == SourceChoice.LocalArchive;

    public string WindowTitle => T("Minecraft Manager", "Менеджер Minecraft");
    public string LocalModeText => T("Local mode · Files change only after review", "Локальный режим · Файлы изменяются только после проверки");
    public string InstancesText => T("Instances", "Установки");
    public string ConfigureInstancesText => T("Configure additional installations below.", "Настройте дополнительные установки ниже.");
    public string DeleteInstanceText => T("Delete selected", "Удалить выбранную");
    public string DeleteInstanceTitle => T("Delete instance?", "Удалить установку?");
    public string DeleteInstanceWarning => T("Remove this instance from Minecraft Manager? The Minecraft folder and its files will not be deleted.", "Удалить эту установку из менеджера Minecraft? Папка Minecraft и её файлы не будут удалены.");
    public string DeleteText => T("Delete", "Удалить");
    public string KeepText => T("Cancel", "Отмена");
    public string DismissText => T("Dismiss", "Закрыть");
    public string InterruptedUpdateText => T("Interrupted update detected", "Обнаружено прерванное обновление");
    public string RollbackAdviceText => T("Rollback is recommended before another update. Recovery data will be preserved if automatic rollback cannot finish.", "Перед следующим обновлением рекомендуется выполнить откат. Данные восстановления сохранятся, если автоматический откат не завершится.");
    public string RollbackText => T("Roll back interrupted update", "Откатить прерванное обновление");
    public string AddInstanceText => T("Add Minecraft instance", "Добавить установку Minecraft");
    public string NameText => T("Name", "Название");
    public string MinecraftFolderText => T("Minecraft folder", "Папка Minecraft");
    public string DefaultText => T("Default", "По умолчанию");
    public string BrowseText => T("Browse…", "Обзор…");
    public string UpdateSourceText => T("Update source", "Источник обновлений");
    public string LocalPackFolderText => IsArchiveSource ? T("Local pack 7z archive", "Архив 7z локального пака") : T("Local pack folder", "Папка локального пака");
    public string StaticHttpUrlText => T("Static HTTP base URL", "Базовый URL статического HTTP-источника");
    public string AllowHttpText => T("Allow explicitly selected private-LAN HTTP (credentials are not protected in transit)", "Разрешить явно выбранный HTTP в частной сети (учётные данные не защищены при передаче)");
    public string ManagedProfileText => T("Managed server profile", "Профиль управляемого сервера");
    public string ManagedPackIdText => T("Managed pack ID", "ID управляемого пака");
    public string SaveInstanceText => T("Save instance", "Сохранить установку");
    public string AddServerText => T("Add managed server", "Добавить управляемый сервер");
    public string CredentialsInfoText => T("Credentials are stored with Windows user-bound protection. Managed mode remains unavailable where no secure store is implemented.", "Учётные данные защищены для текущего пользователя Windows. Управляемый режим недоступен без реализации защищённого хранилища.");
    public string DisplayNameText => T("Display name", "Отображаемое имя");
    public string HttpsAddressText => T("HTTPS address", "HTTPS-адрес");
    public string RegistrationCodeText => T("One-use registration code (optional)", "Одноразовый код регистрации (необязательно)");
    public string SaveRegisterText => T("Save and register", "Сохранить и зарегистрировать");
    public string CheckUpdatesText => T("Check for updates", "Проверить обновления");
    public string ReviewUpdateText => T("Review update", "Проверка обновления");
    public string ExactChangesText => T("The following exact changes will be made. Deletions are never hidden.", "Будут выполнены перечисленные ниже изменения. Удаления никогда не скрываются.");
    public string CloseMinecraftText => T("Close Minecraft before installing. Content is staged and SHA-256 verified first.", "Закройте Minecraft перед установкой. Содержимое сначала подготавливается и проверяется по SHA-256.");
    public string InstallUpdateText => T("Install reviewed update", "Установить проверенное обновление");
    public string CancelText => T("Cancel safely", "Безопасно отменить");
    public string RecentHistoryText => T("Recent update history", "Недавняя история обновлений");
    public string SettingsText => T("Settings and diagnostics", "Настройки и диагностика");
    public string ThemeText => T("Theme", "Тема");
    public string LanguageText => T("Language", "Язык");
    public string MaxDownloadsText => T("Maximum concurrent downloads (1–8)", "Максимум одновременных загрузок (1–8)");
    public string SaveSettingsText => T("Save settings", "Сохранить настройки");
    public string ExportDiagnosticsText => T("Export diagnostics", "Экспортировать диагностику");

    private static readonly string[] LocalizedPropertyNames =
    [
        nameof(WindowTitle), nameof(LocalModeText), nameof(InstancesText), nameof(ConfigureInstancesText), nameof(DeleteInstanceText),
        nameof(DeleteInstanceTitle), nameof(DeleteInstanceWarning), nameof(DeleteText), nameof(KeepText), nameof(DismissText),
        nameof(InterruptedUpdateText), nameof(RollbackAdviceText), nameof(RollbackText), nameof(AddInstanceText), nameof(NameText),
        nameof(MinecraftFolderText), nameof(DefaultText), nameof(BrowseText), nameof(UpdateSourceText), nameof(LocalPackFolderText), nameof(StaticHttpUrlText),
        nameof(AllowHttpText), nameof(ManagedProfileText), nameof(ManagedPackIdText), nameof(SaveInstanceText), nameof(AddServerText),
        nameof(CredentialsInfoText), nameof(DisplayNameText), nameof(HttpsAddressText), nameof(RegistrationCodeText), nameof(SaveRegisterText),
        nameof(CheckUpdatesText), nameof(ReviewUpdateText), nameof(ExactChangesText), nameof(CloseMinecraftText), nameof(InstallUpdateText),
        nameof(CancelText), nameof(RecentHistoryText), nameof(SettingsText), nameof(ThemeText), nameof(LanguageText), nameof(MaxDownloadsText),
        nameof(SaveSettingsText), nameof(ExportDiagnosticsText), nameof(SelectedSummary)
    ];
    private sealed class UpdateUiException(UpdateFailure failure) : Exception(failure.SafeMessage) { public UpdateFailure Failure { get; } = failure; }
}
