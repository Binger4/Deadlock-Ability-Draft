using System.Net.WebSockets;
using System.Text.Json;

namespace AbilityDraft.Contracts;

// Versioned, bounded control channel. No paths, commands, credentials or room objects.
public static class GameNodeProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool Terminal(string state) => state is "Completed" or "Failed";
    public static bool ValidHost(string host) => !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
        Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 &&
        host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
    public static async Task Send<T>(WebSocket socket, T message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("Game Node message too large.");
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token);
    }
    public static async Task<T> Receive<T>(WebSocket socket, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[16384];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (result.MessageType != WebSocketMessageType.Text || data.Length + result.Count > MaxMessageBytes)
                throw new InvalidDataException("Invalid Game Node frame.");
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<T>(data.GetBuffer().AsSpan(0, (int)data.Length), Json)
            ?? throw new InvalidDataException("Missing Game Node message.");
    }
}
public sealed record GameNodeRegistration(int ProtocolVersion, string Version, string PublicHost,
    int FirstPort, int MaxWorkers, string? PublicProbeUrl);
public sealed record GameNodeMatchStatus(string MatchId, string State, int Port, bool ProcessActive,
    int Connected, string[] Abandoned, int AdmissionRevision = 0);
public sealed record GameNodeHeartbeat(GameNodeRegistration Registration, GameNodeMatchStatus[] Matches);
public sealed record StartGameNodeMatch(string MatchId, DraftResult Result, DraftResourceCatalog Catalog,
    DraftSpectator[] Spectators, string[] Abandoned, int AdmissionRevision = 0);
public sealed record GameNodeCommands(int ProtocolVersion, string? Error, StartGameNodeMatch[] StartMatches,
    string[] StopMatches, string[] ForgetMatches);
// Public and admin models deliberately have no reference to configuration/credentials.
public sealed record GameNodeView(string Id, string Name, string Region, bool IsMain, string Status,
    int ActiveMatches, int MaxMatches, string? PublicProbeUrl)
{
    public int FreeSlots => Math.Max(0, MaxMatches - ActiveMatches);
    public bool Available => Status == "Online" && FreeSlots > 0;
}
public sealed record GameNodeAdminView(GameNodeView Node, string PublicHost, string Version,
    DateTime? ConnectedSince, DateTime? LastHeartbeat, string? Error, string[] MatchIds);
