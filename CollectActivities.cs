using Octokit;

/// <summary>Exports a compact current-period activity table using Octokit.</summary>
public static class CollectActivities
{
    public static async Task RunAsync()
    {
        var client = new GitHubClient(new ProductHeaderValue("AxiosCourseAnalysis"));
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "GITHUB_TOKEN is not set. Set it in PowerShell before running the program.");
        }

        client.Credentials = new Credentials(token);
        var period = AnalysisConfig.Current;
        var commitRequest = new CommitRequest { Since = period.Start, Until = period.End };
        var commits = (await client.Repository.Commit.GetAll(
                AnalysisConfig.Owner, AnalysisConfig.Repository, commitRequest))
            .Where(c => c.Commit.Author.Date >= period.Start && c.Commit.Author.Date <= period.End)
            .ToList();

        var pullRequests = await GetItems(client, IssueTypeQualifier.PullRequest, period);
        var issues = await GetItems(client, IssueTypeQualifier.Issue, period);
        var rows = new List<ActivityRow>();

        foreach (var commit in commits)
        {
            rows.Add(new ActivityRow(
                "Commit", commit.Commit.Author.Date,
                commit.Author?.Login ?? commit.Commit.Author.Name,
                commit.Author?.HtmlUrl ?? "", "", "", commit.HtmlUrl,
                commit.Commit.Message.Split('\n')[0]));
        }

        rows.AddRange(pullRequests.Select(item => new ActivityRow(
            "Pull request", item.CreatedAt, item.User.Login, item.User.HtmlUrl,
            item.State.ToString(), item.Number.ToString(), item.HtmlUrl, item.Title)));
        rows.AddRange(issues.Select(item => new ActivityRow(
            "Issue", item.CreatedAt, item.User.Login, item.User.HtmlUrl,
            item.State.ToString(), item.Number.ToString(), item.HtmlUrl, item.Title)));

        var fileName = Path.Combine(AnalysisConfig.OutputDirectory, "github_activity_events.csv");
        var csv = new List<string>
        {
            "Activity,TimestampUtc,Month,Login,ProfileUrl,State,Number,Url,Title"
        };
        csv.AddRange(rows.OrderBy(row => row.TimestampUtc).Select(row => CsvUtilities.Row(
            row.Activity,
            row.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            row.TimestampUtc.ToUniversalTime().ToString("yyyy-MM"),
            row.Login, row.ProfileUrl, row.State, row.Number, row.Url, row.Title)));
        await File.WriteAllLinesAsync(fileName, csv);

        Console.WriteLine($"Commits: {commits.Count}");
        Console.WriteLine($"Pull requests: {pullRequests.Count}");
        Console.WriteLine($"Issues: {issues.Count}");
        Console.WriteLine($"CSV saved to {fileName}");
    }

    private static async Task<IReadOnlyList<Issue>> GetItems(
        GitHubClient client,
        IssueTypeQualifier itemType,
        ObservationPeriod period)
    {
        var items = new List<Issue>();
        var page = 1;
        while (true)
        {
            var request = new SearchIssuesRequest
            {
                Repos = new RepositoryCollection
                    { $"{AnalysisConfig.Owner}/{AnalysisConfig.Repository}" },
                Type = itemType,
                Created = DateRange.Between(period.Start, period.End),
                SortField = IssueSearchSort.Created,
                Order = SortDirection.Ascending,
                PerPage = 100,
                Page = page
            };
            var result = await client.Search.SearchIssues(request);
            items.AddRange(result.Items);
            if (result.Items.Count < 100)
            {
                break;
            }
            page++;
        }

        return items.Where(item => item.CreatedAt >= period.Start && item.CreatedAt <= period.End)
            .ToList();
    }

    private sealed record ActivityRow(
        string Activity,
        DateTimeOffset TimestampUtc,
        string Login,
        string ProfileUrl,
        string State,
        string Number,
        string Url,
        string Title);
}
