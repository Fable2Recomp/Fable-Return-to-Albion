using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Refii.Launcher.Core;

public sealed record GitCommit(string Sha, string Message, string Author, string Date);

public sealed record GitContributor(string Login, string Name, string AvatarUrl, int Contributions);

public static class GitHubApi
{
    private const string UserAgent = "refii-launcher/1.0";
    private const int CommitsPerPage = 10;

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        return client;
    }

    public static string GetRepositoryName() =>
        Ini.GetString("Name", "Fable2Recomp/refii", "Github");

    public static string GetRepositoryBranch() =>
        Ini.GetString("Branch", "master", "Github");

    public static string GetProjectTitle() =>
        Ini.GetString("ProjectTitle", "Fable 2 Recompiled", "About");

    public static string GetProjectPage() =>
        Ini.GetString("ProjectPage", "https://github.com/Fable2Recomp/refii", "About");

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

    public static async Task<string> FetchLatestCommitShaForFileAsync(
        string repo, string branch, string filePath)
    {
        string url = "https://api.github.com/repos/" + repo +
                     "/commits?sha=" + Uri.EscapeDataString(branch) +
                     "&path=" + Uri.EscapeDataString(filePath) +
                     "&per_page=1";

        string? json = await GetStringAsync(url).ConfigureAwait(false);
        if (json is null)
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                return ReadString(element, "sha");
            }
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        return string.Empty;
    }

    public static Task<string?> HttpGetAsync(string url) => GetStringAsync(url);

    public static async Task<byte[]?> HttpGetBytesAsync(string url)
    {
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(url).ConfigureAwait(false);
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

    private static async Task<string?> GetStringAsync(string url)
    {
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
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
