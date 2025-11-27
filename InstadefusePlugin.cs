using System;
using System.Linq;
using System.Data;
using MySqlConnector;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using InstadefusePlugin.Modules;

namespace InstadefusePlugin;

[MinimumApiVersion(220)]
public class InstadefusePlugin : BasePlugin
{
    private const string Version = "1.0.0";
    
    public override string ModuleName => "Instadefuse Plugin";
    public override string ModuleVersion => Version;
    public override string ModuleAuthor => "ketmode";
    public override string ModuleDescription => "https://github.com/mariostnc/instadefuse-cs2";

    public static readonly string LogPrefix = $"[Instadefuse {Version}] ";
    public static string MessagePrefix = $"[{ChatColors.Green}Retakes{ChatColors.White}] ";

    private float _bombPlantedTime = float.NaN;
    private bool _bombTicking;

    private Translator _translator;
    // Paste DB details below, one per line. Fill the values between the quotes.
    // Example MySQL host/port: host = "45.76.85.9"  port = "3306"
    private readonly string DbHost = "";      // host or IP
    private readonly string DbPort = "3306";            // port number
    private readonly string DbUser = "";  // username
    private readonly string DbPassword = ""; // password
    private readonly string DbName = "";        // database name

    private readonly string _vipDbConnectionString;

    public InstadefusePlugin()
    {
        _translator = new Translator(Localizer);

        // Built connection string from fields above. Edit those fields if needed.
        _vipDbConnectionString = $"server={DbHost};port={DbPort};uid={DbUser};pwd={DbPassword};database={DbName};";
    }
    
    public override void Load(bool hotReload)
    {
        _translator = new Translator(Localizer);
        
        Console.WriteLine($"{LogPrefix}Plugin loaded!");
        
        MessagePrefix = T("instadefuse.prefix");
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        Console.WriteLine($"{LogPrefix}OnRoundStart");
        
        _bombPlantedTime = float.NaN;
        _bombTicking = false;

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        Console.WriteLine($"{LogPrefix}OnBombPlanted");
        
        _bombPlantedTime = Server.CurrentTime;
        _bombTicking = true;

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        Console.WriteLine($"{LogPrefix}OnBombBeginDefuse");

        var player = @event.Userid;

        if (player != null && player.IsValid && player.PawnIsAlive)
        {
            // Try instadefuse, but always let the event continue
            // so normal defuse can happen if player is not VIP
            Server.NextFrame(() => AttemptInstadefuse(player));
        }

        // IMPORTANT: Always return Continue to allow normal defuse
        return HookResult.Continue;
    }

    private void AttemptInstadefuse(CCSPlayerController defuser)
    {
        Console.WriteLine($"{LogPrefix}Attempting instadefuse...");

        // Check VIP level in database (requires a table `vip_users` with columns `steamid64` and `vip_level`).
        var steamIdObj = defuser.SteamID;
        var steamIdStr = steamIdObj.ToString();
        
        if (string.IsNullOrEmpty(steamIdStr) || steamIdStr == "0")
        {
            Console.WriteLine($"{LogPrefix}Could not determine steam_id for player {defuser.PlayerName}.");
            // Let normal defuse continue - don't block it
            return;
        }

        // Check VIP status - only apply instadefuse for VIP level 2
        bool isVip2 = false;
        try
        {
            isVip2 = IsPlayerVipLevel2(steamIdStr);
            Console.WriteLine($"{LogPrefix}Player {defuser.PlayerName} ({steamIdStr}) VIP level 2 status: {isVip2}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{LogPrefix}VIP DB check failed: {ex.Message}");
            // On DB error, let normal defuse continue
            return;
        }

        // If not VIP level 2, let normal defuse happen
        if (!isVip2)
        {
            Console.WriteLine($"{LogPrefix}Player {defuser.PlayerName} is not VIP level 2. Normal defuse will proceed.");
            return;
        }

        // Player IS VIP level 2, apply instadefuse logic
        if (!_bombTicking)
        {
            Console.WriteLine($"{LogPrefix}Bomb is not planted!");
            return;
        }

        var plantedBomb = FindPlantedBomb();
        if (plantedBomb == null)
        {
            Console.WriteLine($"{LogPrefix}Planted bomb is null!");
            return;
        }

        if (plantedBomb.CannotBeDefused)
        {
            Console.WriteLine($"{LogPrefix}Planted bomb can not be defused!");
            return;
        }

        var bombTimeUntilDetonation = plantedBomb.TimerLength - (Server.CurrentTime - _bombPlantedTime);

        var defuseLength = plantedBomb.DefuseLength;
        if (defuseLength != 5 && defuseLength != 10)
        {
            defuseLength = defuser.PawnHasDefuser ? 5.0f : 10.0f;
        }
        Console.WriteLine($"{LogPrefix}DefuseLength: {defuseLength}");

        var timeLeftAfterDefuse = bombTimeUntilDetonation - defuseLength;
        var bombCanBeDefusedInTime = timeLeftAfterDefuse >= 0.0f;

        if (!bombCanBeDefusedInTime)
        {
            Server.PrintToChatAll(MessagePrefix + T("instadefuse.unsuccessful", defuser.PlayerName, $"{Math.Abs(timeLeftAfterDefuse):n3}"));

            Server.NextFrame(() =>
            {
                plantedBomb = FindPlantedBomb();
                
                if (plantedBomb == null)
                {
                    Console.WriteLine($"{LogPrefix}Planted bomb is null!");
                    return;
                }
                
                plantedBomb.C4Blow = 1.0f;
            });

            return;
        }

        Server.NextFrame(() =>
        {
            plantedBomb = FindPlantedBomb();
            
            if (plantedBomb == null)
            {
                Console.WriteLine($"{LogPrefix}Planted bomb is null!");
                return;
            }
            
            plantedBomb.DefuseCountDown = 0;

            Server.PrintToChatAll(MessagePrefix + T("instadefuse.successful", defuser.PlayerName, $"{Math.Abs(bombTimeUntilDetonation):n3}"));
        });
    }

