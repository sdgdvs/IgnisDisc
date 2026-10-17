namespace CdBurner.Core.Compilation;

/// <summary>
/// Specifies how duplicate or conflicting file names are resolved when adding items to a compilation directory.
/// </summary>
public enum DuplicateFilePolicy
{
    /// <summary>
    /// Overwrites and replaces any existing item with the same name.
    /// </summary>
    Overwrite,

    /// <summary>
    /// Renames the incoming item with an incremental number suffix, e.g. "report (2).pdf".
    /// </summary>
    RenameWithNumberSuffix,

    /// <summary>
    /// Skips adding the incoming item and leaves the existing item untouched.
    /// </summary>
    Skip,

    /// <summary>
    /// Throws an <see cref="InvalidOperationException"/> if an item with the same name already exists.
    /// </summary>
    ThrowError
}
