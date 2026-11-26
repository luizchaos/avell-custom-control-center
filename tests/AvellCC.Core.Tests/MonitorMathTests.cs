using System.Globalization;
using AvellCC.Core.Monitoring;

namespace AvellCC.Core.Tests;

public class MonitorMathTests
{
    [Fact]
    public void Cpu_percent_counts_kernel_time_minus_idle_as_busy()
    {
        // 1000 ticks elapsed: kernel 600 (of which idle 500) + user 400 → busy 500 → 50 %
        Assert.Equal(50, MonitorMath.CpuPercent(0, 0, 0, 500, 600, 400), 3);
        Assert.Equal(0, MonitorMath.CpuPercent(10, 10, 10, 10, 10, 10));   // no time elapsed
    }

    [Fact]
    public void Luid_is_parsed_from_gpu_counter_instances()
    {
        Assert.Equal(0xEADDL, MonitorMath.ParseLuid("pid_10116_luid_0x00000000_0x0000eadd_phys_0_eng_0_engtype_3d"));
        Assert.Equal((1L << 32) | 0xEE1D, MonitorMath.ParseLuid("luid_0x00000001_0x0000EE1D_phys_0"));
        Assert.Null(MonitorMath.ParseLuid("_Total"));
    }

    [Fact]
    public void Gpu_percent_sums_processes_per_engine_and_takes_the_busiest_engine()
    {
        var samples = new (string, double)[]
        {
            ("pid_1_luid_0x00000000_0x0000eadd_phys_0_eng_0_engtype_3D", 20),
            ("pid_2_luid_0x00000000_0x0000eadd_phys_0_eng_0_engtype_3D", 15),        // 3D engine: 35
            ("pid_1_luid_0x00000000_0x0000eadd_phys_0_eng_5_engtype_VideoDecode", 30), // decode: 30
            ("pid_3_luid_0x00000000_0x0000ee1d_phys_0_eng_0_engtype_3D", 80),
            ("pid_4_luid_0x00000000_0x0000ee1d_phys_0_eng_0_engtype_3D", 70),        // 150 → capped
        };
        var result = MonitorMath.GpuPercent(samples);
        Assert.Equal(35, result[0xEADD], 3);
        Assert.Equal(100, result[0xEE1D], 3);
    }

    [Fact]
    public void Vram_is_summed_per_adapter()
    {
        var result = MonitorMath.VramUsed([("luid_0x00000000_0x0000ee1d_phys_0", 1024), ("luid_0x00000000_0x0000ee1d_phys_1", 512)]);
        Assert.Equal(1536UL, result[0xEE1D]);
    }

    [Fact]
    public void Disk_activity_is_the_busiest_physical_disk()
    {
        Assert.Equal(70, MonitorMath.DiskActivePercent([("0 C:", 90), ("1 D:", 30), ("_Total", 60)])!.Value, 3);
        Assert.Null(MonitorMath.DiskActivePercent([("_Total", 60)]));
        Assert.Equal(0, MonitorMath.DiskActivePercent([("0 C:", 104)])!.Value);   // idle time above 100 happens; clamp
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(714, "714 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(15.1 * 1024 * 1024 * 1024, "15.1 GB")]
    [InlineData(476.0 * 1024 * 1024 * 1024, "476 GB")]
    [InlineData(2.0 * 1024 * 1024 * 1024 * 1024, "2 TB")]
    public void Bytes_are_formatted_in_binary_units(double bytes, string expected) =>
        Assert.Equal(expected, MonitorMath.Bytes(bytes, CultureInfo.InvariantCulture));

    [Fact]
    public void Power_mode_overlay_guids_are_recognised()
    {
        Assert.Equal(PowerMode.BestPerformance, MonitorMath.ModeFromOverlay(new Guid("ded574b5-45a0-4f42-8737-46345c09c238")));
        Assert.Equal(PowerMode.Balanced, MonitorMath.ModeFromOverlay(Guid.Empty));
        Assert.Equal(PowerMode.Unknown, MonitorMath.ModeFromOverlay(Guid.NewGuid()));
    }

    [Fact]
    public void System_monitor_reads_this_machine()
    {
        using var monitor = new SystemMonitor();
        Thread.Sleep(300);
        var s = monitor.Sample();
        Assert.False(string.IsNullOrWhiteSpace(monitor.Info.CpuName));
        Assert.True(monitor.Info.InstalledMemory > 0);
        Assert.InRange(s.CpuPercent, 0, 100);
        Assert.InRange(s.Memory.Percent, 0.1, 100);
        Assert.NotEmpty(s.Drives);
    }
}
