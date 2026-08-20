using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameNight.Launcher.Models;
using GameNight.Launcher.Services;
using Microsoft.Win32;

namespace GameNight.Launcher.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly GameNightService _service = new();
    private readonly DispatcherTimer _refreshDebounceTimer;
    private readonly List<FileSystemWatcher> _autoRefreshWatchers = new();
    private GameManifest _manifest = new();
    private GameItemViewModel? _selectedGame;
    private string _message = "Loading setup...";
    private string _readinessTitle = "Checking";
    private string _readinessDetail = "Loading setup...";
    private Brush _readinessBrush = new SolidColorBrush(Color.FromRgb(72, 78, 90));
    private AppVersionInfo _versionInfo = AppVersionInfo.Fallback;
    private bool _isReadinessVisible;
    private string _netplayMode = "direct";
    private string _netplayNickname = "";
    private int _netplayPort = 2626;
    private string? _selectedControllerName;
    private ControllerMappingViewModel _controllerMapping = new();
    private GraphicsSettingsViewModel _graphicsSettings = new();
    private bool _isSettingsSelected;
    private bool _canPlay;
    private bool _isCapturingControllerInput;
    private bool _isRefreshing;
    private bool _hasPendingRefresh;
    private string _updateCheckStatus = "Update checks are ready once an update feed URL is configured.";
    private string _latestVersion = "Unknown";
    private string _updatePackageUrl = "";
    private string _updateReleaseNotesText = "No remote update has been checked yet.";
    private bool _isUpdateAvailable;

    public MainViewModel()
    {
        ProjectRoot = _service.ProjectRoot;
        _refreshDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _refreshDebounceTimer.Tick += async (_, _) =>
        {
            _refreshDebounceTimer.Stop();
            await RefreshAsync();
        };

        RefreshCommand = new AsyncCommand(RefreshAsync);
        PlayCommand = new AsyncCommand(PlayAsync, () => CanPlay);
        OpenEmulatorCommand = new AsyncCommand(OpenEmulatorAsync, () => SelectedGame is not null);
        OpenNetplayLobbyCommand = new AsyncCommand(OpenNetplayLobbyAsync, () => SelectedGame is not null);
        LocateGameCommand = new AsyncCommand(LocateGameAsync, () => SelectedGame is not null);
        ShowLibraryCommand = new RelayCommand(() => IsSettingsSelected = false);
        ShowSettingsCommand = new RelayCommand(() => IsSettingsSelected = true);
        SaveNetplayCommand = new AsyncCommand(SaveNetplayAsync);
        SaveGraphicsCommand = new AsyncCommand(SaveGraphicsAsync);
        ApplyControllerProfileCommand = new AsyncCommand(ApplyControllerProfileAsync, () => !string.IsNullOrWhiteSpace(SelectedControllerName));
        SaveControllerMappingsCommand = new AsyncCommand(SaveControllerMappingsAsync);
        CaptureControllerButtonCommand = new AsyncParameterCommand(CaptureControllerButtonAsync, parameter => !string.IsNullOrWhiteSpace(SelectedControllerName) && parameter is string);
        CheckForUpdatesCommand = new AsyncCommand(CheckForUpdatesAsync);
        OpenUpdatePackageCommand = new RelayCommand(OpenUpdatePackage, () => HasUpdatePackageUrl);
        StartAutoRefreshWatchers();
        _ = RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProjectRoot { get; }

    public string AppVersion => VersionInfo.Version;

    public string AppChannel => VersionInfo.Channel;

    public string PackageDate => string.IsNullOrWhiteSpace(VersionInfo.PackageDate)
        ? "Unknown"
        : VersionInfo.PackageDate;

    public string UpdateFeedUrl => string.IsNullOrWhiteSpace(VersionInfo.UpdateFeedUrl)
        ? "Not configured"
        : VersionInfo.UpdateFeedUrl;

    public string UpdateStatus => string.IsNullOrWhiteSpace(VersionInfo.UpdateFeedUrl)
        ? "Remote update checks are not configured yet."
        : "Remote update feed is configured. Use Check for Updates to compare this install with the hosted feed.";

    public string ReleaseNotesText => VersionInfo.ReleaseNotesText;

    public string UpdateCheckStatus
    {
        get => _updateCheckStatus;
        private set => SetField(ref _updateCheckStatus, value);
    }

    public string LatestVersion
    {
        get => _latestVersion;
        private set => SetField(ref _latestVersion, value);
    }

    public string UpdatePackageUrl
    {
        get => _updatePackageUrl;
        private set
        {
            if (SetField(ref _updatePackageUrl, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUpdatePackageUrl)));
                if (OpenUpdatePackageCommand is RelayCommand command)
                {
                    command.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public bool HasUpdatePackageUrl => !string.IsNullOrWhiteSpace(UpdatePackageUrl);

    public string UpdateReleaseNotesText
    {
        get => _updateReleaseNotesText;
        private set => SetField(ref _updateReleaseNotesText, value);
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => SetField(ref _isUpdateAvailable, value);
    }

    public AppVersionInfo VersionInfo
    {
        get => _versionInfo;
        private set
        {
            if (SetField(ref _versionInfo, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppVersion)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppChannel)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PackageDate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateFeedUrl)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateStatus)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReleaseNotesText)));
            }
        }
    }

    public ObservableCollection<GameItemViewModel> Games { get; } = new();

    public ObservableCollection<StatusItemViewModel> StatusItems { get; } = new();

    public ObservableCollection<StatusItemViewModel> ControllerStatusItems { get; } = new();

    public ObservableCollection<StatusItemViewModel> NetplayStatusItems { get; } = new();

    public ObservableCollection<string> NetplayModes { get; } = new(["direct", "traversal"]);

    public ObservableCollection<string> GraphicsBackends { get; } = new(["Direct3D 11", "Direct3D 12", "Vulkan", "OpenGL", "Software Renderer"]);

    public ObservableCollection<string> GraphicsAdapters { get; } = new(["Auto", "NVIDIA GeForce RTX 3050"]);

    public ObservableCollection<string> GraphicsAspectRatios { get; } = new(["Auto", "Force 16:9", "Force 4:3", "Stretch to Window"]);

    public ObservableCollection<string> InternalResolutions { get; } = new([
        "Native (640x528)",
        "2x Native (1280x1056) for 720p",
        "3x Native (1920x1584) for 1080p",
        "4x Native (2560x2112) for 1440p",
        "5x Native (3200x2640)",
        "6x Native (3840x3168) for 4K"]);

    public ObservableCollection<string> AntiAliasingModes { get; } = new(["None", "2x MSAA", "4x MSAA", "8x MSAA", "2x SSAA", "4x SSAA", "8x SSAA"]);

    public ObservableCollection<string> TextureFilteringModes { get; } = new(["Default", "2x Anisotropic", "4x Anisotropic", "8x Anisotropic", "16x Anisotropic"]);

    public ObservableCollection<string> OutputResamplingModes { get; } = new(["Default", "Nearest Neighbor", "Bilinear", "Bicubic"]);

    public ObservableCollection<string> PostProcessingEffects { get; } = new(["(off)"]);

    public ObservableCollection<string> ConnectedControllers { get; } = new();

    public ControllerMappingViewModel ControllerMapping
    {
        get => _controllerMapping;
        set => SetField(ref _controllerMapping, value);
    }

    public GraphicsSettingsViewModel GraphicsSettings
    {
        get => _graphicsSettings;
        set => SetField(ref _graphicsSettings, value);
    }

    public bool IsLibrarySelected => !IsSettingsSelected;

    public bool IsSettingsSelected
    {
        get => _isSettingsSelected;
        set
        {
            if (SetField(ref _isSettingsSelected, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLibrarySelected)));
            }
        }
    }

    public string ReadinessTitle
    {
        get => _readinessTitle;
        set => SetField(ref _readinessTitle, value);
    }

    public string ReadinessDetail
    {
        get => _readinessDetail;
        set => SetField(ref _readinessDetail, value);
    }

    public Brush ReadinessBrush
    {
        get => _readinessBrush;
        set => SetField(ref _readinessBrush, value);
    }

    public bool IsReadinessVisible
    {
        get => _isReadinessVisible;
        set => SetField(ref _isReadinessVisible, value);
    }

    public GameItemViewModel? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetField(ref _selectedGame, value))
            {
                if (OpenEmulatorCommand is AsyncCommand command)
                {
                    command.RaiseCanExecuteChanged();
                }

                if (LocateGameCommand is AsyncCommand locateCommand)
                {
                    locateCommand.RaiseCanExecuteChanged();
                }

                if (OpenNetplayLobbyCommand is AsyncCommand netplayLobbyCommand)
                {
                    netplayLobbyCommand.RaiseCanExecuteChanged();
                }

                if (ApplyControllerProfileCommand is AsyncCommand profileCommand)
                {
                    profileCommand.RaiseCanExecuteChanged();
                }

                _ = ValidateSelectedGameAsync();
            }
        }
    }

    public string NetplayMode
    {
        get => _netplayMode;
        set => SetField(ref _netplayMode, value);
    }

    public string NetplayNickname
    {
        get => _netplayNickname;
        set => SetField(ref _netplayNickname, value);
    }

    public int NetplayPort
    {
        get => _netplayPort;
        set => SetField(ref _netplayPort, value);
    }

    public string? SelectedControllerName
    {
        get => _selectedControllerName;
        set
        {
            if (SetField(ref _selectedControllerName, value))
            {
                if (ApplyControllerProfileCommand is AsyncCommand command)
                {
                    command.RaiseCanExecuteChanged();
                }

                if (CaptureControllerButtonCommand is AsyncParameterCommand captureCommand)
                {
                    captureCommand.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public bool CanPlay
    {
        get => _canPlay;
        set
        {
            if (SetField(ref _canPlay, value) && PlayCommand is AsyncCommand command)
            {
                command.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand RefreshCommand { get; }

    public ICommand PlayCommand { get; }

    public ICommand OpenEmulatorCommand { get; }

    public ICommand OpenNetplayLobbyCommand { get; }

    public ICommand LocateGameCommand { get; }

    public ICommand ShowLibraryCommand { get; }

    public ICommand ShowSettingsCommand { get; }

    public ICommand SaveNetplayCommand { get; }

    public ICommand SaveGraphicsCommand { get; }

    public ICommand ApplyControllerProfileCommand { get; }

    public ICommand SaveControllerMappingsCommand { get; }

    public ICommand CaptureControllerButtonCommand { get; }

    public ICommand CheckForUpdatesCommand { get; }

    public ICommand OpenUpdatePackageCommand { get; }

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            _hasPendingRefresh = true;
            return;
        }

        try
        {
            _isRefreshing = true;
            var selectedGameId = SelectedGame?.Config.Id;

            Message = "Refreshing library...";
            SetReadiness("Checking", "Reading config/games.json...", SummaryState.Neutral);
            CanPlay = false;
            VersionInfo = await _service.LoadVersionInfoAsync();
            _manifest = await _service.LoadManifestAsync();
            var importedArtwork = await _service.ImportMissingArtworkAsync(_manifest);
            LoadGlobalSettings();

            Games.Clear();
            foreach (var game in _manifest.Games)
            {
                Games.Add(new GameItemViewModel(game, _service.ProjectRoot));
            }

            SelectedGame = !string.IsNullOrWhiteSpace(selectedGameId)
                ? Games.FirstOrDefault(game => string.Equals(game.Config.Id, selectedGameId, StringComparison.OrdinalIgnoreCase)) ?? Games.FirstOrDefault()
                : Games.FirstOrDefault();
            Message = Games.Count == 0
                ? "No supported games were found in config/games.json or the games folder."
                : importedArtwork > 0
                    ? $"Loaded {Games.Count} game{(Games.Count == 1 ? "" : "s")} and imported {importedArtwork} cover{(importedArtwork == 1 ? "" : "s")}."
                    : $"Loaded {Games.Count} game{(Games.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            StatusItems.Clear();
            ControllerStatusItems.Clear();
            NetplayStatusItems.Clear();
            Message = ex.Message;
            SetReadiness("Needs Attention", ex.Message, SummaryState.Fail);
            CanPlay = false;
        }
        finally
        {
            _isRefreshing = false;
            if (_hasPendingRefresh)
            {
                _hasPendingRefresh = false;
                ScheduleAutoRefresh();
            }
        }
    }

    private void StartAutoRefreshWatchers()
    {
        WatchForChanges("config", "*.json", includeSubdirectories: false);
        WatchForChanges("games", "*.*", includeSubdirectories: true);
        WatchForChanges("artwork", "*.*", includeSubdirectories: true);
    }

    private void WatchForChanges(string relativePath, string filter, bool includeSubdirectories)
    {
        var path = Path.Combine(ProjectRoot, relativePath);
        if (!Directory.Exists(path))
        {
            return;
        }

        var watcher = new FileSystemWatcher(path, filter)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
        };

        watcher.Created += (_, _) => ScheduleAutoRefresh();
        watcher.Changed += (_, _) => ScheduleAutoRefresh();
        watcher.Deleted += (_, _) => ScheduleAutoRefresh();
        watcher.Renamed += (_, _) => ScheduleAutoRefresh();
        watcher.EnableRaisingEvents = true;
        _autoRefreshWatchers.Add(watcher);
    }

    private void ScheduleAutoRefresh()
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            _refreshDebounceTimer.Stop();
            _refreshDebounceTimer.Start();
        });
    }

    private async Task ValidateSelectedGameAsync()
    {
        StatusItems.Clear();
        ControllerStatusItems.Clear();
        NetplayStatusItems.Clear();
        CanPlay = false;

        if (SelectedGame is null)
        {
            Message = "Select a game.";
            SetReadiness("No Game Selected", "Select a game from the library.", SummaryState.Neutral);
            return;
        }

        Message = $"Checking {SelectedGame.Name}...";
        var results = await _service.ValidateGameAsync(SelectedGame.Config, _manifest);

        foreach (var result in results)
        {
            StatusItems.Add(new StatusItemViewModel(result));
        }

        foreach (var result in _service.GetControllerStatus(SelectedGame.Config, _manifest))
        {
            ControllerStatusItems.Add(new StatusItemViewModel(result));
        }

        foreach (var result in _service.GetNetplayStatus(SelectedGame.Config, _manifest))
        {
            NetplayStatusItems.Add(new StatusItemViewModel(result));
        }

        CanPlay = results.Count > 0 && results.All(result => result.IsOk);
        UpdateReadinessSummary(results);
        Message = CanPlay
            ? "Ready to play."
            : "Fix the failed checks before launching.";
    }

    private async Task PlayAsync()
    {
        if (SelectedGame is null)
        {
            return;
        }

        await ValidateSelectedGameAsync();
        if (!CanPlay)
        {
            return;
        }

        _service.Launch(SelectedGame.Config, _manifest);
        Message = $"Launched {SelectedGame.Name}.";
    }

    private Task OpenEmulatorAsync()
    {
        if (SelectedGame is not null)
        {
            _service.OpenEmulator(SelectedGame.Config, _manifest);
            Message = "Opened Dolphin.";
        }

        return Task.CompletedTask;
    }

    private async Task OpenNetplayLobbyAsync()
    {
        if (SelectedGame is null)
        {
            return;
        }

        Message = "Opening Dolphin NetPlay lobby...";
        var result = await _service.OpenNetplayLobbyAsync(SelectedGame.Config, _manifest);
        Message = result.Message;
    }

    private async Task LocateGameAsync()
    {
        if (SelectedGame is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Locate {SelectedGame.Name}",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Game files (*.rvz;*.iso;*.gcm;*.wbfs;*.ciso;*.gcz;*.nkit.iso)|*.rvz;*.iso;*.gcm;*.wbfs;*.ciso;*.gcz;*.nkit.iso|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Message = $"Verifying {SelectedGame.Name}...";
        var result = await _service.ImportGameFileAsync(SelectedGame.Config, dialog.FileName);
        Message = result.Message;
        await ValidateSelectedGameAsync();
    }

    private async Task SaveNetplayAsync()
    {
        var game = SelectedGame?.Config ?? _manifest.Games.FirstOrDefault();
        if (game is null)
        {
            Message = "No games are listed in config/games.json yet.";
            return;
        }

        _service.SaveNetplaySettings(
            game,
            _manifest,
            new NetplaySettings(NetplayMode, NetplayNickname, NetplayPort));
        Message = "Saved Netplay settings.";
        await ValidateSelectedGameAsync();
    }

    private async Task SaveGraphicsAsync()
    {
        var game = SelectedGame?.Config ?? _manifest.Games.FirstOrDefault();
        if (game is null)
        {
            Message = "No games are listed in config/games.json yet.";
            return;
        }

        _service.SaveGraphicsSettings(game, _manifest, GraphicsSettings.ToSettings());
        Message = "Saved graphics settings.";
        await ValidateSelectedGameAsync();
    }

    private async Task ApplyControllerProfileAsync()
    {
        var game = SelectedGame?.Config ?? _manifest.Games.FirstOrDefault();
        if (game is null || string.IsNullOrWhiteSpace(SelectedControllerName))
        {
            return;
        }

        var result = _service.ApplyKnownControllerProfile(game, _manifest, SelectedControllerName);
        Message = result.Message;
        LoadGlobalSettings();
        await ValidateSelectedGameAsync();
    }

    private async Task SaveControllerMappingsAsync()
    {
        var game = SelectedGame?.Config ?? _manifest.Games.FirstOrDefault();
        if (game is null)
        {
            Message = "No games are listed in config/games.json yet.";
            return;
        }

        _service.SaveControllerMappingSettings(game, _manifest, ControllerMapping.ToSettings());
        Message = "Saved controller mappings.";
        LoadGlobalSettings();
        await ValidateSelectedGameAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        UpdateCheckStatus = "Checking for updates...";
        LatestVersion = "Checking...";
        UpdatePackageUrl = "";
        UpdateReleaseNotesText = "Checking remote release notes...";
        IsUpdateAvailable = false;

        var result = await _service.CheckForUpdatesAsync(VersionInfo);
        UpdateCheckStatus = result.Message;

        if (result.Feed is null)
        {
            LatestVersion = "Unknown";
            UpdateReleaseNotesText = "No update feed details are available.";
            return;
        }

        LatestVersion = result.Feed.LatestVersion;
        UpdatePackageUrl = result.Feed.PackageUrl;
        UpdateReleaseNotesText = result.Feed.ReleaseNotesText;
        IsUpdateAvailable = result.IsUpdateAvailable;
    }

    private void OpenUpdatePackage()
    {
        GameNightService.OpenExternalUrl(UpdatePackageUrl);
    }

    private async Task CaptureControllerButtonAsync(object? parameter)
    {
        if (parameter is not string target)
        {
            return;
        }

        if (_isCapturingControllerInput)
        {
            Message = "Already capturing a controller input.";
            return;
        }

        try
        {
            _isCapturingControllerInput = true;
            var captureTypes = GetCaptureTypes(target);
            Message = $"Press or move a controller input for {target}...";
            var result = await _service.CaptureNextInputAsync(SelectedControllerName, TimeSpan.FromSeconds(8), captureTypes);
            if (result.Success && result.Binding is not null)
            {
                SetControllerBinding(target, result.Binding);
            }

            Message = result.Message;
        }
        finally
        {
            _isCapturingControllerInput = false;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void LoadGlobalSettings()
    {
        var game = SelectedGame?.Config ?? _manifest.Games.FirstOrDefault();
        if (game is null)
        {
            return;
        }

        var netplay = _service.GetNetplaySettings(game, _manifest);
        NetplayMode = netplay.Mode;
        NetplayNickname = netplay.Nickname;
        NetplayPort = netplay.Port;
        ControllerMapping = ControllerMappingViewModel.FromSettings(_service.GetControllerMappingSettings(game, _manifest));
        GraphicsSettings = GraphicsSettingsViewModel.FromSettings(_service.GetGraphicsSettings(game, _manifest));
        EnsureOption(GraphicsAdapters, GraphicsSettings.Adapter);

        ConnectedControllers.Clear();
        foreach (var controller in _service.GetConnectedControllerNames())
        {
            ConnectedControllers.Add(controller);
        }

        SelectedControllerName = ConnectedControllers.FirstOrDefault();
    }

    private static void EnsureOption(ObservableCollection<string> options, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !options.Contains(value))
        {
            options.Add(value);
        }
    }

    private void SetControllerBinding(string target, string binding)
    {
        switch (target)
        {
            case "A":
                ControllerMapping.A = binding;
                break;
            case "B":
                ControllerMapping.B = binding;
                break;
            case "X":
                ControllerMapping.X = binding;
                break;
            case "Y":
                ControllerMapping.Y = binding;
                break;
            case "Z":
                ControllerMapping.Z = binding;
                break;
            case "Start":
                ControllerMapping.Start = binding;
                break;
            case "L":
                ControllerMapping.L = binding;
                break;
            case "R":
                ControllerMapping.R = binding;
                break;
            case "LAnalog":
                ControllerMapping.LAnalog = binding;
                break;
            case "RAnalog":
                ControllerMapping.RAnalog = binding;
                break;
            case "MainUp":
                ControllerMapping.MainUp = binding;
                break;
            case "MainDown":
                ControllerMapping.MainDown = binding;
                break;
            case "MainLeft":
                ControllerMapping.MainLeft = binding;
                break;
            case "MainRight":
                ControllerMapping.MainRight = binding;
                break;
            case "CUp":
                ControllerMapping.CUp = binding;
                break;
            case "CDown":
                ControllerMapping.CDown = binding;
                break;
            case "CLeft":
                ControllerMapping.CLeft = binding;
                break;
            case "CRight":
                ControllerMapping.CRight = binding;
                break;
            case "DPadUp":
                ControllerMapping.DPadUp = binding;
                break;
            case "DPadDown":
                ControllerMapping.DPadDown = binding;
                break;
            case "DPadLeft":
                ControllerMapping.DPadLeft = binding;
                break;
            case "DPadRight":
                ControllerMapping.DPadRight = binding;
                break;
        }
    }

    private static InputCaptureTypes GetCaptureTypes(string target)
    {
        return target switch
        {
            "A" or "B" or "X" or "Y" or "Z" or "Start" => InputCaptureTypes.Button,
            "L" or "R" => InputCaptureTypes.Button,
            "LAnalog" or "RAnalog" => InputCaptureTypes.Axis | InputCaptureTypes.FullAxis,
            "DPadUp" or "DPadDown" or "DPadLeft" or "DPadRight" => InputCaptureTypes.Hat | InputCaptureTypes.Button,
            "MainUp" or "MainDown" or "MainLeft" or "MainRight" => InputCaptureTypes.Axis,
            "CUp" or "CDown" or "CLeft" or "CRight" => InputCaptureTypes.Axis,
            _ => InputCaptureTypes.Button
        };
    }

    private void UpdateReadinessSummary(IReadOnlyList<ValidationResult> setupResults)
    {
        var setupFailures = setupResults.Where(result => !result.IsOk).ToList();
        var controllerFailures = ControllerStatusItems.Where(result => !result.IsOk).ToList();
        var netplayFailures = NetplayStatusItems.Where(result => !result.IsOk).ToList();

        if (setupFailures.Count > 0)
        {
            SetReadiness(
                "Cannot Play Yet",
                string.Join("  ", setupFailures.Select(failure => failure.Title)),
                SummaryState.Fail);
            return;
        }

        if (controllerFailures.Count > 0 || netplayFailures.Count > 0)
        {
            var attentionItems = controllerFailures
                .Concat(netplayFailures)
                .Select(failure => failure.Title)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            SetReadiness(
                "Needs Attention",
                string.Join("  ", attentionItems),
                SummaryState.Warn);
            return;
        }

        SetReadiness(
            "Ready to Play",
            $"{StatusItems.Count} setup checks, {ControllerStatusItems.Count} controller checks, and {NetplayStatusItems.Count} netplay checks passed.",
            SummaryState.Ok);
    }

    private void SetReadiness(string title, string detail, SummaryState state)
    {
        ReadinessTitle = title;
        ReadinessDetail = detail;
        ReadinessBrush = state switch
        {
            SummaryState.Ok => new SolidColorBrush(Color.FromRgb(25, 88, 72)),
            SummaryState.Warn => new SolidColorBrush(Color.FromRgb(133, 82, 34)),
            SummaryState.Fail => new SolidColorBrush(Color.FromRgb(128, 42, 72)),
            _ => new SolidColorBrush(Color.FromRgb(42, 49, 62))
        };
        IsReadinessVisible = state is SummaryState.Warn or SummaryState.Fail;
    }
}

public enum SummaryState
{
    Neutral,
    Ok,
    Warn,
    Fail
}

public sealed class GameItemViewModel
{
    private readonly string _projectRoot;

    public GameItemViewModel(GameConfig config, string projectRoot)
    {
        Config = config;
        _projectRoot = projectRoot;
    }

    public GameConfig Config { get; }

    public string Name => Config.Name;

    public string System => Config.System;

    public string EmulatorName => Config.Emulator.Equals("dolphin", StringComparison.OrdinalIgnoreCase)
        ? "Dolphin"
        : Config.Emulator;

    public string Description => $"{Config.System} via {EmulatorName}";

    public string PlatformIconSource => Config.System.Contains("GameCube", StringComparison.OrdinalIgnoreCase)
        ? "pack://application:,,,/Assets/platform-gamecube.png"
        : "pack://application:,,,/Assets/platform-gamecube.png";

    public string? BannerSource => FindArtworkSource("banners", isCover: false);

    public bool HasBannerArt => BannerSource is not null;

    public string CoverSource => FindArtworkSource("covers", isCover: true)
        ?? "pack://application:,,,/Assets/artwork/covers/fallback-cover.png";

    public string LibraryBadge => Config.IsDiscovered ? "Detected" : "Curated";

    public string FileName => Path.GetFileName(Config.RelativePath);

    public string FileFormat
    {
        get
        {
            var fileName = FileName;
            if (fileName.EndsWith(".nkit.iso", StringComparison.OrdinalIgnoreCase))
            {
                return "NKIT ISO";
            }

            return Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        }
    }

    public string FileSize
    {
        get
        {
            var path = Path.GetFullPath(Path.Combine(_projectRoot, Config.RelativePath));
            if (!File.Exists(path))
            {
                return "Missing";
            }

            var size = new FileInfo(path).Length;
            return size switch
            {
                >= 1024L * 1024L * 1024L => $"{size / 1024d / 1024d / 1024d:0.00} GB",
                >= 1024L * 1024L => $"{size / 1024d / 1024d:0.0} MB",
                >= 1024L => $"{size / 1024d:0.0} KB",
                _ => $"{size} bytes"
            };
        }
    }

    public string FileStatus
    {
        get
        {
            var path = Path.GetFullPath(Path.Combine(_projectRoot, Config.RelativePath));
            return File.Exists(path) ? "Installed locally" : "File missing";
        }
    }

    public string VerificationSummary => string.IsNullOrWhiteSpace(Config.Sha256)
        ? "Auto-detected, hash not pinned"
        : "SHA-256 pinned";

    public string CompatibilitySummary => string.IsNullOrWhiteSpace(Config.CompatibilityProfile)
        ? "Default emulator settings"
        : "Compatibility profile included";

    public string RelativePath => Config.RelativePath;

    public string HashSummary => string.IsNullOrWhiteSpace(Config.Sha256)
        ? "Auto-detected from games folder"
        : $"SHA-256: {Config.Sha256[..Math.Min(12, Config.Sha256.Length)]}...";

    private string? FindArtworkSource(string artworkType, bool isCover)
    {
        var artworkDirectory = Path.Combine(_projectRoot, "artwork", artworkType);
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" })
        {
            var byId = Path.Combine(artworkDirectory, Config.Id + extension);
            if (File.Exists(byId))
            {
                return byId;
            }

            var byName = Path.Combine(artworkDirectory, Path.GetFileNameWithoutExtension(FileName) + extension);
            if (File.Exists(byName))
            {
                return byName;
            }
        }

        var gamePath = Path.GetFullPath(Path.Combine(_projectRoot, Config.RelativePath));
        var gameDirectory = Path.GetDirectoryName(gamePath);
        var gameName = Path.GetFileNameWithoutExtension(FileName);
        if (gameDirectory is not null)
        {
            var dolphinNames = isCover
                ? new[] { $"{gameName}.cover", "cover" }
                : new[] { gameName, "icon" };

            foreach (var name in dolphinNames)
            {
                foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" })
                {
                    var path = Path.Combine(gameDirectory, name + extension);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
            }
        }

        return null;
    }
}

public sealed class StatusItemViewModel
{
    public StatusItemViewModel(ValidationResult result)
    {
        IsOk = result.IsOk;
        Badge = result.IsOk ? "OK" : "FAIL";
        Title = result.Title;
        Detail = result.Detail;
        BadgeBrush = result.IsOk
            ? new SolidColorBrush(Color.FromRgb(42, 108, 64))
            : new SolidColorBrush(Color.FromRgb(145, 53, 53));
    }

    public string Badge { get; }

    public bool IsOk { get; }

    public string Title { get; }

    public string Detail { get; }

    public Brush BadgeBrush { get; }
}

public sealed class ControllerMappingViewModel : INotifyPropertyChanged
{
    private string _device = "DInput/0/Wireless Controller";
    private string _a = "`Button 1`";
    private string _b = "`Button 2`";
    private string _x = "`Button 0`";
    private string _y = "`Button 3`";
    private string _z = "`Button 5`";
    private string _start = "`Button 9`";
    private string _mainUp = "`Axis Y-`";
    private string _mainDown = "`Axis Y+`";
    private string _mainLeft = "`Axis X-`";
    private string _mainRight = "`Axis X+`";
    private string _cUp = "`Axis Zr-`";
    private string _cDown = "`Axis Zr+`";
    private string _cLeft = "`Axis Z-`";
    private string _cRight = "`Axis Z+`";
    private string _l = "`Button 4`";
    private string _r = "`Button 5`";
    private string _lAnalog = "`Full Axis Xr+`";
    private string _rAnalog = "`Full Axis Yr+`";
    private string _dPadUp = "`Hat 0 N`";
    private string _dPadDown = "`Hat 0 S`";
    private string _dPadLeft = "`Hat 0 W`";
    private string _dPadRight = "`Hat 0 E`";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Device { get => _device; set => SetField(ref _device, value); }
    public string A { get => _a; set => SetField(ref _a, value); }
    public string B { get => _b; set => SetField(ref _b, value); }
    public string X { get => _x; set => SetField(ref _x, value); }
    public string Y { get => _y; set => SetField(ref _y, value); }
    public string Z { get => _z; set => SetField(ref _z, value); }
    public string Start { get => _start; set => SetField(ref _start, value); }
    public string MainUp { get => _mainUp; set => SetField(ref _mainUp, value); }
    public string MainDown { get => _mainDown; set => SetField(ref _mainDown, value); }
    public string MainLeft { get => _mainLeft; set => SetField(ref _mainLeft, value); }
    public string MainRight { get => _mainRight; set => SetField(ref _mainRight, value); }
    public string CUp { get => _cUp; set => SetField(ref _cUp, value); }
    public string CDown { get => _cDown; set => SetField(ref _cDown, value); }
    public string CLeft { get => _cLeft; set => SetField(ref _cLeft, value); }
    public string CRight { get => _cRight; set => SetField(ref _cRight, value); }
    public string L { get => _l; set => SetField(ref _l, value); }
    public string R { get => _r; set => SetField(ref _r, value); }
    public string LAnalog { get => _lAnalog; set => SetField(ref _lAnalog, value); }
    public string RAnalog { get => _rAnalog; set => SetField(ref _rAnalog, value); }
    public string DPadUp { get => _dPadUp; set => SetField(ref _dPadUp, value); }
    public string DPadDown { get => _dPadDown; set => SetField(ref _dPadDown, value); }
    public string DPadLeft { get => _dPadLeft; set => SetField(ref _dPadLeft, value); }
    public string DPadRight { get => _dPadRight; set => SetField(ref _dPadRight, value); }

    public static ControllerMappingViewModel FromSettings(ControllerMappingSettings settings)
    {
        return new ControllerMappingViewModel
        {
            Device = settings.Device,
            A = settings.A,
            B = settings.B,
            X = settings.X,
            Y = settings.Y,
            Z = settings.Z,
            Start = settings.Start,
            MainUp = settings.MainUp,
            MainDown = settings.MainDown,
            MainLeft = settings.MainLeft,
            MainRight = settings.MainRight,
            CUp = settings.CUp,
            CDown = settings.CDown,
            CLeft = settings.CLeft,
            CRight = settings.CRight,
            L = settings.L,
            R = settings.R,
            LAnalog = settings.LAnalog,
            RAnalog = settings.RAnalog,
            DPadUp = settings.DPadUp,
            DPadDown = settings.DPadDown,
            DPadLeft = settings.DPadLeft,
            DPadRight = settings.DPadRight
        };
    }

    public ControllerMappingSettings ToSettings()
    {
        return new ControllerMappingSettings
        {
            Device = Device,
            A = A,
            B = B,
            X = X,
            Y = Y,
            Z = Z,
            Start = Start,
            MainUp = MainUp,
            MainDown = MainDown,
            MainLeft = MainLeft,
            MainRight = MainRight,
            CUp = CUp,
            CDown = CDown,
            CLeft = CLeft,
            CRight = CRight,
            L = L,
            R = R,
            LAnalog = LAnalog,
            RAnalog = RAnalog,
            DPadUp = DPadUp,
            DPadDown = DPadDown,
            DPadLeft = DPadLeft,
            DPadRight = DPadRight
        };
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class GraphicsSettingsViewModel : INotifyPropertyChanged
{
    private string _backend = "Direct3D 11";
    private string _adapter = "Auto";
    private string _aspectRatio = "Auto";
    private bool _vSync;
    private bool _startFullscreen;
    private string _internalResolution = "3x Native (1920x1584) for 1080p";
    private string _antiAliasing = "None";
    private string _textureFiltering = "Default";
    private string _outputResampling = "Default";
    private bool _colorCorrection;
    private string _postProcessingEffect = "(off)";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Backend { get => _backend; set => SetField(ref _backend, value); }
    public string Adapter { get => _adapter; set => SetField(ref _adapter, value); }
    public string AspectRatio { get => _aspectRatio; set => SetField(ref _aspectRatio, value); }
    public bool VSync { get => _vSync; set => SetField(ref _vSync, value); }
    public bool StartFullscreen { get => _startFullscreen; set => SetField(ref _startFullscreen, value); }
    public string InternalResolution { get => _internalResolution; set => SetField(ref _internalResolution, value); }
    public string AntiAliasing { get => _antiAliasing; set => SetField(ref _antiAliasing, value); }
    public string TextureFiltering { get => _textureFiltering; set => SetField(ref _textureFiltering, value); }
    public string OutputResampling { get => _outputResampling; set => SetField(ref _outputResampling, value); }
    public bool ColorCorrection { get => _colorCorrection; set => SetField(ref _colorCorrection, value); }
    public string PostProcessingEffect { get => _postProcessingEffect; set => SetField(ref _postProcessingEffect, value); }

    public static GraphicsSettingsViewModel FromSettings(GraphicsSettings settings)
    {
        return new GraphicsSettingsViewModel
        {
            Backend = settings.Backend,
            Adapter = settings.Adapter,
            AspectRatio = settings.AspectRatio,
            VSync = settings.VSync,
            StartFullscreen = settings.StartFullscreen,
            InternalResolution = settings.InternalResolution,
            AntiAliasing = settings.AntiAliasing,
            TextureFiltering = settings.TextureFiltering,
            OutputResampling = settings.OutputResampling,
            ColorCorrection = settings.ColorCorrection,
            PostProcessingEffect = settings.PostProcessingEffect
        };
    }

    public GraphicsSettings ToSettings()
    {
        return new GraphicsSettings
        {
            Backend = Backend,
            Adapter = Adapter,
            AspectRatio = AspectRatio,
            VSync = VSync,
            StartFullscreen = StartFullscreen,
            InternalResolution = InternalResolution,
            AntiAliasing = AntiAliasing,
            TextureFiltering = TextureFiltering,
            OutputResampling = OutputResampling,
            ColorCorrection = ColorCorrection,
            PostProcessingEffect = PostProcessingEffect
        };
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncParameterCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public AsyncParameterCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        await _execute(parameter);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
