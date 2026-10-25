using System.Text;
using System.Text.Json;

/// <summary>Collects PR lifecycle and change-size details for the current window.</summary>
public static class CollectPullRequests
{
    private const int PageSize = 100;

    private static readonly string Header = string.Join(",", new[]
    {
        "PrNumber", "PrId", "AuthorLogin", "AuthorId", "AuthorType",
        "AuthorAssociation", "CreatedAt", "UpdatedAt", "ClosedAt", "MergedAt",
        "MergedBy", "State", "IsMerged", "IsDraft", "Labels", "Assignees",
        "RequestedReviewers", "CommentsCount", "ReviewCommentsCount", "CommitsCount",
        "ChangedFiles", "Additions", "Deletions", "BaseBranch", "HeadBranch",
        "HeadRepository", "MergeCommitSha", "Url", "Title", "Body", "PeriodLabel"
    });

    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "GITHUB_TOKEN is not set. In PowerShell, run " +
                "$env:GITHUB_TOKEN = 'your_token' before starting the collector.");
        }

        using var client = new GitHubRestClient(token);
        var output = PrepareOutput(AnalysisConfig.Current);

        Console.WriteLine(
            $"Collecting pull requests for {AnalysisConfig.Owner}/{AnalysisConfig.Repository}...");
        Console.WriteLine("Existing PR numbers in the output file will be skipped.");

        var page = 1;
        var reachedWindowStart = false;

        while (!reachedWindowStart)
        {
            var url = $"repos/{AnalysisConfig.Owner}/{AnalysisConfig.Repository}/pulls" +
                      $"?state=all&sort=created&direction=desc&per_page={PageSize}&page={page}";
            var items = await client.GetJsonAsync(url, cancellationToken);

            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                break;
            }

            Console.WriteLine($"Scanning PR list page {page} ({items.GetArrayLength()} entries)...");

            foreach (var item in items.EnumerateArray())
            {
                var createdAt = ParseTimestamp(item, "created_at");
                if (createdAt < AnalysisConfig.Current.Start)
                {
                    reachedWindowStart = true;
                    break;
                }

                if (createdAt > AnalysisConfig.Current.End)
                {
                    continue;
                }

                var number = GetInt32(item, "number");
                if (output.CompletedNumbers.Contains(number))
                {
                    continue;
                }

                var detail = await client.GetJsonAsync(
                    $"repos/{AnalysisConfig.Owner}/{AnalysisConfig.Repository}/pulls/{number}",
                    cancellationToken);
                var row = BuildRow(detail, AnalysisConfig.Current.Label);

                await File.AppendAllTextAsync(
                    output.Path,
                    row + Environment.NewLine,
                    Encoding.UTF8,
                    cancellationToken);
                output.CompletedNumbers.Add(number);
                output.NewRecords++;

                if (output.NewRecords % 25 == 0)
                {
                    Console.WriteLine(
                        $"  Saved {output.NewRecords} new PR records this run.");
                }
            }

            if (items.GetArrayLength() < PageSize)
            {
                break;
            }

            page++;
        }

        Console.WriteLine(
            $"{output.NewRecords} new, " +
            $"{output.CompletedNumbers.Count} total records -> {output.Path}");
    }

    // Existing PR numbers make a repeated run append only records not yet saved.
    private static CollectorOutput PrepareOutput(ObservationPeriod period)
    {
        var path = Path.Combine(
            AnalysisConfig.OutputDirectory,
            $"pull_requests_{period.FileSuffix}.csv");

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            File.WriteAllText(path, Header + Environment.NewLine, new UTF8Encoding(true));
        }
        else
        {
            var existingHeader = File.ReadLines(path).FirstOrDefault()?.TrimStart('\uFEFF');
            if (!string.Equals(existingHeader, Header, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Existing file has an incompatible header: {path}. " +
                    "Move or rename it before running this collector.");
            }
        }

        var completed = new HashSet<int>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var firstComma = line.IndexOf(',');
            var firstField = firstComma >= 0 ? line[..firstComma] : line;
            if (int.TryParse(firstField.Trim('"'), out var number))
            {
                completed.Add(number);
            }
        }

        return new CollectorOutput(path, completed);
    }

    private static string BuildRow(JsonElement pullRequest, string periodLabel)
    {
        var user = GetObject(pullRequest, "user");
        var mergedBy = GetObject(pullRequest, "merged_by");
        var head = GetObject(pullRequest, "head");
        var headRepo = GetObject(head, "repo");
        var baseInfo = GetObject(pullRequest, "base");

        return CsvUtilities.Row(
            GetInt32(pullRequest, "number"),
            GetInt64(pullRequest, "id"),
            GetString(user, "login"),
            GetNullableInt64(user, "id"),
            GetString(user, "type"),
            GetString(pullRequest, "author_association"),
            GetNullableTimestamp(pullRequest, "created_at"),
            GetNullableTimestamp(pullRequest, "updated_at"),
            GetNullableTimestamp(pullRequest, "closed_at"),
            GetNullableTimestamp(pullRequest, "merged_at"),
            GetString(mergedBy, "login"),
            GetString(pullRequest, "state"),
            GetNullableBoolean(pullRequest, "merged"),
            GetNullableBoolean(pullRequest, "draft"),
            JoinObjectProperty(pullRequest, "labels", "name"),
            JoinObjectProperty(pullRequest, "assignees", "login"),
            JoinObjectProperty(pullRequest, "requested_reviewers", "login"),
            GetNullableInt32(pullRequest, "comments"),
            GetNullableInt32(pullRequest, "review_comments"),
            GetNullableInt32(pullRequest, "commits"),
            GetNullableInt32(pullRequest, "changed_files"),
            GetNullableInt32(pullRequest, "additions"),
            GetNullableInt32(pullRequest, "deletions"),
            GetString(baseInfo, "ref"),
            GetString(head, "ref"),
            GetString(headRepo, "full_name"),
            GetString(pullRequest, "merge_commit_sha"),
            GetString(pullRequest, "html_url"),
            GetString(pullRequest, "title"),
            GetString(pullRequest, "body"),
            periodLabel);
    }

    private static string JoinObjectProperty(
        JsonElement source,
        string arrayName,
        string propertyName)
    {
        if (!source.TryGetProperty(arrayName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(";", array.EnumerateArray()
            .Select(item => GetString(item, propertyName))
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static JsonElement? GetObject(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static JsonElement? GetObject(JsonElement? source, string name) =>
        source.HasValue ? GetObject(source.Value, name) : null;

    private static string GetString(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string GetString(JsonElement? source, string name) =>
        source.HasValue ? GetString(source.Value, name) : string.Empty;

    private static int GetInt32(JsonElement source, string name) =>
        source.GetProperty(name).GetInt32();

    private static int? GetNullableInt32(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static long GetInt64(JsonElement source, string name) =>
        source.GetProperty(name).GetInt64();

    private static long? GetNullableInt64(JsonElement? source, string name) =>
        source.HasValue && source.Value.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static bool? GetNullableBoolean(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTimeOffset ParseTimestamp(JsonElement source, string name) =>
        DateTimeOffset.Parse(source.GetProperty(name).GetString()!);

    private static DateTimeOffset? GetNullableTimestamp(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(value.GetString()!)
            : null;

    private sealed class CollectorOutput
    {
        public CollectorOutput(string path, HashSet<int> completedNumbers)
        {
            Path = path;
            CompletedNumbers = completedNumbers;
        }

        public string Path { get; }
        public HashSet<int> CompletedNumbers { get; }
        public int NewRecords { get; set; }
    }
}
