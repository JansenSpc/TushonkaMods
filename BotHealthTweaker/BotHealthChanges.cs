using System.Collections;
using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace BotHealthTweaker;

public class BotHealthConfig
{
    public bool Enabled { get; set; } = true;
    public bool Debug { get; set; }
    public Dictionary<string, BotEntry> Bots { get; set; } = new();
}

public class BotEntry
{
    public bool Enabled { get; set; }

    // Каждый элемент = один пул HP бота: { "Head": 35, "Chest": 85, ... }
    public List<Dictionary<string, double>> BodyParts { get; set; } = new();
}

[Injectable(TypePriority = OnLoadOrder.Preload + 2)]
public class BotHealthChanges(
    ISptLogger<BotHealthChanges> logger,
    BotTable botTable) : IOnLoad
{
    private const string Tag = "[BotHealthTweaker]";
    private bool _debug;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var config = LoadConfig();
        if (config == null)
        {
            logger.Error($"{Tag} No valid config, bot changes skipped.");
            return Task.CompletedTask;
        }

        _debug = config.Debug;
        if (!config.Enabled)
        {
            logger.Info($"{Tag} Disabled in config.");
            return Task.CompletedTask;
        }

        // BotTable -> словарь типов ботов (ищем по типу значения, а не по имени свойства)
        var typesDict = FindDictionary(botTable, typeof(BotType));
        if (typesDict == null)
        {
            logger.Error($"{Tag} Bot types dictionary not found in BotTable, skipped.");
            return Task.CompletedTask;
        }

        var changed = 0;
        foreach (var (botName, entry) in config.Bots)
        {
            if (!entry.Enabled)
            {
                continue;
            }

            var bot = FindBot(typesDict, botName);
            if (bot == null)
            {
                logger.Warning($"{Tag} {botName}: bot type not found, skipped.");
                continue;
            }

            if (ApplyBot(botName, bot, entry))
            {
                changed++;
            }
        }

        logger.Success($"{Tag} Bot health updated ({changed} bot types changed).");
        return Task.CompletedTask;
    }

    private bool ApplyBot(string botName, object bot, BotEntry entry)
    {
        // Свойство BotType, имеющее тип BotTypeHealth (не зависим от его названия)
        var healthProp = bot.GetType().GetProperties().FirstOrDefault(p => p.PropertyType == typeof(BotTypeHealth));
        var health = healthProp?.GetValue(bot) as BotTypeHealth;
        if (health == null)
        {
            logger.Warning($"{Tag} {botName}: health block not found, skipped.");
            return false;
        }

        // Список пулов BodyParts
        var poolsProp = health.GetType().GetProperties().FirstOrDefault(p =>
            p.PropertyType.IsGenericType &&
            p.PropertyType.GetGenericArguments().Length == 1 &&
            p.PropertyType.GetGenericArguments()[0] == typeof(BodyPart));
        var pools = poolsProp?.GetValue(health) as IList;
        if (pools == null || pools.Count == 0)
        {
            logger.Warning($"{Tag} {botName}: BodyParts not found, skipped.");
            return false;
        }

        if (entry.BodyParts.Count == 0)
        {
            logger.Warning($"{Tag} {botName}: BodyParts is empty in config, skipped.");
            return false;
        }

        if (entry.BodyParts.Count != 1 && entry.BodyParts.Count != pools.Count)
        {
            logger.Warning($"{Tag} {botName}: config has {entry.BodyParts.Count} pools, game has {pools.Count}. Applying by order up to the shorter list.");
        }

        for (var i = 0; i < pools.Count; i++)
        {
            // Один элемент в конфиге = применяется ко всем пулам бота
            var source = entry.BodyParts.Count == 1 ? entry.BodyParts[0] : i < entry.BodyParts.Count ? entry.BodyParts[i] : null;
            if (source == null || pools[i] == null)
            {
                continue;
            }

            foreach (var (partName, hp) in source)
            {
                SetPart(botName, i, pools[i]!, partName, hp);
            }
        }

        return true;
    }

    private void SetPart(string botName, int pool, object bodyPart, string partName, double hp)
    {
        if (hp <= 0)
        {
            logger.Warning($"{Tag} {botName}[{pool}].{partName}: value {hp} must be > 0, skipped.");
            return;
        }

        var partProp = bodyPart.GetType().GetProperty(partName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (partProp == null)
        {
            logger.Warning($"{Tag} {botName}[{pool}]: body part '{partName}' not found, skipped.");
            return;
        }

        var minMax = partProp.GetValue(bodyPart);
        if (minMax == null)
        {
            minMax = Activator.CreateInstance(partProp.PropertyType);
            partProp.SetValue(bodyPart, minMax);
        }

        // Фиксированное значение: min = max = hp
        var min = SetNumber(minMax!, "Min", hp);
        var max = SetNumber(minMax!, "Max", hp);
        if (!min || !max)
        {
            logger.Warning($"{Tag} {botName}[{pool}].{partName}: Min/Max properties not found, skipped.");
            return;
        }

        if (_debug)
        {
            logger.Info($"{Tag} {botName}[{pool}].{partName} = {hp}");
        }
    }

    private static bool SetNumber(object target, string propName, double value)
    {
        var prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop == null || !prop.CanWrite)
        {
            return false;
        }

        var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        prop.SetValue(target, Convert.ChangeType(value, type));
        return true;
    }

    private static IDictionary? FindDictionary(object owner, Type valueType)
    {
        foreach (var prop in owner.GetType().GetProperties())
        {
            var t = prop.PropertyType;
            if (t.IsGenericType && t.GetGenericArguments().Length == 2 && t.GetGenericArguments()[1] == valueType)
            {
                return prop.GetValue(owner) as IDictionary;
            }
        }

        return null;
    }

    private static object? FindBot(IDictionary dict, string name)
    {
        foreach (DictionaryEntry kv in dict)
        {
            if (string.Equals(kv.Key.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value;
            }
        }

        return null;
    }

    private BotHealthConfig? LoadConfig()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var path = System.IO.Path.Combine(dir, "config.json");
            if (!File.Exists(path))
            {
                logger.Error($"{Tag} config.json not found at {path}");
                return null;
            }

            return JsonSerializer.Deserialize<BotHealthConfig>(File.ReadAllText(path), new JsonSerializerOptions
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
}
