namespace MKVToolNixWrapper.Dtos;

public sealed class FileMeta
{
    public string FilePath { get; set; } = string.Empty;
    public bool Include { get; set; }
    public FileStatusEnum Status { get; set; }
}