namespace abilitydraft.Services.InGame;

public sealed class InGameOptions
{
    public bool Enabled { get; set; }
    // Trusted dedicated server credential, configured using InGame__ServerKey. Never sent to Panorama.
    public string ServerKey { get; set; } = "";
    public bool PublicQueueEnabled { get; set; }
    public int PublicMatchSize { get; set; } = 12;
}
