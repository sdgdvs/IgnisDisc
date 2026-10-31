using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// Options for generating DVD-Video menu still-frames and menu VOB program streams.
/// </summary>
public sealed class DvdMenuOptions
{
    public string MenuTitle { get; set; } = "DVD VIDEO";
    public IReadOnlyList<string> TitleNames { get; set; } = Array.Empty<string>();
    public DvdVideoStandard Standard { get; set; } = DvdVideoStandard.Ntsc;
    public DvdAspectRatio AspectRatio { get; set; } = DvdAspectRatio.Widescreen16x9;
    public int DurationSeconds { get; set; } = 2;
}

/// <summary>
/// Renders deterministic 24-bit uncompressed DVD menu BMP images and synthesizes/transcodes
/// DVD-compliant VIDEO_TS.VOB program streams with NAV highlight buttons.
/// </summary>
public static class DvdMenuGenerator
{
    private const int SectorSize = OpticalConstants.DataSectorSize; // 2048 bytes

    /// <summary>
    /// Renders a 24-bit uncompressed Windows .bmp image in pure C# (720x480 for NTSC, 720x576 for PAL)
    /// featuring a dark slate cinema aesthetic, gold/amber accent headers, and play button highlights.
    /// </summary>
    public static byte[] RenderMenuBmpImage(DvdMenuOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int width = 720;
        int height = options.Standard == DvdVideoStandard.Pal ? 576 : 480;

        int rowBytes = width * 3; // 720 * 3 = 2160 (divisible by 4, zero row padding needed)
        int imageSize = rowBytes * height;
        int fileSize = 54 + imageSize;

        byte[] bmp = new byte[fileSize];

        // --- 1. Bitmap File Header (14 bytes) ---
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54); // Offset to pixel data

