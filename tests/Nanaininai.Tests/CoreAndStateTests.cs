using System.Net;
using Nanaininai;
using Xunit;

public class CoreTests
{
    [Theory]
    [InlineData(1, "LAB-PC-01")]
    [InlineData(5, "LAB-PC-05")]
    [InlineData(17, "LAB-PC-17")]
    [InlineData(100, "LAB-PC-100")]
    public void ComputeComputerName_PadsNumber(int number, string expected)
    {
        var config = new AppConfig { ComputerNamePrefix = "LAB-PC", ComputerNamePadding = 2 };
        Assert.Equal(expected, Core.ComputeComputerName(config, number));
    }

    [Theory]
    [InlineData("192.168.50.101", 1, "192.168.50.101")]
    [InlineData("192.168.50.101", 2, "192.168.50.102")]
    [InlineData("192.168.50.101", 17, "192.168.50.117")]
    public void ComputeClientIp_IncrementsLastOctet(string start, int number, string expected)
    {
        var ip = Core.ComputeClientIp(start, number);
        Assert.NotNull(ip);
        Assert.Equal(expected, ip.ToString());
    }

    [Fact]
    public void ComputeClientIp_RejectsOctetOverflow()
    {
        Assert.Null(Core.ComputeClientIp("192.168.50.250", 10));
    }

    [Fact]
    public void ComputeClientIp_RejectsInvalidStart()
    {
        Assert.Null(Core.ComputeClientIp("not-an-ip", 1));
    }

    [Theory]
    [InlineData("LAB-PC-01", true)]
    [InlineData("PC1", true)]
    [InlineData("a-b-c", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("THIS-NAME-IS-WAY-TOO-LONG", false)] // > 15 chars
    [InlineData("-startshyphen", false)]
    [InlineData("endswithhyphen-", false)]
    [InlineData("12345", false)] // all digits
    [InlineData("has space", false)]
    [InlineData("under_score", false)]
    public void IsValidComputerName_FollowsWindowsRules(string? name, bool expected)
    {
        Assert.Equal(expected, Core.IsValidComputerName(name));
    }

    [Theory]
    [InlineData("255.255.255.0", true)]
    [InlineData("255.255.0.0", true)]
    [InlineData("255.0.0.0", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("255.0.255.0", false)] // non-contiguous
    [InlineData("192.168.50.1", false)] // not a mask
    [InlineData("bogus", false)]
    public void IsValidSubnetMask_ChecksContiguity(string mask, bool expected)
    {
        Assert.Equal(expected, Core.IsValidSubnetMask(mask));
    }
}

public class UsbStateStoreTests : IDisposable
{
    private readonly string _dir;

    public UsbStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"netsetup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new UsbStateStore(_dir);
        store.Save(new UsbState { NextComputerNumber = 6 });

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(6, loaded.NextComputerNumber);
    }

    [Fact]
    public void Save_CreatesBackupFile()
    {
        var store = new UsbStateStore(_dir);
        store.Save(new UsbState { NextComputerNumber = 3 });
        Assert.True(File.Exists(Path.Combine(_dir, FileNames.StateBackup)));
    }

    [Fact]
    public void CorruptMainFile_RecoversFromBackup()
    {
        var store = new UsbStateStore(_dir);
        store.Save(new UsbState { NextComputerNumber = 9 });

        File.WriteAllText(Path.Combine(_dir, FileNames.State), "{ not valid json !!");

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(9, loaded.NextComputerNumber);
    }

    [Fact]
    public void NoFiles_ReturnsNull()
    {
        var store = new UsbStateStore(_dir);
        Assert.Null(store.Load());
    }

    [Fact]
    public void RejectsInvalidState_NextNumberBelowOne()
    {
        File.WriteAllText(Path.Combine(_dir, FileNames.State), "{\"nextComputerNumber\": 0}");
        Assert.Null(new UsbStateStore(_dir).Load());
    }
}
