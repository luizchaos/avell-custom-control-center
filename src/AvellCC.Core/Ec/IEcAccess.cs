namespace AvellCC.Core.Ec;

/// <summary>Byte-level access to EC RAM. Implementations must be thread-safe.</summary>
public interface IEcAccess
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
}

public static class EcAccessExtensions
{
    public static byte[] ReadRange(this IEcAccess ec, ushort start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start + length > 0x10000) throw new ArgumentOutOfRangeException(nameof(length));
        var buffer = new byte[length];
        for (var i = 0; i < length; i++) buffer[i] = ec.Read((ushort)(start + i));
        return buffer;
    }

    public static ushort ReadWord(this IEcAccess ec, ushort high, ushort low) =>
        (ushort)((ec.Read(high) << 8) | ec.Read(low));
}
