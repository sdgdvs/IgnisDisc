namespace CdBurner.Core.Compilation;

/// <summary>
/// Builds a <see cref="CompilationDirectory"/> tree from a folder on disk.
/// </summary>
public static class CompilationFolderImporter
{
    /// <summary>
    /// Adds every file and subfolder of <paramref name="sourceFolder"/> to <paramref name="target"/>.
    /// </summary>
    /// <param name="strict">
    /// When true, any file or folder that cannot be read throws, so nothing is silently left off a disc (archives).
    /// When false, unreadable entries are skipped (the CLI's historical behaviour for ad-hoc data discs).
    /// </param>
    /// <param name="skipFile">Optional filter; return true to leave a file out.</param>
    public static void Import(CompilationDirectory target, string sourceFolder, bool strict, Func<string, bool>? skipFile = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var dirInfo = new DirectoryInfo(sourceFolder);
        if (!dirInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Folder not found: {sourceFolder}");
        }

        foreach (var file in dirInfo.GetFiles())
        {
            try
            {
                if (skipFile?.Invoke(file.FullName) == true)
                {
                    continue;
                }
                target.AddFile(new CompilationFile(file.FullName, file.Length));
            }
            catch (Exception ex) when (!strict && ex is not OperationCanceledException)
            {
                // Non-strict: skip unreadable or rejected files.
            }
        }

        foreach (var subDir in dirInfo.GetDirectories())
        {
            try
            {
                var child = target.AddDirectory(subDir.Name);
                Import(child, subDir.FullName, strict, skipFile);
            }
            catch (Exception ex) when (!strict && ex is not OperationCanceledException)
            {
                // Non-strict: skip unreadable folders.
            }
        }
    }

    /// <summary>Creates a root directory populated from <paramref name="sourceFolder"/>.</summary>
    public static CompilationDirectory ImportAsRoot(string sourceFolder, bool strict)
    {
        var root = CompilationDirectory.CreateRoot();
        Import(root, sourceFolder, strict);
        return root;
    }
}
