namespace CdBurner.Core.Compilation;

/// <summary>
/// Builds a <see cref="CompilationDirectory"/> tree from a folder on disk.
/// </summary>
public static class CompilationFolderImporter
{
    public const int MaxDirectoryDepth = 64;

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
        var activeAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ImportCore(target, sourceFolder, strict, skipFile, activeAncestors, depth: 0);
    }

    private static void ImportCore(
        CompilationDirectory target,
        string sourceFolder,
        bool strict,
        Func<string, bool>? skipFile,
        HashSet<string> activeAncestors,
        int depth)
    {
        var dirInfo = new DirectoryInfo(sourceFolder);
        if (!dirInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Folder not found: {sourceFolder}");
        }

        if (depth > MaxDirectoryDepth)
        {
            return;
        }

        string canonicalPath = ResolveCanonicalDirectoryPath(dirInfo);
        if (!activeAncestors.Add(canonicalPath))
        {
            return;
        }

        try
        {
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
                    string subCanonical = ResolveCanonicalDirectoryPath(subDir);
                    if (activeAncestors.Contains(subCanonical))
                    {
                        // Circular NTFS junction or directory symlink pointing to an ancestor: skip
                        continue;
                    }

                    var child = target.AddDirectory(subDir.Name);
                    ImportCore(child, subDir.FullName, strict, skipFile, activeAncestors, depth + 1);
                }
                catch (Exception ex) when (!strict && ex is not OperationCanceledException)
                {
                    // Non-strict: skip unreadable folders.
                }
            }
        }
        finally
        {
            activeAncestors.Remove(canonicalPath);
        }
    }

    private static string ResolveCanonicalDirectoryPath(DirectoryInfo dirInfo)
    {
        string path = dirInfo.FullName;
        try
        {
            if (dirInfo.LinkTarget != null && dirInfo.ResolveLinkTarget(returnFinalTarget: true) is FileSystemInfo resolved)
            {
                path = resolved.FullName;
            }
        }
        catch
        {
        }

        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Creates a root directory populated from <paramref name="sourceFolder"/>.</summary>
    public static CompilationDirectory ImportAsRoot(string sourceFolder, bool strict)
    {
        var root = CompilationDirectory.CreateRoot();
        Import(root, sourceFolder, strict);
        return root;
    }
}
