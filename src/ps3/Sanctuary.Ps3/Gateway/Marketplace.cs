using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// The Marketplace (InGamePurchase, opcode 66) as far as the coin store needs it: the screen that holds the coin store tab. The PS3
/// numbers its sub-opcodes differently from the PC and Sanctuary (receive dispatcher <c>0x9425c</c>, docs/protocol/coin-store.md), so
/// Sanctuary's login packets are sent under the PS3's numbers and the client's requests are answered as Sanctuary answers them:
/// <list type="bullet">
/// <item>EnableMarketplace: PC 24, PS3 28 (<c>0x833b0</c>: one byte, kept at <c>+0x10000+5657</c>).</item>
/// <item>EnablePaymentSources: PC 33, PS3 43.</item>
/// <item>CurrencyCodes: request PS3 15 (PC 16), reply PS3 16 (<c>0x88d30</c>).</item>
/// <item>StateCodes: request PS3 19 (PC 18), reply PS3 20 (<c>0x94044</c>).</item>
/// <item>CountryCodes: request PS3 21 (PC 20), reply PS3 22 (<c>0x87e2c</c>).</item>
/// <item>Membership account info: request PS3 29 (PC AccountInfo 25), reply PS3 30 (<c>0x86148</c>).</item>
/// </list>
/// The replies carry Sanctuary's values (US, USD, Washington) in the PC layouts; the PS3 readers start the same way (error code, then
/// the strings), the lists are not checked against the PS3 yet.
/// </summary>
public static class Marketplace
{
    public const int Opcode = 66;

    public const int EnableMarketplaceSub = 28;
    public const int EnablePaymentSourcesSub = 43;
    public const int CurrencyCodesRequestSub = 15;
    public const int CurrencyCodesResponseSub = 16;
    public const int StateCodesRequestSub = 19;
    public const int StateCodesResponseSub = 20;
    public const int CountryCodesRequestSub = 21;
    public const int CountryCodesResponseSub = 22;
    public const int AccountInfoRequestSub = 29;
    public const int AccountInfoResponseSub = 30;

    /// <summary>Sanctuary's error code for a good reply.</summary>
    private const int Ok = 1;

    /// <summary>EnableMarketplace (PS3 28): on.</summary>
    public static byte[] EnableMarketplace(bool enabled = true) => new PacketWriter().U16(Opcode).U16(EnableMarketplaceSub).U8(enabled ? 1 : 0).ToArray();

    /// <summary>EnablePaymentSources (PS3 43): SMS and PayPal, both on as Sanctuary sends them; the PS3 reads two ints (0x83190), the PC two bytes.</summary>
    public static byte[] EnablePaymentSources() => new PacketWriter().U16(Opcode).U16(EnablePaymentSourcesSub).I32(1).I32(1).ToArray();

    /// <summary>CurrencyCodes reply: error code, locale, then a map of one currency (key: the Jenkins hash of its code) as Sanctuary sends it.</summary>
    public static byte[] CurrencyCodes(string locale) =>
        new PacketWriter()
            .U16(Opcode).U16(CurrencyCodesResponseSub).I32(Ok).Str(locale)
            .I32(1).U32(OneAtATimeHash("USD")).Str("USD").Str("$").Str(".").Str("US Dollar").I32(100).I32(2).U8(0)
            .ToArray();

    /// <summary>StateCodes reply: error code, then one state, as Sanctuary sends it.</summary>
    public static byte[] StateCodes() =>
        new PacketWriter().U16(Opcode).U16(StateCodesResponseSub).I32(Ok).I32(1).Str("WA").Str("Washington").ToArray();

    /// <summary>CountryCodes reply: error code, locale, then one country (code, language, locale, name), as Sanctuary sends it.</summary>
    public static byte[] CountryCodes(string locale) =>
        new PacketWriter()
            .U16(Opcode).U16(CountryCodesResponseSub).I32(Ok).Str(locale)
            .I32(1).Str("US").Str(string.Empty).Str("United States").Str("United States")
            .ToArray();

    /// <summary>Account info reply (PS3 30): error code, default country, default currency, parental password off.</summary>
    public static byte[] AccountInfo() =>
        new PacketWriter().U16(Opcode).U16(AccountInfoResponseSub).I32(Ok).Str("US").Str("USD").U8(0).ToArray();

    /// <summary>The replies to a Marketplace request, or null when <paramref name="inner"/> is not one this server answers.</summary>
    public static List<NamedPacket>? Handle(ReadOnlySpan<byte> inner)
    {
        if (inner.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(inner) != Opcode)
            return null;

        var locale = Locale(inner[4..]) ?? "en_US";

        return BinaryPrimitives.ReadUInt16LittleEndian(inner[2..]) switch
        {
            CurrencyCodesRequestSub => [new NamedPacket("InGamePurchaseCurrencyCodes", CurrencyCodes(locale))],
            StateCodesRequestSub => [new NamedPacket("InGamePurchaseStateCodes", StateCodes())],
            CountryCodesRequestSub => [new NamedPacket("InGamePurchaseCountryCodes", CountryCodes(locale))],
            AccountInfoRequestSub => [new NamedPacket("InGamePurchaseAccountInfo", AccountInfo())],
            _ => null,
        };
    }

    /// <summary>Sanctuary's JenkinsHelper.OneAtATimeHash, as written there: on an int, per UTF-16 char, with arithmetic shifts.</summary>
    public static uint OneAtATimeHash(string text)
    {
        unchecked
        {
            var hash = 0;

            foreach (var value in text)
            {
                hash += value;
                hash += hash << 10;
                hash ^= hash >> 6;
            }

            hash += hash << 3;
            hash ^= hash >> 11;
            hash += hash << 15;

            return (uint)hash;
        }
    }

    /// <summary>The request's locale string (u32 length, then the text), or null.</summary>
    private static string? Locale(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(data);

        return length > 0 && length <= data.Length - 4 ? Encoding.UTF8.GetString(data.Slice(4, length)) : null;
    }
}
