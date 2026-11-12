using System.Text.Json;

namespace Sevencat.Core.Infrastructure;

/// <summary>配置读写：JSON + 原子替换。读失败返回默认配置并保留原文件，不因配置损坏导致无法启动。</summary>
public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string FilePath { get; } = filePath;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string directory = Path.GetDirectoryName(FilePath) ?? AppPaths.ConfigDirectory;
        Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(settings, Options);
        string temp = FilePath + ".tmp";

        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }
}
