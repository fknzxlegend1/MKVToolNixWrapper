namespace MKVToolNixWrapper.Dtos;

public sealed class MkvAttachment
{
    public string? ContentType { get; set; }
    public string? Description { get; set; }
    public string? FileName { get; set; }
    public double Id { get; set; }
    public MkvAttachmentProperties? Properties { get; set; }
    public double Size { get; set; }
}