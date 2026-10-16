using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PKVault.Core;

public class FileIOServiceTests
{
    private readonly MockFileSystem mockFileSystem;
    private readonly FileIOService fileIOService;
    private readonly string root = Directory.GetCurrentDirectory();

    public FileIOServiceTests()
    {
        mockFileSystem = new(new Dictionary<string, MockFileData>(), root);
        fileIOService = new FileIOService(mockFileSystem);
    }

    [Fact]
    public async Task WriteBytes_ShouldWriteContent_AndLeaveNoTempFile()
    {
        var path = Path.Combine(root, "dir", "file.bin");

        await fileIOService.WriteBytes(path, [1, 2, 3]);

        Assert.Equal([1, 2, 3], mockFileSystem.File.ReadAllBytes(path));
        Assert.DoesNotContain(mockFileSystem.AllFiles, f => f.Contains(".tmp-"));
    }

    [Fact]
    public async Task WriteBytes_ShouldReplaceExistingContent()
    {
        var path = Path.Combine(root, "file.bin");
        mockFileSystem.AddFile(path, new MockFileData([9, 9, 9, 9, 9]));

        await fileIOService.WriteBytes(path, [1, 2]);

        Assert.Equal([1, 2], mockFileSystem.File.ReadAllBytes(path));
        Assert.DoesNotContain(mockFileSystem.AllFiles, f => f.Contains(".tmp-"));
    }

    [Fact]
    public async Task WriteJSONFile_ShouldKeepPreviousContent_AndCleanTemp_WhenSerializationFailsMidWrite()
    {
        var path = Path.Combine(root, "data.json");
        mockFileSystem.AddFile(path, new MockFileData("{\"ok\":true}"));

        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Converters = { new ThrowingConverter() },
        };
        var typeInfo = (JsonTypeInfo<Payload>)options.GetTypeInfo(typeof(Payload));

        await Assert.ThrowsAnyAsync<Exception>(() => fileIOService.WriteJSONFile(path, typeInfo, new Payload()));

        Assert.Equal("{\"ok\":true}", mockFileSystem.File.ReadAllText(path));
        Assert.DoesNotContain(mockFileSystem.AllFiles, f => f.Contains(".tmp-"));
    }

    private class Payload;

    private class ThrowingConverter : JsonConverter<Payload>
    {
        public override Payload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Payload value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("partial");
            throw new IOException("simulated failure mid-write");
        }
    }
}
