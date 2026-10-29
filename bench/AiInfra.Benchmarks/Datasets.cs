using System.Text.Json;

namespace AiInfra.Benchmarks;

/// <summary>Locates and loads the shared inputs written by bench/datasets/generate_m0.py.</summary>
public static class Datasets
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string M0 => Path.Combine(RepoRoot, "bench", "datasets", "data", "m0");

    public static JsonElement Manifest(string section)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(M0, "manifest.json")));
        return doc.RootElement.GetProperty(section).Clone();
    }

    /// <summary>Reads a newline-separated file into one blob plus start offsets (offsets[^1] = end).</summary>
    public static (byte[] Blob, int[] Offsets) ReadLines(string fileName)
    {
        byte[] blob = File.ReadAllBytes(Path.Combine(M0, fileName));
        var offsets = new List<int> { 0 };
        for (int i = 0; i < blob.Length; i++)
        {
            if (blob[i] == (byte)'\n')
                offsets.Add(i + 1);
        }
        return (blob, offsets.ToArray());
    }

    private static string FindRepoRoot()
    {
        // BenchmarkDotNet runs benchmarks from a generated project below bin/, so walk up.
        string? dir = Environment.GetEnvironmentVariable("AIINFRA_ROOT") ?? AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "AiInfra.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Repository root not found; set AIINFRA_ROOT.");
    }
}
