using PdnQso.Link.Transfer;

namespace PdnQso.Tests.Transfer;

/// <summary>
/// A file name arrives over the air from somebody else. These are the things it is not allowed
/// to talk this station into creating.
/// </summary>
public class SafeFileNameTests
{
    [Theory]
    [InlineData("readme.txt", "readme.txt")]
    [InlineData("a file with spaces.bin", "a file with spaces.bin")]
    [InlineData("MixedCase-1.2.3.tar.gz", "MixedCase-1.2.3.tar.gz")]
    public void An_Ordinary_Name_Is_Left_Alone(string offered, string expected)
    {
        SafeFileName.From(offered).Should().Be(expected);
    }

    [Theory]
    [InlineData("../../.ssh/authorized_keys", "authorized_keys")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts", "hosts")]
    [InlineData("dir/sub/file.txt", "file.txt")]
    public void A_Path_Becomes_A_Name(string offered, string expected)
    {
        SafeFileName.From(offered).Should().Be(expected);
    }

    [Theory]
    [InlineData("..", SafeFileName.Fallback)]
    [InlineData(".", SafeFileName.Fallback)]
    [InlineData("", SafeFileName.Fallback)]
    [InlineData("   ", SafeFileName.Fallback)]
    [InlineData(null, SafeFileName.Fallback)]
    [InlineData("...", SafeFileName.Fallback)]
    public void A_Name_That_Is_Not_A_Name_Gets_The_Fallback(string? offered, string expected)
    {
        SafeFileName.From(offered).Should().Be(expected);
    }

    [Fact]
    public void A_Leading_Dot_Does_Not_Survive()
    {
        SafeFileName.From(".bashrc").Should().Be("_bashrc");
    }

    [Fact]
    public void Everything_Outside_Printable_Ascii_Becomes_An_Underscore()
    {
        SafeFileName.From("re\u00e7u\ttoday\n.txt").Should().Be("re_u_today_.txt");
    }

    [Fact]
    public void A_Very_Long_Name_Is_Cut_And_Keeps_Its_Extension()
    {
        string offered = new string('x', 400) + ".tar.gz";

        string safe = SafeFileName.From(offered);

        safe.Length.Should().Be(SafeFileName.MaxLength);
        safe.Should().EndWith(".gz");
    }

    [Theory]
    [InlineData("nul", "_nul")]
    [InlineData("COM1.txt", "_COM1.txt")]
    [InlineData("aux", "_aux")]
    public void A_Device_Name_Is_Prefixed(string offered, string expected)
    {
        SafeFileName.From(offered).Should().Be(expected);
    }

    [Fact]
    public void A_Received_File_Never_Overwrites_One_That_Is_There()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pdn-qso-safe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string first = SafeFileName.UniquePath(directory, "log.txt");
            first.Should().Be(Path.Combine(directory, "log.txt"));
            File.WriteAllText(first, "one");

            string second = SafeFileName.UniquePath(directory, "log.txt");
            second.Should().Be(Path.Combine(directory, "log-1.txt"));
            File.WriteAllText(second, "two");

            SafeFileName.UniquePath(directory, "log.txt")
                .Should().Be(Path.Combine(directory, "log-2.txt"));
            File.ReadAllText(first).Should().Be("one");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
