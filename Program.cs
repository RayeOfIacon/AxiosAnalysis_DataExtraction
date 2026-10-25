/// <summary>Dispatches command-line requests to the data collectors.</summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "help";

        switch (command)
        {
            case "pulls":
            case "pull-requests":
                await CollectPullRequests.RunAsync();
                break;
            case "activities":
                await CollectActivities.RunAsync();
                break;
            case "interactions":
                await CollectInteractions.RunAsync();
                break;
            case "issues":
                await CollectIssues.RunAsync();
                break;
            case "actors":
            case "identities":
                await CollectActorsAndIdentities.RunAsync();
                break;
            case "history5y":
            case "contributors-history":
                await CollectFiveYearContributorHistory.RunAsync();
                break;
            default:
                PrintHelp();
                break;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Axios project-health data collectors");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- pulls       Collect PR lifecycle and size data");
        Console.WriteLine("  dotnet run -- interactions  Collect reviews and comments for the current period");
        Console.WriteLine("  dotnet run -- issues      Collect Issue lifecycle data for the current period");
        Console.WriteLine("  dotnet run -- actors      Collect actor profiles, history, and commit identities");
        Console.WriteLine("  dotnet run -- history5y   Collect five pre-current years of contributor activity");
        Console.WriteLine("  dotnet run -- activities  Collect current-period commit, PR, and Issue events");
        Console.WriteLine();
        Console.WriteLine("Set GITHUB_TOKEN in the current PowerShell session before running a collector.");
    }
}
