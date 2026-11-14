using System.Text.RegularExpressions;

namespace LlamaLauncher;

// Read the selected executable's interface; a build number is not a feature contract.
public sealed class EngineCapabilities
{
    readonly string help;
    public EngineCapabilities(string help) => this.help = help ?? "";

    string Option(string flag)
    {
        foreach (var block in Regex.Split(help, @"(?m)(?=^-[A-Za-z-])"))
        {
            string header = block.Split('\n')[0];
            string signature = Regex.Match(header, @"^\s*(-[\w-]+(?:,\s*-[\w-]+)*)").Value;
            if (Regex.IsMatch(signature, @"(?<![\w-])" + Regex.Escape(flag) + @"(?![\w-])"))
                return block;
        }
        return "";
    }

    public bool HasFlag(string flag)
    {
        string option = Option(flag);
        return option.Length > 0 && !option.Contains("argument has been removed", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasSpecType(string type)
    {
        string option = Option("--spec-type");
        // Only the advertised value list establishes support, not examples elsewhere.
        string values = Regex.Match(option.Split('\n')[0], @"--spec-type\s+(\S+)").Groups[1].Value;
        return HasFlag("--spec-type") && values.Split(new[] { ',', '|', '[', ']' }, StringSplitOptions.RemoveEmptyEntries).Contains(type);
    }

    public string RequiredFlag(params string[] aliases) => aliases.FirstOrDefault(HasFlag)
        ?? throw new ArgumentException($"The selected runtime lacks {string.Join(" / ", aliases)}. Update the engine or disable this feature.");
}
