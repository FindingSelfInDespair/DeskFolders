using System;
using System.IO;
using System.Text.Json;
using DeskFolders.Models;

namespace DeskFolders.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _sync = new();

    public string ConfigPath { get; }
    public bool ConfigExistedOnStartup { get; }

    public ConfigService()
    {
        ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        ConfigExistedOnStartup = File.Exists(ConfigPath);
    }

    public AppConfig Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return new AppConfig();

                string json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
            catch
            {
                return new AppConfig();
            }
        }
    }

    public void Save(AppConfig config)
    {
        lock (_sync)
        {
            string tempPath = ConfigPath + ".tmp";
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
    }
}