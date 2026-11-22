using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Refii.Launcher.Core;

public sealed record GitCommit(string Sha, string Message, string Author, string Date);

public sealed record GitContributor(string Login, string Name, string AvatarUrl, int Contributions);

/// <summary>Fetches repository information and release assets for the launcher.</summary>
public static partial class GitHubApi
{
    private const string UserAgent = "refii-launcher/1.0";
    private const int CommitsPerPage = 10;

    private static readonly Lazy<HttpClient> Client = new(CreateClient);

#if !GODOT_ANDROID
    private static Task<HttpResponseMessage> GetResponseAsync(string url) => Client.Value.GetAsync(url);
#endif

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        return client;
    }

    public static string GetRepositoryName() =>
        Ini.GetString("Name", "Fable2Recomp/Fable-Return-to-Albion", "Github");

    public static string GetRepositoryBranch() =>
        Ini.GetString("Branch", "main", "Github");

    public static string GetProjectTitle()
    {
        string raw = Ini.GetString("ProjectTitle", "Fable: Return to Albion", "About");
        return string.IsNullOrWhiteSpace(raw) || raw == "Fable 2: Return to Albion"
            ? "Fable: Return to Albion"
            : raw;
    }

    public static string GetProjectPage() =>
        Ini.GetString("ProjectPage", "https://github.com/Fable2Recomp/Fable-Return-to-Albion", "About");

    public static async Task<List<GitCommit>?> FetchCommitsAsync(string repo, string branch)
    {
        string url = "https://api.github.com/repos/" + repo +
                     "/commits?sha=" + Uri.EscapeDataString(branch) +
                     "&per_page=" + CommitsPerPage;

        string? json = await GetStringAsync(url).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        var commits = new List<GitCommit>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                string sha = ReadString(element, "sha");
                if (sha.Length > 8)
                {
                    sha = sha[..8];
                }

                JsonElement commit = Child(element, "commit");
                string message = ReadString(commit, "message");
                int newline = message.IndexOf('\n');
                if (newline >= 0)
                {
                    message = message[..newline];
                }

                JsonElement author = Child(commit, "author");
                string name = ReadString(author, "name");
                string date = ReadString(author, "date");
                if (date.Length > 10)
                {
                    date = date[..10];
                }

                commits.Add(new GitCommit(sha, message, name, date));
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return commits;
    }

    public static async Task<List<GitContributor>?> FetchContributorsAsync(string repo)
    {
        string url = "https://api.github.com/repos/" + repo + "/contributors";

        string? json = await GetStringAsync(url).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        var contributors = new List<GitContributor>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                contributors.Add(new GitContributor(
                    ReadString(element, "login"),
                    ReadString(element, "name"),
                    ReadString(element, "avatar_url"),
                    ReadInt(element, "contributions")));
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return contributors;
    }

    public static Task<string?> HttpGetAsync(string url) => GetStringAsync(url);

    /// <summary>
    /// A commit title without the pull request GitHub appends to a merged one: "Fix the map (#28)"
    /// is "Fix the map" with <paramref name="pullRequest"/> "28". A title with no such ending is
    /// returned whole, with <paramref name="pullRequest"/> empty.
    /// </summary>
    public static string SplitPullRequest(string title, out string pullRequest)
    {
        pullRequest = string.Empty;
        string trimmed = title.TrimEnd();
        int open = trimmed.LastIndexOf(" (#", StringComparison.Ordinal);
        if (open <= 0 || !trimmed.EndsWith(')'))
        {
            return trimmed;
        }

        string number = trimmed[(open + 3)..^1];
        if (number.Length == 0 || number.Length > 9)
        {
            return trimmed;
        }

        foreach (char digit in number)
        {
            if (digit < '0' || digit > '9')
            {
                return trimmed;
            }
        }

        pullRequest = number;
        return trimmed[..open];
    }

    public static async Task<byte[]?> HttpGetBytesAsync(string url)
    {
        try
        {
            using HttpResponseMessage response = await GetResponseAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string?> GetStringAsync(string url) =>
        (await GetAsync(url).ConfigureAwait(false)).Body;

    /// <summary>
    /// One GET: the body, or null with the reason it was not served, worded for the player.
    /// </summary>
    public static async Task<(string? Body, string Failure)> GetAsync(string url)
    {
        try
        {
            using HttpResponseMessage response = await GetResponseAsync(url).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadAsStringAsync().ConfigureAwait(false), string.Empty);
            }

            return (null, DescribeFailure(
                (int)response.StatusCode, Header(response, "X-RateLimit-Remaining"), Header(response, "X-RateLimit-Reset")));
        }
        catch (TaskCanceledException)
        {
            return (null, "GitHub did not answer in time.");
        }
        catch (Exception exception)
        {
            return (null, "GitHub could not be reached (" + exception.Message + ").");
        }
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : string.Empty;

    /// <summary>
    /// Why GitHub refused a request. An unsigned client gets 60 API requests an hour per network
    /// address; past that GitHub answers 403 or 429 with <c>X-RateLimit-Remaining: 0</c> and the
    /// time the allowance returns in <c>X-RateLimit-Reset</c> (seconds since 1970, UTC).
    /// </summary>
    public static string DescribeFailure(int status, string remaining, string reset)
    {
        if ((status == 403 || status == 429) && remaining.Trim() == "0")
        {
            string when = long.TryParse(reset.Trim(), out long seconds)
                ? " It is available again at " + DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("HH:mm") + "."
                : " It is available again within the hour.";
            return "GitHub's hourly request limit for this network is used up." + when;
        }

        return status == 404
            ? "GitHub has no such page for this project (HTTP 404)."
            : "GitHub refused the request (HTTP " + status + ").";
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement child)
            ? child
            : default;

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number)
            ? number
            : 0;
}
