namespace Fileman.Cli;

/// <summary>Einfache Zerlegung der Kommandozeile: Positionsargumente und Optionen der Form --name wert.</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public Arguments(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg[2..];
                string? value = null;
                if (!Flags.Contains(name) && i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                    value = list[++i];
                _options[name] = value;
            }
            else
            {
                Positional.Add(arg);
            }
        }
    }

    /// <summary>Optionen ohne Wert.</summary>
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "help", "deleted", "quiet", "force", "yes" };

    private Arguments(List<string> positional, Dictionary<string, string?> options)
    {
        Positional = positional;
        _options = options;
    }

    public List<string> Positional { get; } = [];

    /// <summary>Dieselben Optionen, ohne das erste Positionsargument (den Befehl).</summary>
    public Arguments Shift() => new(Positional.Skip(1).ToList(), _options);

    public string? Option(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public bool Flag(string name) => _options.ContainsKey(name);

    public string Require(int index, string description) =>
        index < Positional.Count ? Positional[index] : throw new UsageException($"Es fehlt: {description}.");

    public long RequireId(int index, string description) =>
        long.TryParse(Require(index, description), out var id) ? id : throw new UsageException($"\"{Positional[index]}\" ist keine gültige Nummer ({description}).");
}

internal sealed class UsageException(string message) : Exception(message);
