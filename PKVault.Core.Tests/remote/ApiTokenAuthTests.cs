using PKVault.Core;

public class ApiTokenAuthTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer anything")]
    public void IsAuthorized_ShouldAllowEverything_WhenNoTokenConfigured(string? header)
    {
        Assert.True(ApiTokenAuth.IsAuthorized(null, header));
        Assert.True(ApiTokenAuth.IsAuthorized("", header));
    }

    [Theory]
    [InlineData("GET", "/api/static-data/spritesheet/species", false)]
    [InlineData("get", "/API/Static-Data/Spritesheet/items", false)]
    [InlineData("POST", "/api/static-data/spritesheet/species", true)]
    [InlineData("GET", "/api/static-data", true)]
    [InlineData("GET", "/api/static-data/spritesheet", true)]
    [InlineData("GET", "/api/storage/main/bank", true)]
    [InlineData("GET", "/api/remote/transfers/abc/download", true)]
    [InlineData("GET", "/api/static-data/spritesheet/", true)]
    [InlineData("GET", "/api/static-data/spritesheet/..", true)]
    [InlineData("GET", "/api/static-data/spritesheet/a/b", true)]
    [InlineData("GET", "/api/static-data/spritesheet/../../storage/main/bank", true)]
    [InlineData("GET", "/api/static-data/spritesheet/..%2f..%2fstorage", true)]
    [InlineData("GET", "/api/static-data/spritesheet/..\\storage", true)]
    public void RequiresToken_ShouldOnlyExemptSpritesheetsReading(string method, string path, bool expected)
    {
        // anything with the prefix but a sheet name that could point elsewhere stays token-protected,
        // even if routing would not resolve it: the rule itself must not allow it
        Assert.Equal(expected, ApiTokenAuth.RequiresToken(method, path));
    }

    [Fact]
    public void IsAuthorized_ShouldAcceptCorrectToken()
    {
        Assert.True(ApiTokenAuth.IsAuthorized("s3cret-token", "Bearer s3cret-token"));
    }

    [Fact]
    public void IsAuthorized_ShouldAcceptSchemeInAnyCase_AndSurroundingSpaces()
    {
        Assert.True(ApiTokenAuth.IsAuthorized("s3cret-token", "bearer   s3cret-token  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s3cret-token")]                // no scheme
    [InlineData("Basic s3cret-token")]          // wrong scheme
    [InlineData("Bearer ")]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer s3cret-token-longer")]  // different length must not throw
    [InlineData("Bearer s3cret-toke")]
    [InlineData("Bearer S3CRET-TOKEN")]         // token is case-sensitive
    public void IsAuthorized_ShouldRejectMissingOrWrongToken(string? header)
    {
        Assert.False(ApiTokenAuth.IsAuthorized("s3cret-token", header));
    }
}
