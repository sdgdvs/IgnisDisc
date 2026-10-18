namespace CdBurner.Core.Audio;

/// <summary>
/// Exception thrown when an audio file cannot be decoded or burned because it is encrypted
/// with Digital Rights Management (DRM), such as Apple FairPlay DRM (.m4p) or Windows Media DRM.
/// </summary>
public class DrmProtectedAudioException : NotSupportedException
{
    /// <summary>
    /// File path of the protected audio file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Description of the detected DRM scheme (e.g. Apple FairPlay DRM).
    /// </summary>
    public string DrmType { get; }

    public DrmProtectedAudioException(string filePath, string drmType = "Apple FairPlay DRM", string? message = null)
        : base(message ?? $"The audio file '{Path.GetFileName(filePath)}' is encrypted with {drmType} and cannot be decoded or burned to an Audio CD. Apple Music subscription tracks are protected and cannot be copied to physical media. Please use DRM-free audio files (.wav, .mp3, .flac, .m4a, .ogg, .wma).")
    {
        FilePath = filePath;
        DrmType = drmType;
    }
}
