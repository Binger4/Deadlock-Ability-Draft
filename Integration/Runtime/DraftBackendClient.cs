using System.Net.Http.Json;
using System.Net.Http.Headers;
using AbilityDraft.Contracts;

namespace AbilityDraft.Runtime;

public sealed class DraftBackendClient(HttpClient http, Uri baseUri, string serverKey)
{
    public async Task<DraftArchive> GenerateArchive(string steam, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var request = Request(steam, "export", HttpMethod.Post);
        using var response = await http.SendAsync(request, timeout.Token);
        await CheckResponse(response, timeout.Token);
        var format = response.Headers.TryGetValues("X-AbilityDraft-Format", out var values) ? values.SingleOrDefault() : null;
        if (format is not ("zip" or "vpk")) throw new InvalidOperationException("Invalid archive response.");
        var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (name is null || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !name.EndsWith("." + format, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid archive filename.");
        return new(name, format, await response.Content.ReadAsByteArrayAsync(timeout.Token));
    }
    public Task<CommandReply> Command(string steam, RoomCommand command, CancellationToken ct) =>
        Send<CommandReply>(steam, "command", command, ct);
    public Task<CommandReply> State(string steam, CancellationToken ct) => Send<CommandReply>(steam, "state", null, ct);
    public Task<CommandReply> Abandon(string steam, string? resultId, CancellationToken ct) => Send<CommandReply>(steam, "abandon", new("abandon", Key: resultId), ct);
    public Task<DraftResult> Result(string steam, CancellationToken ct) => Send<DraftResult>(steam, "result", null, ct);
    public Task<MatchView> RequestMatch(string steam, CancellationToken ct) => Send<MatchView>(steam, "match", new("startMatch"), ct);
    public Task<MatchView> Match(string steam, CancellationToken ct) => Send<MatchView>(steam, "match", null, ct);
    public Task<WebsiteEntry> Website(string steam, CancellationToken ct) => Send<WebsiteEntry>(steam, "website", null, ct);
    public Task<WebsiteEntry> WebsiteEntry(string steam, string page, string name, CancellationToken ct) =>
        Send<WebsiteEntry>(steam, "website-entry", new(page, Name: name), ct);
    public Task<QueueReply> Queue(string steam, RoomCommand? command, CancellationToken ct) => Send<QueueReply>(steam, "queue", command, ct);
    public async Task Disconnect(string steam, CancellationToken ct)
    {
        using var request = Request(steam, "disconnect", HttpMethod.Post);
        using var response = await http.SendAsync(request, ct);
    }
    private HttpRequestMessage Request(string steam, string route, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, new Uri(baseUri, "api/ingame/v1/" + route));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serverKey);
        request.Headers.Add("X-Steam-Id", steam);
        return request;
    }
    private async Task<T> Send<T>(string steam, string route, RoomCommand? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        ct = timeout.Token;
        using var request = Request(steam, route, body is null ? HttpMethod.Get : HttpMethod.Post);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        await CheckResponse(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new InvalidOperationException("Empty backend response.");
    }
    private static async Task CheckResponse(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = response.StatusCode == System.Net.HttpStatusCode.BadRequest
                ? (await response.Content.ReadFromJsonAsync<CommandReply>(ct))?.Error : null;
            throw new InvalidOperationException(error ?? $"Draft backend unavailable (HTTP {(int)response.StatusCode}).");
        }
    }
}
