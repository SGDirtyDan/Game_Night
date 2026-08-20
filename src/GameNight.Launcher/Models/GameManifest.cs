using System.Text.Json.Serialization;

namespace GameNight.Launcher.Models;

public sealed class GameManifest
{
    [JsonPropertyName("emulators")]
    public Dictionary<string, EmulatorConfig> Emulators { get; set; } = new();

    [JsonPropertyName("games")]
    public List<GameConfig> Games { get; set; } = new();
}

public sealed class EmulatorConfig
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("executable")]
    public string Executable { get; set; } = "";

    [JsonPropertyName("portableMarker")]
    public string PortableMarker { get; set; } = "";
}

public sealed class GameConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("system")]
    public string System { get; set; } = "";

    [JsonPropertyName("emulator")]
    public string Emulator { get; set; } = "";

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("compatibilityProfile")]
    public string? CompatibilityProfile { get; set; }

    [JsonPropertyName("gameTdbId")]
    public string? GameTdbId { get; set; }

    [JsonIgnore]
    public bool IsDiscovered { get; set; }
}
