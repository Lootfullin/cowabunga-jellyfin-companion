namespace Jellyfin.Plugin.SmartResolver.Api;

public sealed class PreviewRequest
{
    public string Path { get; set; } = string.Empty;

    public string MediaType { get; set; } = "Auto";
}
