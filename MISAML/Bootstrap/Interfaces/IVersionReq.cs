using Semver;

namespace MISAML.Bootstrap.Interfaces;

public interface IVersionReq
{
    SemVersionRange? TryParse(string txt, out string? err);

    SemVersion? ParseVersion(string? txt);
}