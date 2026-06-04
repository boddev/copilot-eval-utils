using System.Text.Json;
using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

/// <summary>
/// Pins the <see cref="DatasetRow"/> wire-shape serializer to a flat
/// <c>{k:v}</c> object that round-trips through the same
/// <see cref="JsonElementConverter"/> the readers use. Without these
/// tests the parity envelopes used by the cross-runtime harness would
/// silently emit <c>{Count, Entries}</c>, breaking every comparison
/// against TS.
/// </summary>
public class DatasetRowJsonConverterTests
{
    [Fact]
    public void Serialize_EmitsFlatObject_NotSurfaceMembers()
    {
        var row = new DatasetRow();
        row.Set("id", 1L);
        row.Set("name", "Alice");

        string json = JsonSerializer.Serialize(row);

        Assert.Equal("{\"id\":1,\"name\":\"Alice\"}", json);
    }

    [Fact]
    public void Serialize_PreservesInsertionOrder()
    {
        var row = new DatasetRow();
        row.Set("z", 1L);
        row.Set("a", 2L);
        row.Set("m", 3L);

        string json = JsonSerializer.Serialize(row);

        Assert.Equal("{\"z\":1,\"a\":2,\"m\":3}", json);
    }

    [Fact]
    public void Serialize_HandlesAllPrimitiveTypes()
    {
        var row = new DatasetRow();
        row.Set("s", "hello");
        row.Set("i", 42L);
        row.Set("d", 3.14);
        row.Set("b", true);
        row.Set("n", null);

        string json = JsonSerializer.Serialize(row);

        Assert.Equal("{\"s\":\"hello\",\"i\":42,\"d\":3.14,\"b\":true,\"n\":null}", json);
    }

    [Fact]
    public void Serialize_NestedDatasetRow_RecursesIntoNestedObject()
    {
        var inner = new DatasetRow();
        inner.Set("a", 1L);
        var outer = new DatasetRow();
        outer.Set("nested", inner);

        string json = JsonSerializer.Serialize(outer);

        Assert.Equal("{\"nested\":{\"a\":1}}", json);
    }

    [Fact]
    public void Serialize_ListValue_EmitsJsonArray()
    {
        var row = new DatasetRow();
        row.Set("arr", new List<object?> { 1L, "two", true });

        string json = JsonSerializer.Serialize(row);

        Assert.Equal("{\"arr\":[1,\"two\",true]}", json);
    }

    [Fact]
    public void Serialize_DictionaryValue_EmitsJsonObject_NotArrayOfPairs()
    {
        // Round-6 GPT-5.5 finding: IDictionary implements IEnumerable,
        // so without an explicit dictionary case it serialized as an
        // array of KeyValuePair entries. Pin the fix so future readers
        // that emit Dictionary<string, object?> stay shape-stable.
        var row = new DatasetRow();
        row.Set("dict", new Dictionary<string, object?>
        {
            { "a", 1L },
            { "b", "two" },
        });

        string json = JsonSerializer.Serialize(row);

        Assert.Equal("{\"dict\":{\"a\":1,\"b\":\"two\"}}", json);
    }

    [Fact]
    public void Serialize_NonStringDictionaryKey_CoercesToString()
    {
        var row = new DatasetRow();
        row.Set("dict", new Dictionary<int, string>
        {
            { 1, "one" },
            { 2, "two" },
        });

        string json = JsonSerializer.Serialize(row);

        Assert.Contains("\"1\":\"one\"", json);
        Assert.Contains("\"2\":\"two\"", json);
    }

    [Fact]
    public void Deserialize_RoundTrip_PreservesShapeAndOrder()
    {
        const string json = "{\"z\":1,\"a\":\"two\",\"m\":null}";

        var row = JsonSerializer.Deserialize<DatasetRow>(json);

        Assert.NotNull(row);
        Assert.Equal(new[] { "z", "a", "m" },
                     row!.Entries.Select(e => e.Key).ToArray());
        Assert.Equal(1L, row["z"]);
        Assert.Equal("two", row["a"]);
        Assert.Null(row["m"]);
    }

    [Fact]
    public void Deserialize_NestedObject_BecomesNestedDatasetRow()
    {
        const string json = "{\"outer\":{\"inner\":42}}";

        var row = JsonSerializer.Deserialize<DatasetRow>(json);

        var nested = Assert.IsType<DatasetRow>(row!["outer"]);
        Assert.Equal(42L, nested["inner"]);
    }
}
