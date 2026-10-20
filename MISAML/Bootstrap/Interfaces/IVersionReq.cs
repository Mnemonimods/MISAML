using Semver;

namespace MISAML.Bootstrap.Interfaces;

public interface IVersionReq
{
    /// <summary>
    /// Tries to parse a version range from a string.
    /// </summary>
    /// <param name="txt">The string to parse.</param>
    /// <param name="err">Outputs the error message if parsing fails.</param>
    /// <returns><c>null</c> and says why in error when the text is not a valid request</returns>
    SemVersionRange? TryParse(string txt, out string? err);

    /// <summary>
    /// Parses a version from a string.
    /// </summary>
    /// <param name="txt">The string to parse</param>
    /// <returns>The mod version, returns <c>Null</c> if it does not look like a proper mod-version</returns>
    /// <remarks>Mod versions are supposed to look like the following: 1.2.3 or 1.2.3-beta.1</remarks>
    SemVersion? ParseVersion(string? txt);
}