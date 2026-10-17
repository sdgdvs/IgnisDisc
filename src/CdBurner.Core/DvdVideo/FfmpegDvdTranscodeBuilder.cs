namespace CdBurner.Core.DvdVideo;

/// <summary>
/// DVD-Video broadcast television system standard.
/// </summary>
public enum DvdVideoStandard
{
    /// <summary>
    /// National Television System Committee (NTSC): 720x480 resolution at 29.97 frames/second.
    /// Used predominantly in North America, Japan, and South Korea.
    /// </summary>
    Ntsc,

    /// <summary>
    /// Phase Alternating Line (PAL): 720x576 resolution at 25 frames/second.
    /// Used predominantly in Europe, Australia, and Asia.
    /// </summary>
    Pal
}

/// <summary>
/// DVD-Video display aspect ratio.
/// </summary>
public enum DvdAspectRatio
{
    /// <summary>
    /// Widescreen anamorphic 16:9 aspect ratio.
    /// </summary>
    Widescreen16x9,

    /// <summary>
    /// Standard full-frame 4:3 aspect ratio.
    /// </summary>
    Standard4x3
}

/// <summary>
/// Builds deterministic FFmpeg command-line arguments for transcoding video files
/// into fully compliant DVD-Video MPEG-2 program stream VOB files (NTSC or PAL).
/// </summary>
public sealed class FfmpegDvdTranscodeBuilder
{
    /// <summary>
    /// Source video file path.
    /// </summary>
    public string? InputPath { get; set; }

    /// <summary>
    /// Destination .vob or .mpg file path.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Target television standard (NTSC or PAL). Default: NTSC.
    /// </summary>
    public DvdVideoStandard Standard { get; set; } = DvdVideoStandard.Ntsc;

    /// <summary>
    /// Target display aspect ratio. Default: 16:9 Widescreen.
    /// </summary>
    public DvdAspectRatio AspectRatio { get; set; } = DvdAspectRatio.Widescreen16x9;

    /// <summary>
    /// Whether to force overwrite the output file without prompting (-y). Default: true.
    /// </summary>
    public bool OverwriteOutput { get; set; } = true;

    /// <summary>
    /// Optional target video bitrate in kilobits per second (e.g. 6000 for 6 Mbps).
    /// </summary>
    public int? VideoBitrateKbps { get; set; }

    /// <summary>
    /// Optional maximum video bitrate in kilobits per second (e.g. 8000 for 8 Mbps).
    /// DVD-Video maximum total bitrate is 9800 kbps (video + audio).
    /// </summary>
    public int? MaxVideoBitrateKbps { get; set; }

    /// <summary>
    /// Optional VBV buffer size in kilobits (e.g. 1835 kb for DVD).
    /// </summary>
    public int? BufferSizeKb { get; set; }

    /// <summary>
    /// Audio codec. Default: "ac3" (Dolby Digital).
    /// </summary>
    public string AudioCodec { get; set; } = "ac3";

    /// <summary>
    /// Audio bitrate in kilobits per second. Default: 192 (stereo) or 448 (5.1 surround).
    /// </summary>
    public int AudioBitrateKbps { get; set; } = 192;

    /// <summary>
    /// Audio sample rate in Hertz. DVD-Video requires 48,000 Hz. Default: 48000.
    /// </summary>
    public int AudioSampleRateHz { get; set; } = 48000;

    /// <summary>
    /// Audio channel count (2 for stereo, 6 for 5.1 surround). Default: 2.
    /// </summary>
    public int AudioChannels { get; set; } = 2;

