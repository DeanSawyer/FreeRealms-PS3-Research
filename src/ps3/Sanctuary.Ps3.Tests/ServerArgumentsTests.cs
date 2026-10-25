using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Gateway;
using Sanctuary.Ps3.Hosting;
using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>The command line of the C# server: same options and defaults as <c>bootstrap/fr_ps3_bootstrap.py</c>.</summary>
[TestClass]
public sealed class ServerArgumentsTests
{
    private static string? NoEnvironment(string name) => null;

    private static ParseResult Parse(params string[] args) => ServerArguments.Parse(args, NoEnvironment);

    [TestMethod]
    public void DefaultsMatchThePythonScript()
    {
        var options = Parse("--client", "C:/game").Options!;

        Assert.AreEqual("C:/game", options.ClientRoot);
        Assert.IsNull(options.DigestPath);
        Assert.IsFalse(options.MatchLocal);
        Assert.AreEqual(0, options.ExtraClients.Count);
        Assert.AreEqual("zs-zlib-zero-trailer", options.Codec);
        Assert.IsFalse(options.ServeMismatch);
        Assert.IsFalse(options.ZoneField);
        Assert.IsFalse(options.ExistingCharacter);
        Assert.AreEqual(2, options.AccountStatus);
        Assert.AreEqual(XmlStyle.Attributes, options.XmlStyle);
        Assert.IsTrue(options.EnableGateway);
        Assert.AreEqual(20260, options.GatewayPort);
        Assert.AreEqual(IPAddress.Loopback, options.Advertise);
        Assert.AreEqual(IPAddress.Parse("1.1.1.1"), options.Upstream);
        Assert.IsTrue(options.EnableDns);
        Assert.AreEqual(IPAddress.Loopback, options.Bind);
        Assert.AreEqual(80, options.HttpPort);
    }

    [TestMethod]
    public void EveryOptionIsRead()
    {
        var options = Parse(
            "--client", "C:/game",
            "--digest", "C:/d.soe",
            "--match-local",
            "--extra-client", "C:/one",
            "--extra-client", "C:/two",
            "--codec", "plain",
            "--serve-mismatch",
            "--server-list-variant", "zone",
            "--existing-character",
            "--account-status", "1",
            "--xml-style", "elements",
            "--gateway-port", "0",
            "--advertise", "192.168.1.20",
            "--upstream", "8.8.8.8",
            "--no-dns",
            "--bind", "0.0.0.0",
            "--port", "8080").Options!;

        Assert.AreEqual("C:/d.soe", options.DigestPath);
        Assert.IsTrue(options.MatchLocal);
        CollectionAssert.AreEqual(new[] { "C:/one", "C:/two" }, options.ExtraClients.ToArray());
        Assert.AreEqual("plain", options.Codec);
        Assert.IsTrue(options.ServeMismatch);
        Assert.IsTrue(options.ZoneField);
        Assert.IsTrue(options.ExistingCharacter);
        Assert.AreEqual(1, options.AccountStatus);
        Assert.AreEqual(XmlStyle.Elements, options.XmlStyle);
        Assert.IsFalse(options.EnableGateway);
        Assert.AreEqual(IPAddress.Parse("192.168.1.20"), options.Advertise);
        Assert.AreEqual(IPAddress.Parse("8.8.8.8"), options.Upstream);
        Assert.IsFalse(options.EnableDns);
        Assert.AreEqual(IPAddress.Any, options.Bind);
        Assert.AreEqual(8080, options.HttpPort);
    }

    [TestMethod]
    public void OptionsMayUseAnEqualsSign()
    {
        var options = Parse("--client=C:/my game", "--port=81", "--codec=zs-zlib-digest-trailer", "--extra-client=C:/x").Options!;

        Assert.AreEqual("C:/my game", options.ClientRoot);
        Assert.AreEqual(81, options.HttpPort);
        Assert.AreEqual("zs-zlib-digest-trailer", options.Codec);
        Assert.AreEqual("C:/x", options.ExtraClients.Single());
    }

