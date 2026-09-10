using System;
using System.IO;
using System.Text.Json;
using TapRunner.App.Models;

namespace TapRunner.App.Services
{
    /// <summary>Loads/saves the TapRunner JSON configuration file.</summary>
    public class ConfigService
    {
        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public string ConfigFilePath { get; }

        public ConfigService()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TapRunner");
            ConfigFilePath = Path.Combine(dir, "config.json");
        }

        public AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                    if (config != null)
                        return config;
                }
            }
            catch (Exception)
            {
                // Corrupt/unreadable config - fall back to defaults rather than crashing startup.
            }

            return new AppConfig();
        }

        public void Save(AppConfig config)
        {
            var dir = Path.GetDirectoryName(ConfigFilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigFilePath, json);
        }
    }
}
