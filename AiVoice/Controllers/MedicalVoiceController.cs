using AiVoice.Models;
using AiVoice.Services;

using Microsoft.AspNetCore.Mvc;

namespace AiVoice.Controllers;


[ApiController]
[Route("api/medical-voice")]
public class MedicalVoiceController
    : ControllerBase
{
    private readonly OpenAiMedicalVoiceService
        _openAiService;


    private static readonly HashSet<string>
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


    public MedicalVoiceController(
        OpenAiMedicalVoiceService openAiService)
    {
        _openAiService =
            openAiService;
    }


    // =========================================================
    // POST api/medical-voice
    // =========================================================

    [HttpPost]

    [RequestSizeLimit(
        26_000_000)]

    public async Task<ActionResult<MedicalVoiceResult>>
        ProcessConversation(
            [FromForm] IFormFile file,
            [FromForm] string language = "auto",
            CancellationToken cancellationToken = default)
    {
        // =====================================================
        // Validation
        // =====================================================

        if (file == null ||
            file.Length == 0)
        {
            return BadRequest(
                new
                {
                    message =
                        "No conversation audio was provided."
                });
        }


        const long MaxFileSize =
            25 * 1024 * 1024;


        if (file.Length >
            MaxFileSize)
        {
            return BadRequest(
                new
                {
                    message =
                        "Audio file is too large."
                });
        }


        string fileName =
            Path.GetFileName(
                file.FileName);


        string extension =
            Path.GetExtension(
                fileName);


        if (!AllowedExtensions.Contains(
                extension))
        {
            return BadRequest(
                new
                {
                    message =
                        $"Unsupported audio format: {extension}"
                });
        }


        // =====================================================
        // Language
        // =====================================================

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
            // =================================================
            // Read voice ONCE
            // =================================================

            byte[] audioBytes;


            await using (
                MemoryStream memoryStream =
                    new())
            {
                await file.CopyToAsync(
                    memoryStream,
                    cancellationToken);


                audioBytes =
                    memoryStream.ToArray();
            }


            if (audioBytes.Length == 0)
            {
                return BadRequest(
                    new
                    {
                        message =
                            "Audio file is empty."
                    });
            }


            // =================================================
            // Service internally:
            //
            // Diarization     ─┐
            //                  ├─ Parallel
            // GPT Transcribe  ─┘
            //
            // Then Terra analysis
            // =================================================

            MedicalVoiceResult result =
                await _openAiService
                    .ProcessConversationAsync(
                        audioBytes,
                        fileName,
                        file.ContentType,
                        language,
                        cancellationToken);


            return Ok(
                result);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(
                499);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,

                new
                {
                    message =
                        ex.Message
                });
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                ex);


            return StatusCode(
                StatusCodes.Status500InternalServerError,

                new
                {
                    message =
                        "Unexpected medical voice processing error."
                });
        }
    }
}