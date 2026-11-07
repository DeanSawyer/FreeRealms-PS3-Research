using System.IO;
using System.Text.Json;

namespace FreeRealms.Bootstrap;

/// <summary>Everything the manager remembers between runs, saved as JSON in %LOCALAPPDATA%\FreeRealmsPS3Server.</summary>
public sealed class ManagerSettings
{
    /// <summary>The manager's folder for settings, traces and logs.</summary>
    public static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeRealmsPS3Server");

    public static readonly string TraceFolder = Path.Combine(DataFolder, "traces");

    private static readonly string SettingsPath = Path.Combine(DataFolder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // --- Client and RPCS3 ---------------------------------------------------------------------------------------------

    /// <summary>The NPUA30048 folder of the installed client.</summary>
    public string ClientFolder { get; set; } = "";

    /// <summary>A second NPUA30048 folder searched for files whose CRC32 matches; optional.</summary>
    public string ExtraClientFolder { get; set; } = "";

    /// <summary>The original patch digest; optional (default: the client's own USRDIR\Digests\NPUA30048.soe).</summary>
    public string OriginalDigest { get; set; } = "";

    /// <summary>Serve a digest that describes the local files (needed for a modified or partial install).</summary>
    public bool MatchLocal { get; set; } = true;

    public string Rpcs3Executable { get; set; } = "";

    public string GameElf { get; set; } = "";

    /// <summary>Put the original ui\scriptsbase.bin.sdat back before a test run when a patched copy is live.</summary>
    public bool RestoreScripts { get; set; } = true;

    // --- Network ------------------------------------------------------------------------------------------------------

    public string Bind { get; set; } = "127.0.0.1";

    public string Advertise { get; set; } = "127.0.0.1";

    public int HttpPort { get; set; } = 80;

    public int GatewayPort { get; set; } = 20260;

    public bool Dns { get; set; } = true;

    // --- Gameplay -----------------------------------------------------------------------------------------------------

    /// <summary>Send what the PC server Sanctuary sends and answer the inventory and job switches (FR_SANCTUARY).</summary>
    public bool SanctuaryFlow { get; set; } = true;

    /// <summary>Give the character Sanctuary's starter items (FR_ITEMS).</summary>
    public bool StarterItems { get; set; } = true;

    /// <summary>"all" or a list of job ids (FR_JOBS).</summary>
    public string Jobs { get; set; } = "all";

    /// <summary>The sky named in ZoneDetails (FR_SKY); empty sends none, which crashes the client when it looks up.</summary>
    public string Sky { get; set; } = "sky_wilds24.xml";

    /// <summary>How the creator's clothes are put on: 7 EquippedItemsChange, 5 EquipItem per piece, 0 not at all (FR_EQUIP).</summary>
    public int ClothesPacket { get; set; } = 7;

    /// <summary>Empty for Sanctuary's spawn, "local" for a tile from the packs, or "x,y,z" (FR_SPAWN).</summary>
    public string Spawn { get; set; } = "";

    public bool WelcomeScreen { get; set; } = true;

    public bool GameTime { get; set; } = true;

    /// <summary>Picks the paths the test setup uses (Desktop\FR_INSTALL_TEST) when they exist and nothing is set yet.</summary>
    public void FillDefaults()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var install = Path.Combine(home, "Desktop", "FR_INSTALL_TEST");

        ClientFolder = Pick(ClientFolder, Path.Combine(install, "NPUA30048"), Directory.Exists);
        Rpcs3Executable = Pick(Rpcs3Executable, Path.Combine(install, "RPCS3_TEST", "rpcs3.exe"), File.Exists);
        GameElf = Pick(GameElf, Path.Combine(install, "RPCS3_TEST", "dev_hdd0", "game", "NPUA30048", "USRDIR", "game.elf"), File.Exists);
        ExtraClientFolder = Pick(ExtraClientFolder, Path.Combine(home, "Downloads", "Free-Realms-PS3-main", "Free-Realms-PS3-main", "NPUA30048"), Directory.Exists);
        OriginalDigest = Pick(OriginalDigest, Path.Combine(home, "Downloads", "NPUA30048.original.soe"), File.Exists);
    }

    /// <summary>The FR_* switches the server reads, from the gameplay settings.</summary>
    public Dictionary<string, string> GatewaySwitches() => new(StringComparer.Ordinal)
    {
        ["FR_SANCTUARY"] = SanctuaryFlow ? "1" : "0",
        ["FR_ITEMS"] = StarterItems ? "1" : "0",
        ["FR_JOBS"] = Jobs,
        ["FR_SKY"] = Sky,
        ["FR_EQUIP"] = ClothesPacket.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["FR_SPAWN"] = Spawn,
        ["FR_WELCOME"] = WelcomeScreen ? "1" : "0",
        ["FR_TIME"] = GameTime ? "1" : "0",

        // As the old test script did: the extra packets with the basic flow, none with the Sanctuary flow (which replaces them).
        ["FR_EXTRA"] = SanctuaryFlow ? "0" : "1",
    };

    public static ManagerSettings Load()
    {
        ManagerSettings settings;

        try
        {
            settings = File.Exists(SettingsPath) ? JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(SettingsPath)) ?? new() : new();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            settings = new();
        }

        settings.FillDefaults();

        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
    }

    private static string Pick(string current, string candidate, Func<string, bool> exists) =>
        string.IsNullOrWhiteSpace(current) && exists(candidate) ? candidate : current;
}
