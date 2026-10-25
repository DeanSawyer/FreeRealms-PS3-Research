using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Tests;

/// <summary>Runs the web-front code on the inputs of tools/gen_ps3_web_vectors.py and compares with what the Python bootstrap answered.</summary>
[TestClass]
public sealed class WebVectorTests
{
    [TestMethod]
    public void RepliesMatchReference()
    {
        foreach (var vector in WebVectors.Replies)
        {
            var options = new Ps3ReplyOptions(
                Enum.Parse<XmlStyle>(vector.Style, ignoreCase: true),
                vector.AccountStatus,
                vector.ExistingCharacter,
                vector.Zone,
                vector.Gateway);

            var replies = Ps3Replies.Build(options);

            Assert.AreEqual(Enum.GetValues<Ps3ReplyKind>().Length, replies.Count, vector.Label);
            Assert.AreEqual(vector.Replies.Length, replies.Count, vector.Label);

            foreach (var (name, body) in vector.Replies)
            {
                var kind = Enum.GetValues<Ps3ReplyKind>().Single(candidate => Ps3WebService.KindName(candidate) == name);

                CollectionAssert.AreEqual(body, replies[kind], $"{vector.Label}: {name}");
            }
        }
    }

    [TestMethod]
    public void RoutesSelectTheSameReplyKinds()
    {
        Assert.AreEqual(Ps3ReplyKind.ServerList, Ps3Replies.KindOf("/ps3ws/game/serverList.action"));
        Assert.AreEqual(Ps3ReplyKind.AccountInfo, Ps3Replies.KindOf("/ps3ws/account/info.action"));
        Assert.AreEqual(Ps3ReplyKind.CheckName, Ps3Replies.KindOf("/ps3ws/character/checkName.action"));
        Assert.AreEqual(Ps3ReplyKind.CharacterCreate, Ps3Replies.KindOf("/ps3ws/character/create.action"));
        Assert.AreEqual(Ps3ReplyKind.CharacterLogin, Ps3Replies.KindOf("/ps3ws/character/login.action"));
        Assert.AreEqual(Ps3ReplyKind.CharacterDelete, Ps3Replies.KindOf("/ps3ws/character/delete.action"));
        Assert.AreEqual(Ps3ReplyKind.FriendsList, Ps3Replies.KindOf("/ps3ws/character/friendsList.action"));
        Assert.AreEqual(Ps3ReplyKind.Generic, Ps3Replies.KindOf("/ps3ws/unknown/thing.action"));
        Assert.AreEqual(Ps3ReplyKind.Generic, Ps3Replies.KindOf("/ps3ws/game/SERVERLIST.action"));
    }

    [TestMethod]
    public void StatusIsReadFromBothXmlStyles()
    {
        Assert.AreEqual("2", Ps3Replies.StatusOf(Ps3Replies.Build()[Ps3ReplyKind.AccountInfo]));
        Assert.AreEqual("1", Ps3Replies.StatusOf(Ps3Replies.Build(new Ps3ReplyOptions(XmlStyle.Elements))[Ps3ReplyKind.ServerList]));
        Assert.IsNull(Ps3Replies.StatusOf([1, 2, 3]));
    }

    [TestMethod]
    public void AppConfigMatchesReference()
    {
        foreach (var (ip, document) in WebVectors.AppConfigs)
            CollectionAssert.AreEqual(document, AppConfigDocument.Build(ip), ip);
    }

    [TestMethod]
    public void DnsQuestionsAndAnswersMatchReference()
    {
        foreach (var vector in WebVectors.Dns)
        {
            if (!vector.Valid)
            {
                Assert.ThrowsExactly<FormatException>(() => DnsPacket.Parse(vector.Packet), vector.Label);

                continue;
            }

            var question = DnsPacket.Parse(vector.Packet);

            Assert.AreEqual(vector.Host, question.Host, vector.Label);
            Assert.AreEqual(vector.Type, question.Type, vector.Label);
            Assert.AreEqual(vector.Class, question.Class, vector.Label);
            CollectionAssert.AreEqual(vector.Raw, question.Raw, vector.Label);
            CollectionAssert.AreEqual(vector.Reply, DnsPacket.Answer(vector.Packet, question, WebVectors.AnswerIp), vector.Label);
        }
    }

    [TestMethod]
    public void PercentDecodingMatchesReference()
    {
        foreach (var (input, output) in WebVectors.Decodes)
            Assert.AreEqual(output, FormBody.PercentDecode(input), input);
    }

    [TestMethod]
    public void FormParsingMatchesReference()
    {
        foreach (var vector in WebVectors.Forms)
            CollectionAssert.AreEqual(vector.Pairs, FormBody.Parse(vector.Input, vector.KeepBlank).ToArray(), $"{vector.Input} keep={vector.KeepBlank}");
    }

    [TestMethod]
    public void FirstValuesKeepTheFirstOfEachName()
    {
        var pairs = FormBody.Parse("b=1&a=2&b=3&c=&a=4", keepBlank: true);

        CollectionAssert.AreEqual(
            new[] { ("b", "1"), ("a", "2"), ("c", string.Empty) },
            FormBody.FirstValues(pairs).ToArray());
    }

    [TestMethod]
    public void RequestTargetsSplitLikeUrlsplit()
    {
        var plain = new WebRequest("GET", "/a/b?x=1&y=2");

        Assert.AreEqual("/a/b", plain.Path);
        Assert.AreEqual("x=1&y=2", plain.Query);

        var fragment = new WebRequest("GET", "/a?x=1#frag?y=2");

        Assert.AreEqual("/a", fragment.Path);
        Assert.AreEqual("x=1", fragment.Query);

        var onlyFragment = new WebRequest("GET", "/a#b?c");

        Assert.AreEqual("/a", onlyFragment.Path);
        Assert.AreEqual(string.Empty, onlyFragment.Query);

        var absolute = new WebRequest("GET", "http://ps3.freerealms.com/ps3ws/game/serverList.action?request_locale=en_US");

        Assert.AreEqual("/ps3ws/game/serverList.action", absolute.Path);
        Assert.AreEqual("request_locale=en_US", absolute.Query);

        Assert.AreEqual("/", new WebRequest("GET", "http://host").Path);
    }
}
