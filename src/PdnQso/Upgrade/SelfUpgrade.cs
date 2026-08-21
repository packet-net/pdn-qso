using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace PdnQso.Upgrade;

/// <summary>
/// <c>pdn-qso --upgrade</c>: fetch the current release's package for this architecture, check
/// it against the release's own checksums, and let the package manager install it over the
/// top.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the tool is handed to somebody who is not going to watch a repository
/// for tags. There is no apt repository to add and no key to trust: the packages hang off the
/// GitHub release under names with no version in them, so one URL per architecture always
/// points at the current one.
/// </para>
/// <para>
/// It is deliberately not automatic. Nothing here checks for updates in the background, and
/// nothing installs anything the operator did not ask for: a station in the middle of a QSO
/// does not want its program replaced underneath it.
/// </para>
/// </remarks>
public static class SelfUpgrade
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    /// <summary>Runs the upgrade, saying what it is doing as it goes.</summary>
    /// <param name="currentVersion">The version running now.</param>
    /// <param name="say">Where the progress lines go; one call is one line.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>A process exit code: 0 when there is nothing to do or the install succeeded,
    /// 1 when the package was fetched but this machine cannot install it unattended, 2 when
    /// the upgrade could not be done at all.</returns>
    public static async Task<int> RunAsync(
        string currentVersion, Action<string> say, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(say);

        if (ReleaseAsset.CurrentArchitecture is not string architecture)
        {
            say($"no package is built for this architecture "
                + $"({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}).");
            return 2;
        }

        string fileName = ReleaseAsset.FileNameFor(architecture);
        Uri latest = ReleaseAsset.LatestFor(architecture);
        say($"looking for the current release of {fileName}");

        Uri resolved;
        string version;
        try
        {
            if (await ResolveAsync(latest, cancellationToken).ConfigureAwait(false) is not
                (Uri found, string tag))
            {
                say($"the current release has no {fileName}. Either it predates this upgrade "
                    + $"command or it was not built for {architecture}; there is a package list "
                    + $"at https://github.com/{ReleaseAsset.Repository}/releases/latest");
                return 2;
            }

            resolved = found;
            version = ReleaseAsset.VersionFromTag(tag);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            say($"could not reach GitHub: {e.Message}");
            return 2;
        }

        int comparison = ReleaseAsset.CompareVersions(version, currentVersion);
        if (comparison == 0)
        {
            say($"pdn-qso {currentVersion} is the current release. Nothing to do.");
            return 0;
        }

        if (comparison < 0)
        {
            // A build from source says 1.0.0, because that is what the SDK stamps when no tag
            // named it. Upgrading that to an older release would be a downgrade dressed up.
            say($"this is pdn-qso {currentVersion}, which is ahead of the current release "
                + $"({version}). Nothing to do.");
            return 0;
        }

        say($"pdn-qso {version} is available; this is {currentVersion}.");

        // GitHub answers latest/download/<anything> with a redirect whether or not the asset
        // is really there, so the 404 for a missing one only arrives at the second hop. Ask
        // now, so that a release without this package says so plainly instead of failing later
        // as a checksum that does not list it.
        try
        {
            if (!await ExistsAsync(resolved, cancellationToken).ConfigureAwait(false))
            {
                say($"the {version} release has no {fileName}. Releases before this upgrade "
                    + "command was written name their packages differently; install it by hand "
                    + $"from https://github.com/{ReleaseAsset.Repository}/releases/latest");
                return 2;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            say($"could not reach GitHub: {e.Message}");
            return 2;
        }

        string download = Path.Combine(Path.GetTempPath(), $"pdn-qso-upgrade-{version}.deb");
        try
        {
            string? expected;
            try
            {
                expected = ReleaseAsset.ChecksumFor(
                    await TextAsync(Beside(resolved, ReleaseAsset.ChecksumFileName), cancellationToken)
                        .ConfigureAwait(false),
                    fileName);

                if (expected is null)
                {
                    // Refusing beats installing bytes nothing vouches for. The operator can
                    // still do it by hand, having decided that for themselves.
                    say($"the {version} release does not list {fileName} in its "
                        + $"{ReleaseAsset.ChecksumFileName}, so the download cannot be checked. "
                        + "Refusing to install it. Fetch it by hand if you are sure: "
                        + resolved);
                    return 2;
                }

                say($"downloading {fileName} from the {version} release");
                await SaveAsync(resolved, download, say, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                say($"the download failed: {e.Message}");
                return 2;
            }

            string actual = await Sha256Async(download, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                say("the download does not match the release's checksum, so it is being thrown "
                    + $"away rather than installed. Expected {expected}, got {actual}.");
                return 2;
            }

            say("checksum matches.");

            ReleaseAsset.InstallCommand? install = ReleaseAsset.InstallWith(
                download,
                isRoot: Environment.IsPrivilegedProcess,
                hasApt: OnPath("apt-get"),
                hasSudo: OnPath("sudo"));

            if (install is not ReleaseAsset.InstallCommand command)
            {
                say($"the package is at {download}. This is not root and there is no sudo here, "
                    + "so install it yourself with:");
                say($"  apt-get install --yes --reinstall {download}");

                // Left on disc on purpose: it is the whole point of the return code.
                download = "";
                return 1;
            }

            if (command.NeedsPassword)
            {
                say($"installing with sudo; it may ask for your password.");
            }

            say($"  {command}");
            int code = await InstallAsync(command, cancellationToken).ConfigureAwait(false);
            if (code != 0)
            {
                say($"the install failed with exit code {code}. The package is at {download}.");
                download = "";
                return code;
            }

            say($"pdn-qso {version} installed. Start it again to run the new one.");
            return 0;
        }
        finally
        {
            if (download.Length > 0)
            {
                try
                {
                    File.Delete(download);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A temp file nobody could delete is not a failed upgrade.
                }
            }
        }
    }

    /// <summary>
    /// Follows the first redirect from a <c>latest/download</c> URL, which is the one that
    /// names the release, and returns both it and the tag.
    /// </summary>
    private static async Task<(Uri Asset, string Tag)?> ResolveAsync(
        Uri latest, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using HttpClient client = Client(handler);

        using var request = new HttpRequestMessage(HttpMethod.Head, latest);
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        Uri? location = response.Headers.Location;
        if (location is not null && !location.IsAbsoluteUri)
        {
            location = new Uri(latest, location);
        }

        return ReleaseAsset.TagFromRedirect(location) is string tag && location is not null
            ? (location, tag)
            : null;
    }

    /// <summary>Whether an asset URL really has something behind it.</summary>
    private static async Task<bool> ExistsAsync(Uri asset, CancellationToken cancellationToken)
    {
        using HttpClient client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Head, asset);
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>The URL of another file in the same release directory.</summary>
    private static Uri Beside(Uri asset, string fileName) =>
        new(asset, fileName);

    private static async Task<string> TextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpClient client = Client();
        return await client.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveAsync(
        Uri uri, string path, Action<string> say, CancellationToken cancellationToken)
    {
        using HttpClient client = Client();
        using HttpResponseMessage response = await client
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using Stream source = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[128 * 1024];
        long done = 0;
        long announced = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            done += read;

            // A line every quarter of the way: enough to show a 30 MB download is moving on a
            // slow link, few enough not to scroll the reason for the upgrade off the screen.
            if (total is long size && size > 0 && done - announced >= size / 4)
            {
                announced = done;
                say(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {done / 1024 / 1024} of {size / 1024 / 1024} MB"));
            }
        }

        say(string.Create(CultureInfo.InvariantCulture, $"  {done / 1024 / 1024} MB downloaded"));
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<int> InstallAsync(
        ReleaseAsset.InstallCommand command, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(command.File)
        {
            // Not redirected: sudo has to be able to reach the terminal to ask for a password,
            // and apt's own output is what the operator wants to see if it goes wrong.
            UseShellExecute = false,
        };

        foreach (string argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(start);
        if (process is null)
        {
            return 2;
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>Whether a program is on PATH, without running it.</summary>
    private static bool OnPath(string program)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, program)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not this command's problem.
            }
        }

        return false;
    }

    private static HttpClient Client(HttpMessageHandler? handler = null)
    {
        HttpClient client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Patience;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("pdn-qso-upgrade");
        return client;
    }
}
