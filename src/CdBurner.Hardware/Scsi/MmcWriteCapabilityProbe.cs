namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Probed MMC-5 recording features and Write Parameters (Mode Page 0x05) state for an optical drive.
/// </summary>
public sealed record MmcWriteCapabilityProbe
{
    public bool HasTaoFeature { get; init; }
    public bool TaoSupportsTestWrite { get; init; }
    public bool TaoSupportsBufferUnderrunFree { get; init; }

    public bool HasCdMasteringFeature { get; init; }
    public bool SupportsSao { get; init; }
    public bool SupportsRawDao { get; init; }
    public bool SupportsCdTextLeadIn { get; init; }
    public bool CdMasteringSupportsTestWrite { get; init; }
    public bool CdMasteringSupportsBufferUnderrunFree { get; init; }

    public bool HasDvdWriteFeature { get; init; }
    public bool DvdSupportsTestWrite { get; init; }
    public bool DvdSupportsBufferUnderrunFree { get; init; }

    public bool HasLayerJumpFeature { get; init; }
    public bool HasBdWriteFeature { get; init; }

    public bool ModePage05Present { get; init; }
    public bool ModePage05BufferUnderrunFreeEnabled { get; init; }
    public bool ModePage05TestWriteEnabled { get; init; }
    public byte ModePage05WriteType { get; init; }

    public IReadOnlyList<ushort> FeatureCodes { get; init; } = Array.Empty<ushort>();

    /// <summary>
    /// True if any probed feature or Mode Page 0x05 confirms Buffer Underrun Free (BURN-Free) protection.
    /// </summary>
    public bool SupportsBufferUnderrunFree =>
        TaoSupportsBufferUnderrunFree ||
        CdMasteringSupportsBufferUnderrunFree ||
        DvdSupportsBufferUnderrunFree ||
        (ModePage05Present && ModePage05BufferUnderrunFreeEnabled);

    /// <summary>
    /// True if any probed feature or Mode Page 0x05 confirms laser-off test write simulation support.
    /// </summary>
    public bool SupportsTestWrite =>
        TaoSupportsTestWrite ||
        CdMasteringSupportsTestWrite ||
        DvdSupportsTestWrite ||
        (ModePage05Present && ModePage05TestWriteEnabled);

    /// <summary>
    /// Produces a human-readable summary of active MMC features for UI display and diagnostics.
    /// </summary>
    public IReadOnlyList<string> ToFeatureSummary()
    {
        var list = new List<string>();
        if (HasTaoFeature) list.Add("0x002D TAO");
        if (HasCdMasteringFeature) list.Add("0x002E SAO/Raw");
        if (HasDvdWriteFeature) list.Add("0x002F DVD Write");
        if (HasLayerJumpFeature) list.Add("0x0033 Layer Jump");
        if (HasBdWriteFeature) list.Add("0x0041 BD Write");
        if (ModePage05Present && ModePage05BufferUnderrunFreeEnabled) list.Add("ModePage 0x05 BUFE");
        return list;
    }
}
