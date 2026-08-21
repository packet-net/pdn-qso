using System.Text;

namespace PdnQso.Tests;

/// <summary>
/// The house style rules from CLAUDE.md, enforced rather than remembered: no em dashes and no
/// en dashes anywhere, and printable strings stay ASCII.
/// </summary>
/// <remarks>
/// The second rule has a practical reason behind the taste: journalctl's pager runs under a C
/// locale on a typical Debian box, so anything above 0x7F comes back as <c>&lt;E2&gt;&lt;80&gt;&lt;94&gt;</c>
/// on a station console that is not ours to configure. Maths notation in a comment is fine and
/// is deliberately not what this checks.
/// </remarks>
public class SourceTextTests
{
    // Spelled by code point on purpose: a file that enforces this rule must not be the
    // one file in the repository that breaks it.
    private const char EmDash = '\u2014';
    private const char EnDash = '\u2013';

    private static readonly string[] Extensions =
        [".cs", ".csproj", ".props", ".slnx", ".md", ".yml", ".yaml", ".sh", ".json", ".py"];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PdnQso.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests should be able to find the repository they are in");
        return directory!.FullName;
    }

    private static IEnumerable<string> SourceFiles()
    {
        string root = RepositoryRoot();
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith("artifacts" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            if (Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    [Fact]
    public void No_File_Contains_An_Em_Dash_Or_An_En_Dash()
    {
        var offenders = new List<string>();
        foreach (string path in SourceFiles())
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            int em = text.IndexOf(EmDash, StringComparison.Ordinal);
            int en = text.IndexOf(EnDash, StringComparison.Ordinal);
            if (em >= 0 || en >= 0)
            {
                int at = em >= 0 ? em : en;
                int line = text.AsSpan(0, at).Count('\n') + 1;
                offenders.Add($"{path}:{line}");
            }
        }

        offenders.Should().BeEmpty(
            "CLAUDE.md is not negotiable about this: a hyphen, a comma, a semicolon or a full stop");
    }

    [Fact]
    public void The_Source_Files_Are_Ascii_Apart_From_Comments()
    {
        // Deliberately narrow: a non-ASCII character inside a string literal is what reaches a
        // terminal, and that is what this is about. Maths notation in a comment is fine.
        var offenders = new List<string>();
        foreach (string path in SourceFiles().Where(p => p.EndsWith(".cs", StringComparison.Ordinal)))
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!line.Contains('"', StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Any(c => c > '\x7E'))
                {
                    offenders.Add($"{path}:{i + 1}");
                }
            }
        }

        offenders.Should().BeEmpty("journalctl under a C locale renders anything above 0x7F as hex");
    }
}
