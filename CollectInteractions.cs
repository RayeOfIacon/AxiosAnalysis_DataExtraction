using System.Text;
using System.Text.Json;

/// <summary>Collects current-period PR reviews and PR or Issue comments.</summary>
public static class CollectInteractions
{
    private const int PageSize = 100;

    private static readonly string ReviewsHeader = string.Join(",", new[]
    {
        "PrNumber", "ReviewId", "ReviewerLogin", "ReviewerId", "ReviewerType",
        "AuthorAssociation", "SubmittedAt", "State", "CommitId", "Body", "Url",
        "PeriodLabel"
    });

    private static readonly string PrCommentsHeader = string.Join(",", new[]
    {
        "PrNumber", "CommentId", "CommentType", "ActorLogin", "ActorId", "ActorType",
        "AuthorAssociation", "CreatedAt", "UpdatedAt", "Body", "Url", "InReplyToId",
        "PullRequestReviewId", "Path", "Line", "Side", "CommitId", "PeriodLabel"
    });

    private static readonly string IssueCommentsHeader = string.Join(",", new[]
    {
        "IssueNumber", "CommentId", "ActorLogin", "ActorId", "ActorType",
        "AuthorAssociation", "CreatedAt", "UpdatedAt", "Body", "Url", "PeriodLabel"
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

        var currentPrPath = Path.Combine(
            AnalysisConfig.OutputDirectory,
            "pull_requests_current.csv");
        var currentPrNumbers = ReadCurrentPrNumbers(currentPrPath);
        if (currentPrNumbers.Count == 0)
        {
            throw new InvalidOperationException(
                $"No Current-period PRs were found in {currentPrPath}. " +
                "Run 'dotnet run -- pulls' first.");
        }

        var reviewsPath = PrepareOutput("pr_reviews_current.csv", ReviewsHeader);
        var prCommentsPath = PrepareOutput("pr_comments_current.csv", PrCommentsHeader);
        var issueCommentsPath = PrepareOutput("issue_comments_current.csv", IssueCommentsHeader);
        var checkpointPath = Path.Combine(
            AnalysisConfig.OutputDirectory,
            "interactions_checkpoint.json");
        var checkpoint = LoadCheckpoint(checkpointPath);

        using var client = new GitHubRestClient(token);
        Console.WriteLine(
            $"Collecting Current-period interactions for {AnalysisConfig.Owner}/{AnalysisConfig.Repository}...");
        Console.WriteLine($"Current PR cohort: {currentPrNumbers.Count} PRs.");

        var reviewCount = await CollectReviewsAsync(
            client,
            currentPrNumbers,
            reviewsPath,
            checkpointPath,
            checkpoint,
            cancellationToken);

        var existingPrCommentKeys = ReadExistingKeys(
            prCommentsPath,
            fields => fields.Length >= 3 ? $"{fields[2]}:{fields[1]}" : null);
        var conversationCount = await CollectConversationAndIssueCommentsAsync(
            client,
            currentPrNumbers,
            prCommentsPath,
            issueCommentsPath,
            existingPrCommentKeys,
            cancellationToken);

        var inlineCount = await CollectInlineReviewCommentsAsync(
            client,
            currentPrNumbers,
            prCommentsPath,
            existingPrCommentKeys,
            cancellationToken);

        Console.WriteLine("Interaction collection completed.");
        Console.WriteLine($"New PR reviews: {reviewCount} -> {reviewsPath}");
        Console.WriteLine($"New PR conversation comments: {conversationCount.PrComments} -> {prCommentsPath}");
        Console.WriteLine($"New PR inline review comments: {inlineCount} -> {prCommentsPath}");
        Console.WriteLine($"New Issue comments: {conversationCount.IssueComments} -> {issueCommentsPath}");
    }

    // The checkpoint avoids repeating completed PR requests; review IDs prevent duplicate rows.
    private static async Task<int> CollectReviewsAsync(
        GitHubRestClient client,
        IReadOnlyCollection<int> currentPrNumbers,
        string outputPath,
        string checkpointPath,
        InteractionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var existingReviewIds = ReadExistingKeys(
            outputPath,
            fields => fields.Length >= 2 ? fields[1] : null);
        var newRecords = 0;
        var processedThisRun = 0;

        Console.WriteLine("Stage 1/3: collecting PR reviews...");
        foreach (var prNumber in currentPrNumbers.OrderBy(number => number))
        {
            if (checkpoint.CompletedReviewPrNumbers.Contains(prNumber))
            {
                continue;
            }

            var page = 1;
            while (true)
            {
                var reviews = await client.GetJsonAsync(
                    $"repos/{AnalysisConfig.Owner}/{AnalysisConfig.Repository}/pulls/{prNumber}" +
                    $"/reviews?per_page={PageSize}&page={page}",
                    cancellationToken);

                foreach (var review in reviews.EnumerateArray())
                {
                    var submittedAt = GetNullableTimestamp(review, "submitted_at");
                    if (!IsInCurrentPeriod(submittedAt))
                    {
                        continue;
                    }

                    var reviewId = GetInt64(review, "id");
                    if (!existingReviewIds.Add(reviewId.ToString()))
                    {
                        continue;
                    }

                    var user = GetObject(review, "user");
                    var row = CsvUtilities.Row(
                        prNumber,
                        reviewId,
                        GetString(user, "login"),
                        GetNullableInt64(user, "id"),
                        GetString(user, "type"),
                        GetString(review, "author_association"),
                        submittedAt,
                        GetString(review, "state"),
                        GetString(review, "commit_id"),
                        GetString(review, "body"),
                        GetString(review, "html_url"),
                        AnalysisConfig.Current.Label);
                    await AppendRowAsync(outputPath, row, cancellationToken);
                    newRecords++;
                }

                if (reviews.GetArrayLength() < PageSize)
                {
                    break;
                }
                page++;
            }

            checkpoint.CompletedReviewPrNumbers.Add(prNumber);
            await SaveCheckpointAsync(checkpointPath, checkpoint, cancellationToken);
            processedThisRun++;
            if (processedThisRun % 50 == 0)
            {
                Console.WriteLine(
                    $"  Reviews: checked {processedThisRun} PRs this run; saved {newRecords} new reviews.");
            }
        }

        return newRecords;
    }

    // The Issues comments endpoint includes comments on both Issues and PRs.
    private static async Task<(int PrComments, int IssueComments)>
        CollectConversationAndIssueCommentsAsync(
            GitHubRestClient client,
            IReadOnlySet<int> currentPrNumbers,
            string prOutputPath,
            string issueOutputPath,
            HashSet<string> existingPrCommentKeys,
            CancellationToken cancellationToken)
    {
        var existingIssueCommentIds = ReadExistingKeys(
            issueOutputPath,
            fields => fields.Length >= 2 ? fields[1] : null);
        var prCount = 0;
        var issueCount = 0;
        var page = 1;
        var reachedEnd = false;
        var since = Uri.EscapeDataString(
            AnalysisConfig.Current.Start.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

        Console.WriteLine("Stage 2/3: collecting PR conversation comments and Issue comments...");
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
                var createdAt = GetNullableTimestamp(comment, "created_at");
                if (createdAt > AnalysisConfig.Current.End)
                {
                    reachedEnd = true;
                    break;
                }
                if (!IsInCurrentPeriod(createdAt))
                {
                    continue;
                }

                var itemNumber = ParseNumberFromUrl(GetString(comment, "issue_url"));
                var commentId = GetInt64(comment, "id");
                var htmlUrl = GetString(comment, "html_url");
                var user = GetObject(comment, "user");
                var isPullRequest = htmlUrl.Contains("/pull/", StringComparison.OrdinalIgnoreCase);

                if (isPullRequest)
                {
                    if (!currentPrNumbers.Contains(itemNumber) ||
                        !existingPrCommentKeys.Add($"Conversation:{commentId}"))
                    {
                        continue;
                    }

                    var row = CsvUtilities.Row(
                        itemNumber,
                        commentId,
                        "Conversation",
                        GetString(user, "login"),
                        GetNullableInt64(user, "id"),
                        GetString(user, "type"),
                        GetString(comment, "author_association"),
                        createdAt,
                        GetNullableTimestamp(comment, "updated_at"),
                        GetString(comment, "body"),
                        htmlUrl,
                        null, null, null, null, null, null,
                        AnalysisConfig.Current.Label);
                    await AppendRowAsync(prOutputPath, row, cancellationToken);
                    prCount++;
                }
                else
                {
                    if (!existingIssueCommentIds.Add(commentId.ToString()))
                    {
                        continue;
                    }

                    var row = CsvUtilities.Row(
                        itemNumber,
                        commentId,
                        GetString(user, "login"),
                        GetNullableInt64(user, "id"),
                        GetString(user, "type"),
                        GetString(comment, "author_association"),
                        createdAt,
                        GetNullableTimestamp(comment, "updated_at"),
                        GetString(comment, "body"),
                        htmlUrl,
                        AnalysisConfig.Current.Label);
                    await AppendRowAsync(issueOutputPath, row, cancellationToken);
                    issueCount++;
                }
            }

            Console.WriteLine(
                $"  Issue-comment page {page}: {prCount} new PR comments, {issueCount} new Issue comments.");
            if (comments.GetArrayLength() < PageSize)
            {
                break;
            }
            page++;
        }

        return (prCount, issueCount);
    }

