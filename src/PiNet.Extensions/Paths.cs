namespace PiNet.Extensions;

public static class ExtensionsPaths
{
    public const string HomeEnvVar = "PI_NET_HOME";

    public static string HomeDir =>
        Environment.GetEnvironmentVariable(HomeEnvVar)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pinet");

    public static string GlobalExtensionsDir => Path.Combine(HomeDir, "extensions");
    public static string GlobalSkillsDir => Path.Combine(HomeDir, "skills");
    public static string SharedSkillsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");
    public static string GlobalPromptsDir => Path.Combine(HomeDir, "prompts");
    public static string PackagesRoot => Path.Combine(HomeDir, "packages");
    public static string PackagesIndexPath => Path.Combine(HomeDir, "packages.json");
    public static string CacheRoot => Path.Combine(HomeDir, "cache");
    public static string ExtensionCacheDir => Path.Combine(CacheRoot, "extensions");
    public static string ProjectExtensionsDir(string cwd) => Path.Combine(cwd, ".pi", "extensions");
    public static string ProjectSkillsDir(string cwd) => Path.Combine(cwd, ".pi", "skills");
    public static string ProjectAgentsSkillsDir(string cwd) => Path.Combine(cwd, ".agents", "skills");
    public static string ProjectPromptsDir(string cwd) => Path.Combine(cwd, ".pi", "prompts");
}