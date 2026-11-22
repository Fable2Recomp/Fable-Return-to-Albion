#if GODOT_ANDROID
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Refii.Core;

namespace Refii.Launcher.Core;

/// <summary>GitHub requests from the Android launcher.</summary>
public static partial class GitHubApi
{
    // NativeAOT's linux-bionic HttpClient loads libssl, which the APK does not ship.
    private static Task<HttpResponseMessage> GetResponseAsync(string url) =>
        Engine_Platform.IsLinuxBionic ? GetGodotResponseAsync(url) : Client.Value.GetAsync(url);

    private static Task<HttpResponseMessage> GetGodotResponseAsync(string url)
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Godot.Callable.From(() =>
        {
            var request = new Godot.HttpRequest { Timeout = 20 };
            try
            {
                var tree = (Godot.SceneTree)Godot.Engine.GetMainLoop();
                tree.Root.AddChild(request);
                request.TreeExiting += () => pending.TrySetCanceled();
                request.RequestCompleted += (result, status, headers, body) =>
                {
                    try
                    {
                        if ((Godot.HttpRequest.Result)result == Godot.HttpRequest.Result.Timeout)
                            pending.TrySetCanceled();
                        else if ((Godot.HttpRequest.Result)result != Godot.HttpRequest.Result.Success)
                            pending.TrySetException(new HttpRequestException(((Godot.HttpRequest.Result)result).ToString()));
                        else
                        {
                            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status)
                            {
                                Content = new ByteArrayContent(body)
                            };
                            foreach (string header in headers)
                            {
                                int colon = header.IndexOf(':');
                                if (colon <= 0) continue;
                                string name = header[..colon];
                                string value = header[(colon + 1)..].Trim();
                                if (!response.Headers.TryAddWithoutValidation(name, value))
                                    response.Content.Headers.TryAddWithoutValidation(name, value);
                            }
                            if (!pending.TrySetResult(response)) response.Dispose();
                        }
                    }
                    catch (Exception error) { pending.TrySetException(error); }
                    finally { request.QueueFree(); }
                };
                var error = request.Request(url, new[] { "User-Agent: " + UserAgent });
                if (error != Godot.Error.Ok) throw new HttpRequestException(error.ToString());
            }
            catch (Exception error)
            {
                pending.TrySetException(error);
                request.QueueFree();
            }
        }).CallDeferred();
        return pending.Task;
    }
}
#endif
