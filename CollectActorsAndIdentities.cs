using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Collects current Git identities and GitHub actor profiles.</summary>
public static partial class CollectActorsAndIdentities
{
    private static readonly string CommitHeader = string.Join(",", new[]
    {
        "Sha", "AuthorNameRaw", "AuthorEmailRaw", "AuthorNameCanonical",
        "AuthorEmailCanonical", "AuthorDate", "CommitterNameRaw", "CommitterEmailRaw",
        "CommitterNameCanonical", "CommitterEmailCanonical", "CommitterDate",
        "LocalSignatureStatus", "SignatureStatusMeaning", "Signer", "SigningKey",
        "Subject", "PeriodLabel"
    });

    private static readonly string ActorsHeader = string.Join(",", new[]
    {
        "Login", "GitHubUserId", "ProfileUrl", "AccountType", "SiteAdmin", "Name",
        "Company", "Blog", "Location", "PublicEmail", "Bio", "TwitterUsername",
        "CreatedAt", "UpdatedAt", "PublicRepos", "PublicGists", "Followers", "Following",
        "IsBotByType", "IsBotByLogin", "ProfileFetchStatus", "CollectedAtUtc"
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

        ValidateLocalRepository();
        var defaultRef = await GetDefaultRemoteRefAsync(cancellationToken);
        var actorCandidates = new Dictionary<string, ActorCandidate>(
            StringComparer.OrdinalIgnoreCase);

        Console.WriteLine(
            $"Collecting actors and identities for {AnalysisConfig.Owner}/{AnalysisConfig.Repository}...");
        Console.WriteLine($"Local Git repository: {AnalysisConfig.LocalRepositoryDirectory}");
        Console.WriteLine($"Git history reference: {defaultRef}");

        Console.WriteLine("Stage 1/2: extracting current commit identities from local Git...");
        await CollectCommitIdentitiesAsync(
            AnalysisConfig.Current,
            defaultRef,
            actorCandidates,
            cancellationToken);

        AddActorsFromExistingCollectorOutputs(actorCandidates);

        using var client = new GitHubRestClient(token);
        Console.WriteLine("Stage 2/2: collecting GitHub account profiles...");
        var actorsPath = PrepareOutput("actors.csv", ActorsHeader);
        var completedLogins = ReadExistingValues(actorsPath, "Login");
        var newProfiles = await CollectActorProfilesAsync(
            client,
            actorCandidates,
            actorsPath,
            completedLogins,
            cancellationToken);

        Console.WriteLine("Actor and identity collection completed.");
        Console.WriteLine($"Actor candidates observed: {actorCandidates.Count}");
        Console.WriteLine($"New actor profile rows: {newProfiles} -> {actorsPath}");
        Console.WriteLine(
            $"Commit identities -> {AnalysisConfig.OutputDirectory}\\commit_identities_current.csv");
    }

    // Keep raw and mailmap-canonical Git identities for later identity verification.
    private static async Task CollectCommitIdentitiesAsync(
        ObservationPeriod period,
        string defaultRef,
        IDictionary<string, ActorCandidate> actorCandidates,
        CancellationToken cancellationToken)
    {
        var outputPath = PrepareOutput(
            $"commit_identities_{period.FileSuffix}.csv",
            CommitHeader);
        var existingShas = ReadExistingValues(outputPath, "Sha");

        const string format =
            "%H%x1f%an%x1f%ae%x1f%aN%x1f%aE%x1f%aI%x1f" +
            "%cn%x1f%ce%x1f%cN%x1f%cE%x1f%cI%x1f" +
            "%G?%x1f%GS%x1f%GK%x1f%s%x1e";

        var output = await RunGitAsync(
            cancellationToken,
            "log",
            defaultRef,
            $"--since={period.Start.ToUniversalTime():O}",
            $"--until={period.End.ToUniversalTime():O}",
            $"--format={format}");

        var newCommits = 0;
        foreach (var rawRecord in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = rawRecord.Trim('\r', '\n');
            if (record.Length == 0)
            {
                continue;
            }

            var fields = record.Split('\x1f');
            if (fields.Length < 15)
            {
                throw new InvalidDataException(
                    $"Unexpected git log record with {fields.Length} fields for {period.Label}.");
            }

            var sha = fields[0];
            var authorDate = DateTimeOffset.Parse(fields[5], CultureInfo.InvariantCulture);
            var committerDate = DateTimeOffset.Parse(fields[10], CultureInfo.InvariantCulture);

            if (existingShas.Add(sha))
            {
                var row = CsvUtilities.Row(
                    sha,
                    fields[1], fields[2], fields[3], fields[4], authorDate,
                    fields[6], fields[7], fields[8], fields[9], committerDate,
                    fields[11], SignatureMeaning(fields[11]), fields[12], fields[13],
                    fields[14], period.Label);
                await AppendRowAsync(outputPath, row, cancellationToken);
                newCommits++;
            }

            var authorLogin = InferLoginFromNoReplyEmail(fields[4]) ??
                              InferLoginFromNoReplyEmail(fields[2]);
            if (!string.IsNullOrWhiteSpace(authorLogin))
            {
                AddActor(actorCandidates, authorLogin);
            }
        }

        Console.WriteLine(
            $"  {period.Label}: {newCommits} new commit identity rows, " +
            $"{existingShas.Count} total.");
    }

    // Profile type and login pattern are separate, preliminary bot signals.
    private static async Task<int> CollectActorProfilesAsync(
        GitHubRestClient client,
        IReadOnlyDictionary<string, ActorCandidate> actorCandidates,
        string outputPath,
        HashSet<string> completedLogins,
        CancellationToken cancellationToken)
    {
        var newProfiles = 0;

        foreach (var candidate in actorCandidates.Values.OrderBy(
                     actor => actor.Login,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!completedLogins.Add(candidate.Login))
            {
                continue;
            }

            var profile = await client.GetJsonOrNullAsync(
                $"users/{Uri.EscapeDataString(candidate.Login)}",
                cancellationToken);
            string row;

            if (profile is null)
            {
                row = CsvUtilities.Row(
                    candidate.Login,
                    null,
                    null,
                    null,
                    null, null, null, null, null, null, null, null,
                    null, null, null, null, null, null,
                    false,
                    IsBotLikeLogin(candidate.Login),
                    "NotFound",
                    DateTimeOffset.UtcNow);
            }
            else
            {
                var value = profile.Value;
                var accountType = GetString(value, "type");
                row = CsvUtilities.Row(
                    GetString(value, "login"),
                    GetNullableInt64(value, "id"),
                    GetString(value, "html_url"),
                    accountType,
                    GetNullableBoolean(value, "site_admin"),
                    GetString(value, "name"),
                    GetString(value, "company"),
                    GetString(value, "blog"),
                    GetString(value, "location"),
                    GetString(value, "email"),
                    GetString(value, "bio"),
                    GetString(value, "twitter_username"),
                    GetNullableTimestamp(value, "created_at"),
                    GetNullableTimestamp(value, "updated_at"),
                    GetNullableInt32(value, "public_repos"),
                    GetNullableInt32(value, "public_gists"),
                    GetNullableInt32(value, "followers"),
                    GetNullableInt32(value, "following"),
                    string.Equals(accountType, "Bot", StringComparison.OrdinalIgnoreCase),
                    IsBotLikeLogin(GetString(value, "login")),
                    "Found",
                    DateTimeOffset.UtcNow);
            }

            await AppendRowAsync(outputPath, row, cancellationToken);
            newProfiles++;
            if (newProfiles % 50 == 0)
            {
                Console.WriteLine($"  Saved {newProfiles} new actor profiles this run.");
            }
        }

        return newProfiles;
    }

    private static void AddActorsFromExistingCollectorOutputs(
        IDictionary<string, ActorCandidate> actors)
    {
        var sources = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["pull_requests_current.csv"] =
                new[] { "AuthorLogin", "MergedBy", "Assignees", "RequestedReviewers" },
            ["issues_current.csv"] =
                new[] { "AuthorLogin", "ClosedBy", "Assignees" },
            ["pr_reviews_current.csv"] = new[] { "ReviewerLogin" },
            ["pr_comments_current.csv"] = new[] { "ActorLogin" },
            ["issue_comments_current.csv"] = new[] { "ActorLogin" }
        };

        foreach (var source in sources)
        {
            var path = Path.Combine(AnalysisConfig.OutputDirectory, source.Key);
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var login in ReadDelimitedColumnValues(path, source.Value))
            {
                AddActor(actors, login);
            }
        }
    }

    private static IEnumerable<string> ReadDelimitedColumnValues(
        string path,
        IReadOnlyCollection<string> requestedColumns)
    {
        using var parser = CreateCsvParser(path);
        if (parser.EndOfData)
        {
            yield break;
        }

        var header = parser.ReadFields() ?? Array.Empty<string>();
        var indices = header
            .Select((name, index) => new { Name = name.TrimStart('\uFEFF'), Index = index })
            .Where(item => requestedColumns.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            .Select(item => item.Index)
            .ToArray();

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is null)
            {
                continue;
            }

            foreach (var index in indices.Where(index => index < fields.Length))
            {
                foreach (var value in fields[index].Split(
                             ';',
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        yield return value;
                    }
                }
            }
        }
    }

    private static void AddActor(
        IDictionary<string, ActorCandidate> actors,
        string? login)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return;
        }

        actors.TryAdd(login, new ActorCandidate(login));
    }

    private static string? InferLoginFromNoReplyEmail(string email)
    {
        var match = NoReplyEmailRegex().Match(email);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsBotLikeLogin(string login) =>
        login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ||
        login.EndsWith("-bot", StringComparison.OrdinalIgnoreCase) ||
        login.EndsWith("_bot", StringComparison.OrdinalIgnoreCase);

    private static string SignatureMeaning(string status) => status switch
    {
        "G" => "Good signature",
        "B" => "Bad signature",
        "U" => "Good signature with unknown validity",
        "X" => "Good signature that has expired",
        "Y" => "Good signature made by an expired key",
        "R" => "Good signature made by a revoked key",
        "E" => "Signature cannot be checked",
        "N" => "No signature",
        _ => "Unknown"
    };

    private static void ValidateLocalRepository()
    {
        var gitDirectory = Path.Combine(AnalysisConfig.LocalRepositoryDirectory, ".git");
        if (!Directory.Exists(gitDirectory) && !File.Exists(gitDirectory))
        {
            throw new DirectoryNotFoundException(
                $"A Git repository was not found at {AnalysisConfig.LocalRepositoryDirectory}. " +
                "Set AXIOS_REPO_PATH if the clone is stored elsewhere.");
        }
    }

    private static async Task<string> GetDefaultRemoteRefAsync(
        CancellationToken cancellationToken)
    {
        var output = await RunGitAsync(
            cancellationToken,
            "symbolic-ref",
            "--short",
            "refs/remotes/origin/HEAD");
        var reference = output.Trim();
        return string.IsNullOrWhiteSpace(reference) ? "HEAD" : reference;
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
                $"git {string.Join(' ', arguments)} failed with exit code " +
                $"{process.ExitCode}: {error}");
        }
        return output;
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

    private static HashSet<string> ReadExistingValues(string path, string columnName)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var parser = CreateCsvParser(path);
        if (parser.EndOfData)
        {
            return values;
        }

        var header = parser.ReadFields() ?? Array.Empty<string>();
        var columnIndex = Array.FindIndex(
            header,
            name => string.Equals(
                name.TrimStart('\uFEFF'),
                columnName,
                StringComparison.OrdinalIgnoreCase));
        if (columnIndex < 0)
        {
            throw new InvalidDataException($"Column '{columnName}' was not found in {path}.");
        }

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is { } && columnIndex < fields.Length &&
                !string.IsNullOrWhiteSpace(fields[columnIndex]))
            {
                values.Add(fields[columnIndex]);
            }
        }
        return values;
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

    private static string GetString(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? GetNullableInt32(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

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

    private static DateTimeOffset? GetNullableTimestamp(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : null;

    [GeneratedRegex(@"^(?:\d+\+)?([^@]+)@users\.noreply\.github\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex NoReplyEmailRegex();

    private sealed class ActorCandidate
    {
        public ActorCandidate(string login)
        {
            Login = login;
        }

        public string Login { get; }
    }
}
