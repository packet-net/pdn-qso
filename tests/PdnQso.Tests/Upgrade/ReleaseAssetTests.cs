using PdnQso.Upgrade;

namespace PdnQso.Tests.Upgrade;

/// <summary>
/// The pure half of <c>pdn-qso --upgrade</c>: where a package lives, which release a redirect
/// came from, which of two versions is the newer, and how to install the result.
/// </summary>
/// <remarks>
/// None of this touches the network. The claims that need GitHub to be up (the redirect really
/// does name the tag, the checksum file really does sit beside the package) are properties of
/// the release pipeline, and the pipeline is what pins them: `release.yml` attaches the two
/// under exactly these names.
/// </remarks>
public class ReleaseAssetTests
{
    [Theory]
    [InlineData("amd64", "pdn-qso_amd64.deb")]
    [InlineData("arm64", "pdn-qso_arm64.deb")]
    [InlineData("armhf", "pdn-qso_armhf.deb")]
    public void A_Package_Is_Named_For_Its_Architecture_And_Nothing_Else(
        string architecture, string expected)
    {
        // No version in the name, which is the whole point: the URL below never changes, so it
        // can be printed in a README, put in a wiki, or built into the program itself.
        ReleaseAsset.FileNameFor(architecture).Should().Be(expected);
        ReleaseAsset.LatestFor(architecture).ToString().Should().Be(
            $"https://github.com/packet-net/pdn-qso/releases/latest/download/{expected}");
    }

    [Fact]
    public void The_First_Redirect_Names_The_Release_It_Came_From()
    {
        // What GitHub answers latest/download with: a 302 to the tagged path. The second hop
        // goes to a signed URL on another host with no tag in it at all, which is why the tag
        // is read here and not from the end of the chain.
        Uri location = new(
            "https://github.com/packet-net/pdn-qso/releases/download/v0.3.0/pdn-qso_arm64.deb");

        string? tag = ReleaseAsset.TagFromRedirect(location);

        tag.Should().Be("v0.3.0");
        ReleaseAsset.VersionFromTag(tag!).Should().Be("0.3.0");
    }

    [Fact]
    public void A_Location_That_Is_Not_A_Release_Download_Names_No_Tag()
    {
        ReleaseAsset.TagFromRedirect(null).Should().BeNull();
        ReleaseAsset.TagFromRedirect(new Uri("https://github.com/packet-net/pdn-qso"))
            .Should().BeNull();
        ReleaseAsset.TagFromRedirect(new Uri("https://example.invalid/download"))
            .Should().BeNull("a path ending at download names nothing after it");
    }

    [Theory]
    [InlineData("0.3.0", "0.2.0")]
    [InlineData("0.10.0", "0.9.9")]
    [InlineData("1.0.0", "0.99.99")]
    [InlineData("0.3.0", "0.3.0-rc1")]
    [InlineData("0.3.0-rc2", "0.3.0-rc1")]
    [InlineData("0.2.1", "0.2")]
    public void The_Newer_Version_Sorts_Above_The_Older(string newer, string older)
    {
        ReleaseAsset.CompareVersions(newer, older).Should().BePositive();
        ReleaseAsset.CompareVersions(older, newer).Should().BeNegative();
    }

    [Theory]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2.0", "0.2.0+abc1234")]
    [InlineData("0.2", "0.2.0")]
    public void The_Same_Version_Spelled_Differently_Is_The_Same_Version(
        string left, string right)
    {
        // The '+' suffix is the SDK's source-revision stamp, and a build of the tagged commit
        // carries it. Treating that as a different version would offer an upgrade to itself.
        ReleaseAsset.CompareVersions(left, right).Should().Be(0);
    }

    [Fact]
    public void A_Build_From_Source_Is_Ahead_Of_Every_Release()
    {
        // With no tag naming it, the SDK stamps 1.0.0. The upgrade command uses this to say
        // "nothing to do" rather than installing an older release over a developer's build.
        ReleaseAsset.CompareVersions("0.3.0", "1.0.0").Should().BeNegative();
    }