    [TestMethod]
    public void TheGatewayOptionsComeFromTheEnvironmentAndWarningsReachTheNotice()
    {
        var environment = new Dictionary<string, string> { ["FR_ZONE"] = "Elsewhere", ["FR_SPAWN"] = "nonsense", ["FR_EXTRA"] = "1" };
        var notices = new List<string>();

        var result = ServerArguments.Parse(["--client", "x"], name => environment.GetValueOrDefault(name), notices.Add);

        Assert.AreEqual("Elsewhere", result.Options!.Gateway.Zone);
        Assert.IsTrue(result.Options.Gateway.Extra);
        Assert.IsNull(result.Options.Gateway.Spawn);
        Assert.AreEqual(1, notices.Count);
        Assert.IsTrue(notices[0].Contains("FR_SPAWN", StringComparison.Ordinal), notices[0]);
    }

    [TestMethod]
    public void HelpIsRecognisedAnywhere()
    {
        Assert.IsTrue(Parse("-h").Help);
        Assert.IsTrue(Parse("--client", "x", "--help").Help);
        Assert.IsNull(Parse("-h").Options);
        Assert.IsNull(Parse("-h").Error);
    }

    [TestMethod]
    [DataRow(new string[0], "--client")]
    [DataRow(new[] { "--port", "80" }, "--client")]
    [DataRow(new[] { "--client" }, "--client needs a value")]
    [DataRow(new[] { "--client", "x", "--digest" }, "--digest needs a value")]
    [DataRow(new[] { "--client", "x", "--codec", "zs-lzma" }, "--codec")]
    [DataRow(new[] { "--client", "x", "--codec" }, "--codec")]
    [DataRow(new[] { "--client", "x", "--server-list-variant", "other" }, "--server-list-variant")]
    [DataRow(new[] { "--client", "x", "--xml-style", "json" }, "--xml-style")]
    [DataRow(new[] { "--client", "x", "--account-status", "two" }, "--account-status")]
    [DataRow(new[] { "--client", "x", "--gateway-port", "70000" }, "--gateway-port")]
    [DataRow(new[] { "--client", "x", "--gateway-port", "-1" }, "--gateway-port")]
    [DataRow(new[] { "--client", "x", "--port", "http" }, "--port")]
    [DataRow(new[] { "--client", "x", "--advertise", "::1" }, "--advertise")]
    [DataRow(new[] { "--client", "x", "--upstream", "dns.example" }, "--upstream")]
    [DataRow(new[] { "--client", "x", "--bind", "300.1.1.1" }, "--bind")]
    [DataRow(new[] { "--client", "x", "--bogus" }, "unrecognized argument: --bogus")]
    [DataRow(new[] { "--client", "x", "stray" }, "unrecognized argument: stray")]
    public void MistakesAreReported(string[] args, string expected)
    {
        var result = Parse(args);

        Assert.IsNull(result.Options);
        Assert.IsFalse(result.Help);
        Assert.IsNotNull(result.Error);
        Assert.IsTrue(result.Error.Contains(expected, StringComparison.Ordinal), result.Error);
    }

    [TestMethod]
    public void NegativeAccountStatusIsAccepted()
    {
        Assert.AreEqual(-1, Parse("--client", "x", "--account-status", "-1").Options!.AccountStatus);
    }

    [TestMethod]
    public void TheUsageTextNamesEveryOption()
    {
        foreach (var option in new[]
        {
            "--client", "--digest", "--match-local", "--extra-client", "--codec", "--serve-mismatch", "--server-list-variant",
            "--existing-character", "--account-status", "--xml-style", "--gateway-port", "--advertise", "--upstream", "--no-dns",
            "--bind", "--port", "--help", "FR_TRACE_DIR",
        })
        {
            Assert.IsTrue(ServerArguments.Usage.Contains(option, StringComparison.Ordinal), option);
        }
    }

    [TestMethod]
    public void TheGatewayOptionsAreTheDefaultsWithoutEnvironment()
    {
        Assert.AreEqual(new GatewayOptions().Zone, Parse("--client", "x").Options!.Gateway.Zone);
    }
}
