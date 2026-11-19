using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace ArmorDurabilityMultiplier;

public class ArmorConfig
{
    public bool Enabled { get; set; } = true;
    public bool Debug { get; set; }
    public Dictionary<string, double> ArmorMaterials { get; set; } = new();
}

[Injectable(TypePriority = OnLoadOrder.Preload + 2)]
public class ArmorChanges(
    ISptLogger<ArmorChanges> logger,
    GlobalTable globalTable) : IOnLoad
{
    private const string Tag = "[ArmorDurabilityMultiplier]";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var config = LoadConfig();
        if (config == null)
        {
            logger.Error($"{Tag} No valid config, armor changes skipped.");
            return Task.CompletedTask;
        }

        if (!config.Enabled)
        {
            logger.Info($"{Tag} Disabled in config.");
            return Task.CompletedTask;
        }

        var materials = globalTable.Configuration.ArmorMaterials;
        if (materials == null)
        {
            logger.Error($"{Tag} ArmorMaterials not found in Globals, skipped.");
            return Task.CompletedTask;
        }

        var applied = 0;
        foreach (var (name, multiplier) in config.ArmorMaterials)
        {
            if (multiplier <= 0)
            {
                logger.Warning($"{Tag} {name}: multiplier {multiplier} must be > 0, skipped.");
                continue;
            }

            // Ищем материал без учёта регистра (ключ может быть enum или строкой)
            var armor = materials
                .Where(kv => string.Equals(kv.Key.ToString(), name, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value)
                .FirstOrDefault();
            if (armor == null)
            {
                logger.Warning($"{Tag} {name}: material not found in Globals, skipped.");
                continue;
            }

            // Мультипликатор в прямом порядке: 3.0 = в три раза больше прочности,
            // значит Destructibility (скорость потери прочности) делим на множитель.
            if (armor.Destructibility is double d)
            {
                armor.Destructibility = d / multiplier;
            }

            if (armor.ExplosionDestructibility is double e)
            {
                armor.ExplosionDestructibility = e / multiplier;
            }

            applied++;
            Debug(config, $"{name}: x{multiplier} -> Destructibility {armor.Destructibility}, Explosion {armor.ExplosionDestructibility}");
        }

        logger.Success($"{Tag} Armor materials updated ({applied} changed).");
        return Task.CompletedTask;
    }

    private ArmorConfig? LoadConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var path = Path.Combine(dir, "config.json");
            if (!File.Exists(path))
            {
                logger.Error($"{Tag} config.json not found at {path}");
                return null;
            }

            return JsonSerializer.Deserialize<ArmorConfig>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception ex)
        {
            logger.Error($"{Tag} Failed to read config.json: {ex.Message}");
            return null;
        }
    }

    private void Debug(ArmorConfig config, string message)
    {
        if (config.Debug)
        {
            logger.Info($"{Tag} {message}");
        }
    }
}
