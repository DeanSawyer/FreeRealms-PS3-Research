using System.Text;

namespace Sanctuary.Ps3.Web;

/// <summary>Host names and routes the PS3 client uses to find its (former) servers.</summary>
public static class Ps3Routes
{
    public const string AppConfig = "/app/appconfig.php";

    public const string DigestRoute = "/patch/freerealms/ps3scelive/ps3-web/FreeRealms.soe";

    public const string PatchPrefix = "/patch/freerealms/ps3scelive/ps3-web/";

    public const string WebServicePrefix = "/ps3ws/";

    /// <summary>Host names the DNS stand-in sends to this machine.</summary>
    public static readonly string[] Hosts =
    [
        "lpconfig.station.sony.com",
        "frps3.patch.station.sony.com",
        "ps3.freerealms.com",
        "fr.patch.station.sony.com", // clientconfig.ini [AssetDelivery]
    ];
}

/// <summary>The AppConfig document, in the newlines and tag order of the recovered 2026-09-19 script.</summary>
public static class AppConfigDocument
{
    public static byte[] Build(string ip)
    {
        string[] lines =
        [
            "<AppConfig>",
            "<Login>",
            $"<URL type=\"access\">http://{ip}/LaunchPad/lpadAPI/login_v2</URL>",
            "</Login>",
            "<GameList>",
            "<Game env=\"live\">",
            "<Labels/>",
            "<Versions>",
            "<Version id=\"scea-live-tails\">",
            $"<Digest>http://frps3.patch.station.sony.com{Ps3Routes.DigestRoute}</Digest>",
            "</Version>",
            "</Versions>",
            "</Game>",
            "</GameList>",
            "</AppConfig>",
        ];

        return new UTF8Encoding(false).GetBytes(string.Join("\n", lines));
    }
}
