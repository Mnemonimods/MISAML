using System;
using System.Linq;
using System.Text.RegularExpressions;
using MISAML.Bootstrap.Interfaces;
using Semver;

namespace MISAML.Bootstrap;

// Cargo style version requirements.
//
// The Semver library does the version comparing, this only translates
// Cargo spelling into Semver lang I suppose
public class VersionReq : IVersionReq
{
    // returns null (and says why in error) when the text is not a valid req
    public SemVersionRange? TryParse(string txt, out string? err)
    {
        err = null;

        if (string.IsNullOrWhiteSpace(txt))
        {
            err = "is empty";
            return null;
        }

        try
        {
            var npm = string.Join(" || ", txt.Split("||").Select(AllOf));
            return SemVersionRange.ParseNpm(npm, includeAllPrerelease: false);
        }
        catch (Exception e)
        {
            err = $"'{txt}' is not a valid version requirement ({e.Message})";
            return null;
        }
    }

    // a mod's own version, must look like 1.2.3 (or 1.2.3-beta.1), null if not
    public SemVersion? ParseVersion(string? txt)
    {
        if (txt is null)
            return null;

        try { return SemVersion.Parse(txt, SemVersionStyles.Strict); }
        catch (Exception) { return null; }
    }

    // comparators separated by commas must all match
    private static string AllOf(string group)
        => string.Join(" ", group
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Comparator));

    private static string Comparator(string c)
    {
        // Cargo allows a space btwn the op and the ver (">= 1.2.3")
        c = Regex.Replace(c, @"^([\^~=<>]+)\s+", "$1");

        // a bare version like 1.2.3 means ^1.2.3 in Cargo, wildcards (1.*) stay as they are
        var bare = char.IsDigit(c[0]) && c.IndexOfAny(['*', 'x', 'X']) < 0;
        return bare ? "^" + c : c;
    }
}