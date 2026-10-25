using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sanctuary.Ps3.Web;

/// <summary>How simple values (Status, Name, Online, ...) are written in a ps3ws reply.</summary>
public enum XmlStyle
{
    /// <summary>Attributes of the current element. The client's reader has this mode switched on, so it is the default.</summary>
    Attributes,

    /// <summary>Child elements (the earlier form).</summary>
    Elements,
}

/// <summary>The replies the stub answers; each <c>/ps3ws/...action</c> route maps to one (anything else gets <see cref="Generic"/>).</summary>
public enum Ps3ReplyKind
{
    ServerList,
    AccountInfo,
    CheckName,
    CharacterCreate,
    CharacterLogin,
    CharacterDelete,
    FriendsList,
    Generic,
}

/// <param name="Style">How simple values are written.</param>
/// <param name="AccountStatus">Status for an account without characters. 2 is the client's "no existing character" result, 1 a success with an empty list (which crashes the client).</param>
/// <param name="ExistingCharacter">Report one sample character instead of an empty account.</param>
/// <param name="ZoneField">Experimental: one extra <c>Zone</c> field in the server list, unverified against the parser.</param>
/// <param name="Gateway">Where the character login reply sends the client's UDP connection.</param>
public sealed record Ps3ReplyOptions(
    XmlStyle Style = XmlStyle.Attributes,
    int AccountStatus = 2,
    bool ExistingCharacter = false,
    bool ZoneField = false,
    string Gateway = "127.0.0.1:20260");

/// <summary>
/// The ps3ws web-service replies. Every reply is XML read by the client's own reader; the client treats a reply as successful
/// only when <c>Status</c> is exactly 1. Formats recovered from the decrypted client (docs/protocol/ps3ws-replies.md); ported from
/// <c>build_replies</c> in <c>bootstrap/fr_ps3_bootstrap.py</c>.
/// </summary>
public static partial class Ps3Replies
{
    public const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n";

    private static readonly Dictionary<string, Ps3ReplyKind> Routes = new(StringComparer.Ordinal)
    {
        ["/ps3ws/game/serverList.action"] = Ps3ReplyKind.ServerList,
        ["/ps3ws/account/info.action"] = Ps3ReplyKind.AccountInfo,
        ["/ps3ws/character/checkName.action"] = Ps3ReplyKind.CheckName,
        ["/ps3ws/character/create.action"] = Ps3ReplyKind.CharacterCreate,
        ["/ps3ws/character/login.action"] = Ps3ReplyKind.CharacterLogin,
        ["/ps3ws/character/delete.action"] = Ps3ReplyKind.CharacterDelete,
        ["/ps3ws/character/friendsList.action"] = Ps3ReplyKind.FriendsList,
    };

    /// <summary>The reply kind for a <c>/ps3ws/</c> route; unknown routes get the generic reply.</summary>
    public static Ps3ReplyKind KindOf(string route) => Routes.TryGetValue(route, out var kind) ? kind : Ps3ReplyKind.Generic;

    public static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>Renders <c>&lt;tag&gt;</c> with simple values <paramref name="fields"/> as attributes or child elements, plus nested elements.</summary>
    public static string Element(string tag, IReadOnlyList<(string Key, object Value)>? fields, IReadOnlyList<string>? children, XmlStyle style)
    {
        fields ??= [];

        var kids = children is null ? string.Empty : string.Concat(children);
        var text = new StringBuilder();

        if (style == XmlStyle.Attributes)
        {
            text.Append('<').Append(tag);

            foreach (var (key, value) in fields)
                text.Append(' ').Append(key).Append("=\"").Append(Escape(Format(value))).Append('"');

            if (kids.Length == 0)
                return text.Append("/>").ToString();

            return text.Append('>').Append(kids).Append("</").Append(tag).Append('>').ToString();
        }

        text.Append('<').Append(tag).Append('>');

        foreach (var (key, value) in fields)
            text.Append('<').Append(key).Append('>').Append(Escape(Format(value))).Append("</").Append(key).Append('>');

        return text.Append(kids).Append("</").Append(tag).Append('>').ToString();
    }

    public static IReadOnlyDictionary<Ps3ReplyKind, byte[]> Build(Ps3ReplyOptions? options = null)
    {
        options ??= new Ps3ReplyOptions();

        var style = options.Style;

        string El(string tag, (string, object)[]? fields = null, string[]? children = null) => Element(tag, fields, children, style);

        byte[] Reply(string root, (string, object)[]? fields = null, string[]? children = null, int status = 1, string message = "Success")
        {
            var head = new List<(string, object)> { ("Status", status), ("StatusMessage", message) };

            if (fields is not null)
                head.AddRange(fields);

            return new UTF8Encoding(false).GetBytes(XmlDeclaration + Element(root, head, children, style) + "\r\n");
        }

        var server = new List<(string, object)> { ("Name", "NorthForge"), ("Online", 1), ("Locked", 0) };

        if (options.ZoneField)
            server.Add(("Zone", 1));

        var account = El("Account", [("Member", 1), ("MaxCharacters", 3), ("Admin", 0)]);

        // The sample character's layout is a guess, see the docs.
        var sample = El("Character", [("HeadshotUrl", "")], [El("Details", [("Guid", 1), ("Name", "NorthForge")])]);

        byte[] accountReply;

        if (options.ExistingCharacter)
            accountReply = Reply("AccountInfoReply", children: [account, El("Characters", children: [sample])]);
        else if (options.AccountStatus != 1)
            accountReply = Reply("AccountInfoReply", children: [account], status: options.AccountStatus, message: "No characters");
        else
            accountReply = Reply("AccountInfoReply", children: [account, El("Characters")]);

        return new Dictionary<Ps3ReplyKind, byte[]>
        {
            [Ps3ReplyKind.ServerList] = Reply("ServerListReply", children: [El("Servers", children: [El("Server", [.. server])])]),
            [Ps3ReplyKind.AccountInfo] = accountReply,
            [Ps3ReplyKind.CheckName] = Reply("CheckNameReply", children: [El("Suggestions")]),
            [Ps3ReplyKind.CharacterCreate] = Reply("CharacterCreateReply", [("Guid", 1)]),

            // The client connects to GatewayAddress over UDP; see the gateway.
            [Ps3ReplyKind.CharacterLogin] = Reply("CharacterLoginReply", [("GatewayAddress", options.Gateway), ("GatewayTicket", "0"), ("Key", "0")]),
            [Ps3ReplyKind.CharacterDelete] = Reply("CharacterDeleteReply"),
            [Ps3ReplyKind.FriendsList] = Reply("CharacterFriendsReply", children: [El("Friends")]),
            [Ps3ReplyKind.Generic] = Reply("Response"),
        };
    }

    /// <summary>The <c>Status</c> value in a reply, for the log line; null when there is none.</summary>
    public static string? StatusOf(byte[] reply)
    {
        var match = StatusPattern().Match(Encoding.Latin1.GetString(reply));

        return match.Success ? match.Groups[1].Value : null;
    }

    private static string Format(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    [GeneratedRegex("Status(?:=\"|>)(\\d+)")]
    private static partial Regex StatusPattern();
}