        // --- 2. DIB Header (BITMAPINFOHEADER - 40 bytes) ---
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40); // biSize
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width); // biWidth
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height); // biHeight (bottom-up)
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1); // biPlanes
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24); // biBitCount (24 bpp)
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(30), 0); // biCompression (BI_RGB)
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(38), 2835); // 72 DPI
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(42), 2835);

        // --- 3. Pixel Drawing Surface ---
        // Helper closures targeting bottom-up BMP layout
        void SetPixel(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            int bottomUpY = height - 1 - y;
            int offset = 54 + (bottomUpY * rowBytes) + (x * 3);
            bmp[offset + 0] = b;
            bmp[offset + 1] = g;
            bmp[offset + 2] = r;
        }

        void FillRect(int x, int y, int w, int h, byte r, byte g, byte b)
        {
            int xEnd = Math.Min(width, x + w);
            int yEnd = Math.Min(height, y + h);
            for (int py = Math.Max(0, y); py < yEnd; py++)
            {
                for (int px = Math.Max(0, x); px < xEnd; px++)
                {
                    SetPixel(px, py, r, g, b);
                }
            }
        }

        void DrawRect(int x, int y, int w, int h, byte r, byte g, byte b, int thickness = 1)
        {
            FillRect(x, y, w, thickness, r, g, b); // Top
            FillRect(x, y + h - thickness, w, thickness, r, g, b); // Bottom
            FillRect(x, y, thickness, h, r, g, b); // Left
            FillRect(x + w - thickness, y, thickness, h, r, g, b); // Right
        }

        void DrawChar(char c, int startX, int startY, byte r, byte g, byte b, int scale)
        {
            byte[] glyph = GetGlyph8x8(c);
            for (int gy = 0; gy < 8; gy++)
            {
                byte row = glyph[gy];
                for (int gx = 0; gx < 8; gx++)
                {
                    if ((row & (0x80 >> gx)) != 0)
                    {
                        FillRect(startX + gx * scale, startY + gy * scale, scale, scale, r, g, b);
                    }
                }
            }
        }

        void DrawText(string text, int x, int y, byte r, byte g, byte b, int scale)
        {
            int cx = x;
            foreach (char c in text)
            {
                DrawChar(c, cx, y, r, g, b, scale);
                cx += 8 * scale;
            }
        }

        void DrawTextCentered(string text, int y, byte r, byte g, byte b, int scale)
        {
            int textWidth = text.Length * 8 * scale;
            int x = Math.Max(0, (width - textWidth) / 2);
            DrawText(text, x, y, r, g, b, scale);
        }

        // Fill background with Dark Slate (#12161F)
        FillRect(0, 0, width, height, 18, 22, 31);

        // Top Cinema Bar (Black) & Gold Accent Line
        FillRect(0, 0, width, 44, 0, 0, 0);
        FillRect(0, 44, width, 3, 229, 169, 60); // Gold/Amber

        // Bottom Cinema Bar (Black) & Gold Accent Line
        FillRect(0, height - 44, width, 44, 0, 0, 0);
        FillRect(0, height - 47, width, 3, 229, 169, 60);

        // Header Text: Menu Title (Gold / White, Scale 3)
        string title = string.IsNullOrWhiteSpace(options.MenuTitle) ? "DVD VIDEO" : options.MenuTitle.ToUpperInvariant();
        if (title.Length > 24) title = title[..24];
        DrawTextCentered(title, 64, 255, 215, 0, scale: 3); // Gold

        // Subtitle banner
        DrawTextCentered("IGNISDISC OPTICAL STUDIO", 100, 160, 175, 190, scale: 1);

        // Highlighted Button Box: [ > PLAY MOVIE ] or [ > PLAY ALL ]
        string buttonText = options.TitleNames.Count > 1 ? "> PLAY ALL" : "> PLAY MOVIE";
        int btnW = buttonText.Length * 8 * 2 + 40;
        int btnH = 36;
        int btnX = (width - btnW) / 2;
        int btnY = 135;

        // Button background & glowing golden border
        FillRect(btnX, btnY, btnW, btnH, 35, 30, 20); // Amber tint background
        DrawRect(btnX, btnY, btnW, btnH, 229, 169, 60, thickness: 2); // Gold border
        DrawTextCentered(buttonText, btnY + 10, 255, 255, 255, scale: 2); // White text

        // Movie Titles List (up to 8 titles)
        if (options.TitleNames.Count > 0)
        {
            int listY = 195;
            int maxTitlesToShow = Math.Min(8, options.TitleNames.Count);

            for (int i = 0; i < maxTitlesToShow; i++)
            {
                string raw = options.TitleNames[i];
                if (raw.Length > 36) raw = raw[..36] + "...";
                string itemText = $"{i + 1}. {raw}";

                DrawText(itemText, 100, listY, 220, 225, 235, scale: 2);
                listY += 28;
            }

            if (options.TitleNames.Count > 8)
            {
                DrawText($"... and {options.TitleNames.Count - 8} more title(s)", 100, listY, 150, 160, 170, scale: 1);
            }
        }

        // Bottom status note
        DrawTextCentered("PRESS ENTER OR PLAY TO START", height - 30, 180, 180, 180, scale: 1);

        return bmp;
    }

    /// <summary>
    /// Generates a deterministic sectorCount * 2048 byte DVD-compliant MPEG-2 Program Stream VOB buffer
    /// with NAV pack, MPEG-2 video PES, and AC-3 audio PES sectors.
    /// </summary>
    public static byte[] GenerateSyntheticMenuVob(DvdMenuOptions options, int sectorCount = 16)
    {
        ArgumentNullException.ThrowIfNull(options);
        sectorCount = Math.Max(16, sectorCount);
        byte[] vob = new byte[sectorCount * SectorSize];

        // Format Sector 0 as DVD NAV Pack
        int buttonCount = Math.Max(1, options.TitleNames.Count);

        // Format subsequent sectors (1..sectorCount - 1) as alternating MPEG-2 Video & AC-3 Audio PES packets
        for (int s = 1; s < sectorCount; s++)
        {
            int offset = s * SectorSize;

            // Pack Header (14 bytes)
            byte[] packHeader = [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x89, 0xC3, 0xF8];
            packHeader.CopyTo(vob, offset);

            if (s % 2 == 1)
            {
                // MPEG-2 Video PES packet (Stream 0xE0)
                vob[offset + 14] = 0x00;
                vob[offset + 15] = 0x00;
                vob[offset + 16] = 0x01;
                vob[offset + 17] = 0xE0;
                BinaryPrimitives.WriteUInt16BigEndian(vob.AsSpan(offset + 18), (ushort)(SectorSize - 20)); // Length
                vob[offset + 20] = 0x80; // Flags
                vob[offset + 21] = 0x00;
                vob[offset + 22] = 0x00;

                // MPEG-2 Sequence Header (00 00 01 B3)
                vob[offset + 23] = 0x00;
                vob[offset + 24] = 0x00;
                vob[offset + 25] = 0x01;
                vob[offset + 26] = 0xB3;
                vob[offset + 27] = 0x2D; // 720 width
                vob[offset + 28] = 0x01;
                vob[offset + 29] = options.Standard == DvdVideoStandard.Pal ? (byte)0x40 : (byte)0xE0; // 576 or 480 height
            }
            else
            {
                // AC-3 Audio PES packet (Private Stream 1 0xBD, Substream 0x80)
                vob[offset + 14] = 0x00;
                vob[offset + 15] = 0x00;
                vob[offset + 16] = 0x01;
                vob[offset + 17] = 0xBD;
                BinaryPrimitives.WriteUInt16BigEndian(vob.AsSpan(offset + 18), (ushort)(SectorSize - 20)); // Length
                vob[offset + 20] = 0x80;
                vob[offset + 21] = 0x00;
                vob[offset + 22] = 0x00;
                vob[offset + 23] = 0x80; // AC-3 Substream 0

                // AC-3 sync frame header (0B 77)
                vob[offset + 24] = 0x0B;
                vob[offset + 25] = 0x77;
            }
        }

        // Apply NAV pack patching to ensure valid PCI highlights and JumpTT 1 command
        DvdIfoGenerator.PatchVobNavPackets(vob, vobId: 1, cellId: 1, isMenu: true, buttonCount: buttonCount);

        return vob;
    }

    /// <summary>
    /// Generates a DVD-compliant menu VOB stream: using FFmpeg if available to transcode
    /// the rendered menu BMP into an MPEG-2 Program Stream, or falling back to deterministic synthetic VOB generation.
    /// Always patches NAV packets with button highlights and JumpTT 1 command.
    /// </summary>
    public static async Task<byte[]> GenerateMenuVobAsync(
        DvdMenuOptions options,
        string? ffmpegPath = null,
        bool allowFfmpeg = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        byte[]? vobBytes = null;
        string? resolvedFfmpeg = null;

        if (allowFfmpeg && FfmpegLocator.IsFfmpegAvailable(out resolvedFfmpeg, ffmpegPath))
        {
            string tempBmp = Path.Combine(Path.GetTempPath(), $"ignis_menu_{Guid.NewGuid():N}.bmp");
            string tempVob = Path.Combine(Path.GetTempPath(), $"ignis_menu_{Guid.NewGuid():N}.vob");

            try
            {
                byte[] bmpBytes = RenderMenuBmpImage(options);
                await File.WriteAllBytesAsync(tempBmp, bmpBytes, cancellationToken).ConfigureAwait(false);

                string target = options.Standard == DvdVideoStandard.Pal ? "pal-dvd" : "ntsc-dvd";
                string aspect = options.AspectRatio == DvdAspectRatio.Standard4x3 ? "4:3" : "16:9";
                int duration = Math.Clamp(options.DurationSeconds, 1, 10);

                var psi = new ProcessStartInfo
                {
                    FileName = resolvedFfmpeg!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-loop");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(tempBmp);
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("lavfi");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add("anullsrc=r=48000:cl=stereo");
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(duration.ToString());
                psi.ArgumentList.Add("-target");
                psi.ArgumentList.Add(target);
                psi.ArgumentList.Add("-aspect");
                psi.ArgumentList.Add(aspect);
                psi.ArgumentList.Add(tempVob);

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(15));

                    try
                    {
                        var stdOutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                        var stdErrTask = proc.StandardError.ReadToEndAsync(cts.Token);
                        await Task.WhenAll(stdOutTask, stdErrTask, proc.WaitForExitAsync(cts.Token)).ConfigureAwait(false);
                    }
                    catch
                    {
                        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                        throw;
                    }

                    if (proc.ExitCode == 0 && File.Exists(tempVob))
                    {
                        var fi = new FileInfo(tempVob);
                        if (fi.Length >= 16 * SectorSize)
                        {
                            vobBytes = await File.ReadAllBytesAsync(tempVob, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Fall back gracefully to synthetic generation
            }
            finally
            {
                try { if (File.Exists(tempBmp)) File.Delete(tempBmp); } catch { }
                try { if (File.Exists(tempVob)) File.Delete(tempVob); } catch { }
            }
        }

        // Fall back to synthetic VOB if FFmpeg failed or was unavailable
        if (vobBytes == null || vobBytes.Length < 16 * SectorSize)
        {
            vobBytes = GenerateSyntheticMenuVob(options, 16);
        }

        // Guarantee minimum 16 sectors (32 KB)
        if (vobBytes.Length < 16 * SectorSize)
        {
            byte[] padded = new byte[16 * SectorSize];
            Array.Copy(vobBytes, padded, vobBytes.Length);
            vobBytes = padded;
        }

        // Always patch NAV packets so PCI highlights exist
        DvdIfoGenerator.PatchVobNavPackets(
            vobBytes,
            vobId: 1,
            cellId: 1,
            isMenu: true,
            buttonCount: Math.Max(1, options.TitleNames.Count));

        return vobBytes;
    }

    /// <summary>
    /// Returns 8x8 bitmap font glyph row bytes for standard ASCII characters.
    /// </summary>
    private static byte[] GetGlyph8x8(char c)
    {
        char upper = char.ToUpperInvariant(c);
        return upper switch
        {
            'A' => [0x18, 0x3C, 0x66, 0x7E, 0x66, 0x66, 0x66, 0x00],
            'B' => [0x7C, 0x66, 0x66, 0x7C, 0x66, 0x66, 0x7C, 0x00],
            'C' => [0x3C, 0x66, 0x60, 0x60, 0x60, 0x66, 0x3C, 0x00],
            'D' => [0x78, 0x6C, 0x66, 0x66, 0x66, 0x6C, 0x78, 0x00],
            'E' => [0x7E, 0x60, 0x60, 0x7C, 0x60, 0x60, 0x7E, 0x00],
            'F' => [0x7E, 0x60, 0x60, 0x7C, 0x60, 0x60, 0x60, 0x00],
            'G' => [0x3C, 0x66, 0x60, 0x6E, 0x66, 0x66, 0x3E, 0x00],
            'H' => [0x66, 0x66, 0x66, 0x7E, 0x66, 0x66, 0x66, 0x00],
            'I' => [0x3C, 0x18, 0x18, 0x18, 0x18, 0x18, 0x3C, 0x00],
            'J' => [0x1E, 0x0C, 0x0C, 0x0C, 0x0C, 0x6C, 0x38, 0x00],
            'K' => [0x66, 0x6C, 0x78, 0x70, 0x78, 0x6C, 0x66, 0x00],
            'L' => [0x60, 0x60, 0x60, 0x60, 0x60, 0x60, 0x7E, 0x00],
            'M' => [0x63, 0x77, 0x7F, 0x6B, 0x63, 0x63, 0x63, 0x00],
            'N' => [0x66, 0x76, 0x7E, 0x7E, 0x6E, 0x66, 0x66, 0x00],
            'O' => [0x3C, 0x66, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x00],
            'P' => [0x7C, 0x66, 0x66, 0x7C, 0x60, 0x60, 0x60, 0x00],
            'Q' => [0x3C, 0x66, 0x66, 0x66, 0x6E, 0x3C, 0x0E, 0x00],
            'R' => [0x7C, 0x66, 0x66, 0x7C, 0x78, 0x6C, 0x66, 0x00],
            'S' => [0x3C, 0x66, 0x60, 0x3C, 0x06, 0x66, 0x3C, 0x00],
            'T' => [0x7E, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x00],
            'U' => [0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x00],
            'V' => [0x66, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x18, 0x00],
            'W' => [0x63, 0x63, 0x63, 0x6B, 0x7F, 0x77, 0x63, 0x00],
            'X' => [0x66, 0x66, 0x3C, 0x18, 0x3C, 0x66, 0x66, 0x00],
            'Y' => [0x66, 0x66, 0x66, 0x3C, 0x18, 0x18, 0x18, 0x00],
            'Z' => [0x7E, 0x06, 0x0C, 0x18, 0x30, 0x60, 0x7E, 0x00],

            '0' => [0x3C, 0x66, 0x6E, 0x76, 0x66, 0x66, 0x3C, 0x00],
            '1' => [0x18, 0x38, 0x18, 0x18, 0x18, 0x18, 0x7E, 0x00],
            '2' => [0x3C, 0x66, 0x06, 0x0C, 0x18, 0x30, 0x7E, 0x00],
            '3' => [0x3C, 0x66, 0x06, 0x1C, 0x06, 0x66, 0x3C, 0x00],
            '4' => [0x0C, 0x1C, 0x34, 0x64, 0x7E, 0x04, 0x04, 0x00],
            '5' => [0x7E, 0x60, 0x7C, 0x06, 0x06, 0x66, 0x3C, 0x00],
            '6' => [0x3C, 0x60, 0x7C, 0x66, 0x66, 0x66, 0x3C, 0x00],
            '7' => [0x7E, 0x06, 0x0C, 0x18, 0x30, 0x30, 0x30, 0x00],
            '8' => [0x3C, 0x66, 0x66, 0x3C, 0x66, 0x66, 0x3C, 0x00],
            '9' => [0x3C, 0x66, 0x66, 0x3E, 0x06, 0x0C, 0x38, 0x00],

            '.' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x00],
            ',' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x30],
            ':' => [0x00, 0x18, 0x18, 0x00, 0x18, 0x18, 0x00, 0x00],
            '-' => [0x00, 0x00, 0x00, 0x7E, 0x00, 0x00, 0x00, 0x00],
            '_' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x7E, 0x00],
            '/' => [0x02, 0x06, 0x0C, 0x18, 0x30, 0x60, 0x40, 0x00],
            '(' => [0x0C, 0x18, 0x30, 0x30, 0x30, 0x18, 0x0C, 0x00],
            ')' => [0x30, 0x18, 0x0C, 0x0C, 0x0C, 0x18, 0x30, 0x00],
            '[' => [0x3C, 0x30, 0x30, 0x30, 0x30, 0x30, 0x3C, 0x00],
            ']' => [0x3C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x3C, 0x00],
            '>' => [0x60, 0x30, 0x18, 0x0C, 0x18, 0x30, 0x60, 0x00],
            '<' => [0x06, 0x0C, 0x18, 0x30, 0x18, 0x0C, 0x06, 0x00],
            '!' => [0x18, 0x18, 0x18, 0x18, 0x18, 0x00, 0x18, 0x00],
            '?' => [0x3C, 0x66, 0x0C, 0x18, 0x18, 0x00, 0x18, 0x00],
            _ => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] // Space
        };
    }
}
