using LTAI.Mm.Core;
using LTAI.Mm.Ir;
using LTAI.Mm.Tree;
using Xunit;

namespace LTAI.Mm.Tests;

public class JsoncTests
{
    [Fact]
    public void Parse_Simple_Object()
    {
        var jsonc = @"{""name"": ""Alice"", ""age"": 30}";
        var node = LTAI.Mm.MetaMessage.ParseJsonc(jsonc);
        Assert.IsType<MmMap>(node);
        var map = (MmMap)node;
        Assert.Equal(2, map.Entries.Count);
    }

    [Fact]
    public void Parse_With_Tag()
    {
        var jsonc = @"{
            // mm: type=str; desc=姓名
            ""name"": ""Alice"",
            // mm: type=i; desc=年龄
            ""age"": 30
        }";
        var node = LTAI.Mm.MetaMessage.ParseJsonc(jsonc);
        var map = (MmMap)node;
        Assert.Equal(2, map.Entries.Count);

        var nameEntry = map.Entries[0];
        Assert.NotNull(nameEntry.Value.Tag);
        Assert.Equal("姓名", nameEntry.Value.Tag!.Desc);
    }

    [Fact]
    public void Roundtrip_Jsonc()
    {
        var person = new { name = "Alice", age = 30, active = true };
        string jsonc1 = LTAI.Mm.MetaMessage.ValueToJsonc(person);

        var node = LTAI.Mm.MetaMessage.ParseJsonc(jsonc1);
        string jsonc2 = LTAI.Mm.MetaMessage.ValueToJsonc(node);

        Assert.Equal(jsonc1, jsonc2);
    }

    [Fact]
    public void Bind_Jsonc_To_Object()
    {
        var jsonc = @"{""Name"": ""Bob"", ""Age"": 25}";
        var person = LTAI.Mm.MetaMessage.FromJsonc<SimplePerson>(jsonc);
        Assert.Equal("Bob", person.Name);
        Assert.Equal(25, person.Age);
    }

    [Fact]
    public void Parse_Array()
    {
        var jsonc = @"[1, 2, 3]";
        var node = LTAI.Mm.MetaMessage.ParseJsonc(jsonc);
        Assert.IsType<MmArray>(node);
        var arr = (MmArray)node;
        Assert.Equal(3, arr.Children.Count);
    }

    [Fact]
    public void Parse_With_Comments()
    {
        var jsonc = @"{
            /* block comment */
            ""key"": ""value""
        }";
        var node = LTAI.Mm.MetaMessage.ParseJsonc(jsonc);
        var map = (MmMap)node;
        Assert.Single(map.Entries);
    }

    [Fact]
    public void Jsonc_To_Bytes_Roundtrip()
    {
        var jsonc = @"{""msg"": ""hello"", ""num"": 42}";
        byte[] data = LTAI.Mm.MetaMessage.FromJsoncToBytes(jsonc);

        var decoded = LTAI.Mm.MetaMessage.DecodeToJsonc(data);
        var node2 = LTAI.Mm.MetaMessage.ParseJsonc(decoded);
        var map2 = (MmMap)node2;
        Assert.Equal(2, map2.Entries.Count);
    }
}
