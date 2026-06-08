using System.Text.Json;
using LTAI.Agent.Vector;
using Xunit;

namespace LTAI.Tests;

public class KgStoreDtoTests
{
    [Fact]
    public void KgStoreDto_Serialization_RoundTrip()
    {
        var node = new NodeRow
        {
            Id = 1,
            ExtId = "ext-1",
            Kind = "class",
            Name = "Foo",
            Namespace = "MyApp",
            Signature = "public class Foo",
            Source = "src/Foo.cs",
            Props = """{"key":"value"}""",
            CreatedAt = "2024-01-01T00:00:00Z",
            UpdatedAt = "2024-06-01T00:00:00Z"
        };

        var json = JsonSerializer.Serialize(node);
        var deserialized = JsonSerializer.Deserialize<NodeRow>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(node.Id, deserialized.Id);
        Assert.Equal(node.Kind, deserialized.Kind);
        Assert.Equal(node.Name, deserialized.Name);
        Assert.Equal(node.Namespace, deserialized.Namespace);
    }

    [Fact]
    public void KgStoreSchema_GetTableName_ReturnsCorrect()
    {
        Assert.True(KgStoreSchema.IsValidKind("class"));
        Assert.True(KgStoreSchema.IsValidKind("document"));
        Assert.False(KgStoreSchema.IsValidKind("nonexistent_type"));

        Assert.True(KgStoreSchema.IsValidRelation("contains"));
        Assert.True(KgStoreSchema.IsValidRelation("references"));
        Assert.False(KgStoreSchema.IsValidRelation("invalid_relation"));
    }

    [Fact]
    public void KgStoreSchema_ValidateNode_ReturnsError_ForInvalidKind()
    {
        var error = KgStoreSchema.ValidateNode("unicorn", null);
        Assert.NotNull(error);
        Assert.Contains("unicorn", error);
    }

    [Fact]
    public void KgStoreSchema_ValidateNode_ReturnsNull_ForValidKind()
    {
        var error = KgStoreSchema.ValidateNode("class", null);
        Assert.Null(error);
    }

    [Fact]
    public void EdgeRow_ToString_FormatsCorrectly()
    {
        var edge = new EdgeRow { Id = 1, Src = 10, Dst = 20, Relation = "references", Weight = 1.0 };
        Assert.Contains("references", edge.ToString());
        Assert.Contains("10", edge.ToString());
        Assert.Contains("20", edge.ToString());
    }

    [Fact]
    public void NodeRow_GetProps_ReturnsDeserialized()
    {
        var node = new NodeRow { Props = """{"key":"value"}""" };
        var props = node.GetProps();
        Assert.NotNull(props);
        Assert.Equal("value", props["key"]!.ToString());
    }

    [Fact]
    public void NodeRow_GetProps_NullProps_ReturnsNull()
    {
        var node = new NodeRow { Props = null };
        Assert.Null(node.GetProps());
    }
}
