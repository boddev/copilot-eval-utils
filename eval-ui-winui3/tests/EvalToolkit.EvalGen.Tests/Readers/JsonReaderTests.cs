using EvalToolkit.Core;
using EvalToolkit.EvalGen.Readers;

namespace EvalToolkit.EvalGen.Tests.Readers;

public class JsonReaderTests : IDisposable
{
    private readonly string _tmpDir;

    public JsonReaderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "evaltoolkit-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
        {
            Directory.Delete(_tmpDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private string Write(string fileName, string content, bool bom = false)
    {
        string path = Path.Combine(_tmpDir, fileName);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(bom));
        return path;
    }

    [Fact]
    public void Read_JsonArrayOfObjects_ReturnsAllRecords()
    {
        string path = Write("arr.json", "[{\"id\":1,\"name\":\"A\"},{\"id\":2,\"name\":\"B\"}]");
        var result = new JsonReader().Read(path);
        Assert.Equal(InputFormat.Json, result.Format);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal(1L, result.Records[0]["id"]);
        Assert.Equal("A", result.Records[0]["name"]);
    }

    [Fact]
    public void Read_SingleJsonObject_WrappedInList()
    {
        string path = Write("one.json", "{\"k\":\"v\"}");
        var result = new JsonReader().Read(path);
        Assert.Single(result.Records);
        Assert.Equal("v", result.Records[0]["k"]);
    }

    [Fact]
    public void Read_NumericValues_DistinguishIntFromFloat()
    {
        string path = Write("nums.json", "[{\"i\":42,\"f\":3.14}]");
        var result = new JsonReader().Read(path);
        Assert.Equal(42L, result.Records[0]["i"]);
        Assert.Equal(3.14, result.Records[0]["f"]);
    }

    [Fact]
    public void Read_BooleansAndNulls_PreservedAsClrTypes()
    {
        string path = Write("bn.json", "[{\"t\":true,\"f\":false,\"n\":null}]");
        var result = new JsonReader().Read(path);
        Assert.Equal(true, result.Records[0]["t"]);
        Assert.Equal(false, result.Records[0]["f"]);
        Assert.Null(result.Records[0]["n"]);
    }

    [Fact]
    public void Read_NestedObjectsAndArrays_DecodeToNestedRowAndList()
    {
        string path = Write("nested.json", "[{\"obj\":{\"a\":1},\"arr\":[1,2,3]}]");
        var result = new JsonReader().Read(path);
        var nestedObj = Assert.IsType<DatasetRow>(result.Records[0]["obj"]);
        Assert.Equal(1L, nestedObj["a"]);
        var nestedArr = Assert.IsAssignableFrom<IReadOnlyList<object?>>(result.Records[0]["arr"]);
        Assert.Equal(new object?[] { 1L, 2L, 3L }, nestedArr.ToArray());
    }

    [Fact]
    public void Read_PreservesInsertionOrder()
    {
        string path = Write("ord.json", "[{\"z\":1,\"a\":2,\"m\":3}]");
        string[] keys = new JsonReader().Read(path).Records[0].Entries
            .Select(e => e.Key).ToArray();
        Assert.Equal(new[] { "z", "a", "m" }, keys);
    }

    [Fact]
    public void Read_BomPrefixed_Throws_MatchingTS()
    {
        // Verified ground truth (round 5):
        //   JSON.parse('\uFEFF{"a":1}') → SyntaxError
        // System.Text.Json's JsonDocument.Parse also throws on a
        // BOM-prefixed input. By reading raw bytes (no auto-BOM
        // detection) we keep the C# reader byte-faithful to TS.
        string path = Write("bom.json", "[{\"id\":1}]", bom: true);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => new JsonReader().Read(path));
    }

    [Fact]
    public void Read_TopLevelString_ThrowsWithTsMatchingMessage()
    {
        string path = Write("str.json", "\"hello\"");
        var ex = Assert.Throws<InvalidDataException>(() => new JsonReader().Read(path));
        Assert.Contains("must contain an array of objects or a single object", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_TopLevelNumber_ThrowsWithTsMatchingMessage()
    {
        string path = Write("num.json", "42");
        var ex = Assert.Throws<InvalidDataException>(() => new JsonReader().Read(path));
        Assert.Contains("must contain an array of objects or a single object", ex.Message, StringComparison.Ordinal);
    }
}