    private static async Task<int> CollectInlineReviewCommentsAsync(
        GitHubRestClient client,
        IReadOnlySet<int> currentPrNumbers,
        string outputPath,
        HashSet<string> existingCommentKeys,
        CancellationToken cancellationToken)
    {
        var newRecords = 0;
        var page = 1;
        var reachedEnd = false;
        var since = Uri.EscapeDataString(
            AnalysisConfig.Current.Start.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));

        Console.WriteLine("Stage 3/3: collecting inline PR review comments...");
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
                var createdAt = GetNullableTimestamp(comment, "created_at");
                if (createdAt > AnalysisConfig.Current.End)
                {
                    reachedEnd = true;
                    break;
                }
                if (!IsInCurrentPeriod(createdAt))
                {
                    continue;
                }

                var prNumber = ParseNumberFromUrl(GetString(comment, "pull_request_url"));
                var commentId = GetInt64(comment, "id");
                if (!currentPrNumbers.Contains(prNumber) ||
                    !existingCommentKeys.Add($"InlineReview:{commentId}"))
                {
                    continue;
                }

                var user = GetObject(comment, "user");
                var row = CsvUtilities.Row(
                    prNumber,
                    commentId,
                    "InlineReview",
                    GetString(user, "login"),
                    GetNullableInt64(user, "id"),
                    GetString(user, "type"),
                    GetString(comment, "author_association"),
                    createdAt,
                    GetNullableTimestamp(comment, "updated_at"),
                    GetString(comment, "body"),
                    GetString(comment, "html_url"),
                    GetNullableInt64(comment, "in_reply_to_id"),
                    GetNullableInt64(comment, "pull_request_review_id"),
                    GetString(comment, "path"),
                    GetNullableInt32(comment, "line"),
                    GetString(comment, "side"),
                    GetString(comment, "commit_id"),
                    AnalysisConfig.Current.Label);
                await AppendRowAsync(outputPath, row, cancellationToken);
                newRecords++;
            }

            Console.WriteLine($"  Review-comment page {page}: {newRecords} new inline comments.");
            if (comments.GetArrayLength() < PageSize)
            {
                break;
            }
            page++;
        }

        return newRecords;
    }

    private static HashSet<int> ReadCurrentPrNumbers(string path)
    {
        if (!File.Exists(path))
        {
            return new HashSet<int>();
        }

        var numbers = new HashSet<int>();
        using var parser = CreateCsvParser(path);
        if (!parser.EndOfData)
        {
            parser.ReadFields();
        }

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is { Length: > 0 } && int.TryParse(fields[0], out var number))
            {
                numbers.Add(number);
            }
        }
        return numbers;
    }

    private static string PrepareOutput(string fileName, string header)
    {
        var path = Path.Combine(AnalysisConfig.OutputDirectory, fileName);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            File.WriteAllText(path, header + Environment.NewLine, new UTF8Encoding(true));
            return path;
        }

        var existingHeader = File.ReadLines(path).FirstOrDefault()?.TrimStart('\uFEFF');
        if (!string.Equals(existingHeader, header, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Existing file has an incompatible header: {path}. " +
                "Move or rename it before running this collector.");
        }
        return path;
    }

    private static HashSet<string> ReadExistingKeys(
        string path,
        Func<string[], string?> keySelector)
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
            if (fields is null)
            {
                continue;
            }
            var key = keySelector(fields);
            if (!string.IsNullOrWhiteSpace(key))
            {
                keys.Add(key);
            }
        }
        return keys;
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

    private static InteractionCheckpoint LoadCheckpoint(string path)
    {
        if (!File.Exists(path))
        {
            return new InteractionCheckpoint();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<InteractionCheckpoint>(json) ??
               new InteractionCheckpoint();
    }

    private static async Task SaveCheckpointAsync(
        string path,
        InteractionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(
            checkpoint,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken);
        File.Move(temporaryPath, path, true);
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

    private static bool IsInCurrentPeriod(DateTimeOffset? timestamp) =>
        timestamp.HasValue &&
        timestamp.Value >= AnalysisConfig.Current.Start &&
        timestamp.Value <= AnalysisConfig.Current.End;

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

    private static long GetInt64(JsonElement source, string name) =>
        source.GetProperty(name).GetInt64();

    private static long? GetNullableInt64(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static long? GetNullableInt64(JsonElement? source, string name) =>
        source.HasValue ? GetNullableInt64(source.Value, name) : null;

    private static int? GetNullableInt32(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static DateTimeOffset? GetNullableTimestamp(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(value.GetString()!)
            : null;

    private sealed class InteractionCheckpoint
    {
        public HashSet<int> CompletedReviewPrNumbers { get; set; } = new();
    }
}
