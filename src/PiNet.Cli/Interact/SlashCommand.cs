namespace PiNet.Cli.Interact;

public enum SlashCommandKind
{
    Unknown,
    Help,
    Quit,
    NewSession,
    Resume,
    Fork,
    Model
}

public sealed record SlashCommand(SlashCommandKind Kind, string Name, string? Arg, string Raw);

public static class SlashCommandParser
{
    public static SlashCommand Parse(string line)
    {
        var trimmed = line.TrimStart();
        var raw = line;
        var rest = trimmed.StartsWith('/') ? trimmed[1..].TrimStart() : trimmed;
        var nameEnd = rest.IndexOfAny([' ', '\t']);
        var name = nameEnd < 0 ? rest : rest[..nameEnd];
        var arg = nameEnd < 0 ? null : rest[(nameEnd + 1)..].Trim();
        if (string.IsNullOrEmpty(name))
            name = string.Empty;

        var kind = name.ToLowerInvariant() switch
        {
            "help" => SlashCommandKind.Help,
            "quit" or "exit" => SlashCommandKind.Quit,
            "new" => SlashCommandKind.NewSession,
            "resume" => SlashCommandKind.Resume,
            "fork" => SlashCommandKind.Fork,
            "model" => SlashCommandKind.Model,
            _ => SlashCommandKind.Unknown
        };

        return new SlashCommand(kind, name, arg, raw);
    }
}