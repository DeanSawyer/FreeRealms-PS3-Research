using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

/// <summary>How much of each saved character <c>account/info</c> sends (environment variable <c>FR_SELECT_DETAIL</c>).</summary>
public enum CharacterDetail
{
    /// <summary><c>Guid</c> and <c>Name</c> only, like the old sample character.</summary>
    Minimal,

    /// <summary>Plus <c>Model</c>. The default: the full record froze the game in character select (13 November test).</summary>
    Basic,

    /// <summary>Plus the appearance: head, hair, customization, face paint, skin tone (asset names) and the eye and hair colours.</summary>
    Looks,

    /// <summary>Plus one <c>Attachment</c> per piece of clothing.</summary>
    Full,
}

/// <param name="Style">How simple values are written.</param>
/// <param name="AccountStatus">Status for an account without characters. 2 is the client's "no existing character" result, 1 a success with an empty list (which crashes the client).</param>
/// <param name="ExistingCharacter">Report one sample character instead of an empty account.</param>
/// <param name="ZoneField">Experimental: one extra <c>Zone</c> field in the server list, unverified against the parser.</param>
/// <param name="Gateway">Where the character login reply sends the client's UDP connection.</param>
/// <param name="Detail">How much of each saved character the account reply sends.</param>
public sealed record Ps3ReplyOptions(
    XmlStyle Style = XmlStyle.Attributes,
    int AccountStatus = 2,
    bool ExistingCharacter = false,
    bool ZoneField = false,
    string Gateway = "127.0.0.1:20260",
    CharacterDetail Detail = CharacterDetail.Basic);

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

    /// <summary>
    /// <c>account/info</c> for an account with <paramref name="characters"/> (at least one): <c>Status</c> 1 and a <c>Character</c>
    /// for each, so the client goes to character select. The layout is the one the client's reader takes (<c>0xd9b6f8</c>):
    /// <c>HeadshotUrl</c> on <c>Character</c>; <c>Guid</c>, <c>Name</c>, <c>Model</c>, the appearance asset names (as the game
    /// server's SendSelfToClient sends them), <c>EyeColor</c>, <c>HairColor</c> and <c>Attachments/Attachment</c> (Model, Texture,
    /// Tint, TintOverrideId, EffectId, Slot) on <c>Details</c>.
    /// </summary>
    public static byte[] AccountInfo(IReadOnlyList<SavedCharacter> characters, XmlStyle style = XmlStyle.Attributes, CharacterDetail detail = CharacterDetail.Full, int maxCharacters = MaxCharacters, bool newCharacterStop = false)
    {
        string El(string tag, (string, object)[]? fields = null, string[]? children = null) => Element(tag, fields, children, style);

        var list = new List<string>(characters.Count + 1);
        var entries = characters.Select(character => (character.Guid, character.Name, character.Values)).ToList();

        // The character wheel's last stop (the RPCS3 patch "character wheel"): guid NewCharacterGuid, which the patched select
        // screen turns into "go to the creator" instead of a login. Select builds a preview of whatever it shows, so the stop has a
        // whole default look and a guid of its own (a guid 0 or an empty look gives no preview, and the game stops). Without the
        // patch only the first character is ever shown, so it is never reached.
        if (newCharacterStop && characters.Count < maxCharacters)
            entries.Add((NewCharacterGuid, NewCharacterName, NewCharacterLook));

        foreach (var (guid, name, values) in entries)
        {
            var fields = new List<(string, object)> { ("Guid", guid), ("Name", name) };

            if (detail >= CharacterDetail.Basic)
                fields.Add(("Model", Gateway.CharacterMappings.Number(values, "Model", 1)));

            if (detail >= CharacterDetail.Looks)
            {
                fields.AddRange(
                [
                    ("Head", Gateway.CharacterMappings.Head(values)),
                    ("Hair", Gateway.CharacterMappings.Hair(values)),
                    ("ModelCustomization", Gateway.CharacterMappings.ModelCustomization(values)),
                    ("FacePaint", Gateway.CharacterMappings.FacePaint(values)),
                    ("SkinTone", Gateway.CharacterMappings.SkinTone(values)),
                    ("EyeColor", Gateway.CharacterMappings.Number(values, "EyeColor", 0)),
                    ("HairColor", Gateway.CharacterMappings.Number(values, "HairColor", 0)),
                ]);
            }

            string[]? children = null;

            if (detail >= CharacterDetail.Full)
            {
                var attachments = Gateway.Clothing.Attachments(values)
                    .Select(piece => El("Attachment", [
                        ("Model", piece.Model), ("Texture", piece.TextureAlias), ("Tint", piece.TintAlias),
                        ("TintOverrideId", piece.Tint), ("EffectId", piece.CompositeEffect), ("Slot", piece.Slot)]))
                    .ToArray();

                children = [El("Attachments", children: attachments)];
            }

            var details = El("Details", [.. fields], children);

            list.Add(El("Character", [("HeadshotUrl", string.Empty)], [details]));
        }

        var account = El("Account", [("Member", 1), ("MaxCharacters", maxCharacters), ("Admin", 0)]);

        return Document("AccountInfoReply", null, [account, El("Characters", children: [.. list])], style);
    }

    /// <summary>The name of the character wheel's "make a new character" stop.</summary>
    public const string NewCharacterName = "New character";

    /// <summary>
    /// The guid of the character wheel's "make a new character" stop; the patch (<c>0x791c60</c>) compares the shown character's
    /// guid with it. Saved characters count up from 1 and never get near it. It is not 0: the preview of the shown character is
    /// an actor with the character's guid as its id, and the game already has an actor with id 0.
    /// </summary>
    public const ulong NewCharacterGuid = 0x7FFF0000;

    /// <summary>What the "make a new character" stop looks like on the select screen: the creator's first choices and its starter outfit.</summary>
    public static readonly IReadOnlyDictionary<string, string> NewCharacterLook = new Dictionary<string, string>
    {
        ["Model"] = "1", ["Head"] = "1", ["Hair"] = "1", ["SkinTone"] = "1", ["EyeColor"] = "0", ["HairColor"] = "0",
        ["ItemChest"] = "28216", ["ItemChestTint"] = "234", ["ItemLegs"] = "392", ["ItemLegsTint"] = "262",
        ["ItemFeet"] = "17397", ["ItemFeetTint"] = "234",
    };

    /// <summary><c>character/create</c> answered with the guid the server gave the new character.</summary>
    public static byte[] CharacterCreate(ulong guid, XmlStyle style = XmlStyle.Attributes) =>
        Document("CharacterCreateReply", [("Guid", guid)], null, style);

    /// <summary>How many characters an account may have (the <c>MaxCharacters</c> the client is told with saved characters): Sanctuary's default.</summary>
    public const int MaxCharacters = CharacterStore.MaxCharacters;

    /// <summary>
    /// <c>character/checkName</c>: 1 when the name is free, 2 ("Name already taken", the creator shows <c>Char_Name_Taken_PS3</c>)
    /// when a saved character has it, as Sanctuary refuses a duplicate name.
    /// </summary>
    public static byte[] CheckName(bool taken, XmlStyle style = XmlStyle.Attributes) =>
        taken
            ? Document("CheckNameReply", null, [Element("Suggestions", null, null, style)], style, status: 2, message: "Name already taken")
            : Document("CheckNameReply", null, [Element("Suggestions", null, null, style)], style);

    private static byte[] Document(string root, (string, object)[]? fields, string[]? children, XmlStyle style, int status = 1, string message = "Success")
    {
        var head = new List<(string, object)> { ("Status", status), ("StatusMessage", message) };

        if (fields is not null)
            head.AddRange(fields);

        return new UTF8Encoding(false).GetBytes(XmlDeclaration + Element(root, head, children, style) + "\r\n");
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
