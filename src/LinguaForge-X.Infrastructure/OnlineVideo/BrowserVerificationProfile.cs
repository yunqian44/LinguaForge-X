namespace LinguaForge_X.Infrastructure.OnlineVideo;

/// <summary>A disposable browser directory owned by one session, never a user's browser profile.</summary>
internal sealed class BrowserVerificationProfile
{
    internal string Root { get; }
    internal string DirectoryPath { get; }
    private readonly string _id;

    internal BrowserVerificationProfile(string root)
    {
        Root = Path.GetFullPath(root);
        RejectLinkedAncestors(Root);
        Directory.CreateDirectory(Root);
        _id = Guid.NewGuid().ToString("N");
        DirectoryPath = Path.Combine(Root, _id);
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, ".linguaforge-verification"), _id);
    }

    internal void Delete()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return;
            if (!Path.GetDirectoryName(Path.GetFullPath(DirectoryPath))!.Equals(Root, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(DirectoryPath).Equals(_id, StringComparison.Ordinal) || !Guid.TryParseExact(_id, "N", out _)) return;
            RejectLinkedAncestors(DirectoryPath);
            var marker = new FileInfo(Path.Combine(DirectoryPath, ".linguaforge-verification"));
            if (!marker.Exists || marker.Length != _id.Length || (marker.Attributes & FileAttributes.ReparsePoint) != 0 ||
                File.ReadAllText(marker.FullName) != _id) return;
            DeleteContents(DirectoryPath, marker.FullName);
            File.Delete(marker.FullName);
            Directory.Delete(DirectoryPath, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { /* A locked or unexpectedly replaced profile is safer to leave than traverse. */ }
    }

    internal async Task DeleteAsync()
    {
        // Browser child processes may briefly retain file handles after the main process closes.
        // Keep and recheck the ownership marker on every attempt until all other entries are gone.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            Delete();
            if (!Directory.Exists(DirectoryPath)) return;
            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    private static void DeleteContents(string directory, string ownershipMarker)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.FullName.Equals(ownershipMarker, StringComparison.OrdinalIgnoreCase)) continue;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Removing a link itself never recurses into its destination.
                if ((entry.Attributes & FileAttributes.Directory) != 0) Directory.Delete(entry.FullName, false);
                else File.Delete(entry.FullName);
            }
            else if (entry is DirectoryInfo)
            {
                DeleteContents(entry.FullName, ownershipMarker);
                Directory.Delete(entry.FullName, false);
            }
            else File.Delete(entry.FullName);
        }
    }

    private static void RejectLinkedAncestors(string path)
    {
        for (DirectoryInfo? item = new(path); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("验证浏览器目录不能使用符号链接或目录联接，请使用普通的本地目录。");
    }
}
