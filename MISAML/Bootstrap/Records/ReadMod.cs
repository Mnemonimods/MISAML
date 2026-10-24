namespace MISAML.Bootstrap.Records;

// a mod folder w/ parsed manifest
public record ReadMod(string Dir, ModManifest Manifest);