using System.Collections.Generic;

namespace MKVToolNixWrapper.Dtos;

public sealed class MkvFile
{
    public List<MkvAttachment>? Attachments { get; set; }
    public List<MkvChapter>? Chapters { get; set; }
    public MkvContainer? Container { get; set; }
    public List<object>? Errors { get; set; }
    public string? FileName { get; set; }
    public List<object>? GlobalTags { get; set; }
    public double IdentificationFormatVersion { get; set; }
    public List<object>? TrackTags { get; set; }
    public List<MkvTrack>? Tracks { get; set; }
}