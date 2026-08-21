using System.Globalization;
using System.Runtime.InteropServices;

namespace PdnQso.Upgrade;

/// <summary>
/// Where a release lives and what it is called: the pure half of <see cref="SelfUpgrade"/>, so
/// the naming, the version arithmetic and the choice of install command can be tested without
/// a network or a package manager.
/// </summary>
/// <remarks>
/// The packages are attached to each release under a name with no version in it, so
/// <c>releases/latest/download/pdn-qso_&lt;arch&gt;.deb</c> is a URL that never has to be
/// rewritten. The version is still in the package's own control data, and GitHub still says
/// which release it came from: the first redirect from that URL names the tag, which is how
/// <see cref="TagFromRedirect"/> learns what the latest version is without an API call, a
/// token or a rate limit.
/// </remarks>
public static class ReleaseAsset
{
    /// <summary>The repository these packages come from.</summary>
    public const string Repository = "packet-net/pdn-qso";

    /// <summary>The unchanging URL of the current release's package for one architecture.</summary>
    /// <param name="architecture">A Debian architecture name: amd64, arm64 or armhf.</param>
    public static Uri LatestFor(string architecture) => new(
        $"https://github.com/{Repository}/releases/latest/download/{FileNameFor(architecture)}");

    /// <summary>What the package for one architecture is called.</summary>
    /// <param name="architecture">A Debian architecture name.</param>
    public static string FileNameFor(string architecture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);
        return $"pdn-qso_{architecture}.deb";
    }

    /// <summary>The checksum file that sits beside the packages in every release.</summary>
    public const string ChecksumFileName = "SHA256SUMS";

    /// <summary>
    /// The Debian architecture this process is running as, or null if it is one no package is
    /// built for.
    /// </summary>
    /// <remarks>
    /// From the process architecture rather than from <c>dpkg --print-architecture</c>: a
    /// 32-bit build running on a 64-bit kernel has to be told to upgrade itself with the
    /// package it can actually execute, and dpkg would name the kernel's.
    /// </remarks>
    public static string? CurrentArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "armhf",
        _ => null,
    };

    /// <summary>
    /// The release tag in the first redirect from a <c>latest/download</c> URL, or null if the
    /// location is not one.
    /// </summary>
    /// <remarks>
    /// GitHub answers <c>latest/download/&lt;name&gt;</c> with a 302 to
    /// <c>releases/download/&lt;tag&gt;/&lt;name&gt;</c>, and only then with a second redirect
    /// to a signed asset URL on another host that carries no tag at all. So the tag is read
    /// from the first hop, and the second hop's URL is the one to download: that way the bytes
    /// fetched are the bytes of the release that was just identified, even if a new one is
    /// published in between.
    /// </remarks>
    /// <param name="location">The <c>Location</c> header of the first response.</param>
    public static string? TagFromRedirect(Uri? location)
    {
        if (location is null)
        {
            return null;
        }

        string[] segments = location.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int download = Array.LastIndexOf(segments, "download");
        return download >= 0 && download + 1 < segments.Length ? segments[download + 1] : null;
    }

    /// <summary>A release tag as a plain version: <c>v0.2.0</c> is <c>0.2.0</c>.</summary>
    /// <param name="tag">The tag, with or without its leading v.</param>
    public static string VersionFromTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.StartsWith('v') ? tag[1..] : tag;
    }

    /// <summary>
    /// Compares two versions the way the release tags are numbered: dotted numbers first, and
    /// then a release beats any prerelease of the same numbers, so 0.3.0 is above 0.3.0-rc1 is
    /// above 0.2.9.
    /// </summary>
    /// <returns>Negative if <paramref name="left"/> is the older, zero if they are the same
    /// version, positive if it is the newer.</returns>
    /// <remarks>
    /// Deliberately not a full SemVer implementation: these tags are numbers and the occasional
    /// rc, and an unparsable part sorts as zero rather than throwing. An upgrade command that
    /// crashed on an odd version string would be worse than one that offered an upgrade it did
    /// not need to.
    /// </remarks>
    public static int CompareVersions(string? left, string? right)
    {
        (int[] leftNumbers, string leftTail) = Split(left);
        (int[] rightNumbers, string rightTail) = Split(right);

        for (int i = 0; i < Math.Max(leftNumbers.Length, rightNumbers.Length); i++)
        {
            int a = i < leftNumbers.Length ? leftNumbers[i] : 0;
            int b = i < rightNumbers.Length ? rightNumbers[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        // Same numbers: no prerelease suffix is the finished thing and outranks any suffix.
        if (leftTail.Length == 0 && rightTail.Length == 0)
        {
            return 0;
        }

        if (leftTail.Length == 0)
        {
            return 1;
        }

        if (rightTail.Length == 0)
        {
            return -1;
        }

        return string.CompareOrdinal(leftTail, rightTail);
    }

    /// <summary>The SHA-256 a <c>sha256sum</c> file gives for one name, or null if it has none.</summary>
    /// <param name="checksums">The whole file: one "hash  name" line per package.</param>
    /// <param name="fileName">The name to look up.</param>
    public static string? ChecksumFor(string? checksums, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (string.IsNullOrWhiteSpace(checksums))
        {
            return null;
        }

        foreach (string line in checksums.Split('\n'))
        {
            string[] parts = line.Trim().Split(
                (char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            // "<hash>  <name>", with a leading * on the name in binary mode.
            if (parts.Length >= 2 && parts[^1].TrimStart('*') == fileName)
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>How to install a downloaded package on this machine.</summary>
    /// <param name="File">The program to run.</param>
    /// <param name="Arguments">Its arguments, already split.</param>
    /// <param name="NeedsPassword">True when it goes through sudo and may ask for a password.</param>
    public readonly record struct InstallCommand(
        string File, IReadOnlyList<string> Arguments, bool NeedsPassword)
    {
        /// <summary>The command as somebody would type it, for a message.</summary>
        public override string ToString() =>
            string.Join(' ', [File, .. Arguments]);
    }

    /// <summary>
    /// Picks the install command for the privileges and tools this machine has, or null when it
    /// has neither root nor sudo and the operator has to finish the job themselves.
    /// </summary>
    /// <param name="package">The downloaded .deb, as an absolute path.</param>
    /// <param name="isRoot">Whether this process is already root.</param>
    /// <param name="hasApt">Whether <c>apt-get</c> is on the path.</param>
    /// <param name="hasSudo">Whether <c>sudo</c> is on the path.</param>
    /// <remarks>
    /// apt-get in preference to dpkg because the package depends on
    /// <c>libasound2 | libasound2t64</c>, and an alternation is exactly what dpkg cannot
    /// resolve on its own. On a machine that already has pdn-qso installed the dependencies are
    /// satisfied either way, so dpkg is a sound fallback rather than a broken one.
    /// </remarks>
    public static InstallCommand? InstallWith(
        string package, bool isRoot, bool hasApt, bool hasSudo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);

        string[] arguments = hasApt
            ? ["install", "--yes", "--reinstall", package]
            : ["--install", package];
        string program = hasApt ? "apt-get" : "dpkg";

        if (isRoot)
        {
            return new InstallCommand(program, arguments, NeedsPassword: false);
        }

        return hasSudo
            ? new InstallCommand("sudo", [program, .. arguments], NeedsPassword: true)
            : null;
    }

    private static (int[] Numbers, string Tail) Split(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return ([], "");
        }

        string text = version.Trim();
        if (text.StartsWith('v'))
        {
            text = text[1..];
        }

        // The '+' suffix is the SDK's source-revision stamp and says nothing about the release.
        int plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        int dash = text.IndexOf('-', StringComparison.Ordinal);
        string tail = dash >= 0 ? text[(dash + 1)..] : "";
        string numbers = dash >= 0 ? text[..dash] : text;

        string[] parts = numbers.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            values[i] = int.TryParse(parts[i], CultureInfo.InvariantCulture, out int value) ? value : 0;
        }

        return (values, tail);
    }
}
