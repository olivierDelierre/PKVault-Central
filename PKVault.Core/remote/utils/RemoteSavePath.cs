namespace PKVault.Core;

/// <summary>
/// Remote saves are stored as {remoteRoot}/{deviceId}/{saveId}/{fileName}.
/// </summary>
public static class RemoteSavePath
{
    public static string GetSaveDirectory(string remoteRoot, string deviceId, uint saveId)
        => Path.Combine(remoteRoot, deviceId, saveId.ToString());

    /// <summary>
    /// True if the path is anywhere under the remote saves root.
    /// Paths can be relative (ex: "./remote-saves/...") or absolute.
    /// </summary>
    public static bool IsUnderRoot(string remoteRoot, string path)
        => TrySplit(remoteRoot, path, out _);

    public static bool TryParse(string remoteRoot, string path, out string deviceId, out uint saveId)
    {
        deviceId = string.Empty;
        saveId = 0;

        // deviceId / saveId / fileName
        if (!TrySplit(remoteRoot, path, out var segments) || segments.Length != 3 || !uint.TryParse(segments[1], out saveId))
        {
            return false;
        }

        deviceId = segments[0];
        return true;
    }

    private static bool TrySplit(string remoteRoot, string path, out string[] segments)
    {
        segments = [];

        var root = MatcherUtil.NormalizePath(Path.GetFullPath(remoteRoot)).TrimEnd('/') + "/";
        var normalized = MatcherUtil.NormalizePath(Path.GetFullPath(path));

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!normalized.StartsWith(root, comparison))
        {
            return false;
        }

        segments = normalized[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return true;
    }
}
