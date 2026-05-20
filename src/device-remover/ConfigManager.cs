using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceRemover
{
    public partial class AliasConfig
    {
        public string? Description { get; set; }
        public string MatchField { get; set; } = "Any"; // Any, InstanceId, HardwareId, FriendlyName
        public string MatchMode { get; set; } = "Contains"; // Contains, Exact, Regex, Wildcard
        public string Value { get; set; } = string.Empty;
    }

    public class ProfileConfig
    {
        public string? Description { get; set; }
        public Dictionary<string, string> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class AppConfig
    {
        public Dictionary<string, AliasConfig> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ProfileConfig> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ActiveProfile { get; set; }
    }

    // Custom converter to support: "alias": "value" AND "alias": { "Value": "value", ... }
    public class AliasConfigConverter : JsonConverter<AliasConfig>
    {
        public override AliasConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return new AliasConfig
                {
                    Value = reader.GetString() ?? string.Empty,
                    MatchField = "Any",
                    MatchMode = "Contains"
                };
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var config = new AliasConfig();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        return config;
                    }

                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        string propName = reader.GetString() ?? string.Empty;
                        reader.Read();

                        if (string.Equals(propName, "Description", StringComparison.OrdinalIgnoreCase))
                        {
                            config.Description = reader.GetString();
                        }
                        else if (string.Equals(propName, "MatchField", StringComparison.OrdinalIgnoreCase))
                        {
                            config.MatchField = reader.GetString() ?? "Any";
                        }
                        else if (string.Equals(propName, "MatchMode", StringComparison.OrdinalIgnoreCase))
                        {
                            config.MatchMode = reader.GetString() ?? "Contains";
                        }
                        else if (string.Equals(propName, "Value", StringComparison.OrdinalIgnoreCase))
                        {
                            config.Value = reader.GetString() ?? string.Empty;
                        }
                    }
                }
            }

            throw new JsonException("Expected string or object for AliasConfig");
        }

        public override void Write(Utf8JsonWriter writer, AliasConfig value, JsonSerializerOptions options)
        {
            // If it's a simple alias with no description and default match rules, write as a compact string
            if (string.IsNullOrEmpty(value.Description) &&
                string.Equals(value.MatchField, "Any", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(value.MatchMode, "Contains", StringComparison.OrdinalIgnoreCase))
            {
                writer.WriteStringValue(value.Value);
            }
            else
            {
                writer.WriteStartObject();
                if (value.Description != null)
                {
                    writer.WriteString("Description", value.Description);
                }
                writer.WriteString("MatchField", value.MatchField);
                writer.WriteString("MatchMode", value.MatchMode);
                writer.WriteString("Value", value.Value);
                writer.WriteEndObject();
            }
        }
    }

    // Register converter on type definition so Native AOT Source Generator compiles it
    [JsonConverter(typeof(AliasConfigConverter))]
    public partial class AliasConfig { }

    // Native AOT Source Generator JSON Context
    [JsonSourceGenerationOptions(
        WriteIndented = true, 
        PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(AppConfig))]
    internal partial class AppConfigJsonContext : JsonSerializerContext
    {
    }

    public static class ConfigManager
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
            "device-remover"
        );

        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        public static AppConfig LoadConfig()
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }

                if (!File.Exists(ConfigPath))
                {
                    var defaultConfig = CreateDefaultConfig();
                    SaveConfig(defaultConfig);
                    return defaultConfig;
                }

                string json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                return config ?? CreateDefaultConfig();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[!] Ошибка загрузки конфига, сброс на дефолтный: {ex.Message}");
                return CreateDefaultConfig();
            }
        }

        public static void SaveConfig(AppConfig config)
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }

                string json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[!] Ошибка сохранения конфига: {ex.Message}");
            }
        }

        public static string GetConfigPath() => ConfigPath;

        private static AppConfig CreateDefaultConfig()
        {
            var config = new AppConfig
            {
                Aliases = new Dictionary<string, AliasConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    {
                        "keyboard", new AliasConfig
                        {
                            Description = "Встроенная клавиатура ноутбука (обычно PS/2)",
                            MatchField = "InstanceId",
                            MatchMode = "Contains",
                            Value = "ACPI\\PNP0303"
                        }
                    },
                    {
                        "touchpad", new AliasConfig
                        {
                            Description = "Встроенный тачпад ноутбука",
                            MatchField = "HardwareId",
                            MatchMode = "Contains",
                            Value = "VID_04F3&PID_304B" // Пример ID
                        }
                    }
                },
                Profiles = new Dictionary<string, ProfileConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    {
                        "docked", new ProfileConfig
                        {
                            Description = "Подключены внешняя клавиатура и мышь. Встроенные отключены.",
                            Devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                { "keyboard", "Disabled" },
                                { "touchpad", "Disabled" }
                            }
                        }
                    },
                    {
                        "mobile", new ProfileConfig
                        {
                            Description = "Режим в дороге. Встроенные устройства включены.",
                            Devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                { "keyboard", "Enabled" },
                                { "touchpad", "Enabled" }
                            }
                        }
                    }
                },
                ActiveProfile = "mobile"
            };

            return config;
        }
    }
}
