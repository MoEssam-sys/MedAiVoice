using OpenAI.Audio;
using OpenAI.Chat;
using System.IO;
using System.Threading.Tasks;

public class AiService
{
    private readonly string _openAiApiKey;

    public AiService(IConfiguration configuration)
    {
        _openAiApiKey = configuration["OpenAI:ApiKey"];
    }

    // 1. دالة لتحويل الصوت إلى نص
    public async Task<string> TranscribeAudioAsync(Stream audioStream, string fileName)
    {
        var client = new AudioClient("whisper-1", _openAiApiKey);
        var options = new AudioTranscriptionOptions { Language = "ar" }; // تحديد اللغة العربية

        var response = await client.TranscribeAudioAsync(audioStream, fileName, options);
        return response.Value.Text;
    }

    // 2. دالة لإرسال النص مع بيانات الداتابيز للـ AI
    public async Task<string> GetAiResponseAsync(string userMessage, string systemPromptContext)
    {
        var client = new ChatClient("gpt-4o", _openAiApiKey);

        var messages = new List<ChatMessage>
    {
        new SystemChatMessage(systemPromptContext), // السياق وبيانات الداتابيز
        new UserChatMessage(userMessage) // ما قاله المستخدم (النص المستخرج من الصوت)
    };

        var response = await client.CompleteChatAsync(messages);
        return response.Value.Content[0].Text;
    }
}