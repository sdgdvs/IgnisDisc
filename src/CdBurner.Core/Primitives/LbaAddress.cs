namespace CdBurner.Core.Primitives;

/// <summary>
/// Logical Block Address (LBA) representing a 0-indexed optical disc sector.
/// LBA 0 corresponds to Red Book MSF 00:02:00.
/// </summary>
public readonly record struct LbaAddress(int Value) : IComparable<LbaAddress>
{
    public static readonly LbaAddress Zero = new(0);

    /// <summary>
    /// Converts this LBA to its corresponding Red Book MSF address.
    /// </summary>
    public MsfAddress ToMsf() => MsfAddress.FromLba(Value);

    /// <summary>
    /// Creates an LBA from a Red Book MSF address.
    /// </summary>
    public static LbaAddress FromMsf(MsfAddress msf) => new(msf.ToLba());

    /// <summary>
    /// Computes the linear byte offset from the start of the user data area.
    /// </summary>
    public long ToByteOffset(int sectorSize = OpticalConstants.DataSectorSize) => (long)Value * sectorSize;

    /// <summary>
    /// Creates an LBA from a byte offset using the given sector size.
    /// </summary>
    public static LbaAddress FromByteOffset(long byteOffset, int sectorSize = OpticalConstants.DataSectorSize)
    {
        if (byteOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset), "Byte offset cannot be negative.");
        }
        return new LbaAddress((int)(byteOffset / sectorSize));
    }

    public override string ToString() => Value.ToString();

    public int CompareTo(LbaAddress other) => Value.CompareTo(other.Value);

    public static implicit operator int(LbaAddress address) => address.Value;
    public static implicit operator LbaAddress(int value) => new(value);

    public static explicit operator MsfAddress(LbaAddress address) => address.ToMsf();
    public static explicit operator LbaAddress(MsfAddress msf) => FromMsf(msf);

    public static LbaAddress operator +(LbaAddress left, int sectors) => new(left.Value + sectors);
    public static LbaAddress operator -(LbaAddress left, int sectors) => new(left.Value - sectors);
    public static int operator -(LbaAddress left, LbaAddress right) => left.Value - right.Value;

    public static bool operator <(LbaAddress left, LbaAddress right) => left.Value < right.Value;
    public static bool operator <=(LbaAddress left, LbaAddress right) => left.Value <= right.Value;
    public static bool operator >(LbaAddress left, LbaAddress right) => left.Value > right.Value;
    public static bool operator >=(LbaAddress left, LbaAddress right) => left.Value >= right.Value;
}
