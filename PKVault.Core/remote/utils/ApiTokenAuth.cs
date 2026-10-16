using System.Security.Cryptography;
using System.Text;

namespace PKVault.Core;

public static class ApiTokenAuth
{
    /// <summary>
    /// Game sprites are static assets loaded by image tags, which cannot send the token.
    /// Nothing user-related is exposed there.
    /// </summary>
    public static bool RequiresToken(string httpMethod, string path)
    {
        const string spritesheetPrefix = "/api/static-data/spritesheet/";

        if (!string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase)
            || !path.StartsWith(spritesheetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // only the sheet name is allowed after the prefix: nothing that could point elsewhere
        var sheetName = path[spritesheetPrefix.Length..];
        var isSimpleName = sheetName.Length > 0
            && sheetName != ".."
            && sheetName.IndexOfAny(['/', '\\', '%']) == -1;

        return !isSimpleName;
    }

    /// <summary>
    /// No token configured means no authentication (default, same as before).
    /// Otherwise the request must send "Authorization: Bearer {token}".
    /// </summary>
    public static bool IsAuthorized(string? expectedToken, string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(expectedToken))
        {
            return true;
        }

        const string prefix = "Bearer ";
        if (authorizationHeader == null || !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var givenToken = authorizationHeader[prefix.Length..].Trim();

        // hash both to compare values of the same length in constant time
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken));
        var givenHash = SHA256.HashData(Encoding.UTF8.GetBytes(givenToken));
        return CryptographicOperations.FixedTimeEquals(expectedHash, givenHash);
    }
}
