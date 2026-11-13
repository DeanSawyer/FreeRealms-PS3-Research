using System.IO;
using System.Text.Json;

namespace FreeRealms.Bootstrap;

/// <summary>Everything the manager remembers between runs, saved as JSON in %LOCALAPPDATA%\FreeRealmsPS3Server.</summary>
public sealed class ManagerSettings
{
    /// <summary>The manager's folder for settings, traces and logs.</summary>
    public static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeRealmsPS3Server");

    public static readonly string TraceFolder = Path.Combine(DataFolder, "traces");

    /// <summary>The characters made in the game, kept between runs (the console server's default file too).</summary>
    public static readonly string CharacterFile = Path.Combine(DataFolder, "characters.db");

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

    /// <summary>Add the game fixes (Rpcs3\FreeRealms_PS3_fixes.yml) to RPCS3 and turn them on before a test run.</summary>
    public bool InstallPatches { get; set; } = true;

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

    /// <summary>
    /// The game as RPCS3 installs it next to <paramref name="rpcs3Executable"/>: its <c>dev_hdd0\game\NPUA30048</c> folder and the
    /// executable to start (a decrypted <c>game.elf</c> if there is one, else <c>EBOOT.BIN</c>). Each is null when it does not exist.
    /// </summary>
    public static (string? Client, string? Executable) GameInRpcs3(string rpcs3Executable)
    {
        var folder = Path.GetDirectoryName(rpcs3Executable);

        if (string.IsNullOrEmpty(folder))
            return (null, null);

        var client = Path.Combine(folder, "dev_hdd0", "game", "NPUA30048");
        var usrDir = Path.Combine(client, "USRDIR");
        var executable = new[] { Path.Combine(usrDir, "game.elf"), Path.Combine(usrDir, "EBOOT.BIN") }.FirstOrDefault(File.Exists);

        return (Directory.Exists(client) ? client : null, executable);
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

        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
    }
}
