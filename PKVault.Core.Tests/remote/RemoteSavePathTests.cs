using PKVault.Core;

public class RemoteSavePathTests
{
    private readonly string root = Path.Combine(Directory.GetCurrentDirectory(), "remote-saves");

    [Fact]
    public void TryParse_ShouldReadDeviceAndSaveId_FromAbsolutePath()
    {
        var path = Path.Combine(root, "device-1", "12345", "main.sav");

        Assert.True(RemoteSavePath.TryParse(root, path, out var deviceId, out var saveId));
        Assert.Equal("device-1", deviceId);
        Assert.Equal(12345u, saveId);
    }

    [Fact]
    public void TryParse_ShouldAcceptRelativePath_AsReturnedByGlobSearch()
    {
        // the glob matcher returns paths relative to the app directory
        Assert.True(RemoteSavePath.TryParse(root, "./remote-saves/device-1/12345/main.sav", out var deviceId, out var saveId));
        Assert.Equal("device-1", deviceId);
        Assert.Equal(12345u, saveId);
    }

    [Theory]
    [InlineData("saves-uploads/main.sav")]
    [InlineData("remote-saves-other/device-1/12345/main.sav")]
    [InlineData("remote-saves/device-1/main.sav")]               // missing save folder
    [InlineData("remote-saves/device-1/not-a-number/main.sav")]
    [InlineData("remote-saves/device-1/12345/sub/main.sav")]     // too deep
    public void TryParse_ShouldReject_PathsOutOfExpectedLayout(string relativePath)
    {
        Assert.False(RemoteSavePath.TryParse(root, Path.Combine(Directory.GetCurrentDirectory(), relativePath), out _, out _));
    }

    [Fact]
    public void IsUnderRoot_ShouldBeTrue_ForAnyDepth_ButNotForSiblings()
    {
        Assert.True(RemoteSavePath.IsUnderRoot(root, Path.Combine(root, "a", "b", "c", "d.sav")));
        Assert.True(RemoteSavePath.IsUnderRoot(root, Path.Combine(root, "stray.sav")));
        Assert.False(RemoteSavePath.IsUnderRoot(root, root + "-other/a.sav"));
        Assert.False(RemoteSavePath.IsUnderRoot(root, Path.Combine(Directory.GetCurrentDirectory(), "saves-uploads", "a.sav")));
    }

    [Fact]
    public void TryParse_ShouldNotEscapeRoot_WithParentSegments()
    {
        var path = Path.Combine(root, "..", "elsewhere", "12345", "main.sav");

        Assert.False(RemoteSavePath.TryParse(root, path, out _, out _));
    }

    [Fact]
    public void GetSaveDirectory_ShouldMatchParsedLayout()
    {
        var dir = RemoteSavePath.GetSaveDirectory(root, "device-1", 12345);

        Assert.True(RemoteSavePath.TryParse(root, Path.Combine(dir, "main.sav"), out var deviceId, out var saveId));
        Assert.Equal("device-1", deviceId);
        Assert.Equal(12345u, saveId);
    }
}
