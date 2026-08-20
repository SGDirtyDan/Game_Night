using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using GameNight.Launcher.Models;
using SharpDX.DirectInput;

namespace GameNight.Launcher.Services;

public sealed class GameNightService
{
    private static readonly HttpClient ArtworkHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly IReadOnlyDictionary<string, DiscoveredGameType> GameTypesByExtension =
        new Dictionary<string, DiscoveredGameType>(StringComparer.OrdinalIgnoreCase)
        {
            [".rvz"] = new("dolphin", "GameCube / Wii"),
            [".iso"] = new("dolphin", "GameCube / Wii"),
            [".gcm"] = new("dolphin", "GameCube"),
            [".wbfs"] = new("dolphin", "Wii"),
            [".ciso"] = new("dolphin", "GameCube / Wii"),
            [".gcz"] = new("dolphin", "GameCube / Wii"),
            [".nkit.iso"] = new("dolphin", "GameCube / Wii")
        };

    private static readonly IReadOnlyDictionary<string, string> KnownGameTdbIds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gauntlet-dark-legacy"] = "GUNE5D",
            ["mario-golf-toadstool-tour"] = "GFTE01",
            ["mario-kart-double-dash"] = "GM4E01",
            ["mario-party-4"] = "GMPE01",
            ["mario-party-5"] = "GP5E01",
            ["mario-party-6"] = "GP6E01",
            ["mario-party-7"] = "GP7E01",
            ["mario-power-tennis"] = "GOME01",
            ["mario-superstar-baseball"] = "GYQE01",
            ["super-mario-strikers"] = "G4QE01",
            ["super-smash-bros-melee"] = "GALE01"
        };

    public string ProjectRoot { get; }

    public GameNightService()
    {
        ProjectRoot = FindProjectRoot();
    }

    public async Task<GameManifest> LoadManifestAsync()
    {
        var manifestPath = Path.Combine(ProjectRoot, "config", "games.json");
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<GameManifest>(stream) ?? new GameManifest();
        ApplyKnownArtworkIds(manifest);
        AddDiscoveredGames(manifest);
        return manifest;
    }

    public async Task<int> ImportMissingArtworkAsync(GameManifest manifest)
    {
        var imported = 0;
        var coversDirectory = Path.Combine(ProjectRoot, "artwork", "covers");
        Directory.CreateDirectory(coversDirectory);

        foreach (var game in manifest.Games)
        {
            var gameTdbId = ResolveGameTdbId(game);
            if (string.IsNullOrWhiteSpace(gameTdbId))
            {
                continue;
            }

            var coverPath = Path.Combine(coversDirectory, game.Id + ".png");
            if (File.Exists(coverPath))
            {
                continue;
            }

            var cover = await TryDownloadGameTdbCoverAsync(gameTdbId);
            if (cover is null)
            {
                continue;
            }

            await File.WriteAllBytesAsync(coverPath, cover);
            imported++;
        }

        return imported;
    }

    public async Task<AppVersionInfo> LoadVersionInfoAsync()
    {
        var versionPath = Path.Combine(ProjectRoot, "version.json");
        if (!File.Exists(versionPath))
        {
            return AppVersionInfo.Fallback;
        }

        await using var stream = File.OpenRead(versionPath);
        return await JsonSerializer.DeserializeAsync<AppVersionInfo>(stream) ?? AppVersionInfo.Fallback;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(AppVersionInfo installedVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion.UpdateFeedUrl))
        {
            return new UpdateCheckResult(
                false,
                "No update feed is configured yet. Add a hosted update.json URL to version.json when the GitHub release feed is ready.",
                null);
        }

        try
        {
            using var response = await ArtworkHttpClient.GetAsync(installedVersion.UpdateFeedUrl);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            var feed = await JsonSerializer.DeserializeAsync<UpdateFeedInfo>(stream);

            if (feed is null || string.IsNullOrWhiteSpace(feed.LatestVersion))
            {
                return new UpdateCheckResult(false, "The update feed was reachable, but it did not include a latestVersion value.", feed);
            }

            var isUpdateAvailable = IsVersionNewer(feed.LatestVersion, installedVersion.Version);
            var message = isUpdateAvailable
                ? $"Update available: {feed.LatestVersion}"
                : $"Game Night is up to date: {installedVersion.Version}";

            return new UpdateCheckResult(isUpdateAvailable, message, feed);
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheckResult(false, "Could not reach the update feed: " + ex.Message, null);
        }
        catch (JsonException ex)
        {
            return new UpdateCheckResult(false, "The update feed is not valid JSON: " + ex.Message, null);
        }
        catch (TaskCanceledException)
        {
            return new UpdateCheckResult(false, "The update check timed out.", null);
        }
    }

    public static void OpenExternalUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private void AddDiscoveredGames(GameManifest manifest)
    {
        var gamesDirectory = Path.Combine(ProjectRoot, "games");
        if (!Directory.Exists(gamesDirectory))
        {
            return;
        }

        var existingPaths = manifest.Games
            .Select(game => NormalizeRelativePath(game.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingIds = manifest.Games
            .Select(game => game.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var discoveredGames = new List<GameConfig>();
        foreach (var gamePath in Directory.EnumerateFiles(gamesDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(ProjectRoot, gamePath));
            if (existingPaths.Contains(relativePath))
            {
                continue;
            }

            var extension = GetGameExtension(gamePath);
            if (!GameTypesByExtension.TryGetValue(extension, out var gameType)
                || !manifest.Emulators.ContainsKey(gameType.Emulator))
            {
                continue;
            }

            var displayName = CreateDisplayName(gamePath);
            var id = CreateUniqueGameId(displayName, existingIds);
            existingPaths.Add(relativePath);
            discoveredGames.Add(new GameConfig
            {
                Id = id,
                Name = displayName,
                System = gameType.System,
                Emulator = gameType.Emulator,
                RelativePath = relativePath,
                Sha256 = "",
                CompatibilityProfile = null,
                GameTdbId = ResolveKnownGameTdbId(displayName),
                IsDiscovered = true
            });
        }

        manifest.Games.AddRange(discoveredGames.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static void ApplyKnownArtworkIds(GameManifest manifest)
    {
        foreach (var game in manifest.Games)
        {
            if (string.IsNullOrWhiteSpace(game.GameTdbId))
            {
                game.GameTdbId = ResolveKnownGameTdbId(game.Name);
            }
        }
    }

    private static string? ResolveGameTdbId(GameConfig game)
    {
        return !string.IsNullOrWhiteSpace(game.GameTdbId)
            ? game.GameTdbId
            : ResolveKnownGameTdbId(game.Name);
    }

    private static string? ResolveKnownGameTdbId(string gameName)
    {
        return KnownGameTdbIds.TryGetValue(CreateLookupKey(gameName), out var gameTdbId)
            ? gameTdbId
            : null;
    }

    private static async Task<byte[]?> TryDownloadGameTdbCoverAsync(string gameTdbId)
    {
        foreach (var region in new[] { "US", "EN" })
        {
            try
            {
                var url = $"https://art.gametdb.com/wii/cover/{region}/{gameTdbId}.png";
                var bytes = await ArtworkHttpClient.GetByteArrayAsync(url);
                if (LooksLikePng(bytes))
                {
                    return bytes;
                }
            }
            catch
            {
                // Artwork is optional. Missing covers or network failures should not block the launcher.
            }
        }

        return null;
    }

    private static bool LooksLikePng(byte[] bytes)
    {
        return bytes.Length > 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4e
            && bytes[3] == 0x47;
    }

    public async Task<IReadOnlyList<ValidationResult>> ValidateGameAsync(GameConfig game, GameManifest manifest)
    {
        var results = new List<ValidationResult>();

        if (!manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            results.Add(ValidationResult.Fail("Emulator config", $"Unknown emulator '{game.Emulator}'."));
            return results;
        }

        var emulatorPath = FullPath(emulator.Executable);
        results.Add(File.Exists(emulatorPath)
            ? ValidationResult.Ok("Dolphin found", emulator.Executable)
            : ValidationResult.Fail("Dolphin missing", emulator.Executable));

        var portablePath = FullPath(emulator.PortableMarker);
        results.Add(File.Exists(portablePath)
            ? ValidationResult.Ok("Portable mode enabled", emulator.PortableMarker)
            : ValidationResult.Fail("Portable mode missing", emulator.PortableMarker));

        var gamePath = FullPath(game.RelativePath);
        if (!File.Exists(gamePath))
        {
            results.Add(ValidationResult.Fail("Game file missing", game.RelativePath));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(game.Sha256))
            {
                results.Add(ValidationResult.Ok("Game detected", $"{game.RelativePath} (hash not pinned)."));
            }
            else
            {
                var actualHash = await ComputeSha256Async(gamePath);
                results.Add(string.Equals(actualHash, game.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? ValidationResult.Ok("Game verified", game.RelativePath)
                    : ValidationResult.Fail("Game hash mismatch", $"Expected {game.Sha256}; found {actualHash}."));
            }
        }

        if (!string.IsNullOrWhiteSpace(game.CompatibilityProfile))
        {
            var profilePath = FullPath(game.CompatibilityProfile);
            results.Add(File.Exists(profilePath)
                ? ValidationResult.Ok("Compatibility profile", game.CompatibilityProfile)
                : ValidationResult.Fail("Compatibility profile missing", game.CompatibilityProfile));
        }

        return results;
    }

    public IReadOnlyList<ValidationResult> GetControllerStatus(GameConfig? game, GameManifest manifest)
    {
        var results = new List<ValidationResult>();

        if (game is null)
        {
            results.Add(ValidationResult.Fail("Controller", "Select a game to check controller requirements."));
            return results;
        }

        if (!string.Equals(game.Emulator, "dolphin", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(ValidationResult.Ok("Controller", "Controller status is currently implemented for Dolphin games."));
            return results;
        }

        if (!manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            results.Add(ValidationResult.Fail("Controller", $"Unknown emulator '{game.Emulator}'."));
            return results;
        }

        var emulatorDirectory = Path.GetDirectoryName(FullPath(emulator.Executable)) ?? ProjectRoot;
        var gcPadPath = Path.Combine(emulatorDirectory, "User", "Config", "GCPadNew.ini");

        if (!File.Exists(gcPadPath))
        {
            results.Add(ValidationResult.Fail("GameCube Port 1", "Open Dolphin Controllers once and configure Port 1."));
            return results;
        }

        var gcPad1 = ReadIniSection(gcPadPath, "GCPad1");
        if (!gcPad1.TryGetValue("Device", out var device) || string.IsNullOrWhiteSpace(device))
        {
            results.Add(ValidationResult.Fail("GameCube Port 1", "No input device is configured for Port 1."));
            return results;
        }

        if (IsKeyboardMouse(device))
        {
            results.Add(ValidationResult.Fail("GameCube Port 1", "Port 1 is still set to Keyboard Mouse."));
        }
        else
        {
            results.Add(ValidationResult.Ok("GameCube Port 1", $"Configured as {device}."));

            var connectedControllers = ControllerDeviceDetector.GetConnectedControllerNames();
            var configuredName = ExtractDolphinDeviceName(device);
            var matchingController = FindBestControllerMatch(connectedControllers, configuredName);

            results.Add(matchingController is not null
                ? ValidationResult.Ok("Controller connected", matchingController)
                : ValidationResult.Fail(
                    "Controller disconnected",
                    connectedControllers.Count == 0
                        ? $"Dolphin expects {configuredName}, but Windows is not reporting a connected game controller."
                        : $"Dolphin expects {configuredName}. Connected: {string.Join(", ", connectedControllers)}"));
        }

        var requiredMappings = new Dictionary<string, string>
        {
            ["A"] = "Buttons/A",
            ["B"] = "Buttons/B",
            ["X"] = "Buttons/X",
            ["Y"] = "Buttons/Y",
            ["Start"] = "Buttons/Start",
            ["Main Stick"] = "Main Stick/Up",
            ["C-Stick"] = "C-Stick/Up",
            ["L Trigger"] = "Triggers/L",
            ["R Trigger"] = "Triggers/R"
        };

        var missing = requiredMappings
            .Where(mapping => !gcPad1.TryGetValue(mapping.Value, out var value) || string.IsNullOrWhiteSpace(value))
            .Select(mapping => mapping.Key)
            .ToList();

        results.Add(missing.Count == 0
            ? ValidationResult.Ok("Mapping coverage", "A, B, X, Y, Start, sticks, and triggers are mapped.")
            : ValidationResult.Fail("Mapping coverage", "Missing: " + string.Join(", ", missing)));

        return results;
    }

    public IReadOnlyList<ValidationResult> GetNetplayStatus(GameConfig? game, GameManifest manifest)
    {
        var results = new List<ValidationResult>();

        if (game is null)
        {
            results.Add(ValidationResult.Fail("Netplay", "Select a game to check netplay settings."));
            return results;
        }

        if (!string.Equals(game.Emulator, "dolphin", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(ValidationResult.Ok("Netplay", "Netplay status is currently implemented for Dolphin games."));
            return results;
        }

        if (!manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            results.Add(ValidationResult.Fail("Netplay", $"Unknown emulator '{game.Emulator}'."));
            return results;
        }

        var dolphinIniPath = GetDolphinConfigPath(emulator, "Dolphin.ini");
        if (!File.Exists(dolphinIniPath))
        {
            results.Add(ValidationResult.Fail("Dolphin.ini", "Launch Dolphin once to create portable NetPlay settings."));
            return results;
        }

        var netplay = ReadIniSection(dolphinIniPath, "NetPlay");
        var traversalChoice = GetIniValue(netplay, "TraversalChoice", "not set");
        results.Add(string.Equals(traversalChoice, "not set", StringComparison.OrdinalIgnoreCase)
            ? ValidationResult.Fail("Netplay mode", "Choose Direct or Traversal in Settings.")
            : ValidationResult.Ok("Netplay mode", FormatNetplayMode(traversalChoice)));

        var nickname = GetIniValue(netplay, "Nickname", "");
        results.Add(!string.IsNullOrWhiteSpace(nickname)
            ? ValidationResult.Ok("Nickname", nickname)
            : ValidationResult.Fail("Nickname", "Set a NetPlay nickname in Dolphin."));

        var portValue = GetIniValue(netplay, "HostPort", GetIniValue(netplay, "ListenPort", ""));
        var port = ParseDolphinInteger(portValue);
        results.Add(port is > 0
            ? ValidationResult.Ok("Host port", $"{port} ({portValue})")
            : ValidationResult.Fail("Host port", "No valid NetPlay host/listen port found."));

        var upnp = GetIniValue(netplay, "UseUPNP", "False");
        results.Add(string.Equals(upnp, "False", StringComparison.OrdinalIgnoreCase)
            ? ValidationResult.Ok("UPnP", "Off")
            : ValidationResult.Fail("UPnP", "On. Manual port/VPN/traversal setup is easier to reason about with UPnP off."));

        return results;
    }

    public NetplaySettings GetNetplaySettings(GameConfig? game, GameManifest manifest)
    {
        if (game is null || !manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            return new NetplaySettings("direct", "", 2626);
        }

        var dolphinIniPath = GetDolphinConfigPath(emulator, "Dolphin.ini");
        if (!File.Exists(dolphinIniPath))
        {
            return new NetplaySettings("direct", "", 2626);
        }

        var netplay = ReadIniSection(dolphinIniPath, "NetPlay");
        var mode = NormalizeNetplayMode(GetIniValue(netplay, "TraversalChoice", "direct"));
        var nickname = GetIniValue(netplay, "Nickname", "");
        var port = ParseDolphinInteger(GetIniValue(netplay, "HostPort", "")) ?? 2626;
        return new NetplaySettings(mode, nickname, port);
    }

    public void SaveNetplaySettings(GameConfig game, GameManifest manifest, NetplaySettings settings)
    {
        if (!manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            throw new InvalidOperationException($"Unknown emulator '{game.Emulator}'.");
        }

        var dolphinIniPath = GetDolphinConfigPath(emulator, "Dolphin.ini");
        var mode = NormalizeNetplayMode(settings.Mode);
        var port = Math.Clamp(settings.Port, 1, 65535);
        var portValue = FormatDolphinHex(port);

        WriteIniValue(dolphinIniPath, "NetPlay", "TraversalChoice", mode);
        WriteIniValue(dolphinIniPath, "NetPlay", "Nickname", settings.Nickname.Trim());
        WriteIniValue(dolphinIniPath, "NetPlay", "HostPort", portValue);
        WriteIniValue(dolphinIniPath, "NetPlay", "ListenPort", portValue);
        WriteIniValue(dolphinIniPath, "NetPlay", "ConnectPort", portValue);
        WriteIniValue(dolphinIniPath, "NetPlay", "UseUPNP", "False");
        WriteIniValue(dolphinIniPath, "NetPlay", "UseIndex", "False");
    }

    public GraphicsSettings GetGraphicsSettings(GameConfig? game, GameManifest manifest)
    {
        var fallback = new GraphicsSettings();
        if (game is null || !manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            return fallback;
        }

        var dolphinIniPath = GetDolphinConfigPath(emulator, "Dolphin.ini");
        var gfxIniPath = GetDolphinConfigPath(emulator, "GFX.ini");
        var core = ReadIniSection(dolphinIniPath, "Core");
        var display = ReadIniSection(dolphinIniPath, "Display");
        var settings = ReadIniSection(gfxIniPath, "Settings");
        var hardware = ReadIniSection(gfxIniPath, "Hardware");
        var enhancements = ReadIniSection(gfxIniPath, "Enhancements");

        return new GraphicsSettings
        {
            Backend = FormatGraphicsBackend(GetIniValue(core, "GFXBackend", MapGraphicsBackend(fallback.Backend))),
            Adapter = GetIniValue(hardware, "Adapter", fallback.Adapter),
            AspectRatio = FormatAspectRatio(GetIniValue(settings, "AspectRatio", MapAspectRatio(fallback.AspectRatio))),
            VSync = ParseBoolean(GetIniValue(hardware, "VSync", fallback.VSync.ToString())),
            StartFullscreen = ParseBoolean(GetIniValue(display, "Fullscreen", fallback.StartFullscreen.ToString())),
            InternalResolution = FormatInternalResolution(GetIniValue(settings, "InternalResolution", MapInternalResolution(fallback.InternalResolution))),
            AntiAliasing = FormatAntiAliasing(
                GetIniValue(enhancements, "MSAA", "1"),
                ParseBoolean(GetIniValue(enhancements, "SSAA", "False"))),
            TextureFiltering = FormatTextureFiltering(GetIniValue(enhancements, "MaxAnisotropy", "0")),
            OutputResampling = FormatOutputResampling(GetIniValue(enhancements, "OutputResampling", "0")),
            ColorCorrection = ParseBoolean(GetIniValue(enhancements, "ColorCorrection", fallback.ColorCorrection.ToString())),
            PostProcessingEffect = FormatPostProcessingEffect(GetIniValue(enhancements, "PostProcessingShader", ""))
        };
    }

    public void SaveGraphicsSettings(GameConfig game, GameManifest manifest, GraphicsSettings settings)
    {
        if (!manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            throw new InvalidOperationException($"Unknown emulator '{game.Emulator}'.");
        }

        var dolphinIniPath = GetDolphinConfigPath(emulator, "Dolphin.ini");
        var gfxIniPath = GetDolphinConfigPath(emulator, "GFX.ini");
        var (msaa, ssaa) = MapAntiAliasing(settings.AntiAliasing);

        WriteIniValue(dolphinIniPath, "Core", "GFXBackend", MapGraphicsBackend(settings.Backend));
        WriteIniValue(dolphinIniPath, "Display", "Fullscreen", settings.StartFullscreen ? "True" : "False");
        WriteIniValue(gfxIniPath, "Hardware", "Adapter", string.IsNullOrWhiteSpace(settings.Adapter) ? "Auto" : settings.Adapter.Trim());
        WriteIniValue(gfxIniPath, "Hardware", "VSync", settings.VSync ? "True" : "False");
        WriteIniValue(gfxIniPath, "Settings", "AspectRatio", MapAspectRatio(settings.AspectRatio));
        WriteIniValue(gfxIniPath, "Settings", "InternalResolution", MapInternalResolution(settings.InternalResolution));
        WriteIniValue(gfxIniPath, "Enhancements", "MSAA", msaa);
        WriteIniValue(gfxIniPath, "Enhancements", "SSAA", ssaa ? "True" : "False");
        WriteIniValue(gfxIniPath, "Enhancements", "MaxAnisotropy", MapTextureFiltering(settings.TextureFiltering));
        WriteIniValue(gfxIniPath, "Enhancements", "OutputResampling", MapOutputResampling(settings.OutputResampling));
        WriteIniValue(gfxIniPath, "Enhancements", "ColorCorrection", settings.ColorCorrection ? "True" : "False");
        WriteIniValue(gfxIniPath, "Enhancements", "PostProcessingShader", MapPostProcessingEffect(settings.PostProcessingEffect));
    }

    public IReadOnlyList<string> GetConnectedControllerNames()
    {
        return ControllerDeviceDetector.GetConnectedControllerNames()
            .Concat(DirectInputCapture.GetControllerNames())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<InputCaptureResult> CaptureNextInputAsync(string? controllerName, TimeSpan timeout, InputCaptureTypes captureTypes)
    {
        return await DirectInputCapture.CaptureNextInputAsync(controllerName, timeout, captureTypes);
    }

    public ControllerMappingSettings GetControllerMappingSettings(GameConfig? game, GameManifest manifest)
    {
        var fallback = new ControllerMappingSettings();
        if (game is null || !manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            return fallback;
        }

        var savedSettings = LoadSavedControllerMappingSettings();
        if (savedSettings is not null)
        {
            return savedSettings;
        }

        var gcPadPath = GetDolphinGCPadPath(emulator);
        if (!File.Exists(gcPadPath))
        {
            return fallback;
        }

        var gcPad1 = ReadIniSection(gcPadPath, "GCPad1");
        return new ControllerMappingSettings
        {
            Device = GetIniValue(gcPad1, "Device", fallback.Device),
            A = GetIniValue(gcPad1, "Buttons/A", fallback.A),
            B = GetIniValue(gcPad1, "Buttons/B", fallback.B),
            X = GetIniValue(gcPad1, "Buttons/X", fallback.X),
            Y = GetIniValue(gcPad1, "Buttons/Y", fallback.Y),
            Z = GetIniValue(gcPad1, "Buttons/Z", fallback.Z),
            Start = GetIniValue(gcPad1, "Buttons/Start", fallback.Start),
            MainUp = GetIniValue(gcPad1, "Main Stick/Up", fallback.MainUp),
            MainDown = GetIniValue(gcPad1, "Main Stick/Down", fallback.MainDown),
            MainLeft = GetIniValue(gcPad1, "Main Stick/Left", fallback.MainLeft),
            MainRight = GetIniValue(gcPad1, "Main Stick/Right", fallback.MainRight),
            CUp = GetIniValue(gcPad1, "C-Stick/Up", fallback.CUp),
            CDown = GetIniValue(gcPad1, "C-Stick/Down", fallback.CDown),
            CLeft = GetIniValue(gcPad1, "C-Stick/Left", fallback.CLeft),
            CRight = GetIniValue(gcPad1, "C-Stick/Right", fallback.CRight),
            L = GetIniValue(gcPad1, "Triggers/L", fallback.L),
            R = GetIniValue(gcPad1, "Triggers/R", fallback.R),
            LAnalog = GetIniValue(gcPad1, "Triggers/L-Analog", fallback.LAnalog),
            RAnalog = GetIniValue(gcPad1, "Triggers/R-Analog", fallback.RAnalog),
            DPadUp = GetIniValue(gcPad1, "D-Pad/Up", fallback.DPadUp),
            DPadDown = GetIniValue(gcPad1, "D-Pad/Down", fallback.DPadDown),
            DPadLeft = GetIniValue(gcPad1, "D-Pad/Left", fallback.DPadLeft),
            DPadRight = GetIniValue(gcPad1, "D-Pad/Right", fallback.DPadRight)
        };
    }

    public void SaveControllerMappingSettings(GameConfig game, GameManifest manifest, ControllerMappingSettings settings)
    {
        if (!manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            throw new InvalidOperationException($"Unknown emulator '{game.Emulator}'.");
        }

        var gcPadPath = GetDolphinGCPadPath(emulator);
        WriteControllerMappingSettings(gcPadPath, settings);
        SaveControllerProfile(settings);
    }

    public ControllerProfileResult ApplyKnownControllerProfile(GameConfig game, GameManifest manifest, string controllerName)
    {
        if (!manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            return new ControllerProfileResult(false, $"Unknown emulator '{game.Emulator}'.");
        }

        if (!TryCreateKnownControllerProfile(controllerName, out var profileLines))
        {
            return new ControllerProfileResult(false, $"No built-in mapping profile for '{controllerName}' yet.");
        }

        var gcPadPath = GetDolphinGCPadPath(emulator);
        Directory.CreateDirectory(Path.GetDirectoryName(gcPadPath) ?? ProjectRoot);
        File.WriteAllLines(gcPadPath, profileLines);
        SaveControllerProfile(ReadControllerMappingSettings(gcPadPath));

        return new ControllerProfileResult(true, $"Applied controller profile for {controllerName}.");
    }

    public void Launch(GameConfig game, GameManifest manifest)
    {
        if (!manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            throw new InvalidOperationException($"Unknown emulator '{game.Emulator}'.");
        }

        var emulatorPath = FullPath(emulator.Executable);
        var gamePath = FullPath(game.RelativePath);
        EnsureDolphinGameDirectory(game, emulator);
        RestoreSavedControllerProfile(emulator);

        var startInfo = new ProcessStartInfo
        {
            FileName = emulatorPath,
            WorkingDirectory = Path.GetDirectoryName(emulatorPath) ?? ProjectRoot,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("--batch");
        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add(gamePath);

        Process.Start(startInfo);
    }

    public void OpenEmulator(GameConfig game, GameManifest manifest)
    {
        if (!manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            throw new InvalidOperationException($"Unknown emulator '{game.Emulator}'.");
        }

        var emulatorPath = FullPath(emulator.Executable);
        EnsureDolphinGameDirectory(game, emulator);
        var startInfo = new ProcessStartInfo
        {
            FileName = emulatorPath,
            WorkingDirectory = Path.GetDirectoryName(emulatorPath) ?? ProjectRoot,
            UseShellExecute = false
        };

        Process.Start(startInfo);
    }

    public async Task<NetplayLobbyResult> OpenNetplayLobbyAsync(GameConfig game, GameManifest manifest)
    {
        if (!manifest.Emulators.TryGetValue(game.Emulator, out var emulator))
        {
            return new NetplayLobbyResult(false, $"Unknown emulator '{game.Emulator}'.");
        }

        var emulatorPath = FullPath(emulator.Executable);
        EnsureDolphinGameDirectory(game, emulator);

        var startInfo = new ProcessStartInfo
        {
            FileName = emulatorPath,
            WorkingDirectory = Path.GetDirectoryName(emulatorPath) ?? ProjectRoot,
            UseShellExecute = false
        };

        var process = Process.Start(startInfo);
        if (process is null)
        {
            return new NetplayLobbyResult(false, "Could not start Dolphin.");
        }

        return await Task.Run(() => OpenNetplayMenu(process));
    }

    public async Task<LocateGameResult> ImportGameFileAsync(GameConfig game, string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            return new LocateGameResult(false, "Selected file does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(game.Sha256))
        {
            var actualHash = await ComputeSha256Async(sourcePath);
            if (!string.Equals(actualHash, game.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new LocateGameResult(
                    false,
                    $"Hash mismatch. Expected {game.Sha256}; selected file was {actualHash}.");
            }
        }

        var targetPath = FullPath(game.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? ProjectRoot);

        if (!string.Equals(Path.GetFullPath(sourcePath), targetPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        return new LocateGameResult(true, $"Imported {game.Name}.");
    }

    public string FullPath(string relativePath) => Path.GetFullPath(Path.Combine(ProjectRoot, relativePath));

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace('\\', '/');
    }

    private static string GetGameExtension(string gamePath)
    {
        var fileName = Path.GetFileName(gamePath);
        if (fileName.EndsWith(".nkit.iso", StringComparison.OrdinalIgnoreCase))
        {
            return ".nkit.iso";
        }

        return Path.GetExtension(fileName);
    }

    private static string CreateDisplayName(string gamePath)
    {
        var fileName = Path.GetFileName(gamePath);
        var extension = GetGameExtension(fileName);
        var name = fileName[..^extension.Length];
        name = Regex.Replace(name, @"[_\.]+", " ");
        name = Regex.Replace(name, @"\s*[\(\[].*?[\)\]]", "");
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fileName) : name;
    }

    private static string CreateUniqueGameId(string displayName, ISet<string> existingIds)
    {
        var baseId = CreateLookupKey(displayName);
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = "game";
        }

        var id = baseId;
        var suffix = 2;
        while (existingIds.Contains(id))
        {
            id = $"{baseId}-{suffix}";
            suffix++;
        }

        existingIds.Add(id);
        return id;
    }

    private static string CreateLookupKey(string value)
    {
        return Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string GetDolphinConfigPath(EmulatorConfig emulator, string fileName)
    {
        var emulatorDirectory = Path.GetDirectoryName(FullPath(emulator.Executable)) ?? ProjectRoot;
        return Path.Combine(emulatorDirectory, "User", "Config", fileName);
    }

    private string GetDolphinGCPadPath(EmulatorConfig emulator)
    {
        return GetDolphinConfigPath(emulator, "GCPadNew.ini");
    }

    private string GetControllerProfilePath()
    {
        return Path.Combine(ProjectRoot, "config", "controller-profile.json");
    }

    private ControllerMappingSettings? LoadSavedControllerMappingSettings()
    {
        var profilePath = GetControllerProfilePath();
        if (!File.Exists(profilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(profilePath);
            return JsonSerializer.Deserialize<ControllerMappingSettings>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void SaveControllerProfile(ControllerMappingSettings settings)
    {
        var profilePath = GetControllerProfilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath) ?? ProjectRoot);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(profilePath, json);
    }

    private void RestoreSavedControllerProfile(EmulatorConfig emulator)
    {
        var savedSettings = LoadSavedControllerMappingSettings();
        if (savedSettings is null)
        {
            return;
        }

        WriteControllerMappingSettings(GetDolphinGCPadPath(emulator), savedSettings);
    }

    private ControllerMappingSettings ReadControllerMappingSettings(string gcPadPath)
    {
        var fallback = new ControllerMappingSettings();
        if (!File.Exists(gcPadPath))
        {
            return fallback;
        }

        var gcPad1 = ReadIniSection(gcPadPath, "GCPad1");
        return new ControllerMappingSettings
        {
            Device = GetIniValue(gcPad1, "Device", fallback.Device),
            A = GetIniValue(gcPad1, "Buttons/A", fallback.A),
            B = GetIniValue(gcPad1, "Buttons/B", fallback.B),
            X = GetIniValue(gcPad1, "Buttons/X", fallback.X),
            Y = GetIniValue(gcPad1, "Buttons/Y", fallback.Y),
            Z = GetIniValue(gcPad1, "Buttons/Z", fallback.Z),
            Start = GetIniValue(gcPad1, "Buttons/Start", fallback.Start),
            MainUp = GetIniValue(gcPad1, "Main Stick/Up", fallback.MainUp),
            MainDown = GetIniValue(gcPad1, "Main Stick/Down", fallback.MainDown),
            MainLeft = GetIniValue(gcPad1, "Main Stick/Left", fallback.MainLeft),
            MainRight = GetIniValue(gcPad1, "Main Stick/Right", fallback.MainRight),
            CUp = GetIniValue(gcPad1, "C-Stick/Up", fallback.CUp),
            CDown = GetIniValue(gcPad1, "C-Stick/Down", fallback.CDown),
            CLeft = GetIniValue(gcPad1, "C-Stick/Left", fallback.CLeft),
            CRight = GetIniValue(gcPad1, "C-Stick/Right", fallback.CRight),
            L = GetIniValue(gcPad1, "Triggers/L", fallback.L),
            R = GetIniValue(gcPad1, "Triggers/R", fallback.R),
            LAnalog = GetIniValue(gcPad1, "Triggers/L-Analog", fallback.LAnalog),
            RAnalog = GetIniValue(gcPad1, "Triggers/R-Analog", fallback.RAnalog),
            DPadUp = GetIniValue(gcPad1, "D-Pad/Up", fallback.DPadUp),
            DPadDown = GetIniValue(gcPad1, "D-Pad/Down", fallback.DPadDown),
            DPadLeft = GetIniValue(gcPad1, "D-Pad/Left", fallback.DPadLeft),
            DPadRight = GetIniValue(gcPad1, "D-Pad/Right", fallback.DPadRight)
        };
    }

    private static void WriteControllerMappingSettings(string gcPadPath, ControllerMappingSettings settings)
    {
        WriteIniValue(gcPadPath, "GCPad1", "Device", settings.Device);
        WriteIniValue(gcPadPath, "GCPad1", "Buttons/A", settings.A);
        WriteIniValue(gcPadPath, "GCPad1", "Buttons/B", settings.B);
        WriteIniValue(gcPadPath, "GCPad1", "Buttons/X", settings.X);
        WriteIniValue(gcPadPath, "GCPad1", "Buttons/Y", settings.Y);
        WriteIniValue(gcPadPath, "GCPad1", "Buttons/Z", settings.Z);
        WriteIniValue(gcPadPath, "GCPad1", "Buttons/Start", settings.Start);
        WriteIniValue(gcPadPath, "GCPad1", "Main Stick/Up", settings.MainUp);
        WriteIniValue(gcPadPath, "GCPad1", "Main Stick/Down", settings.MainDown);
        WriteIniValue(gcPadPath, "GCPad1", "Main Stick/Left", settings.MainLeft);
        WriteIniValue(gcPadPath, "GCPad1", "Main Stick/Right", settings.MainRight);
        WriteIniValue(gcPadPath, "GCPad1", "C-Stick/Up", settings.CUp);
        WriteIniValue(gcPadPath, "GCPad1", "C-Stick/Down", settings.CDown);
        WriteIniValue(gcPadPath, "GCPad1", "C-Stick/Left", settings.CLeft);
        WriteIniValue(gcPadPath, "GCPad1", "C-Stick/Right", settings.CRight);
        WriteIniValue(gcPadPath, "GCPad1", "Triggers/L", settings.L);
        WriteIniValue(gcPadPath, "GCPad1", "Triggers/R", settings.R);
        WriteIniValue(gcPadPath, "GCPad1", "Triggers/L-Analog", settings.LAnalog);
        WriteIniValue(gcPadPath, "GCPad1", "Triggers/R-Analog", settings.RAnalog);
        WriteIniValue(gcPadPath, "GCPad1", "D-Pad/Up", settings.DPadUp);
        WriteIniValue(gcPadPath, "GCPad1", "D-Pad/Down", settings.DPadDown);
        WriteIniValue(gcPadPath, "GCPad1", "D-Pad/Left", settings.DPadLeft);
        WriteIniValue(gcPadPath, "GCPad1", "D-Pad/Right", settings.DPadRight);
        WriteIniValue(gcPadPath, "GCPad2", "Device", "DInput/0/Keyboard Mouse");
        WriteIniValue(gcPadPath, "GCPad3", "Device", "DInput/0/Keyboard Mouse");
        WriteIniValue(gcPadPath, "GCPad4", "Device", "DInput/0/Keyboard Mouse");
    }

    private static string GetIniValue(Dictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out var value) ? value : fallback;
    }

    private static bool IsVersionNewer(string candidate, string installed)
    {
        if (TryParseVersion(candidate, out var candidateVersion) && TryParseVersion(installed, out var installedVersion))
        {
            return candidateVersion > installedVersion;
        }

        return !string.Equals(candidate.Trim(), installed.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4)
        {
            version = new Version();
            return false;
        }

        while (parts.Length < 4)
        {
            parts = [.. parts, "0"];
        }

        return Version.TryParse(string.Join(".", parts), out version!);
    }

    private static int? ParseDolphinInteger(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            return hex;
        }

        return int.TryParse(value, out var number) ? number : null;
    }

    private static bool ParseBoolean(string value)
    {
        return bool.TryParse(value, out var result) && result;
    }

    private static string MapGraphicsBackend(string value)
    {
        return value switch
        {
            "Direct3D 11" => "D3D",
            "Direct3D 12" => "D3D12",
            "OpenGL" => "OGL",
            "Vulkan" => "Vulkan",
            "Software Renderer" => "Software Renderer",
            "" => "D3D",
            _ => value
        };
    }

    private static string FormatGraphicsBackend(string value)
    {
        return value switch
        {
            "D3D" => "Direct3D 11",
            "D3D12" => "Direct3D 12",
            "OGL" => "OpenGL",
            "Vulkan" => "Vulkan",
            "Software Renderer" => "Software Renderer",
            _ => string.IsNullOrWhiteSpace(value) ? "Direct3D 11" : value
        };
    }

    private static string MapAspectRatio(string value)
    {
        return value switch
        {
            "Auto" => "0",
            "Force 16:9" => "1",
            "Force 4:3" => "2",
            "Stretch to Window" => "3",
            _ => value
        };
    }

    private static string FormatAspectRatio(string value)
    {
        return value switch
        {
            "0" => "Auto",
            "1" => "Force 16:9",
            "2" => "Force 4:3",
            "3" => "Stretch to Window",
            _ => string.IsNullOrWhiteSpace(value) ? "Auto" : value
        };
    }

    private static string MapInternalResolution(string value)
    {
        return value switch
        {
            "Native (640x528)" => "1",
            "2x Native (1280x1056) for 720p" => "2",
            "3x Native (1920x1584) for 1080p" => "3",
            "4x Native (2560x2112) for 1440p" => "4",
            "5x Native (3200x2640)" => "5",
            "6x Native (3840x3168) for 4K" => "6",
            _ => value
        };
    }

    private static string FormatInternalResolution(string value)
    {
        return value switch
        {
            "1" => "Native (640x528)",
            "2" => "2x Native (1280x1056) for 720p",
            "3" => "3x Native (1920x1584) for 1080p",
            "4" => "4x Native (2560x2112) for 1440p",
            "5" => "5x Native (3200x2640)",
            "6" => "6x Native (3840x3168) for 4K",
            _ => string.IsNullOrWhiteSpace(value) ? "3x Native (1920x1584) for 1080p" : value
        };
    }

    private static (string Msaa, bool Ssaa) MapAntiAliasing(string value)
    {
        return value switch
        {
            "2x MSAA" => ("2", false),
            "4x MSAA" => ("4", false),
            "8x MSAA" => ("8", false),
            "2x SSAA" => ("2", true),
            "4x SSAA" => ("4", true),
            "8x SSAA" => ("8", true),
            _ => ("1", false)
        };
    }

    private static string FormatAntiAliasing(string msaa, bool ssaa)
    {
        if (msaa is "2" or "4" or "8")
        {
            return ssaa ? $"{msaa}x SSAA" : $"{msaa}x MSAA";
        }

        return "None";
    }

    private static string MapTextureFiltering(string value)
    {
        return value switch
        {
            "2x Anisotropic" => "2",
            "4x Anisotropic" => "4",
            "8x Anisotropic" => "8",
            "16x Anisotropic" => "16",
            _ => "0"
        };
    }

    private static string FormatTextureFiltering(string value)
    {
        return value switch
        {
            "2" => "2x Anisotropic",
            "4" => "4x Anisotropic",
            "8" => "8x Anisotropic",
            "16" => "16x Anisotropic",
            _ => "Default"
        };
    }

    private static string MapOutputResampling(string value)
    {
        return value switch
        {
            "Nearest Neighbor" => "1",
            "Bilinear" => "2",
            "Bicubic" => "3",
            _ => "0"
        };
    }

    private static string FormatOutputResampling(string value)
    {
        return value switch
        {
            "1" => "Nearest Neighbor",
            "2" => "Bilinear",
            "3" => "Bicubic",
            _ => "Default"
        };
    }

    private static string MapPostProcessingEffect(string value)
    {
        return value == "(off)" ? "" : value;
    }

    private static string FormatPostProcessingEffect(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(off)" : value;
    }

    private static NetplayLobbyResult OpenNetplayMenu(Process process)
    {
        var handle = WaitForMainWindow(process, TimeSpan.FromSeconds(10));
        if (handle == IntPtr.Zero)
        {
            return new NetplayLobbyResult(false, "Opened Dolphin, but could not find its main window.");
        }

        SetForegroundWindow(handle);
        SendAltAccelerator(KeysT);

        if (TryInvokeNetplayMenuItem())
        {
            var netplayWindow = WaitForNetplayWindowHandle(process, TimeSpan.FromSeconds(5));
            if (netplayWindow == IntPtr.Zero)
            {
                return new NetplayLobbyResult(true, "Opened Dolphin NetPlay lobby.");
            }

            SetForegroundWindow(netplayWindow);
            _ = Task.Run(() => CloseDolphinWhenNetplayCloses(process));
            return new NetplayLobbyResult(true, "Opened NetPlay lobby.");
        }

        return new NetplayLobbyResult(
            false,
            "Opened Dolphin, but could not open NetPlay automatically. Use Tools > Start NetPlay.");
    }

    private static IntPtr WaitForMainWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return IntPtr.Zero;
            }

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            Thread.Sleep(100);
        }

        return IntPtr.Zero;
    }

    private static IntPtr WaitForNetplayWindowHandle(Process process, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return IntPtr.Zero;
            }

            var window = FindTopLevelWindow(process.Id, title => title.Contains("NetPlay", StringComparison.OrdinalIgnoreCase));
            if (window != IntPtr.Zero)
            {
                return window;
            }

            Thread.Sleep(100);
        }

        return IntPtr.Zero;
    }

    private static void CloseDolphinWhenNetplayCloses(Process process)
    {
        try
        {
            var missingSince = (DateTimeOffset?)null;
            while (!process.HasExited)
            {
                var netplayWindow = FindNetplayWindow(process.Id);
                if (netplayWindow != IntPtr.Zero)
                {
                    missingSince = null;
                    Thread.Sleep(250);
                    continue;
                }

                missingSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - missingSince >= TimeSpan.FromSeconds(4))
                {
                    break;
                }

                Thread.Sleep(250);
            }

            if (process.HasExited)
            {
                return;
            }

            if (!process.CloseMainWindow())
            {
                process.Kill(entireProcessTree: true);
                return;
            }

            if (!process.WaitForExit(3000) && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static IntPtr FindNetplayWindow(int processId)
    {
        return FindTopLevelWindow(processId, title => title.Contains("NetPlay", StringComparison.OrdinalIgnoreCase));
    }

    private static IntPtr FindTopLevelWindow(int processId, Func<string, bool> titleMatches)
    {
        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var windowProcessId);
            if (windowProcessId != processId || !IsWindowVisible(window))
            {
                return true;
            }

            var title = GetWindowTitle(window);
            if (!titleMatches(title))
            {
                return true;
            }

            result = window;
            return false;
        }, IntPtr.Zero);

        return result;
    }

    private static string GetWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private static bool TryInvokeNetplayMenuItem()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var menuItems = AutomationElement.RootElement.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

            foreach (AutomationElement item in menuItems)
            {
                var name = item.Current.Name ?? string.Empty;
                if (!name.Contains("NetPlay", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Netplay", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                    && pattern is InvokePattern invokePattern)
                {
                    invokePattern.Invoke();
                    return true;
                }
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static void SendAltAccelerator(byte key)
    {
        const uint keyEventKeyUp = 0x0002;
        keybd_event(KeysMenu, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, keyEventKeyUp, UIntPtr.Zero);
        keybd_event(KeysMenu, 0, keyEventKeyUp, UIntPtr.Zero);
    }

    private static string NormalizeNetplayMode(string mode)
    {
        mode = mode.Trim().ToLowerInvariant();
        return mode switch
        {
            "traversal" or "traversal server" => "traversal",
            "direct" or "direct connection" => "direct",
            _ => mode
        };
    }

    private static string FormatNetplayMode(string mode)
    {
        return NormalizeNetplayMode(mode) switch
        {
            "traversal" => "Traversal server",
            "direct" => "Direct connection",
            "" => "not set",
            var value => value
        };
    }

    private static string FormatDolphinHex(int value)
    {
        return "0x" + value.ToString("x8");
    }

    private const byte KeysMenu = 0x12;
    private const byte KeysT = 0x54;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    private void EnsureDolphinGameDirectory(GameConfig game, EmulatorConfig emulator)
    {
        if (!string.Equals(game.Emulator, "dolphin", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dolphinIniPath = GetDolphinConfigPath(emulator, "Dolphin.ini");
        var gameDirectory = Path.GetDirectoryName(FullPath(game.RelativePath)) ?? Path.Combine(ProjectRoot, "games");

        WriteIniValue(dolphinIniPath, "General", "ISOPaths", "1");
        WriteIniValue(dolphinIniPath, "General", "ISOPath0", gameDirectory);
    }

    private static void WriteIniValue(string path, string sectionName, string key, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();

        var sectionHeader = $"[{sectionName}]";
        var sectionIndex = lines.FindIndex(line => string.Equals(line.Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase));

        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add("");
            }

            lines.Add(sectionHeader);
            lines.Add($"{key} = {value}");
            File.WriteAllLines(path, lines);
            return;
        }

        var insertIndex = sectionIndex + 1;
        for (var i = sectionIndex + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                break;
            }

            insertIndex = i + 1;

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var currentKey = trimmed[..separatorIndex].Trim();
            if (string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key} = {value}";
                File.WriteAllLines(path, lines);
                return;
            }
        }

        lines.Insert(insertIndex, $"{key} = {value}");
        File.WriteAllLines(path, lines);
    }

    private static Dictionary<string, string> ReadIniSection(string path, string sectionName)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = string.Equals(line[1..^1], sectionName, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            values[key] = value;
        }

        return values;
    }

    private static bool IsKeyboardMouse(string device)
    {
        return device.Contains("Keyboard Mouse", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractDolphinDeviceName(string device)
    {
        var parts = device.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts[^1] : device;
    }

    private static bool ControllerNamesMatch(string connectedName, string configuredName)
    {
        return connectedName.Contains(configuredName, StringComparison.OrdinalIgnoreCase)
            || configuredName.Contains(connectedName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindBestControllerMatch(IReadOnlyList<string> connectedControllers, string configuredName)
    {
        return connectedControllers
            .Where(controller => ControllerNamesMatch(controller, configuredName))
            .OrderBy(controller => string.Equals(controller, configuredName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(controller => IsAudioEndpoint(controller) ? 1 : 0)
            .ThenBy(controller => controller.Length)
            .FirstOrDefault();
    }

    private static bool IsAudioEndpoint(string deviceName)
    {
        return deviceName.Contains("headset", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("microphone", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("earphone", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("audio", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateKnownControllerProfile(string controllerName, out string[] profileLines)
    {
        if (controllerName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase)
            || controllerName.Contains("DualShock", StringComparison.OrdinalIgnoreCase)
            || controllerName.Contains("DualSense", StringComparison.OrdinalIgnoreCase))
        {
            profileLines =
            [
                "[GCPad1]",
                "Device = DInput/0/Wireless Controller",
                "Buttons/A = `Button 1`",
                "Buttons/B = `Button 2`",
                "Buttons/X = `Button 0`",
                "Buttons/Y = `Button 3`",
                "Buttons/Z = `Button 5`",
                "Buttons/Start = `Button 9`",
                "Main Stick/Up = `Axis Y-`",
                "Main Stick/Down = `Axis Y+`",
                "Main Stick/Left = `Axis X-`",
                "Main Stick/Right = `Axis X+`",
                "Main Stick/Modifier = `Shift`",
                "Main Stick/Calibration = 100.00 141.42 100.00 141.42 100.00 141.42 100.00 141.42",
                "C-Stick/Up = `Axis Zr-`",
                "C-Stick/Down = `Axis Zr+`",
                "C-Stick/Left = `Axis Z-`",
                "C-Stick/Right = `Axis Z+`",
                "C-Stick/Modifier = `Ctrl`",
                "C-Stick/Calibration = 100.00 141.42 100.00 141.42 100.00 141.42 100.00 141.42",
                "Triggers/L = `Button 4`",
                "Triggers/R = `Button 5`",
                "D-Pad/Up = `Hat 0 N`",
                "D-Pad/Down = `Hat 0 S`",
                "D-Pad/Left = `Hat 0 W`",
                "D-Pad/Right = `Hat 0 E`",
                "Triggers/L-Analog = `Full Axis Xr+`",
                "Triggers/R-Analog = `Full Axis Yr+`",
                "[GCPad2]",
                "Device = DInput/0/Keyboard Mouse",
                "[GCPad3]",
                "Device = DInput/0/Keyboard Mouse",
                "[GCPad4]",
                "Device = DInput/0/Keyboard Mouse"
            ];
            return true;
        }

        profileLines = [];
        return false;
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "config", "games.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find config/games.json in this app directory or its parents.");
    }
}

public sealed record ValidationResult(bool IsOk, string Title, string Detail)
{
    public static ValidationResult Ok(string title, string detail) => new(true, title, detail);

    public static ValidationResult Fail(string title, string detail) => new(false, title, detail);
}

public sealed record LocateGameResult(bool Success, string Message);

public sealed record NetplaySettings(string Mode, string Nickname, int Port);

public sealed record NetplayLobbyResult(bool Success, string Message);

public sealed record ControllerProfileResult(bool Success, string Message);

public sealed record InputCaptureResult(bool Success, string Message, string? Binding);

public sealed record UpdateCheckResult(bool IsUpdateAvailable, string Message, UpdateFeedInfo? Feed);

internal sealed record DiscoveredGameType(string Emulator, string System);

public sealed class AppVersionInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "dev";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "local";

    [JsonPropertyName("packageDate")]
    public string PackageDate { get; set; } = "";

    [JsonPropertyName("updateFeedUrl")]
    public string UpdateFeedUrl { get; set; } = "";

    [JsonPropertyName("releaseNotes")]
    public List<string> ReleaseNotes { get; set; } = [];

    [JsonIgnore]
    public string ReleaseNotesText => ReleaseNotes.Count == 0
        ? "No release notes are included with this package."
        : string.Join(Environment.NewLine, ReleaseNotes.Select(note => "- " + note));

    public static AppVersionInfo Fallback => new();
}

public sealed class UpdateFeedInfo
{
    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("packageUrl")]
    public string PackageUrl { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("releaseNotes")]
    public List<string> ReleaseNotes { get; set; } = [];

    [JsonIgnore]
    public string ReleaseNotesText => ReleaseNotes.Count == 0
        ? "No release notes are included with the update feed."
        : string.Join(Environment.NewLine, ReleaseNotes.Select(note => "- " + note));
}

public sealed class GraphicsSettings
{
    public string Backend { get; set; } = "Direct3D 11";
    public string Adapter { get; set; } = "Auto";
    public string AspectRatio { get; set; } = "Auto";
    public bool VSync { get; set; }
    public bool StartFullscreen { get; set; }
    public string InternalResolution { get; set; } = "3x Native (1920x1584) for 1080p";
    public string AntiAliasing { get; set; } = "None";
    public string TextureFiltering { get; set; } = "Default";
    public string OutputResampling { get; set; } = "Default";
    public bool ColorCorrection { get; set; }
    public string PostProcessingEffect { get; set; } = "(off)";
}

[Flags]
public enum InputCaptureTypes
{
    None = 0,
    Button = 1,
    Axis = 2,
    Hat = 4,
    FullAxis = 8
}

public sealed class ControllerMappingSettings
{
    public string Device { get; set; } = "DInput/0/Wireless Controller";
    public string A { get; set; } = "`Button 1`";
    public string B { get; set; } = "`Button 2`";
    public string X { get; set; } = "`Button 0`";
    public string Y { get; set; } = "`Button 3`";
    public string Z { get; set; } = "`Button 5`";
    public string Start { get; set; } = "`Button 9`";
    public string MainUp { get; set; } = "`Axis Y-`";
    public string MainDown { get; set; } = "`Axis Y+`";
    public string MainLeft { get; set; } = "`Axis X-`";
    public string MainRight { get; set; } = "`Axis X+`";
    public string CUp { get; set; } = "`Axis Zr-`";
    public string CDown { get; set; } = "`Axis Zr+`";
    public string CLeft { get; set; } = "`Axis Z-`";
    public string CRight { get; set; } = "`Axis Z+`";
    public string L { get; set; } = "`Button 4`";
    public string R { get; set; } = "`Button 5`";
    public string LAnalog { get; set; } = "`Full Axis Xr+`";
    public string RAnalog { get; set; } = "`Full Axis Yr+`";
    public string DPadUp { get; set; } = "`Hat 0 N`";
    public string DPadDown { get; set; } = "`Hat 0 S`";
    public string DPadLeft { get; set; } = "`Hat 0 W`";
    public string DPadRight { get; set; } = "`Hat 0 E`";
}

internal static class ControllerDeviceDetector
{
    private const int MaxProductNameLength = 32;
    private const int JoyerrNoError = 0;

    public static IReadOnlyList<string> GetConnectedControllerNames()
    {
        var controllers = new List<string>();
        controllers.AddRange(GetWinMmControllerNames());
        controllers.AddRange(GetPnpControllerNames());

        return controllers
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> GetWinMmControllerNames()
    {
        var controllers = new List<string>();
        var deviceCount = joyGetNumDevs();

        for (uint id = 0; id < deviceCount; id++)
        {
            var info = new JoyInfoEx
            {
                Size = Marshal.SizeOf<JoyInfoEx>(),
                Flags = 0x000000FF
            };

            if (joyGetPosEx(id, ref info) != JoyerrNoError)
            {
                continue;
            }

            var caps = new JoyCaps();
            var capsSize = (uint)Marshal.SizeOf<JoyCaps>();
            if (joyGetDevCaps(id, ref caps, capsSize) == JoyerrNoError && !string.IsNullOrWhiteSpace(caps.ProductName))
            {
                controllers.Add(caps.ProductName);
            }
            else
            {
                controllers.Add($"Controller {id + 1}");
            }
        }

        return controllers;
    }

    private static IReadOnlyList<string> GetPnpControllerNames()
    {
        const string command =
            "$devices = Get-CimInstance Win32_PnPEntity | Where-Object { " +
            "$_.Status -eq 'OK' -and (" +
            "$_.Name -match 'gamepad|xbox|dualsense|dualshock|playstation|wireless controller|pro controller|joy-con|steam deck' -or " +
            "($_.PNPClass -eq 'HIDClass' -and $_.Name -match 'game controller')" +
            ") }; $devices | ForEach-Object { $_.Name }";

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteProcessArgument(command),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process.Start();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }

            if (process.ExitCode != 0)
            {
                return [];
            }

            return process.StandardOutput
                .ReadToEnd()
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    [DllImport("winmm.dll")]
    private static extern uint joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(uint joystickId, ref JoyInfoEx info);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int joyGetDevCaps(uint joystickId, ref JoyCaps caps, uint capsSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public int Size;
        public int Flags;
        public int XPos;
        public int YPos;
        public int ZPos;
        public int RPos;
        public int UPos;
        public int VPos;
        public int Buttons;
        public int ButtonNumber;
        public int Pov;
        public int Reserved1;
        public int Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort Mid;
        public ushort Pid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxProductNameLength)]
        public string ProductName;

        public uint XMin;
        public uint XMax;
        public uint YMin;
        public uint YMax;
        public uint ZMin;
        public uint ZMax;
        public uint NumButtons;
        public uint PeriodMin;
        public uint PeriodMax;
        public uint RMin;
        public uint RMax;
        public uint UMin;
        public uint UMax;
        public uint VMin;
        public uint VMax;
        public uint Caps;
        public uint MaxAxes;
        public uint NumAxes;
        public uint MaxButtons;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxProductNameLength)]
        public string RegKey;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string OemVxD;
    }
}

internal static class DirectInputCapture
{
    public static IReadOnlyList<string> GetControllerNames()
    {
        try
        {
            using var directInput = new DirectInput();
            return directInput
                .GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
                .Select(device => string.IsNullOrWhiteSpace(device.InstanceName) ? device.ProductName : device.InstanceName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static async Task<InputCaptureResult> CaptureNextInputAsync(string? controllerName, TimeSpan timeout, InputCaptureTypes captureTypes)
    {
        try
        {
            using var directInput = new DirectInput();
            var deviceInfo = directInput
                .GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
                .FirstOrDefault(device => ControllerMatches(device, controllerName));

            if (deviceInfo is null || deviceInfo.InstanceGuid == Guid.Empty)
            {
                return new InputCaptureResult(false, "No DirectInput controller matched the selected device.", null);
            }

            using var joystick = new Joystick(directInput, deviceInfo.InstanceGuid);
            joystick.Acquire();

            await WarmUpAsync(joystick);
            var initialState = GetState(joystick);
            var deadline = DateTimeOffset.UtcNow + timeout;
            var axisCaptureStartsAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250);
            string? pendingAxisBinding = null;
            var pendingAxisSamples = 0;

            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(30);
                var currentState = GetState(joystick);
                if (captureTypes.HasFlag(InputCaptureTypes.Button))
                {
                    var buttonCapture = CaptureButton(initialState, currentState);
                    if (buttonCapture is not null)
                    {
                        return buttonCapture;
                    }
                }

                if (captureTypes.HasFlag(InputCaptureTypes.Hat))
                {
                    var hatCapture = CaptureHat(initialState, currentState);
                    if (hatCapture is not null)
                    {
                        return hatCapture;
                    }
                }

                if (captureTypes.HasFlag(InputCaptureTypes.Axis) && DateTimeOffset.UtcNow >= axisCaptureStartsAt)
                {
                    var axisCapture = CaptureAxis(
                        initialState,
                        currentState,
                        captureTypes.HasFlag(InputCaptureTypes.FullAxis));
                    if (axisCapture is null)
                    {
                        pendingAxisBinding = null;
                        pendingAxisSamples = 0;
                    }
                    else if (axisCapture.Binding == pendingAxisBinding)
                    {
                        pendingAxisSamples++;
                        if (pendingAxisSamples >= 3)
                        {
                            return axisCapture;
                        }
                    }
                    else
                    {
                        pendingAxisBinding = axisCapture.Binding;
                        pendingAxisSamples = 1;
                    }
                }
            }

            return new InputCaptureResult(false, "Capture timed out. Press or move a controller input after clicking Capture.", null);
        }
        catch (Exception ex)
        {
            return new InputCaptureResult(false, $"Input capture failed: {ex.Message}", null);
        }
    }

    private static JoystickState GetState(Joystick joystick)
    {
        joystick.Poll();
        return joystick.GetCurrentState();
    }

    private static async Task WarmUpAsync(Joystick joystick)
    {
        for (var i = 0; i < 5; i++)
        {
            GetState(joystick);
            await Task.Delay(30);
        }
    }

    private static InputCaptureResult? CaptureButton(JoystickState initialState, JoystickState currentState)
    {
        var initialButtons = initialState.Buttons;
        var currentButtons = currentState.Buttons;
        var buttonCount = Math.Min(initialButtons.Length, currentButtons.Length);

        for (var i = 0; i < buttonCount; i++)
        {
            if (!initialButtons[i] && currentButtons[i])
            {
                return new InputCaptureResult(true, $"Captured Button {i}.", $"`Button {i}`");
            }
        }

        return null;
    }

    private static InputCaptureResult? CaptureAxis(JoystickState initialState, JoystickState currentState, bool useFullAxisBinding)
    {
        var threshold = useFullAxisBinding ? 3000 : 6000;
        var axes = new[]
        {
            ("X", initialState.X, currentState.X),
            ("Y", initialState.Y, currentState.Y),
            ("Z", initialState.Z, currentState.Z),
            ("Xr", initialState.RotationX, currentState.RotationX),
            ("Yr", initialState.RotationY, currentState.RotationY),
            ("Zr", initialState.RotationZ, currentState.RotationZ)
        };

        foreach (var (name, initialValue, currentValue) in axes)
        {
            var delta = currentValue - initialValue;
            if (Math.Abs(delta) >= threshold)
            {
                var sign = delta < 0 ? "-" : "+";
                var axisPrefix = useFullAxisBinding ? "Full Axis" : "Axis";
                return new InputCaptureResult(true, $"Captured {axisPrefix} {name}{sign}.", $"`{axisPrefix} {name}{sign}`");
            }
        }

        var initialSliders = initialState.Sliders;
        var currentSliders = currentState.Sliders;
        var sliderCount = Math.Min(initialSliders.Length, currentSliders.Length);
        for (var i = 0; i < sliderCount; i++)
        {
            var delta = currentSliders[i] - initialSliders[i];
            if (Math.Abs(delta) >= threshold)
            {
                var sign = delta < 0 ? "-" : "+";
                return new InputCaptureResult(true, $"Captured Slider {i}{sign}.", $"`Slider {i}{sign}`");
            }
        }

        return null;
    }

    private static InputCaptureResult? CaptureHat(JoystickState initialState, JoystickState currentState)
    {
        var initialHats = initialState.PointOfViewControllers;
        var currentHats = currentState.PointOfViewControllers;
        var hatCount = Math.Min(initialHats.Length, currentHats.Length);

        for (var i = 0; i < hatCount; i++)
        {
            if (currentHats[i] < 0 || currentHats[i] == initialHats[i])
            {
                continue;
            }

            var direction = FormatHatDirection(currentHats[i]);
            return new InputCaptureResult(true, $"Captured Hat {i} {direction}.", $"`Hat {i} {direction}`");
        }

        return null;
    }

    private static string FormatHatDirection(int angle)
    {
        var normalized = ((angle % 36000) + 36000) % 36000;
        return normalized switch
        {
            >= 31500 or < 4500 => "N",
            >= 4500 and < 13500 => "E",
            >= 13500 and < 22500 => "S",
            _ => "W"
        };
    }

    private static bool ControllerMatches(DeviceInstance device, string? controllerName)
    {
        if (string.IsNullOrWhiteSpace(controllerName))
        {
            return true;
        }

        var instanceName = device.InstanceName ?? string.Empty;
        var productName = device.ProductName ?? string.Empty;

        return instanceName.Contains(controllerName, StringComparison.OrdinalIgnoreCase)
            || productName.Contains(controllerName, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(instanceName) && controllerName.Contains(instanceName, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(productName) && controllerName.Contains(productName, StringComparison.OrdinalIgnoreCase));
    }
}
