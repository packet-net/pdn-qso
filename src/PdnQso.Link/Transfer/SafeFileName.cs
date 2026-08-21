using System.Globalization;
using System.Text;

namespace PdnQso.Link.Transfer;

/// <summary>
/// Turns a name somebody else chose into a name this station is willing to create.
/// </summary>
/// <remarks>
/// <para>
/// A file offer's name arrives over the air from a station that is not necessarily friendly
/// and not necessarily correct. <c>../../.ssh/authorized_keys</c> is a valid UTF-8 string, and
/// so is a name with a newline in it, or one 60 000 bytes long, or one that is simply empty.
/// Everything here is about the receiver never creating a path it did not intend to.
/// </para>
/// <para>
/// The rules: keep only the last path component, keep only printable ASCII letters, digits,
/// dot, dash, underscore and space (everything else becomes an underscore, which also settles
/// the house rule that a printable string stays ASCII), never start with a dot, never be one
/// of the Windows device names, never be empty, and never be longer than
/// <see cref="MaxLength"/> characters.
/// </para>
/// </remarks>
public static class SafeFileName
{
    /// <summary>The name used when nothing usable survives the rules.</summary>
    public const string Fallback = "received.bin";

    /// <summary>The longest name this will produce.</summary>
    public const int MaxLength = 120;

    private static readonly string[] DeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Makes a name safe to create.</summary>
    /// <param name="offered">The name as it arrived.</param>
    /// <returns>A name with no directory in it, printable, ASCII and non-empty.</returns>
    public static string From(string? offered)
    {
        if (string.IsNullOrEmpty(offered))
        {
            return Fallback;
        }

        // Both separators, whatever this machine's is: the sender may not be on the same
        // operating system, and a backslash is a perfectly ordinary character in a Linux file
        // name, which is exactly why it must not survive into one.
        int cut = offered.LastIndexOfAny(['/', '\\']);
        string leaf = cut >= 0 ? offered[(cut + 1)..] : offered;

        var builder = new StringBuilder(leaf.Length);
        foreach (char c in leaf)
        {
            bool keep = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '.' or '-' or '_' or ' ';
            builder.Append(keep ? c : '_');
        }

        string name = builder.ToString().Trim();
        while (name.StartsWith('.'))
        {
            name = string.Concat("_", name.AsSpan(1));
        }

        if (name.Length > MaxLength)
        {
            // Keep the extension: a truncated name is a nuisance, a truncated extension is a
            // file the desktop cannot open.
            string extension = Path.GetExtension(name);
            if (extension.Length is > 0 and <= 16)
            {
                name = string.Concat(name.AsSpan(0, MaxLength - extension.Length), extension);
            }
            else
            {
                name = name[..MaxLength];
            }
        }

        if (name.Length == 0 || name.Trim('_', ' ', '.').Length == 0)
        {
            return Fallback;
        }

        string stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        foreach (string device in DeviceNames)
        {
            if (string.Equals(stem, device, StringComparison.Ordinal))
            {
                return string.Concat("_", name);
            }
        }

        return name;
    }

    /// <summary>
    /// A path in <paramref name="directory"/> that does not exist yet, from a name that may.
    /// </summary>
    /// <param name="directory">Where the file is to go.</param>
    /// <param name="offered">The name as it arrived.</param>
    /// <returns>A full path whose file does not exist: the safe name, or that name with
    /// <c>-1</c>, <c>-2</c> and so on before the extension.</returns>
    /// <remarks>
    /// A received file never overwrites one that is already there. Two transfers of the same
    /// name in one session are two files, and the operator decides which to keep.
    /// </remarks>
    public static string UniquePath(string directory, string? offered)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string name = From(offered);
        string candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        for (int i = 1; i < 10000; i++)
        {
            string numbered = string.Create(
                CultureInfo.InvariantCulture, $"{stem}-{i}{extension}");
            candidate = Path.Combine(directory, numbered);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException(
            $"there are already ten thousand files called '{name}' in '{directory}'");
    }
}
