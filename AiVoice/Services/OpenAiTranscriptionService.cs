using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AiVoice.Services;

public class OpenAiTranscriptionService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;


    // =============================================
    // Models
    // =============================================

    private const string TranscriptionModel =
        "gpt-transcribe";

    private const string TranslationModel =
        "gpt-5.6-terra";


    // =============================================
    // Medical keywords
    // =============================================

    // بعد كده ممكن نجيبهم من الـ DB
    // بدل Static List.

    private static readonly string[]
        DiagnosisKeywords =
        {
            "Type 1 diabetes mellitus",
            "Type 2 diabetes mellitus",
            "Hypertension",
            "Hypotension",
            "Pneumonia",
            "COPD",
            "Bronchial asthma",
            "Chronic kidney disease",
            "CKD",
            "GERD",
            "Gastroesophageal reflux disease",
            "Myocardial infarction",
            "Acute myocardial infarction",
            "Atrial fibrillation",
            "Heart failure",
            "Hypothyroidism",
            "Hyperthyroidism",
            "Iron deficiency anemia",
            "Anemia",
            "Acute bronchitis"
        };


    // =============================================
    // Constructor
    // =============================================

    public OpenAiTranscriptionService(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient =
            httpClient;

        _apiKey =
            configuration["OpenAI:ApiKey"]
            ?? throw new InvalidOperationException(
                "OpenAI API Key is missing.");
    }


    // =============================================
    // Voice -> Text
    // =============================================

    public async Task<string>
        TranscribeDiagnosisAsync(
            Stream audioStream,
            string fileName,
            string? contentType,
            string? language,
            CancellationToken cancellationToken = default)
    {
        // -----------------------------------------
        // IMPORTANT
        //
        // إحنا بنقرأ نفس bytes الملف
        // بدون تحويل أو تغيير صيغة.
        // -----------------------------------------

        using MemoryStream memoryStream =
            new MemoryStream();

        await audioStream.CopyToAsync(
            memoryStream,
            cancellationToken);

        byte[] audioBytes =
            memoryStream.ToArray();


        if (audioBytes.Length == 0)
        {
            throw new InvalidOperationException(
                "Audio file is empty.");
        }


        string safeFileName =
            Path.GetFileName(
                fileName);


        if (string.IsNullOrWhiteSpace(
                safeFileName))
        {
            throw new InvalidOperationException(
                "Audio file name is invalid.");
        }


        // =========================================
        // Debug
        // =========================================

        Console.WriteLine(
            $"Audio File: {safeFileName}");

        Console.WriteLine(
            $"Audio Size: {audioBytes.Length} bytes");

        Console.WriteLine(
            $"Browser ContentType: {contentType}");


        // =========================================
        // Determine MIME without changing file
        // =========================================

        string actualContentType =
            GetContentType(
                safeFileName,
                contentType);


        Console.WriteLine(
            $"Sending ContentType: {actualContentType}");


        // =========================================
        // Multipart
        // =========================================

        using MultipartFormDataContent form =
            new MultipartFormDataContent();


        // IMPORTANT:
        // نفس bytes اللي جت من الـBrowser.
        using ByteArrayContent audioContent =
            new ByteArrayContent(
                audioBytes);


        audioContent.Headers.ContentType =
            new MediaTypeHeaderValue(
                actualContentType);


        form.Add(
            audioContent,
            "file",
            safeFileName);


        // =========================================
        // Model
        // =========================================

        form.Add(
            new StringContent(
                TranscriptionModel),
            "model");


        // =========================================
        // Response format
        // =========================================

        form.Add(
            new StringContent("json"),
            "response_format");


        // =========================================
        // Language hints
        // =========================================

        AddLanguageHints(
            form,
            language);


        // =========================================
        // Medical keyword hints
        // =========================================

        foreach (
            string keyword
            in DiagnosisKeywords)
        {
            form.Add(
                new StringContent(
                    keyword),
                "keywords[]");
        }


        // =========================================
        // Create request
        // =========================================

        using HttpRequestMessage request =
            new HttpRequestMessage(
                HttpMethod.Post,
                "v1/audio/transcriptions");


        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _apiKey);


        request.Content =
            form;


        // =========================================
        // Send
        // =========================================

        using HttpResponseMessage response =
            await _httpClient.SendAsync(
                request,
                cancellationToken);


        string responseBody =
            await response.Content
                .ReadAsStringAsync(
                    cancellationToken);


        // =========================================
        // Error
        // =========================================

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenAI transcription failed. " +
                $"Status: {(int)response.StatusCode}. " +
                $"Response: {responseBody}");
        }


        // =========================================
        // Read JSON
        // =========================================

        using JsonDocument json =
            JsonDocument.Parse(
                responseBody);


        if (!json.RootElement
            .TryGetProperty(
                "text",
                out JsonElement textElement))
        {
            throw new InvalidOperationException(
                "OpenAI did not return transcription text.");
        }


        string transcript =
            textElement
                .GetString()?
                .Trim()
            ?? string.Empty;


        if (string.IsNullOrWhiteSpace(
                transcript))
        {
            throw new InvalidOperationException(
                "No speech was detected.");
        }


        return transcript;
    }


    // =============================================
    // Language hints
    // =============================================

    private static void AddLanguageHints(
        MultipartFormDataContent form,
        string? language)
    {
        if (language == "en")
        {
            form.Add(
                new StringContent("en"),
                "languages[]");

            return;
        }


        if (language == "ar")
        {
            form.Add(
                new StringContent("ar"),
                "languages[]");

            return;
        }


        // Auto:
        // الدكتور ممكن يتكلم عربي وإنجليزي
        // في نفس التشخيص.

        form.Add(
            new StringContent("ar"),
            "languages[]");

        form.Add(
            new StringContent("en"),
            "languages[]");
    }


    // =============================================
    // Arabic / Mixed -> Clinical English
    // =============================================

    public async Task<string>
        ConvertDiagnosisToEnglishAsync(
            string originalText,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(
                originalText))
        {
            return string.Empty;
        }


        var body =
            new
            {
                model =
                    TranslationModel,

                reasoning =
                    new
                    {
                        effort =
                            "none"
                    },

                instructions =
                    """
                    You are a medical diagnosis
                    translation and normalization component.

                    Convert the supplied diagnosis into
                    concise clinical English.

                    Strict rules:

                    - Preserve the exact medical meaning.
                    - Never invent a diagnosis.
                    - Never infer a diagnosis that was not spoken.
                    - Do not add clinical information.
                    - Preserve all negation.
                    - Preserve numbers.
                    - Preserve type.
                    - Preserve stage.
                    - Preserve grade.
                    - Preserve severity.
                    - Preserve laterality.
                    - Preserve medical abbreviations when appropriate.
                    - Preserve disease names accurately.
                    - If the input is already clinical English,
                      return clean clinical English.
                    - Return ONLY the diagnosis text.
                    - Do not explain.
                    - Do not add punctuation unless useful.

                    Example:

                    Input:
                    المريض عنده التهاب رئوي

                    Output:
                    Pneumonia

                    Input:
                    Type two diabetes mellitus

                    Output:
                    Type 2 diabetes mellitus
                    """,

                input =
                    originalText
            };


        using HttpRequestMessage request =
            new HttpRequestMessage(
                HttpMethod.Post,
                "v1/responses");


        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _apiKey);


        request.Content =
            JsonContent.Create(
                body);


        using HttpResponseMessage response =
            await _httpClient.SendAsync(
                request,
                cancellationToken);


        string responseBody =
            await response.Content
                .ReadAsStringAsync(
                    cancellationToken);


        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenAI English conversion failed. " +
                $"Status: {(int)response.StatusCode}. " +
                $"Response: {responseBody}");
        }


        using JsonDocument json =
            JsonDocument.Parse(
                responseBody);


        string? result =
            ExtractResponseText(
                json.RootElement);


        if (string.IsNullOrWhiteSpace(
                result))
        {
            throw new InvalidOperationException(
                "OpenAI did not return the English diagnosis.");
        }


        return result.Trim();
    }


    // =============================================
    // Extract Responses API text
    // =============================================

    private static string?
        ExtractResponseText(
            JsonElement root)
    {
        // بعض clients ممكن يظهر عندهم
        // output_text مباشرة.

        if (root.TryGetProperty(
                "output_text",
                out JsonElement outputText)
            &&
            outputText.ValueKind ==
                JsonValueKind.String)
        {
            return outputText.GetString();
        }


        if (!root.TryGetProperty(
                "output",
                out JsonElement output)
            ||
            output.ValueKind !=
                JsonValueKind.Array)
        {
            return null;
        }


        StringBuilder builder =
            new StringBuilder();


        foreach (
            JsonElement outputItem
            in output.EnumerateArray())
        {
            if (!outputItem
                .TryGetProperty(
                    "content",
                    out JsonElement content)
                ||
                content.ValueKind !=
                    JsonValueKind.Array)
            {
                continue;
            }


            foreach (
                JsonElement contentItem
                in content.EnumerateArray())
            {
                if (contentItem
                    .TryGetProperty(
                        "text",
                        out JsonElement text)
                    &&
                    text.ValueKind ==
                        JsonValueKind.String)
                {
                    builder.Append(
                        text.GetString());
                }
            }
        }


        return builder.ToString();
    }


    // =============================================
    // Content Type
    // =============================================

    private static string GetContentType(
        string fileName,
        string? browserContentType)
    {
        string extension =
            Path.GetExtension(
                    fileName)
                .ToLowerInvariant();


        // IMPORTANT:
        //
        // الامتداد لازم يكون الامتداد الحقيقي.
        //
        // voice.webm = WebM حقيقي
        // voice.mp3  = MP3 حقيقي
        //
        // ممنوع Rename من webm -> mp3.


        return extension switch
        {
            ".webm" =>
                "audio/webm",

            ".mp3" =>
                "audio/mpeg",

            ".wav" =>
                "audio/wav",

            ".m4a" =>
                "audio/mp4",

            ".mp4" =>
                "audio/mp4",

            ".ogg" =>
                "audio/ogg",

            ".flac" =>
                "audio/flac",

            ".mpeg" =>
                "audio/mpeg",

            ".mpga" =>
                "audio/mpeg",

            _ =>
                CleanBrowserContentType(
                    browserContentType)
        };
    }


    // =============================================
    // Clean Content-Type
    // =============================================

    private static string
        CleanBrowserContentType(
            string? contentType)
    {
        if (string.IsNullOrWhiteSpace(
                contentType))
        {
            return
                "application/octet-stream";
        }


        // Example:
        //
        // audio/webm;codecs=opus
        //
        // becomes:
        //
        // audio/webm
        //
        // ده تغيير في HTTP Header فقط.
        // الملف نفسه لم يتغير.

        return contentType
            .Split(';')[0]
            .Trim();
    }
}