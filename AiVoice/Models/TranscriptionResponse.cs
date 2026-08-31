namespace AiVoice.Models;

public class TranscriptionResponse
{
    public string OriginalTranscript { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}