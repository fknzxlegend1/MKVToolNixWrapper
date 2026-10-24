namespace MKVToolNixWrapper.Dtos;

public sealed class MkvTrackProperties
{
    public string? CodecId { get; set; }
    public string? CodecPrivateData { get; set; }
    public double CodecPrivateLength { get; set; }
    public double DefaultDuration { get; set; }
    public bool DefaultTrack { get; set; }
    public bool EnabledTrack { get; set; }
    public bool ForcedTrack { get; set; }
    public string? Language { get; set; }
    public double MinimumTimestamp { get; set; }
    public double Number { get; set; }
    public string? Packetizer { get; set; }
    public string? PixelDimensions { get; set; }
    public double Uid { get; set; }
    public string? TrackName { get; set; }
}