using LTAI.Mm;
using LTAI.Mm.Core;
using LTAI.Mm.Ir;
using LTAI.Mm.Tree;
using Xunit;

namespace LTAI.Mm.Tests;

public class SimplePerson
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class Person
{
    [MM("desc=用户ID")]
    public long Id { get; set; }

    [MM("desc=用户名; min=1; max=50")]
    public string Name { get; set; } = "";

    [MM("type=email; desc=电子邮箱")]
    public string Email { get; set; } = "";

    [MM("desc=年龄; min=0; max=150")]
    public byte Age { get; set; }

    [MM("desc=是否激活")]
    public bool IsActive { get; set; }

    [MM("-")]
    public string InternalNotes { get; set; } = "";
}

public class ReflectionTests
{
    [Fact]
    public void Encode_Decode_SimplePerson()
    {
        var p = new SimplePerson { Name = "Alice", Age = 30 };
        byte[] data = MetaMessage.Encode(p);
        var restored = MetaMessage.Decode<SimplePerson>(data);
        Assert.Equal(p.Name, restored.Name);
        Assert.Equal(p.Age, restored.Age);
    }

    [Fact]
    public void Encode_Decode_Person_Roundtrip()
    {
        var person = new Person
        {
            Id = 1001,
            Name = "Alice",
            Email = "alice@example.com",
            Age = 30,
            IsActive = true,
            InternalNotes = "secret"
        };

        byte[] data = MetaMessage.Encode(person);
        var restored = MetaMessage.Decode<Person>(data);

        Assert.Equal(person.Id, restored.Id);
        Assert.Equal(person.Name, restored.Name);
        Assert.Equal(person.Email, restored.Email);
        Assert.Equal(person.Age, restored.Age);
        Assert.Equal(person.IsActive, restored.IsActive);
        Assert.Equal("", restored.InternalNotes);
    }

    [Fact]
    public void Encode_Decode_List()
    {
        var list = new List<int> { 1, 2, 3, 4, 5 };
        byte[] data = MetaMessage.Encode(list);
        var restored = MetaMessage.Decode<List<int>>(data);
        Assert.Equal(list, restored);
    }

    [Fact]
    public void Encode_Decode_Dictionary()
    {
        var dict = new Dictionary<string, int> { { "a", 1 }, { "b", 2 }, { "c", 3 } };
        byte[] data = MetaMessage.Encode(dict);
        var restored = MetaMessage.Decode<Dictionary<string, int>>(data);
        Assert.Equal(dict, restored);
    }

    [Fact]
    public void Encode_Decode_Array()
    {
        int[] arr = [10, 20, 30];
        byte[] data = MetaMessage.Encode(arr);
        var restored = MetaMessage.Decode<int[]>(data);
        Assert.Equal(arr, restored);
    }

    [Fact]
    public void Encode_Decode_Nullable_Types()
    {
        int? value = 42;
        byte[] data = MetaMessage.Encode(value);
        var restored = MetaMessage.Decode<int?>(data);
        Assert.Equal(42, restored);
    }

    [Fact]
    public void Decode_To_Existing_Object()
    {
        var person = new Person { Name = "Original" };
        byte[] data = MetaMessage.Encode(new Person { Name = "Updated", Age = 25 });

        MetaMessage.Decode(data, person);
        Assert.Equal("Updated", person.Name);
        Assert.Equal(25, person.Age);
    }

    [Fact]
    public void TypeInfer_Int()
    {
        Assert.Equal(MmValueType.I, MetaMessage.InferType(typeof(int)));
        Assert.Equal(MmValueType.I64, MetaMessage.InferType(typeof(long)));
        Assert.Equal(MmValueType.Str, MetaMessage.InferType(typeof(string)));
        Assert.Equal(MmValueType.Bool, MetaMessage.InferType(typeof(bool)));
        Assert.Equal(MmValueType.F64, MetaMessage.InferType(typeof(double)));
        Assert.Equal(MmValueType.DateTime, MetaMessage.InferType(typeof(DateTime)));
        Assert.Equal(MmValueType.Uuid, MetaMessage.InferType(typeof(Guid)));
    }

    [Fact]
    public void FromValue_With_Tag()
    {
        byte[] data = MetaMessage.FromValue(42, "type=i; desc=答案");
        var tree = MetaMessage.DecodeToTree(data);
        Assert.NotNull(tree);
        if (tree is NodeScalar scalar)
        {
            Assert.NotNull(scalar.Tag);
            Assert.Equal("答案", scalar.Tag!.Desc);
        }
    }

    [Fact]
    public void Encode_Null()
    {
        byte[] data = MetaMessage.Encode<object?>(null);
        var tree = MetaMessage.DecodeToTree(data);
        Assert.NotNull(tree);
    }
}
