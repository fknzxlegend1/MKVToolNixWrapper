namespace MKVToolNixWrapper.Dtos;

public sealed class MkvContainerProperties
{
    public double ContainerType { get; set; }
    public string? DateLocal { get; set; }
    public string? DateUtc { get; set; }
    public long Duration { get; set; }
    public bool IsProvidingTimestamps { get; set; }
    public string? MuxingApplication { get; set; }
    public string? SegmentUid { get; set; }
    public string? Title { get; set; }
    public string? WritingApplication { get; set; }
}