    [Fact]
    public void A_Checksum_Is_Found_By_Its_File_Name()
    {
        string sums = string.Join(
            '\n',
            "3b1f2c4d5e6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e  pdn-qso_amd64.deb",
            "aa1f2c4d5e6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5daa  pdn-qso_arm64.deb",
            "bb1f2c4d5e6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5dbb  pdn-qso_armhf.deb");

        ReleaseAsset.ChecksumFor(sums, "pdn-qso_arm64.deb")
            .Should().Be("aa1f2c4d5e6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5daa");
        ReleaseAsset.ChecksumFor(sums, "pdn-qso_riscv64.deb")
            .Should().BeNull("a package that is not in the release has no checksum to match");
        ReleaseAsset.ChecksumFor(null, "pdn-qso_amd64.deb").Should().BeNull();
    }

    [Fact]
    public void A_Binary_Mode_Checksum_Line_Is_Read_The_Same_Way()
    {
        // sha256sum -b marks the name with a star. Same file, same hash.
        ReleaseAsset.ChecksumFor(
            "3b1f2c4d5e6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e *pdn-qso_amd64.deb",
            "pdn-qso_amd64.deb")
            .Should().Be("3b1f2c4d5e6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e");
    }

    [Fact]
    public void Root_Installs_With_Apt_And_No_Password()
    {
        ReleaseAsset.InstallCommand? command = ReleaseAsset.InstallWith(
            "/tmp/pdn-qso.deb", isRoot: true, hasApt: true, hasSudo: true);

        command.Should().NotBeNull();
        command!.Value.NeedsPassword.Should().BeFalse();
        command.Value.ToString().Should()
            .Be("apt-get install --yes --reinstall /tmp/pdn-qso.deb");
    }

    [Fact]
    public void An_Ordinary_User_Goes_Through_Sudo()
    {
        ReleaseAsset.InstallCommand? command = ReleaseAsset.InstallWith(
            "/tmp/pdn-qso.deb", isRoot: false, hasApt: true, hasSudo: true);

        command.Should().NotBeNull();
        command!.Value.NeedsPassword.Should().BeTrue("sudo may well ask, and saying so first is kinder");
        command.Value.ToString().Should()
            .Be("sudo apt-get install --yes --reinstall /tmp/pdn-qso.deb");
    }

    [Fact]
    public void Without_Apt_It_Falls_Back_To_Dpkg()
    {
        // apt-get is preferred because the package depends on libasound2 | libasound2t64 and an
        // alternation is what dpkg cannot resolve. On a machine that already has pdn-qso the
        // dependencies are satisfied either way, so this is a fallback and not a trap.
        ReleaseAsset.InstallWith("/tmp/pdn-qso.deb", isRoot: true, hasApt: false, hasSudo: false)
            .Should().NotBeNull();
        ReleaseAsset.InstallWith("/tmp/pdn-qso.deb", isRoot: true, hasApt: false, hasSudo: false)!
            .Value.ToString().Should().Be("dpkg --install /tmp/pdn-qso.deb");
    }

    [Fact]
    public void With_Neither_Root_Nor_Sudo_There_Is_No_Command_To_Run()
    {
        // The upgrade stops here and tells the operator what to run, rather than pretending.
        ReleaseAsset.InstallWith("/tmp/pdn-qso.deb", isRoot: false, hasApt: true, hasSudo: false)
            .Should().BeNull();
    }

    [Fact]
    public void This_Machines_Architecture_Is_One_A_Package_Is_Built_For()
    {
        // Not a tautology: it is the check that the three names here and the three the release
        // pipeline builds are the same three, on whatever the tests are running on.
        ReleaseAsset.CurrentArchitecture.Should().BeOneOf("amd64", "arm64", "armhf");
    }
}