    private string T(string key, params object[] args)
    {
        string res;
        try
        {
            res = _translator[key];
        }
        catch
        {
            res = key;
        }

        if (string.IsNullOrEmpty(res) || res.Contains(key))
        {
            // Fallback messages when localization is missing
            res = key switch
            {
                "instadefuse.prefix" => "[Retakes] ",
                "instadefuse.no_steamid" => "Could not determine SteamID for {0}.",
                "instadefuse.not_vip" => "You need VIP level 2 to use instant defuse.",
                "instadefuse.db_error" => "VIP check failed (DB error). Contact the server admin.",
                "instadefuse.unsuccessful" => "{0} was [DARK_RED]{1} seconds[WHITE] away from defusing.",
                "instadefuse.successful" => "{0} defused with [GREEN]{1} seconds[WHITE] left on the bomb.",
                _ => key
            };
        }

        if (args != null && args.Length > 0)
        {
            try { res = string.Format(res, args); } catch { }
        }

        return res;
    }

    private bool IsPlayerVipLevel2(string steam_id)
    {
        using var conn = new MySqlConnection(_vipDbConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        // `group` is a reserved/ambiguous word in SQL — escape identifiers with backticks.
        cmd.CommandText = "SELECT `group` FROM `deadswim_users` WHERE `steam_id` = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", steam_id);

        var result = cmd.ExecuteScalar();
        
        try
        {
            // If no result or null, player has no VIP
            if (result == null || result == DBNull.Value)
            {
                Console.WriteLine($"{LogPrefix}VIP lookup for {steam_id}: No VIP record found");
                return false;
            }

            var raw = result.ToString();
            Console.WriteLine($"{LogPrefix}VIP lookup for {steam_id} returned: '{raw}'");

            // Try to parse as integer
            if (int.TryParse(raw, out var level))
            {
                Console.WriteLine($"{LogPrefix}Parsed VIP level: {level}. Required: 2. Match: {level == 2}");
                // STRICT check: Only level 2 exactly
                return level == 2;
            }

            // If it's a string like "vip2" or "level2", try to extract the number
            if (raw.Length > 0)
            {
                var digits = new string(raw.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var extractedLevel))
                {
                    Console.WriteLine($"{LogPrefix}Extracted VIP level from '{raw}': {extractedLevel}. Required: 2. Match: {extractedLevel == 2}");
                    return extractedLevel == 2;
                }
            }

            // If result isn't an int or extractable number, log and return false
            Console.WriteLine($"{LogPrefix}VIP lookup returned non-integer value that couldn't be parsed: '{raw}'");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{LogPrefix}Error while parsing VIP lookup result: {ex}");
            return false;
        }
    }

    private static CPlantedC4? FindPlantedBomb()
    {
        var plantedBombList = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").ToList();

        if (plantedBombList.Any())
        {
            return plantedBombList.FirstOrDefault();
        }
        
        Console.WriteLine($"{LogPrefix}No planted bomb entities have been found!");
        return null;
    }

}
