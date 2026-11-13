namespace CdBurner.Lithography.Geometry;

/// <summary>
/// Answers "is there ink here?" for a point on one page of a <see cref="PolarPageLayout"/>.
/// A later phase plugs a glyph font in here. Implementations must not allocate: this is called once
/// for every sample along the track.
/// </summary>
public interface IPagePixelSource
{
    /// <summary>True when the point should be dark (ink).</summary>
    /// <param name="ring">Ring index of the page, 0 = innermost.</param>
    /// <param name="sector">Index of the page within its ring.</param>
    /// <param name="uMeters">Tangential position on the page in meters, 0 to the page width.</param>
    /// <param name="vMeters">Radial position on the page in meters, 0 (inner edge) to the page height.</param>
    bool IsInk(int ring, int sector, double uMeters, double vMeters);
}
