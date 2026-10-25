using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

/// <summary>Calls the authenticated GitHub REST API and handles rate-limit waits.</summary>
public sealed class GitHubRestClient : IDisposable
{
    private const int LowRemainingThreshold = 50;
    private readonly HttpClient httpClient;

    public GitHubRestClient(string token)
    {
        httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromMinutes(2)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AxiosCourseAnalysis/1.0");
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<JsonElement> GetJsonAsync(
        string relativeUrl,
        CancellationToken cancellationToken = default)
    {
        return (await GetJsonCoreAsync(relativeUrl, false, cancellationToken))!.Value;
    }

    public Task<JsonElement?> GetJsonOrNullAsync(
        string relativeUrl,
        CancellationToken cancellationToken = default) =>
        GetJsonCoreAsync(relativeUrl, true, cancellationToken);

    private async Task<JsonElement?> GetJsonCoreAsync(
        string relativeUrl,
        bool returnNullWhenNotFound,
        CancellationToken cancellationToken)
    {
        var secondaryLimitAttempts = 0;

        while (true)
        {
            using var response = await httpClient.GetAsync(relativeUrl, cancellationToken);

            if (returnNullWhenNotFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                var result = document.RootElement.Clone();
                await PauseIfPrimaryLimitIsLowAsync(response, cancellationToken);
                return result;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var delay = GetRateLimitDelay(response, secondaryLimitAttempts);
                secondaryLimitAttempts++;

                if (secondaryLimitAttempts > 6)
                {
                    var error = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException(
                        $"GitHub rate limit was still active after several retries. " +
                        $"Status {(int)response.StatusCode}: {error}");
                }

                Console.WriteLine(
                    $"GitHub rate limit reached. Waiting {Math.Ceiling(delay.TotalMinutes)} minute(s) before retrying...");
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"GitHub request failed ({(int)response.StatusCode} {response.ReasonPhrase}) " +
                $"for {relativeUrl}: {body}");
        }
    }

    // Pause before exhausting the primary quota so collectors can resume safely.
    private static async Task PauseIfPrimaryLimitIsLowAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!TryGetIntHeader(response, "X-RateLimit-Remaining", out var remaining) ||
            remaining > LowRemainingThreshold ||
            !TryGetLongHeader(response, "X-RateLimit-Reset", out var resetEpoch))
        {
            return;
        }

        var reset = DateTimeOffset.FromUnixTimeSeconds(resetEpoch);
        var delay = reset - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        Console.WriteLine(
            $"Only {remaining} API requests remain. Progress is saved; waiting until {reset:O} UTC...");
        await Task.Delay(delay, cancellationToken);
    }

    private static TimeSpan GetRateLimitDelay(
        HttpResponseMessage response,
        int secondaryLimitAttempts)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
        {
            return retryAfter + TimeSpan.FromSeconds(5);
        }

        if (TryGetIntHeader(response, "X-RateLimit-Remaining", out var remaining) &&
            remaining == 0 &&
            TryGetLongHeader(response, "X-RateLimit-Reset", out var resetEpoch))
        {
            var delay = DateTimeOffset.FromUnixTimeSeconds(resetEpoch) -
                        DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(10);
        }

        var minutes = Math.Min(16, Math.Pow(2, secondaryLimitAttempts));
        return TimeSpan.FromMinutes(minutes);
    }

    private static bool TryGetIntHeader(
        HttpResponseMessage response,
        string name,
        out int value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values) &&
               int.TryParse(values.FirstOrDefault(), out value);
    }

    private static bool TryGetLongHeader(
        HttpResponseMessage response,
        string name,
        out long value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values) &&
               long.TryParse(values.FirstOrDefault(), out value);
    }

    public void Dispose() => httpClient.Dispose();
}
