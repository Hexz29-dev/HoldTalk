using System.Text.Json;

namespace Holdtalk;

public sealed class Settings
{
    public string FfmpegPath { get; set; } = "tools/ffmpeg/bin/ffmpeg.exe";
    public string TranscribeLibraryPath { get; set; } = "tools/transcribe-vulkan/transcribe.dll";
    public string ModelPath { get; set; } = "models/cohere-transcribe-03-2026-Q4_K_M.gguf";
    public string Microphone { get; set; } = "";
    public string DictationKey { get; set; } = "RControlKey";
    public string CleanupKey { get; set; } = "F8";
    public string Language { get; set; } = "pt";
    public static readonly string[] SupportedLanguages = ["pt", "en", "fr", "de", "es", "it", "nl", "pl", "el", "ar", "ja", "zh", "vi", "ko"];
    public int MaxRecordingSeconds { get; set; } = 300;
    public int TranscriptionTimeoutSeconds { get; set; } = 300;
    public int ClipboardRestoreDelayMs { get; set; } = 500;

    public static string FindAppDirectory(string executableDirectory)
    {
        for (var parent = new DirectoryInfo(executableDirectory); parent is not null; parent = parent.Parent)
        {
            if (!File.Exists(Path.Combine(parent.FullName, "src", "Holdtalk.csproj"))) continue;
            string buildDirectory = Path.Combine(parent.FullName, "src", "bin") + Path.DirectorySeparatorChar;
            string preparedDirectory = Path.Combine(parent.FullName, "dist", "Holdtalk");
            if (Path.GetFullPath(executableDirectory).StartsWith(buildDirectory, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(preparedDirectory, "config.json"))) return preparedDirectory;
        }
        return executableDirectory;
    }

    public static Settings Load(string directory)
    {
        string path = Path.Combine(directory, "config.json");
        if (!File.Exists(path)) File.Copy(Path.Combine(directory, "config.example.json"), path);
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("config.json must contain an object.");
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FfmpegPath) || string.IsNullOrWhiteSpace(TranscribeLibraryPath) ||
            string.IsNullOrWhiteSpace(ModelPath) || string.IsNullOrWhiteSpace(Language))
            throw new InvalidDataException("Executable paths, modelPath and language must not be empty.");
        if (!SupportedLanguages.Contains(Language))
            throw new InvalidDataException("Cohere requires an explicit supported language (for example pt or en); auto is not supported.");
        if (MaxRecordingSeconds > 400)
            throw new InvalidDataException("Cohere accepts at most 400 seconds of audio per recording.");
        if (ParseKey(DictationKey) == ParseKey(CleanupKey))
            throw new InvalidDataException("The two hotkeys must be different.");
        if (MaxRecordingSeconds is < 1 or > 3600 || TranscriptionTimeoutSeconds is < 1 or > 3600 ||
            ClipboardRestoreDelayMs is < 100 or > 5000)
            throw new InvalidDataException("Timeouts must be 1–3600 seconds; clipboard delay must be 100–5000 ms.");
    }

    public void Save(string directory)
    {
        Validate();
        string path = Path.Combine(directory, "config.json");
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static Keys ParseKey(string value)
    {
        if (Enum.TryParse<Keys>(value, true, out var key) && Enum.IsDefined(key) &&
            (key is Keys.RControlKey or Keys.LControlKey or Keys.RShiftKey or Keys.LShiftKey ||
             key is >= Keys.F1 and <= Keys.F24)) return key;
        throw new InvalidDataException($"Unsupported hotkey '{value}'. Use RControlKey, LControlKey, RShiftKey, LShiftKey or F1–F24.");
    }

    public static string ResolvePath(string directory, string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), directory);

    public static Dictionary<string, string> ReadEnv(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return values;
        foreach (string line in File.ReadLines(path))
        {
            string value = line.Trim();
            if (value.Length == 0 || value.StartsWith('#')) continue;
            int equals = value.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException("Invalid .env line; expected NAME=value.");
            string name = value[..equals].Trim();
            value = value[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            values[name] = value;
        }
        return values;
    }
}
