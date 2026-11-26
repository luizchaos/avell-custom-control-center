using AvellCC.Core.Ec;

namespace AvellCC.Core.Tests;

public class WmiEcEncodingTests
{
    [Theory]
    [InlineData(0x0740, 0x10000000740UL)]
    [InlineData(0x0F00, 0x10000000F00UL)]
    [InlineData(0xFFFF, 0x1000000FFFFUL)]
    public void Read_sets_bit40_plus_address(ushort address, ulong expected) =>
        Assert.Equal(expected, WmiEc.EncodeRead(address));

    [Fact]
    public void Write_puts_value_in_bits_16_to_23_and_never_sets_read_flag()
    {
        Assert.Equal(0x00AB0F20UL, WmiEc.EncodeWrite(0x0F20, 0xAB));
        Assert.Equal(0UL, WmiEc.EncodeWrite(0xFFFF, 0xFF) & WmiEc.ReadFlag);
    }

    [Fact]
    public void Read_encoding_is_never_a_valid_write_encoding()
    {
        for (var a = 0; a <= 0xFFFF; a += 0x101)
            Assert.NotEqual(0UL, WmiEc.EncodeRead((ushort)a) & WmiEc.ReadFlag);
    }
}
