namespace AvellCC.Core.Ec;

/// <summary>In-memory EC, for tests.</summary>
public sealed class FakeEc : IEcAccess
{
    private readonly byte[] _ram = new byte[0x10000];
    private readonly object _lock = new();
    public List<(ushort Address, byte Value)> Writes { get; } = [];

    public byte Read(ushort address) { lock (_lock) return _ram[address]; }

    public void Write(ushort address, byte value)
    {
        lock (_lock) { _ram[address] = value; Writes.Add((address, value)); }
    }

    public void Seed(ushort start, ReadOnlySpan<byte> data)
    {
        lock (_lock) data.CopyTo(_ram.AsSpan(start));
    }
}
