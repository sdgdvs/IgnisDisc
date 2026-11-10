using CdBurner.Core.Primitives;

namespace CdBurner.Core.Compilation;

/// <summary>How much of one recording layer a compilation would use.</summary>
/// <param name="Layer">Layer number, 0 for the layer read first.</param>
/// <param name="UsedSectors">Sectors of the compilation on this layer.</param>
/// <param name="CapacitySectors">What the layer holds.</param>
/// <param name="StartLba">First logical sector of the compilation on this layer.</param>
public sealed record LayerUsage(int Layer, int UsedSectors, int CapacitySectors, int StartLba);

/// <summary>Projected split of a data compilation across the layers of a multi-layer disc.</summary>
public sealed record LayerSplitPreview(
    DiscMediaType MediaType,
    int TotalSectors,
    int MediaSectors,
    IReadOnlyList<LayerUsage> Layers,
    int OverflowSectors)
{
    /// <summary>Layers that receive any data.</summary>
    public int LayersUsed => Layers.Count(l => l.UsedSectors > 0);

    /// <summary>True when the drive will have to move to another layer while reading the disc.</summary>
    public bool CrossesLayerChange => LayersUsed > 1;

    /// <summary>Logical sectors where the drive moves to the next layer, one per change.</summary>
    public IReadOnlyList<int> LayerChangeLbas =>
        Layers.Skip(1).Where(l => l.UsedSectors > 0).Select(l => l.StartLba).ToArray();

    /// <summary>One line for the data screen, e.g. "BD-R DL — Layer 0: 23.3 GB (full) · Layer 1: 4.6 GB · layer change at 23.3 GB".</summary>
    public string Summary
    {
        get
        {
            var parts = Layers
                .Where(l => l.UsedSectors > 0 || l.Layer == 0)
                .Select(l => $"Layer {l.Layer}: {FormatSize(l.UsedSectors)}" + (l.UsedSectors >= l.CapacitySectors ? " (full)" : ""))
                .ToList();

            string tail = CrossesLayerChange
                ? (LayerChangeLbas.Count == 1
                    ? $"layer change at {FormatSize(LayerChangeLbas[0])}"
                    : $"layer changes at {string.Join(", ", LayerChangeLbas.Select(FormatSize))}")
                : "no layer change";

            if (OverflowSectors > 0)
            {
                tail += $" · {FormatSize(OverflowSectors)} does not fit";
            }

            return $"{MediaType.GetDisplayName()} — {string.Join(" · ", parts)} · {tail}";
        }
    }

    // Same units as the rest of the app (1 GB = 1024 MB), so the layers add up to the size on the disc badge.
    internal static string FormatSize(int sectors)
    {
        double mb = (double)sectors * OpticalConstants.DataSectorSize / (1024.0 * 1024.0);
        return mb >= 1024 ? $"{mb / 1024:F1} GB" : $"{mb:F0} MB";
    }
}

/// <summary>
/// Projects where a data compilation lands on a DVD±R DL, BD-R/RE DL or BDXL disc. Windows' writer does not set a
/// layer break for data discs, so the drive fills layer 0 to its end before moving on; this shows that split.
/// Choosing a different break (and sending it to the drive) is a separate feature.
/// </summary>
public static class LayerSplitPlanner
{
    /// <summary>
    /// Returns the projected split, or null for single-layer media or an empty compilation.
    /// </summary>
    /// <param name="totalSectors">Sectors the compilation needs, file system included.</param>
    /// <param name="mediaType">Target disc.</param>
    /// <param name="reportedCapacitySectors">Capacity the drive reported for the inserted disc; 0 to plan with the
    /// nominal size for <paramref name="mediaType"/>.</param>
    public static LayerSplitPreview? Project(int totalSectors, DiscMediaType mediaType, int reportedCapacitySectors = 0)
    {
        int layers = mediaType.GetLayerCount();
        if (layers < 2 || totalSectors <= 0)
        {
            return null;
        }

        int capacity = reportedCapacitySectors > 0 ? reportedCapacitySectors : mediaType.GetNominalCapacitySectors();
        if (capacity <= 0)
        {
            return null;
        }

        // Layers hold equal shares, in whole ECC blocks (16 sectors on DVD) or clusters (32 on Blu-ray).
        int block = mediaType.IsBluRay() ? OpticalConstants.BdClusterSectors : OpticalConstants.DvdEccBlockSectors;
        int perLayer = capacity / layers / block * block;

        var usage = new List<LayerUsage>(layers);
        int remaining = totalSectors;
        int start = 0;
        for (int layer = 0; layer < layers; layer++)
        {
            int layerCapacity = layer == layers - 1 ? capacity - perLayer * (layers - 1) : perLayer;
            int used = Math.Min(remaining, layerCapacity);
            usage.Add(new LayerUsage(layer, used, layerCapacity, start));
            remaining -= used;
            start += layerCapacity;
        }

        return new LayerSplitPreview(mediaType, totalSectors, capacity, usage, remaining);
    }
}
