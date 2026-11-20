namespace DocDetectives.Application.Skills;

/// <summary>Loads prompt rules embedded from the Prompts folder (see the EmbeddedResource item in the .csproj).</summary>
internal static class PromptLoader
{
    public static string Load(string fileName)
    {
        using var stream = typeof(PromptLoader).Assembly
            .GetManifestResourceStream($"DocDetectives.Prompts.{fileName}")
            ?? throw new InvalidOperationException($"Embedded prompt '{fileName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
