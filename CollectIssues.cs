using System.Text;
using System.Text.Json;

/// <summary>Collects current-period Issue lifecycle, assignment, and label data.</summary>
public static class CollectIssues
{
    private const int PageSize = 100;

    private static readonly string Header = string.Join(",", new[]
    {
        "IssueNumber", "IssueId", "AuthorLogin", "AuthorId", "AuthorType",
        "AuthorAssociation", "CreatedAt", "UpdatedAt", "ClosedAt", "ClosedBy",
        "State", "StateReason", "Labels", "Assignees", "MilestoneTitle",
        "MilestoneNumber", "CommentsCount", "Locked", "ActiveLockReason", "Url",
        "Title", "Body", "PeriodLabel"
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

        var outputPath = PrepareOutput();
        var completedIssueNumbers = ReadExistingIssueNumbers(outputPath);
        var newRecords = 0;
        var skippedPullRequests = 0;
        var page = 1;
        var reachedStart = false;

        using var client = new GitHubRestClient(token);
        Console.WriteLine(
            $"Collecting Current-period Issues for {AnalysisConfig.Owner}/{AnalysisConfig.Repository}...");
        Console.WriteLine("Pull requests returned by GitHub's Issues endpoint will be excluded.");
        Console.WriteLine(
            $"Existing records that will be skipped: {completedIssueNumbers.Count}.");

        while (!reachedStart)
        {
            var url = $"repos/{AnalysisConfig.Owner}/{AnalysisConfig.Repository}/issues" +
                      $"?filter=all&state=all&sort=created&direction=desc" +
                      $"&per_page={PageSize}&page={page}";
            var items = await client.GetJsonAsync(url, cancellationToken);

            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                break;
            }

            Console.WriteLine($"Scanning Issue endpoint page {page} ({items.GetArrayLength()} entries)...");

            foreach (var item in items.EnumerateArray())
            {
                var createdAt = GetTimestamp(item, "created_at");
                if (createdAt < AnalysisConfig.Current.Start)
                {
                    reachedStart = true;
                    break;
                }

                if (createdAt > AnalysisConfig.Current.End)
                {
                    continue;
                }

                // GitHub's list-Issues endpoint returns both Issues and Pull Requests.
                if (item.TryGetProperty("pull_request", out _))
                {
                    skippedPullRequests++;
                    continue;
                }

                var issueNumber = GetInt32(item, "number");
                if (!completedIssueNumbers.Add(issueNumber))
                {
                    continue;
                }

                var row = BuildRow(item);
                await File.AppendAllTextAsync(
                    outputPath,
                    row + Environment.NewLine,
                    Encoding.UTF8,
                    cancellationToken);
                newRecords++;

                if (newRecords % 25 == 0)
                {
                    Console.WriteLine($"  Saved {newRecords} new Issue records this run.");
                }
            }

            if (items.GetArrayLength() < PageSize)
            {
                break;
            }
            page++;
        }

        Console.WriteLine("Issue collection completed.");
        Console.WriteLine($"New Issues: {newRecords}");
        Console.WriteLine($"Total Issues in output: {completedIssueNumbers.Count}");
        Console.WriteLine($"PR entries excluded during this run: {skippedPullRequests}");
        Console.WriteLine($"CSV saved to {outputPath}");
    }

    private static string BuildRow(JsonElement issue)
    {
        var user = GetObject(issue, "user");
        var closedBy = GetObject(issue, "closed_by");
        var milestone = GetObject(issue, "milestone");

        return CsvUtilities.Row(
            GetInt32(issue, "number"),
            GetInt64(issue, "id"),
            GetString(user, "login"),
            GetNullableInt64(user, "id"),
            GetString(user, "type"),
            GetString(issue, "author_association"),
            GetNullableTimestamp(issue, "created_at"),
            GetNullableTimestamp(issue, "updated_at"),
            GetNullableTimestamp(issue, "closed_at"),
            GetString(closedBy, "login"),
            GetString(issue, "state"),
            GetString(issue, "state_reason"),
            JoinLabels(issue),
            JoinObjectProperty(issue, "assignees", "login"),
            GetString(milestone, "title"),
            GetNullableInt32(milestone, "number"),
            GetNullableInt32(issue, "comments"),
            GetNullableBoolean(issue, "locked"),
            GetString(issue, "active_lock_reason"),
            GetString(issue, "html_url"),
            GetString(issue, "title"),
            GetString(issue, "body"),
            AnalysisConfig.Current.Label);
    }

    private static string PrepareOutput()
    {
        var path = Path.Combine(AnalysisConfig.OutputDirectory, "issues_current.csv");
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

    private static HashSet<int> ReadExistingIssueNumbers(string path)
    {
        var numbers = new HashSet<int>();
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(path, Encoding.UTF8)
        {
            TextFieldType = Microsoft.VisualBasic.FileIO.FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");

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

    private static string JoinLabels(JsonElement source)
    {
        if (!source.TryGetProperty("labels", out var labels) ||
            labels.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(";", labels.EnumerateArray()
            .Select(label => label.ValueKind == JsonValueKind.String
                ? label.GetString() ?? string.Empty
                : GetString(label, "name"))
            .Where(label => !string.IsNullOrWhiteSpace(label)));
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

    private static int? GetNullableInt32(JsonElement? source, string name) =>
        source.HasValue ? GetNullableInt32(source.Value, name) : null;

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

    private static DateTimeOffset GetTimestamp(JsonElement source, string name) =>
        DateTimeOffset.Parse(source.GetProperty(name).GetString()!);

    private static DateTimeOffset? GetNullableTimestamp(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(value.GetString()!)
            : null;
}
