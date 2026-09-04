using System.Text.RegularExpressions;
using Xunit;

namespace MongoMigrationWebApp.Tests;

/// <summary>
/// Guards the app against navigation that escapes its IIS application path.
/// </summary>
/// <remarks>
/// We host several instances of the utility as sub-applications under one site
/// (/, /pipeline-v1, /bench), so BaseUri carries a path base. NavigationManager resolves a
/// relative target against that base, but an absolute "/..." target ignores it and lands on
/// whichever instance owns the site root. That failure is invisible in a single-instance
/// deployment and in the test host, so nothing else catches it — hence a source scan.
/// </remarks>
public class RelativeNavigationTests
{
    // A string literal opening with a slash, whether plain ("/x") or interpolated ($"/x"),
    // anywhere in the argument list — which also covers the `ReturnUrl ?? "/"` fallback form.
    private static readonly Regex AbsoluteTarget = new(
        @"NavigateTo\([^;]*?\$?""/",
        RegexOptions.Compiled);

    [Fact]
    public void No_navigation_target_is_rooted_at_the_site_root()
    {
        var appRoot = Path.Combine(RepositoryRoot(), "MongoMigrationWebApp");
        Assert.True(Directory.Exists(appRoot), $"App source not found at {appRoot}");

        var offenders = Directory
            .EnumerateFiles(appRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (File: Path.GetRelativePath(appRoot, f), Number: i + 1, Text: line))
                .Where(l => AbsoluteTarget.IsMatch(l.Text)))
            .Select(l => $"{l.File}:{l.Number}  {l.Text.Trim()}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Navigation target must be relative so it resolves against the application base URI. "
                + "Drop the leading slash:" + Environment.NewLine
                + string.Join(Environment.NewLine, offenders));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OnlineMongoMigration.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
