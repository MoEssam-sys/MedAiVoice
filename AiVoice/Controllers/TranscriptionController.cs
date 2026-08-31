using AiVoice.Models;
using AiVoice.Services;

using Microsoft.AspNetCore.Mvc;

namespace AiVoice.Controllers;


[ApiController]
[Route("api/transcription")]
public class TranscriptionController
    : ControllerBase
{
    private readonly
        OpenAiTranscriptionService
        _transcriptionService;


    // =============================================
    // Supported extensions
    // =============================================

    private static readonly
        HashSet<string>
        AllowedExtensions =
        new(
            StringComparer.OrdinalIgnoreCase)
        {
            ".webm",
            ".mp3",
            ".wav",
            ".m4a",
            ".mp4",
            ".ogg",
            ".flac",
            ".mpeg",
            ".mpga"
        };


    // =============================================
    // Constructor
    // =============================================

    public TranscriptionController(
        OpenAiTranscriptionService
            transcriptionService)
    {
        _transcriptionService =
            transcriptionService;
    }


    // =============================================
    // POST api/transcription
    // =============================================

    [HttpPost]

    [RequestSizeLimit(
        26_000_000)]

    public async Task<
        ActionResult<TranscriptionResponse>>
        Transcribe(
            [FromForm] IFormFile file,
            [FromForm] string language = "auto",
            [FromForm] bool englishOnly = true,
            CancellationToken cancellationToken = default)
    {
        // =========================================
        // Validate file
        // =========================================

        if (file == null ||
            file.Length == 0)
        {
            return BadRequest(
                new
                {
                    message =
                        "No audio file was provided."
                });
        }


        // Diagnosis المفروض صغير جدًا.
        // كمان بنمنع الملفات الضخمة.

        const long MaxAudioSize =
            25 * 1024 * 1024;


        if (file.Length >
            MaxAudioSize)
        {
            return BadRequest(
                new
                {
                    message =
                        "Audio file is too large."
                });
        }


        string originalFileName =
            Path.GetFileName(
                file.FileName);


        string extension =
            Path.GetExtension(
                originalFileName);


        if (!AllowedExtensions
            .Contains(extension))
        {
            return BadRequest(
                new
                {
                    message =
                        $"Unsupported audio format: {extension}"
                });
        }


        // =========================================
        // Validate language
        // =========================================

        language =
            language?
                .Trim()
                .ToLowerInvariant()
            ?? "auto";


        if (language != "auto" &&
            language != "ar" &&
            language != "en")
        {
            language =
                "auto";
        }


        try
        {
            // =====================================
            // Voice -> Original Transcript
            // =====================================

            await using Stream stream =
                file.OpenReadStream();


            string originalTranscript =
                await _transcriptionService
                    .TranscribeDiagnosisAsync(
                        stream,
                        originalFileName,
                        file.ContentType,
                        language,
                        cancellationToken);


            // =====================================
            // Final Text
            // =====================================

            string finalText =
                originalTranscript;


            if (englishOnly)
            {
                finalText =
                    await _transcriptionService
                        .ConvertDiagnosisToEnglishAsync(
                            originalTranscript,
                            cancellationToken);
            }


            // =====================================
            // Return
            // =====================================

            return Ok(
                new TranscriptionResponse
                {
                    OriginalTranscript =
                        originalTranscript,

                    Text =
                        finalText
                });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(
                499);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(
                StatusCodes
                    .Status502BadGateway,

                new
                {
                    message =
                        ex.Message
                });
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);


            return StatusCode(
                StatusCodes
                    .Status500InternalServerError,

                new
                {
                    message =
                        "Unexpected transcription error."
                });
        }
    }
}