    public FfmpegDvdTranscodeBuilder WithInput(string inputPath)
    {
        InputPath = inputPath;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithOutput(string outputPath)
    {
        OutputPath = outputPath;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithStandard(DvdVideoStandard standard)
    {
        Standard = standard;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithAspectRatio(DvdAspectRatio aspectRatio)
    {
        AspectRatio = aspectRatio;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithVideoBitrate(int bitrateKbps, int? maxRateKbps = null, int? bufferSizeKb = null)
    {
        VideoBitrateKbps = bitrateKbps;
        MaxVideoBitrateKbps = maxRateKbps;
        BufferSizeKb = bufferSizeKb;
        return this;
    }

    public FfmpegDvdTranscodeBuilder WithAudio(string codec = "ac3", int bitrateKbps = 192, int channels = 2)
    {
        AudioCodec = codec;
        AudioBitrateKbps = bitrateKbps;
        AudioChannels = channels;
        return this;
    }

    /// <summary>
    /// Builds a deterministic, ordered list of CLI arguments suitable for <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    public IReadOnlyList<string> BuildArgumentList()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            throw new InvalidOperationException("Input path must be specified.");
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            throw new InvalidOperationException("Output path must be specified.");
        }

        var args = new List<string>();

        if (OverwriteOutput)
        {
            args.Add("-y");
        }

        args.Add("-i");
        args.Add(InputPath);

        // Target preset: -target ntsc-dvd or -target pal-dvd
        string target = Standard switch
        {
            DvdVideoStandard.Ntsc => "ntsc-dvd",
            DvdVideoStandard.Pal => "pal-dvd",
            _ => throw new ArgumentOutOfRangeException(nameof(Standard))
        };
        args.Add("-target");
        args.Add(target);

        // Aspect ratio: -aspect 16:9 or -aspect 4:3
        string aspect = AspectRatio switch
        {
            DvdAspectRatio.Widescreen16x9 => "16:9",
            DvdAspectRatio.Standard4x3 => "4:3",
            _ => "16:9"
        };
        args.Add("-aspect");
        args.Add(aspect);

        // Optional custom video bitrates
        if (VideoBitrateKbps.HasValue)
        {
            args.Add("-b:v");
            args.Add($"{VideoBitrateKbps.Value}k");
        }

        if (MaxVideoBitrateKbps.HasValue)
        {
            args.Add("-maxrate");
            args.Add($"{MaxVideoBitrateKbps.Value}k");
        }

        if (BufferSizeKb.HasValue)
        {
            args.Add("-bufsize");
            args.Add($"{BufferSizeKb.Value}k");
        }

        // Audio parameters
        if (!string.IsNullOrWhiteSpace(AudioCodec))
        {
            args.Add("-c:a");
            args.Add(AudioCodec);
        }

        if (AudioBitrateKbps > 0)
        {
            args.Add("-b:a");
            args.Add($"{AudioBitrateKbps}k");
        }

        if (AudioSampleRateHz > 0)
        {
            args.Add("-ar");
            args.Add(AudioSampleRateHz.ToString());
        }

        if (AudioChannels > 0)
        {
            args.Add("-ac");
            args.Add(AudioChannels.ToString());
        }

        args.Add(OutputPath);

        return args;
    }

    /// <summary>
    /// Builds the argument string (excluding the ffmpeg executable name) with proper shell escaping.
    /// </summary>
    public string BuildArguments()
    {
        var list = BuildArgumentList();
        return string.Join(" ", list.Select(EscapeCliArgument));
    }

    /// <summary>
    /// Builds the full command line string (e.g. ffmpeg -y -i "input.mp4" -target ntsc-dvd -aspect 16:9 "output.vob").
    /// </summary>
    public string BuildCommandLine(string ffmpegExecutable = "ffmpeg")
    {
        string escapedExe = EscapeCliArgument(ffmpegExecutable);
        string args = BuildArguments();
        return $"{escapedExe} {args}";
    }

    /// <summary>
    /// Creates a builder configured for NTSC DVD transcoding.
    /// </summary>
    public static FfmpegDvdTranscodeBuilder ForNtsc(
        string inputPath,
        string outputPath,
        DvdAspectRatio aspectRatio = DvdAspectRatio.Widescreen16x9)
    {
        return new FfmpegDvdTranscodeBuilder
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            Standard = DvdVideoStandard.Ntsc,
            AspectRatio = aspectRatio
        };
    }

    /// <summary>
    /// Creates a builder configured for PAL DVD transcoding.
    /// </summary>
    public static FfmpegDvdTranscodeBuilder ForPal(
        string inputPath,
        string outputPath,
        DvdAspectRatio aspectRatio = DvdAspectRatio.Widescreen16x9)
    {
        return new FfmpegDvdTranscodeBuilder
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            Standard = DvdVideoStandard.Pal,
            AspectRatio = aspectRatio
        };
    }

    /// <summary>
    /// Generates standard NTSC argument string: -target ntsc-dvd -aspect 16:9.
    /// </summary>
    public static string BuildNtscArguments(string inputPath, string outputPath, string aspectRatio = "16:9")
    {
        var aspectEnum = aspectRatio == "4:3" ? DvdAspectRatio.Standard4x3 : DvdAspectRatio.Widescreen16x9;
        return ForNtsc(inputPath, outputPath, aspectEnum).BuildArguments();
    }

    /// <summary>
    /// Generates standard PAL argument string: -target pal-dvd -aspect 16:9.
    /// </summary>
    public static string BuildPalArguments(string inputPath, string outputPath, string aspectRatio = "16:9")
    {
        var aspectEnum = aspectRatio == "4:3" ? DvdAspectRatio.Standard4x3 : DvdAspectRatio.Widescreen16x9;
        return ForPal(inputPath, outputPath, aspectEnum).BuildArguments();
    }

    private static string EscapeCliArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        if (!arg.Contains(' ') && !arg.Contains('\t') && !arg.Contains('"'))
        {
            return arg;
        }

        return $"\"{arg.Replace("\"", "\\\"")}\"";
    }
}
