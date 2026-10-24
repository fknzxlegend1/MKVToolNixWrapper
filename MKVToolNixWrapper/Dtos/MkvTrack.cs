namespace MKVToolNixWrapper.Dtos;

public sealed class MkvTrack
{
    public string? Codec { get; set; }
    public double Id { get; set; }
    public MkvTrackProperties? Properties { get; set; }
    public string? Type { get; set; }
}