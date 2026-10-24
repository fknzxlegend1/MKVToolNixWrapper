namespace MKVToolNixWrapper.Dtos;

public sealed class TrackListMeta
{
    public double Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Codec { get; set; } = string.Empty;
    public bool Include { get; set; }
    public bool Default { get; set; }
    public bool Forced { get; set; }
}