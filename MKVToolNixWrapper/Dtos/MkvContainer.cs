namespace MKVToolNixWrapper.Dtos;

public sealed class MkvContainer
{
    public MkvContainerProperties? Properties { get; set; }
    public bool Recognized { get; set; }
    public bool Supported { get; set; }
    public string? Type { get; set; }
}