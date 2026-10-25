/// <summary>Defines one inclusive UTC observation window and its output label.</summary>
public sealed record ObservationPeriod(
    string Label,
    DateTimeOffset Start,
    DateTimeOffset End,
    string FileSuffix);

/// <summary>Centralizes the repository, observation windows, and local data paths.</summary>
public static class AnalysisConfig
{
    public const string Owner = "axios";
    public const string Repository = "axios";

    public static readonly ObservationPeriod Current = new(
        "Current",
        new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 31, 23, 59, 59, TimeSpan.Zero),
        "current");

    public static string OutputDirectory
    {
        get
        {
            var path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "Extracted_data"));
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string LocalRepositoryDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("AXIOS_REPO_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return Path.GetFullPath(overridePath);
            }

            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "Repo", "axios_repository"));
        }
    }
}
