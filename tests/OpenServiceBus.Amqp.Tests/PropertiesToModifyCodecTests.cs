using Amqp;
using Amqp.Framing;
using Amqp.Types;
using OpenServiceBus.Amqp.Settlement;

namespace OpenServiceBus.Amqp.Tests;

/// <summary>
/// Decoding of the PropertiesToModify wire shapes (issue #30) and the payload merge. Keys
/// arrive as Symbol from the .NET SDK and as plain strings from proton-j - both must work.
/// </summary>
public class PropertiesToModifyCodecTests
{
    private static byte[] Encode(Message msg)
    {
        var buffer = msg.Encode();
        var copy = new byte[buffer.Length];
        Array.Copy(buffer.Buffer, buffer.Offset, copy, 0, buffer.Length);
        return copy;
    }

    private static Message Decode(byte[] bytes)
    {
        var buffer = new ByteBuffer(bytes, 0, bytes.Length, bytes.Length);
        return Message.Decode(buffer);
    }

    [Fact]
    public void FromModified_ReadsSymbolAndStringKeys()
    {
        var modified = new Modified { MessageAnnotations = new Fields() };
        modified.MessageAnnotations.Add(new Symbol("retry-reason"), "timeout");
        var props = PropertiesToModifyCodec.FromModified(modified);

        props.ShouldNotBeNull();
        props["retry-reason"].ShouldBe("timeout");
    }

    [Fact]
    public void FromModified_NoAnnotations_IsNull()
    {
        PropertiesToModifyCodec.FromModified(new Modified()).ShouldBeNull();
    }

    [Fact]
    public void FromRejected_SplitsReasonDescriptionAndProperties()
    {
        var error = new Error(new Symbol("com.microsoft:dead-letter")) { Info = new Fields() };
        error.Info.Add(new Symbol("DeadLetterReason"), "poison");
        error.Info.Add(new Symbol("DeadLetterErrorDescription"), "bad payload");
        error.Info.Add(new Symbol("attempt"), 3);
        var (reason, description, props) = PropertiesToModifyCodec.FromRejected(new Rejected { Error = error });

        reason.ShouldBe("poison");
        description.ShouldBe("bad payload");
        props.ShouldNotBeNull();
        props.Count.ShouldBe(1);
        props["attempt"].ShouldBe(3);
    }

    [Fact]
    public void FromRejected_OnlyReason_NoPropertiesMap()
    {
        var error = new Error(new Symbol("com.microsoft:dead-letter")) { Info = new Fields() };
        error.Info.Add(new Symbol("DeadLetterReason"), "poison");
        var (reason, _, props) = PropertiesToModifyCodec.FromRejected(new Rejected { Error = error });

        reason.ShouldBe("poison");
        props.ShouldBeNull();
    }

    [Fact]
    public void FromMap_ReadsAPlainAmqpMap()
    {
        var map = new Map { ["k1"] = "v1", ["k2"] = 42L };
        var props = PropertiesToModifyCodec.FromMap(map);

        props.ShouldNotBeNull();
        props["k1"].ShouldBe("v1");
        props["k2"].ShouldBe(42L);
    }

    [Fact]
    public void FromMap_NonMap_IsNull()
    {
        PropertiesToModifyCodec.FromMap(null).ShouldBeNull();
        PropertiesToModifyCodec.FromMap("nope").ShouldBeNull();
    }

    [Fact]
    public void MergeIntoEncoded_AddsAndOverwrites_LastWriteWins()
    {
        var original = new Message("body-stays")
        {
            Properties = new Properties { MessageId = "m-1" },
            ApplicationProperties = new ApplicationProperties(),
        };
        original.ApplicationProperties["existing"] = "old";
        original.ApplicationProperties["untouched"] = "keep";

        var merged = PropertiesToModifyCodec.MergeIntoEncoded(Encode(original), new Dictionary<string, object?>
        {
            ["existing"] = "new",
            ["added"] = 7,
        });

        var roundTripped = Decode(merged);
        roundTripped.Properties.MessageId.ShouldBe("m-1");
        roundTripped.Body.ShouldBe("body-stays");
        roundTripped.ApplicationProperties["existing"].ShouldBe("new");
        roundTripped.ApplicationProperties["untouched"].ShouldBe("keep");
        roundTripped.ApplicationProperties["added"].ShouldBe(7);
    }

    [Fact]
    public void MergeIntoEncoded_MessageWithoutApplicationProperties_CreatesTheSection()
    {
        var original = new Message("bare") { Properties = new Properties { MessageId = "m-2" } };

        var merged = PropertiesToModifyCodec.MergeIntoEncoded(Encode(original), new Dictionary<string, object?>
        {
            ["fresh"] = "value",
        });

        Decode(merged).ApplicationProperties["fresh"].ShouldBe("value");
    }
}
