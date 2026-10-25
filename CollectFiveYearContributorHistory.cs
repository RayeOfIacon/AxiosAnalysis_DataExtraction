using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Collects raw contributor activity from the five years immediately before the
/// Current observation period. It deliberately excludes 2025-09-01 onward.
/// Python analysis should perform identity resolution and contributor classification.
/// </summary>
public static partial class CollectFiveYearContributorHistory
{
    private const int PageSize = 100;

    private static readonly DateTimeOffset HistoryStart =
        new(2020, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset HistoryEnd =
        new(2025, 8, 31, 23, 59, 59, TimeSpan.Zero);

    private const string HistoryWindowLabel = "PreCurrent5Y";

    private static readonly string Header = string.Join(",", new[]
    {
        "EventKey", "ActivityType", "TimestampUtc", "Login", "GitHubUserId",
        "AccountType", "AuthorAssociation", "ItemNumber", "Sha", "GitAuthorName",
        "GitAuthorEmail", "Url", "HistoryWindow"
    });

    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "GITHUB_TOKEN is not set. Set it in the current PowerShell session first.");
        }

        ValidateLocalRepository();

        var outputPath = PrepareOutput();
        var existingEventKeys = ReadExistingEventKeys(outputPath);
        var checkpointPath = Path.Combine(
            AnalysisConfig.OutputDirectory,
            "contributor_history_5y_checkpoint.json");
        var checkpoint = existingEventKeys.Count == 0
            ? new HistoryCheckpoint()
            : LoadCheckpoint(checkpointPath);

        Console.WriteLine(
            $"Collecting contributor activity from {HistoryStart:O} through {HistoryEnd:O}.");
        Console.WriteLine("The Current observation period is explicitly excluded.");

        var totalNewRecords = 0;

        Console.WriteLine("Stage 1/5: extracting commits from local Git...");
        totalNewRecords += await CollectCommitsAsync(
            outputPath,
            existingEventKeys,
            cancellationToken);

        using var client = new GitHubRestClient(token);

        Console.WriteLine("Stage 2/5: collecting PR and Issue authors...");
        var itemResult = await CollectPullRequestsAndIssuesAsync(
            client,
            outputPath,
            existingEventKeys,
            cancellationToken);
        totalNewRecords += itemResult.NewRecords;

        Console.WriteLine(
            $"Stage 3/5: collecting reviews for {itemResult.PullRequestNumbers.Count} historical PRs...");
        totalNewRecords += await CollectReviewsAsync(
            client,
            itemResult.PullRequestNumbers,
            outputPath,
            existingEventKeys,
            checkpointPath,
            checkpoint,
            cancellationToken);

        Console.WriteLine("Stage 4/5: collecting PR conversation and Issue comments...");
        totalNewRecords += await CollectIssueEndpointCommentsAsync(
            client,
            outputPath,
            existingEventKeys,
            cancellationToken);

        Console.WriteLine("Stage 5/5: collecting inline PR review comments...");
        totalNewRecords += await CollectInlineReviewCommentsAsync(
            client,
            outputPath,
            existingEventKeys,
            cancellationToken);

        Console.WriteLine("Five-year contributor history collection completed.");
        Console.WriteLine($"New event rows this run: {totalNewRecords}");
        Console.WriteLine($"Total event rows: {existingEventKeys.Count}");
        Console.WriteLine($"CSV saved to {outputPath}");
    }

    // Local Git supplies commit authors without spending GitHub API requests.
    private static async Task<int> CollectCommitsAsync(
        string outputPath,
        HashSet<string> existingEventKeys,
        CancellationToken cancellationToken)
    {
        var defaultRef = (await RunGitAsync(
            cancellationToken,
            "symbolic-ref",
            "--short",
            "refs/remotes/origin/HEAD")).Trim();

        if (string.IsNullOrWhiteSpace(defaultRef))
        {
            defaultRef = "HEAD";
        }

        const string format = "%H%x1f%an%x1f%ae%x1f%cI%x1e";
        var rawLog = await RunGitAsync(
            cancellationToken,
            "log",
            defaultRef,
            $"--since={HistoryStart.ToUniversalTime():O}",
            $"--until={HistoryEnd.ToUniversalTime():O}",
            $"--format={format}");

        var newRecords = 0;
        foreach (var rawRecord in rawLog.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = rawRecord.Trim('\r', '\n').Split('\x1f');
            if (fields.Length < 4)
            {
                continue;
            }

            var sha = fields[0];
            var timestamp = DateTimeOffset.Parse(fields[3], CultureInfo.InvariantCulture);
            if (!IsInsideHistoryWindow(timestamp))
            {
                continue;
            }

            var eventKey = $"Commit:{sha}";
            if (!existingEventKeys.Add(eventKey))
            {
                continue;
            }

            var login = InferLoginFromNoReplyEmail(fields[2]);
            var row = CsvUtilities.Row(
                eventKey,
                "Commit",
                timestamp,
                login,
                null,
                null,
                null,
                null,
                sha,
                fields[1],
                fields[2],
                $"https://github.com/{AnalysisConfig.Owner}/{AnalysisConfig.Repository}/commit/{sha}",
                HistoryWindowLabel);
            await AppendRowAsync(outputPath, row, cancellationToken);
            newRecords++;
        }

        Console.WriteLine($"  Saved {newRecords} new commit events.");
        return newRecords;
    }

    private static async Task<ItemCollectionResult> CollectPullRequestsAndIssuesAsync(
        GitHubRestClient client,
        string outputPath,
        HashSet<string> existingEventKeys,
        CancellationToken cancellationToken)
    {
        var pullRequestNumbers = new HashSet<int>();
        var page = 1;
        var reachedStart = false;
        var newRecords = 0;

        while (!reachedStart)
        {
            var items = await client.GetJsonAsync(
                $"repos/{AnalysisConfig.Owner}/{AnalysisConfig.Repository}/issues" +
                $"?filter=all&state=all&sort=created&direction=desc" +
                $"&per_page={PageSize}&page={page}",
                cancellationToken);

            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in items.EnumerateArray())
            {
                var createdAt = GetTimestamp(item, "created_at");
                if (createdAt < HistoryStart)
                {
                    reachedStart = true;
                    break;
                }
                if (createdAt > HistoryEnd)
                {
                    continue;
                }

                var isPullRequest = item.TryGetProperty("pull_request", out _);
                var activityType = isPullRequest ? "PullRequest" : "Issue";
                var number = GetInt32(item, "number");
                if (isPullRequest)
                {
                    pullRequestNumbers.Add(number);
                }

                var eventKey = $"{activityType}:{number}";
                if (!existingEventKeys.Add(eventKey))
                {
                    continue;
                }

                var user = GetObject(item, "user");
                var row = CsvUtilities.Row(
                    eventKey,
                    activityType,
                    createdAt,
                    GetString(user, "login"),
                    GetNullableInt64(user, "id"),
                    GetString(user, "type"),
                    GetString(item, "author_association"),
                    number,
                    null,
                    null,
                    null,
                    GetString(item, "html_url"),
                    HistoryWindowLabel);
                await AppendRowAsync(outputPath, row, cancellationToken);
                newRecords++;
            }

            Console.WriteLine(
                $"  Item page {page}: {newRecords} new PR/Issue author events.");
            if (items.GetArrayLength() < PageSize)
            {
                break;
            }
            page++;
        }

        return new ItemCollectionResult(pullRequestNumbers, newRecords);
    }

    // Checkpoint completed PRs and deduplicate individual reviews on reruns.
    private static async Task<int> CollectReviewsAsync(
        GitHubRestClient client,
        IReadOnlyCollection<int> pullRequestNumbers,
        string outputPath,
        HashSet<string> existingEventKeys,
        string checkpointPath,
        HistoryCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var newRecords = 0;
        var checkedThisRun = 0;

        foreach (var prNumber in pullRequestNumbers.OrderBy(number => number))
        {
            if (checkpoint.CompletedReviewPrNumbers.Contains(prNumber))
            {
                continue;
            }

            var page = 1;
            while (true)
            {
                var reviews = await client.GetJsonOrNullAsync(
                    $"repos/{AnalysisConfig.Owner}/{AnalysisConfig.Repository}/pulls/{prNumber}" +
                    $"/reviews?per_page={PageSize}&page={page}",
                    cancellationToken);
                if (reviews is null)
                {
                    break;
                }

                foreach (var review in reviews.Value.EnumerateArray())
                {
                    var submittedAt = GetNullableTimestamp(review, "submitted_at");
                    if (!submittedAt.HasValue || !IsInsideHistoryWindow(submittedAt.Value))
                    {
                        continue;
                    }

                    var reviewId = GetInt64(review, "id");
                    var eventKey = $"Review:{reviewId}";
                    if (!existingEventKeys.Add(eventKey))
                    {
                        continue;
                    }

                    var user = GetObject(review, "user");
                    var row = CsvUtilities.Row(
                        eventKey,
                        "Review",
                        submittedAt,
                        GetString(user, "login"),
                        GetNullableInt64(user, "id"),
                        GetString(user, "type"),
                        GetString(review, "author_association"),
                        prNumber,
                        GetString(review, "commit_id"),
                        null,
                        null,
                        GetString(review, "html_url"),
                        HistoryWindowLabel);
                    await AppendRowAsync(outputPath, row, cancellationToken);
                    newRecords++;
                }

                if (reviews.Value.GetArrayLength() < PageSize)
                {
                    break;
                }
                page++;
            }

            checkpoint.CompletedReviewPrNumbers.Add(prNumber);
            checkedThisRun++;
            if (checkedThisRun % 25 == 0)
            {
                await SaveCheckpointAsync(checkpointPath, checkpoint, cancellationToken);
                Console.WriteLine(
                    $"  Checked {checkedThisRun} PRs this run; saved {newRecords} new reviews.");
            }
        }

        await SaveCheckpointAsync(checkpointPath, checkpoint, cancellationToken);
        return newRecords;
    }

    private static async Task<int> CollectIssueEndpointCommentsAsync(
        GitHubRestClient client,
        string outputPath,
        HashSet<string> existingEventKeys,
        CancellationToken cancellationToken)
    {
        var page = 1;
        var reachedEnd = false;
        var newRecords = 0;
        var since = Uri.EscapeDataString(
            HistoryStart.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

        while (!reachedEnd)
        {
            var comments = await client.GetJsonAsync(
                $"repos/{AnalysisConfig.Owner}/{AnalysisConfig.Repository}/issues/comments" +
                $"?sort=created&direction=asc&since={since}&per_page={PageSize}&page={page}",
                cancellationToken);
            if (comments.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var comment in comments.EnumerateArray())
            {
                var createdAt = GetTimestamp(comment, "created_at");
                if (createdAt > HistoryEnd)
                {
                    reachedEnd = true;
                    break;
                }
                if (createdAt < HistoryStart)
                {
                    continue;
                }

                var htmlUrl = GetString(comment, "html_url");
                var activityType = htmlUrl.Contains(
                    "/pull/",
                    StringComparison.OrdinalIgnoreCase)
                    ? "PRConversationComment"
                    : "IssueComment";
                var commentId = GetInt64(comment, "id");
                var eventKey = $"{activityType}:{commentId}";
                if (!existingEventKeys.Add(eventKey))
                {
                    continue;
                }

                var user = GetObject(comment, "user");
                var row = CsvUtilities.Row(
                    eventKey,
                    activityType,
                    createdAt,
                    GetString(user, "login"),
                    GetNullableInt64(user, "id"),
                    GetString(user, "type"),
                    GetString(comment, "author_association"),
                    ParseNumberFromUrl(GetString(comment, "issue_url")),
                    null,
                    null,
                    null,
                    htmlUrl,
                    HistoryWindowLabel);
                await AppendRowAsync(outputPath, row, cancellationToken);
                newRecords++;
            }

            Console.WriteLine($"  Conversation page {page}: {newRecords} new events.");
            if (comments.GetArrayLength() < PageSize)
            {
                break;
            }
            page++;
        }

        return newRecords;
    }

    private static async Task<int> CollectInlineReviewCommentsAsync(
        GitHubRestClient client,
        string outputPath,
        HashSet<string> existingEventKeys,
        CancellationToken cancellationToken)
    {
        var page = 1;
        var reachedEnd = false;
        var newRecords = 0;
        var since = Uri.EscapeDataString(
            HistoryStart.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

        while (!reachedEnd)
        {
            var comments = await client.GetJsonAsync(
                $"repos/{AnalysisConfig.Owner}/{AnalysisConfig.Repository}/pulls/comments" +
                $"?sort=created&direction=asc&since={since}&per_page={PageSize}&page={page}",
                cancellationToken);
            if (comments.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var comment in comments.EnumerateArray())
            {
                var createdAt = GetTimestamp(comment, "created_at");
                if (createdAt > HistoryEnd)
                {
                    reachedEnd = true;
                    break;
                }
                if (createdAt < HistoryStart)
                {
                    continue;
                }

                var commentId = GetInt64(comment, "id");
                var eventKey = $"InlineReviewComment:{commentId}";
                if (!existingEventKeys.Add(eventKey))
                {
                    continue;
                }

                var user = GetObject(comment, "user");
                var row = CsvUtilities.Row(
                    eventKey,
                    "InlineReviewComment",
                    createdAt,
                    GetString(user, "login"),
                    GetNullableInt64(user, "id"),
                    GetString(user, "type"),
                    GetString(comment, "author_association"),
                    ParseNumberFromUrl(GetString(comment, "pull_request_url")),
                    GetString(comment, "commit_id"),
                    null,
                    null,
                    GetString(comment, "html_url"),
                    HistoryWindowLabel);
                await AppendRowAsync(outputPath, row, cancellationToken);
                newRecords++;
            }

            Console.WriteLine($"  Inline-comment page {page}: {newRecords} new events.");
            if (comments.GetArrayLength() < PageSize)
            {
                break;
            }
            page++;
        }

        return newRecords;
    }

    private static bool IsInsideHistoryWindow(DateTimeOffset timestamp) =>
        timestamp >= HistoryStart && timestamp <= HistoryEnd;

    private static string PrepareOutput()
    {
        var path = Path.Combine(
            AnalysisConfig.OutputDirectory,
            "contributor_activity_pre_current_5y.csv");
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            File.WriteAllText(path, Header + Environment.NewLine, new UTF8Encoding(true));
            return path;
        }

        var existingHeader = File.ReadLines(path).FirstOrDefault()?.TrimStart('\uFEFF');
        if (!string.Equals(existingHeader, Header, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Existing file has an incompatible header: {path}. " +
                "Move or rename it before running this collector.");
        }
        return path;
    }

    private static HashSet<string> ReadExistingEventKeys(string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var parser = CreateCsvParser(path);
        if (!parser.EndOfData)
        {
            parser.ReadFields();
        }
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is { Length: > 0 } && !string.IsNullOrWhiteSpace(fields[0]))
            {
                keys.Add(fields[0]);
            }
        }
        return keys;
    }

    private static HistoryCheckpoint LoadCheckpoint(string path)
    {
        if (!File.Exists(path))
        {
            return new HistoryCheckpoint();
        }

        return JsonSerializer.Deserialize<HistoryCheckpoint>(File.ReadAllText(path)) ??
               new HistoryCheckpoint();
    }

    private static async Task SaveCheckpointAsync(
        string path,
        HistoryCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(
            checkpoint,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    private static Microsoft.VisualBasic.FileIO.TextFieldParser CreateCsvParser(string path)
    {
        var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(path, Encoding.UTF8)
        {
            TextFieldType = Microsoft.VisualBasic.FileIO.FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        return parser;
    }

    private static Task AppendRowAsync(
        string path,
        string row,
        CancellationToken cancellationToken) =>
        File.AppendAllTextAsync(
            path,
            row + Environment.NewLine,
            Encoding.UTF8,
            cancellationToken);

    private static void ValidateLocalRepository()
    {
        var gitDirectory = Path.Combine(AnalysisConfig.LocalRepositoryDirectory, ".git");
        if (!Directory.Exists(gitDirectory) && !File.Exists(gitDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Git repository not found at {AnalysisConfig.LocalRepositoryDirectory}.");
        }
    }

    private static async Task<string> RunGitAsync(
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = AnalysisConfig.LocalRepositoryDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(AnalysisConfig.LocalRepositoryDirectory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("Unable to start git.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {error}");
        }
        return output;
    }

    private static string? InferLoginFromNoReplyEmail(string email)
    {
        var match = NoReplyEmailRegex().Match(email);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int ParseNumberFromUrl(string url)
    {
        var segment = url.TrimEnd('/').Split('/').LastOrDefault();
        return int.TryParse(segment, out var number) ? number : 0;
    }

    private static JsonElement? GetObject(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string GetString(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string GetString(JsonElement? source, string name) =>
        source.HasValue ? GetString(source.Value, name) : string.Empty;

    private static int GetInt32(JsonElement source, string name) =>
        source.GetProperty(name).GetInt32();

    private static long GetInt64(JsonElement source, string name) =>
        source.GetProperty(name).GetInt64();

    private static long? GetNullableInt64(JsonElement? source, string name) =>
        source.HasValue && source.Value.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static DateTimeOffset GetTimestamp(JsonElement source, string name) =>
        DateTimeOffset.Parse(source.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    private static DateTimeOffset? GetNullableTimestamp(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : null;

    [GeneratedRegex(@"^(?:\d+\+)?([^@]+)@users\.noreply\.github\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex NoReplyEmailRegex();

    private sealed record ItemCollectionResult(
        HashSet<int> PullRequestNumbers,
        int NewRecords);

    private sealed class HistoryCheckpoint
    {
        public HashSet<int> CompletedReviewPrNumbers { get; set; } = new();
    }
}
