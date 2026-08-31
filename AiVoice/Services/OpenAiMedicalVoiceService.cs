using AiVoice.Models;

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AiVoice.Services;


public class OpenAiMedicalVoiceService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _ffmpegPath;


    // =========================================================
    // Models
    // =========================================================

    private const string DiarizationModel =
        "gpt-4o-transcribe-diarize";

    private const string AccurateTranscriptionModel =
        "gpt-transcribe";

    private const string LanguageModel =
        "gpt-5.6-luna";

    private const string MedicalModel =
        "gpt-5.6-terra";


    // =========================================================
    // Performance
    // =========================================================

    // أقصى عدد Transcription calls في نفس الوقت.
    //
    // 4 Balance كويس:
    // - سريع
    // - مش بنفتح requests زيادة جدًا
    // - مناسب للRate Limits الطبيعية
    private const int MaxParallelTranscriptions =
        4;


    // لو نفس Speaker ظهر في Segments متتالية
    // وبينهم أقل من 0.75 ثانية،
    // نعتبرهم Turn واحد.
    private const double MergeGapSeconds =
        0.75;


    // Padding صغير عشان ما نقطعش آخر حرف
    // زي كلمة "بكح".
    private const double StartPaddingSeconds =
        0.12;

    private const double EndPaddingSeconds =
        0.25;


    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };


    // =========================================================
    // Constructor
    // =========================================================

    public OpenAiMedicalVoiceService(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient =
            httpClient;


        _apiKey =
            configuration["OpenAI:ApiKey"]
            ?? throw new InvalidOperationException(
                "OpenAI API Key is missing.");


        // لو مش محدد Path:
        //
        // هيستخدم ffmpeg من PATH.
        _ffmpegPath =
            configuration["FFmpeg:Path"]
            ?? "ffmpeg";
    }


    // =========================================================
    // MAIN
    // =========================================================

    public async Task<MedicalVoiceResult>
        ProcessConversationAsync(
            byte[] audioBytes,
            string fileName,
            string? browserContentType,
            string? language,
            CancellationToken cancellationToken = default)
    {
        if (audioBytes.Length == 0)
        {
            throw new InvalidOperationException(
                "Audio file is empty.");
        }


        language =
            NormalizeLanguage(
                language);


        // =====================================================
        // تأكد إن FFmpeg موجود
        // =====================================================

        await EnsureFfmpegAvailableAsync(
            cancellationToken);


        // =====================================================
        // CALL #1
        //
        // Full audio
        // ↓
        // Speaker A / B + timestamps
        // =====================================================

        DiarizedTranscript diarization =
            await TranscribeWithDiarizationAsync(
                audioBytes,
                fileName,
                browserContentType,
                language,
                cancellationToken);


        // =====================================================
        // Local only - FREE
        //
        // Merge consecutive speaker segments
        // =====================================================

        List<AudioTurn> turns =
            MergeSegmentsIntoTurns(
                diarization);


        if (turns.Count == 0)
        {
            throw new InvalidOperationException(
                "No usable conversation turns were found.");
        }


        Console.WriteLine();
        Console.WriteLine(
            $"Diarization Segments: {diarization.Segments.Count}");

        Console.WriteLine(
            $"Merged Audio Turns: {turns.Count}");

        Console.WriteLine();


        // =====================================================
        // Write original audio ONCE
        // =====================================================

        string tempDirectory =
            Path.Combine(
                Path.GetTempPath(),
                "AiVoice",
                Guid.NewGuid().ToString("N"));


        Directory.CreateDirectory(
            tempDirectory);


        string extension =
            GetSafeExtension(
                fileName);


        string originalAudioPath =
            Path.Combine(
                tempDirectory,
                $"original{extension}");


        try
        {
            await File.WriteAllBytesAsync(
                originalAudioPath,
                audioBytes,
                cancellationToken);


            // =================================================
            // CALLS #2...N
            //
            // Turn transcription.
            //
            // Controlled parallel.
            // =================================================

            await TranscribeTurnsAsync(
                turns,
                originalAudioPath,
                diarization.Duration,
                tempDirectory,
                language,
                cancellationToken);


            // =================================================
            // Build exact Raw Transcript
            // =================================================

            string rawTranscript =
                BuildRawTranscript(
                    turns);


            Console.WriteLine();
            Console.WriteLine(
                "==============================================");

            Console.WriteLine(
                "FINAL TURN TRANSCRIPT:");

            Console.WriteLine(
                "==============================================");

            foreach (
                AudioTurn turn
                in turns)
            {
                Console.WriteLine(
                    $"{turn.Id} | " +
                    $"{turn.SpeakerLabel} | " +
                    $"{turn.OriginalText}");
            }

            Console.WriteLine(
                "==============================================");

            Console.WriteLine();


            // =================================================
            // Luna
            //
            // Text only.
            //
            // IMPORTANT:
            // Luna does NOT return Arabic OriginalText.
            // =================================================

            ConversationLanguageResult languageResult =
                await ProcessLanguageAsync(
                    turns,
                    cancellationToken);


            // =================================================
            // Build UI chat.
            //
            // Arabic stays directly from gpt-transcribe.
            // =================================================

            List<ConversationMessage> conversation =
                BuildConversation(
                    turns,
                    languageResult);


            // =================================================
            // Terra only when Luna detects possible diagnosis.
            // =================================================

            List<DiagnosisResult> diagnoses =
                new();


            if (languageResult.RequiresDiagnosisReview)
            {
                string diagnosisInput =
                    BuildDiagnosisInput(
                        conversation,
                        languageResult);


                if (!string.IsNullOrWhiteSpace(
                        diagnosisInput))
                {
                    DiagnosisReviewResult diagnosisResult =
                        await ReviewDiagnosesAsync(
                            diagnosisInput,
                            cancellationToken);


                    diagnoses =
                        diagnosisResult.Diagnoses;
                }
            }


            // =================================================
            // Final result
            // =================================================

            return new MedicalVoiceResult
            {
                RawTranscript =
                    rawTranscript,

                DoctorSpeakerLabel =
                    languageResult.DoctorSpeakerLabel,

                PatientSpeakerLabel =
                    languageResult.PatientSpeakerLabel,

                SpeakerMappingConfidence =
                    languageResult.SpeakerMappingConfidence,

                Conversation =
                    conversation,

                Diagnoses =
                    diagnoses,

                Summary =
                    languageResult.Summary
            };
        }
        finally
        {
            // =================================================
            // Cleanup temp audio
            // =================================================

            try
            {
                if (Directory.Exists(
                        tempDirectory))
                {
                    Directory.Delete(
                        tempDirectory,
                        recursive: true);
                }
            }
            catch
            {
                // Cleanup failure must not break result.
            }
        }
    }


    // =========================================================
    // CALL #1
    //
    // Full Audio Diarization
    // =========================================================

    private async Task<DiarizedTranscript>
        TranscribeWithDiarizationAsync(
            byte[] audioBytes,
            string fileName,
            string? browserContentType,
            string language,
            CancellationToken cancellationToken)
    {
        string safeFileName =
            Path.GetFileName(
                fileName);


        string contentType =
            GetContentType(
                safeFileName,
                browserContentType);


        using MultipartFormDataContent form =
            new();


        using ByteArrayContent audioContent =
            new(
                audioBytes);


        audioContent.Headers.ContentType =
            new MediaTypeHeaderValue(
                contentType);


        form.Add(
            audioContent,
            "file",
            safeFileName);


        form.Add(
            new StringContent(
                DiarizationModel),
            "model");


        // speaker segments
        form.Add(
            new StringContent(
                "diarized_json"),
            "response_format");


        form.Add(
            new StringContent(
                "auto"),
            "chunking_strategy");


        if (language == "ar" ||
            language == "en")
        {
            form.Add(
                new StringContent(
                    language),
                "language");
        }


        using HttpRequestMessage request =
            CreateAuthorizedRequest(
                HttpMethod.Post,
                "v1/audio/transcriptions");


        request.Content =
            form;


        using HttpResponseMessage response =
            await _httpClient.SendAsync(
                request,
                cancellationToken);


        string body =
            await response.Content
                .ReadAsStringAsync(
                    cancellationToken);


        ThrowIfFailed(
            response,
            body,
            "Speaker diarization");


        using JsonDocument json =
            JsonDocument.Parse(
                body);


        JsonElement root =
            json.RootElement;


        DiarizedTranscript result =
            new();


        if (root.TryGetProperty(
                "text",
                out JsonElement fullText))
        {
            result.Text =
                fullText
                    .GetString()?
                    .Trim()
                ?? string.Empty;
        }


        if (root.TryGetProperty(
                "duration",
                out JsonElement duration)
            &&
            duration.ValueKind ==
                JsonValueKind.Number)
        {
            result.Duration =
                duration.GetDouble();
        }


        if (root.TryGetProperty(
                "segments",
                out JsonElement segments)
            &&
            segments.ValueKind ==
                JsonValueKind.Array)
        {
            int counter =
                1;


            foreach (
                JsonElement segment
                in segments.EnumerateArray())
            {
                DiarizedSegment item =
                    new()
                    {
                        Id =
                            $"S{counter++}"
                    };


                if (segment.TryGetProperty(
                        "speaker",
                        out JsonElement speaker))
                {
                    item.SpeakerLabel =
                        speaker.GetString()
                        ?? string.Empty;
                }


                if (segment.TryGetProperty(
                        "start",
                        out JsonElement start)
                    &&
                    start.ValueKind ==
                        JsonValueKind.Number)
                {
                    item.Start =
                        start.GetDouble();
                }


                if (segment.TryGetProperty(
                        "end",
                        out JsonElement end)
                    &&
                    end.ValueKind ==
                        JsonValueKind.Number)
                {
                    item.End =
                        end.GetDouble();
                }


                if (segment.TryGetProperty(
                        "text",
                        out JsonElement text))
                {
                    item.Text =
                        text.GetString()?.Trim()
                        ?? string.Empty;
                }


                if (!string.IsNullOrWhiteSpace(
                        item.SpeakerLabel)
                    &&
                    item.End > item.Start)
                {
                    result.Segments.Add(
                        item);
                }
            }
        }


        if (result.Segments.Count == 0)
        {
            throw new InvalidOperationException(
                "No speech segments were detected.");
        }


        int speakerCount =
            result.Segments
                .Select(
                    x => x.SpeakerLabel)
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Count();


        if (speakerCount < 2)
        {
            throw new InvalidOperationException(
                "Only one speaker was detected. " +
                "Make sure both doctor and patient are audible.");
        }


        return result;
    }


    // =========================================================
    // LOCAL
    //
    // Merge:
    //
    // A
    // A
    // A
    //
    // ↓
    //
    // One A turn
    // =========================================================

    private static List<AudioTurn>
        MergeSegmentsIntoTurns(
            DiarizedTranscript transcript)
    {
        List<DiarizedSegment> ordered =
            transcript.Segments
                .OrderBy(
                    x => x.Start)
                .ToList();


        List<AudioTurn> turns =
            new();


        foreach (
            DiarizedSegment segment
            in ordered)
        {
            if (turns.Count == 0)
            {
                turns.Add(
                    CreateTurn(
                        turns.Count + 1,
                        segment));

                continue;
            }


            AudioTurn previous =
                turns[^1];


            double gap =
                segment.Start -
                previous.End;


            bool sameSpeaker =
                previous.SpeakerLabel.Equals(
                    segment.SpeakerLabel,
                    StringComparison.OrdinalIgnoreCase);


            bool smallGap =
                gap <=
                MergeGapSeconds;


            if (sameSpeaker &&
                smallGap)
            {
                previous.End =
                    Math.Max(
                        previous.End,
                        segment.End);


                if (!string.IsNullOrWhiteSpace(
                        segment.Text))
                {
                    previous.DiarizedText =
                        JoinText(
                            previous.DiarizedText,
                            segment.Text);
                }
            }
            else
            {
                turns.Add(
                    CreateTurn(
                        turns.Count + 1,
                        segment));
            }
        }


        return turns;
    }


    private static AudioTurn CreateTurn(
        int number,
        DiarizedSegment segment)
    {
        return new AudioTurn
        {
            Id =
                $"T{number}",

            SpeakerLabel =
                segment.SpeakerLabel,

            Start =
                segment.Start,

            End =
                segment.End,

            DiarizedText =
                segment.Text
        };
    }


    // =========================================================
    // Turn transcription
    //
    // Max 4 requests parallel.
    // =========================================================

    private async Task TranscribeTurnsAsync(
        List<AudioTurn> turns,
        string originalAudioPath,
        double totalDuration,
        string tempDirectory,
        string language,
        CancellationToken cancellationToken)
    {
        using SemaphoreSlim semaphore =
            new(
                MaxParallelTranscriptions,
                MaxParallelTranscriptions);


        List<Task> tasks =
            turns
                .Select(
                    turn =>
                        ProcessSingleTurnAsync(
                            turn,
                            originalAudioPath,
                            totalDuration,
                            tempDirectory,
                            language,
                            semaphore,
                            cancellationToken))
                .ToList();


        await Task.WhenAll(
            tasks);
    }


    private async Task ProcessSingleTurnAsync(
        AudioTurn turn,
        string originalAudioPath,
        double totalDuration,
        string tempDirectory,
        string language,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(
            cancellationToken);


        try
        {
            // =================================================
            // Tiny padding.
            //
            // Important for endings like "بكح".
            // =================================================

            double start =
                Math.Max(
                    0,
                    turn.Start -
                    StartPaddingSeconds);


            double end =
                turn.End +
                EndPaddingSeconds;


            if (totalDuration > 0)
            {
                end =
                    Math.Min(
                        totalDuration,
                        end);
            }


            double duration =
                Math.Max(
                    0.20,
                    end - start);


            string outputPath =
                Path.Combine(
                    tempDirectory,
                    $"{turn.Id}.wav");


            // =================================================
            // FREE LOCAL CALL
            // =================================================

            await ExtractAudioTurnAsync(
                originalAudioPath,
                outputPath,
                start,
                duration,
                cancellationToken);


            byte[] turnBytes =
                await File.ReadAllBytesAsync(
                    outputPath,
                    cancellationToken);


            // =================================================
            // gpt-transcribe
            // =================================================

            string transcription =
                await TranscribeTurnAsync(
                    turnBytes,
                    $"{turn.Id}.wav",
                    language,
                    cancellationToken);


            // IMPORTANT:
            //
            // From this point on this value is immutable
            // as far as Luna/Terra are concerned.
            turn.OriginalText =
                !string.IsNullOrWhiteSpace(
                    transcription)
                    ? transcription.Trim()
                    : turn.DiarizedText.Trim();


            Console.WriteLine(
                $"{turn.Id} " +
                $"[{turn.Start:F2}-{turn.End:F2}] " +
                $"Speaker {turn.SpeakerLabel}: " +
                $"{turn.OriginalText}");
        }
        finally
        {
            semaphore.Release();
        }
    }


    // =========================================================
    // FFmpeg local audio crop
    // =========================================================

    private async Task ExtractAudioTurnAsync(
        string inputPath,
        string outputPath,
        double startSeconds,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo =
            new()
            {
                FileName =
                    _ffmpegPath,

                RedirectStandardError =
                    true,

                RedirectStandardOutput =
                    true,

                UseShellExecute =
                    false,

                CreateNoWindow =
                    true
            };


        // -i first gives more accurate seeking.
        startInfo.ArgumentList.Add(
            "-hide_banner");

        startInfo.ArgumentList.Add(
            "-loglevel");

        startInfo.ArgumentList.Add(
            "error");


        startInfo.ArgumentList.Add(
            "-y");


        startInfo.ArgumentList.Add(
            "-i");

        startInfo.ArgumentList.Add(
            inputPath);


        startInfo.ArgumentList.Add(
            "-ss");

        startInfo.ArgumentList.Add(
            startSeconds.ToString(
                "0.000",
                CultureInfo.InvariantCulture));


        startInfo.ArgumentList.Add(
            "-t");

        startInfo.ArgumentList.Add(
            durationSeconds.ToString(
                "0.000",
                CultureInfo.InvariantCulture));


        // Audio only
        startInfo.ArgumentList.Add(
            "-vn");


        // Mono voice
        startInfo.ArgumentList.Add(
            "-ac");

        startInfo.ArgumentList.Add(
            "1");


        // 16 kHz is enough for speech
        // and keeps uploads small.
        startInfo.ArgumentList.Add(
            "-ar");

        startInfo.ArgumentList.Add(
            "16000");


        // Lossless PCM WAV.
        startInfo.ArgumentList.Add(
            "-c:a");

        startInfo.ArgumentList.Add(
            "pcm_s16le");


        startInfo.ArgumentList.Add(
            outputPath);


        using Process process =
            new()
            {
                StartInfo =
                    startInfo
            };


        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Unable to run FFmpeg. " +
                "Make sure FFmpeg is installed and available in PATH.",
                ex);
        }


        string errorTask =
            await process.StandardError
                .ReadToEndAsync(
                    cancellationToken);


        await process.WaitForExitAsync(
            cancellationToken);


        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg failed while extracting an audio turn. " +
                $"ExitCode: {process.ExitCode}. " +
                $"Error: {errorTask}");
        }


        if (!File.Exists(
                outputPath))
        {
            throw new InvalidOperationException(
                "FFmpeg did not create the audio segment.");
        }


        FileInfo fileInfo =
            new(
                outputPath);


        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException(
                "Extracted audio segment is empty.");
        }
    }


    // =========================================================
    // gpt-transcribe for ONE turn
    // =========================================================

    private async Task<string>
        TranscribeTurnAsync(
            byte[] audioBytes,
            string fileName,
            string language,
            CancellationToken cancellationToken)
    {
        using MultipartFormDataContent form =
            new();


        using ByteArrayContent audioContent =
            new(
                audioBytes);


        audioContent.Headers.ContentType =
            new MediaTypeHeaderValue(
                "audio/wav");


        form.Add(
            audioContent,
            "file",
            fileName);


        form.Add(
            new StringContent(
                AccurateTranscriptionModel),
            "model");


        form.Add(
            new StringContent(
                "json"),
            "response_format");


        // =====================================================
        // Main language
        // =====================================================

        if (language == "en")
        {
            form.Add(
                new StringContent("en"),
                "language");
        }
        else
        {
            // Default is Arabic because consultation
            // is mainly Egyptian Arabic.
            form.Add(
                new StringContent("ar"),
                "language");
        }


        // =====================================================
        // Context
        // =====================================================

        string prompt =
            language == "en"
                ? GetEnglishTurnPrompt()
                : GetArabicTurnPrompt();


        form.Add(
            new StringContent(
                prompt),
            "prompt");


        form.Add(
            new StringContent("0"),
            "temperature");


        using HttpRequestMessage request =
            CreateAuthorizedRequest(
                HttpMethod.Post,
                "v1/audio/transcriptions");


        request.Content =
            form;


        using HttpResponseMessage response =
            await _httpClient.SendAsync(
                request,
                cancellationToken);


        string body =
            await response.Content
                .ReadAsStringAsync(
                    cancellationToken);


        ThrowIfFailed(
            response,
            body,
            "Turn transcription");


        using JsonDocument json =
            JsonDocument.Parse(
                body);


        if (!json.RootElement.TryGetProperty(
                "text",
                out JsonElement text))
        {
            return string.Empty;
        }


        return text
            .GetString()?
            .Trim()
            ?? string.Empty;
    }


    // =========================================================
    // CALL: Luna
    //
    // No Arabic generation.
    // =========================================================

    private async Task<ConversationLanguageResult>
        ProcessLanguageAsync(
            List<AudioTurn> turns,
            CancellationToken cancellationToken)
    {
        List<string> speakers =
            turns
                .Select(
                    x => x.SpeakerLabel)
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();


        StringBuilder input =
            new();


        foreach (
            AudioTurn turn
            in turns)
        {
            input.Append(
                turn.Id);

            input.Append('|');

            input.Append(
                turn.SpeakerLabel);

            input.Append('|');

            input.Append(
                FormatSeconds(turn.Start));

            input.Append('|');

            input.AppendLine(
                turn.OriginalText);
        }


        var schema =
            new
            {
                type =
                    "object",

                properties =
                    new
                    {
                        doctorSpeakerLabel =
                            new
                            {
                                type =
                                    "string"
                            },


                        patientSpeakerLabel =
                            new
                            {
                                type =
                                    "string"
                            },


                        speakerMappingConfidence =
                            new
                            {
                                type =
                                    "number",

                                minimum =
                                    0,

                                maximum =
                                    1
                            },


                        translations =
                            new
                            {
                                type =
                                    "array",

                                items =
                                    new
                                    {
                                        type =
                                            "object",

                                        properties =
                                            new
                                            {
                                                turnId =
                                                    new
                                                    {
                                                        type =
                                                            "string"
                                                    },

                                                englishText =
                                                    new
                                                    {
                                                        type =
                                                            "string"
                                                    }
                                            },

                                        required =
                                            new[]
                                            {
                                                "turnId",
                                                "englishText"
                                            },

                                        additionalProperties =
                                            false
                                    }
                            },


                        requiresDiagnosisReview =
                            new
                            {
                                type =
                                    "boolean"
                            },


                        diagnosisEvidenceTurnIds =
                            new
                            {
                                type =
                                    "array",

                                items =
                                    new
                                    {
                                        type =
                                            "string"
                                    }
                            },


                        summary =
                            new
                            {
                                type =
                                    "string"
                            }
                    },


                required =
                    new[]
                    {
                        "doctorSpeakerLabel",
                        "patientSpeakerLabel",
                        "speakerMappingConfidence",
                        "translations",
                        "requiresDiagnosisReview",
                        "diagnosisEvidenceTurnIds",
                        "summary"
                    },


                additionalProperties =
                    false
            };


        string instructions =
            """
            Process one medical consultation between exactly
            one Doctor and one Patient.

            Each line has:
            TURN_ID|SPEAKER_LABEL|TIME|ORIGINAL_TEXT

            ORIGINAL_TEXT is authoritative transcription.
            Never rewrite, correct, return, or modify it.

            Determine one global speaker mapping:
            one Doctor and one Patient.
            Never change the mapping per turn.

            Do not assume the first speaker is Doctor.

            Return an English translation for every TURN_ID.
            Preserve medical meaning, negation, numbers,
            medication names, tests, diagnoses, doses and units.

            requiresDiagnosisReview must be true if the Doctor
            states, suspects, excludes, discusses, or references
            a diagnosis/medical condition.

            When uncertain, set it to true.

            diagnosisEvidenceTurnIds should contain only the
            Doctor TURN_IDs relevant to possible diagnosis
            assessment.

            Produce a concise clinical summary in English
            using only information stated in the conversation.
            """;


        var requestBody =
            new
            {
                model =
                    LanguageModel,

                store =
                    false,

                reasoning =
                    new
                    {
                        effort =
                            "none"
                    },

                max_output_tokens =
                    6000,

                instructions =
                    instructions,

                input =
                    input.ToString(),

                text =
                    new
                    {
                        format =
                            new
                            {
                                type =
                                    "json_schema",

                                name =
                                    "medical_language_result",

                                strict =
                                    true,

                                schema =
                                    schema
                            }
                    }
            };


        string responseText =
            await SendStructuredResponseAsync(
                requestBody,
                cancellationToken);


        ConversationLanguageResult? result =
            JsonSerializer.Deserialize
                <ConversationLanguageResult>(
                    responseText,
                    JsonOptions);


        if (result == null)
        {
            throw new InvalidOperationException(
                "Unable to deserialize Luna result.");
        }


        // =====================================================
        // Validate speaker mapping
        // =====================================================

        bool doctorValid =
            speakers.Contains(
                result.DoctorSpeakerLabel,
                StringComparer.OrdinalIgnoreCase);


        bool patientValid =
            speakers.Contains(
                result.PatientSpeakerLabel,
                StringComparer.OrdinalIgnoreCase);


        bool same =
            result.DoctorSpeakerLabel.Equals(
                result.PatientSpeakerLabel,
                StringComparison.OrdinalIgnoreCase);


        if (!doctorValid ||
            !patientValid ||
            same)
        {
            throw new InvalidOperationException(
                "Invalid Doctor/Patient speaker mapping.");
        }


        return result;
    }


    // =========================================================
    // Build conversation
    //
    // IMPORTANT:
    // OriginalText = AudioTurn.OriginalText
    //
    // Luna CANNOT touch it.
    // =========================================================

    private static List<ConversationMessage>
        BuildConversation(
            List<AudioTurn> turns,
            ConversationLanguageResult languageResult)
    {
        Dictionary<string, string> translations =
            languageResult.Translations
                .GroupBy(
                    x => x.TurnId)
                .ToDictionary(
                    x => x.Key,
                    x => x.First().EnglishText);


        List<ConversationMessage> result =
            new();


        foreach (
            AudioTurn turn
            in turns)
        {
            string role =
                "Unknown";


            if (turn.SpeakerLabel.Equals(
                    languageResult.DoctorSpeakerLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                role =
                    "Doctor";
            }
            else if (
                turn.SpeakerLabel.Equals(
                    languageResult.PatientSpeakerLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                role =
                    "Patient";
            }


            translations.TryGetValue(
                turn.Id,
                out string? englishText);


            result.Add(
                new ConversationMessage
                {
                    SegmentId =
                        turn.Id,

                    SpeakerLabel =
                        turn.SpeakerLabel,

                    SpeakerRole =
                        role,

                    // =========================================
                    // NEVER FROM LUNA
                    // =========================================
                    OriginalText =
                        turn.OriginalText,

                    EnglishText =
                        string.IsNullOrWhiteSpace(
                            englishText)
                            ? turn.OriginalText
                            : englishText,

                    StartSeconds =
                        turn.Start,

                    EndSeconds =
                        turn.End
                });
        }


        return result;
    }


    // =========================================================
    // Terra input
    //
    // Only selected Doctor turns.
    // =========================================================

    private static string BuildDiagnosisInput(
        List<ConversationMessage> conversation,
        ConversationLanguageResult languageResult)
    {
        HashSet<string> evidenceIds =
            languageResult
                .DiagnosisEvidenceTurnIds
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);


        List<ConversationMessage> selected =
            conversation
                .Where(
                    x =>
                        x.SpeakerRole == "Doctor"
                        &&
                        evidenceIds.Contains(
                            x.SegmentId))
                .ToList();


        // Luna said diagnosis exists but returned
        // no evidence IDs:
        //
        // accuracy-first fallback → all doctor turns.
        if (selected.Count == 0 &&
            languageResult.RequiresDiagnosisReview)
        {
            selected =
                conversation
                    .Where(
                        x => x.SpeakerRole == "Doctor")
                    .ToList();
        }


        StringBuilder builder =
            new();


        foreach (
            ConversationMessage message
            in selected)
        {
            builder.AppendLine(
                $"TURN {message.SegmentId}");

            builder.AppendLine(
                $"Arabic: {message.OriginalText}");

            builder.AppendLine(
                $"English: {message.EnglishText}");

            builder.AppendLine();
        }


        return builder
            .ToString()
            .Trim();
    }


    // =========================================================
    // Terra
    //
    // Diagnosis + ICD only
    // =========================================================

    private async Task<DiagnosisReviewResult>
        ReviewDiagnosesAsync(
            string diagnosisInput,
            CancellationToken cancellationToken)
    {
        var schema =
            new
            {
                type =
                    "object",

                properties =
                    new
                    {
                        diagnoses =
                            new
                            {
                                type =
                                    "array",

                                items =
                                    new
                                    {
                                        type =
                                            "object",

                                        properties =
                                            new
                                            {
                                                diagnosisName =
                                                    new
                                                    {
                                                        type =
                                                            "string"
                                                    },


                                                icd10Code =
                                                    new
                                                    {
                                                        type =
                                                            "string"
                                                    },


                                                icd10Name =
                                                    new
                                                    {
                                                        type =
                                                            "string"
                                                    },


                                                status =
                                                    new
                                                    {
                                                        type =
                                                            "string",

                                                        @enum =
                                                            new[]
                                                            {
                                                                "Confirmed",
                                                                "Suspected",
                                                                "RuledOut",
                                                                "History"
                                                            }
                                                    },


                                                confidence =
                                                    new
                                                    {
                                                        type =
                                                            "number",

                                                        minimum =
                                                            0,

                                                        maximum =
                                                            1
                                                    },


                                                evidence =
                                                    new
                                                    {
                                                        type =
                                                            "string"
                                                    }
                                            },

                                        required =
                                            new[]
                                            {
                                                "diagnosisName",
                                                "icd10Code",
                                                "icd10Name",
                                                "status",
                                                "confidence",
                                                "evidence"
                                            },

                                        additionalProperties =
                                            false
                                    }
                            }
                    },


                required =
                    new[]
                    {
                        "diagnoses"
                    },


                additionalProperties =
                    false
            };


        string instructions =
            """
            Perform a focused medical documentation review.

            The supplied text contains Doctor speech only.

            Extract a diagnosis only when the Doctor explicitly
            states, suspects, excludes, or references the
            condition.

            Never diagnose independently from symptoms.

            Status:
            Confirmed = established by Doctor.
            Suspected = likely/possible/probable/suspected.
            RuledOut = Doctor explicitly excludes it.
            History = historical diagnosis.

            For every returned diagnosis provide:
            diagnosisName,
            ICD-10 code,
            ICD-10 English name,
            status,
            confidence,
            evidence.

            Never invent unsupported laterality, organism,
            severity, stage, subtype or complication.

            If specificity is not stated, prefer the appropriate
            valid unspecified/less-specific ICD-10 code.

            If no actual diagnosis exists, return an empty array.
            """;


        var requestBody =
            new
            {
                model =
                    MedicalModel,

                store =
                    false,

                reasoning =
                    new
                    {
                        effort =
                            "low"
                    },

                max_output_tokens =
                    2000,

                instructions =
                    instructions,

                input =
                    diagnosisInput,

                text =
                    new
                    {
                        format =
                            new
                            {
                                type =
                                    "json_schema",

                                name =
                                    "diagnosis_review",

                                strict =
                                    true,

                                schema =
                                    schema
                            }
                    }
            };


        string responseText =
            await SendStructuredResponseAsync(
                requestBody,
                cancellationToken);


        return
            JsonSerializer.Deserialize
                <DiagnosisReviewResult>(
                    responseText,
                    JsonOptions)
            ??
            new DiagnosisReviewResult();
    }


    // =========================================================
    // Responses API helper
    // =========================================================

    private async Task<string>
        SendStructuredResponseAsync(
            object requestBody,
            CancellationToken cancellationToken)
    {
        using HttpRequestMessage request =
            CreateAuthorizedRequest(
                HttpMethod.Post,
                "v1/responses");


        request.Content =
            JsonContent.Create(
                requestBody);


        using HttpResponseMessage response =
            await _httpClient.SendAsync(
                request,
                cancellationToken);


        string body =
            await response.Content
                .ReadAsStringAsync(
                    cancellationToken);


        ThrowIfFailed(
            response,
            body,
            "OpenAI structured analysis");


        using JsonDocument json =
            JsonDocument.Parse(
                body);


        string? text =
            ExtractResponseText(
                json.RootElement);


        if (string.IsNullOrWhiteSpace(
                text))
        {
            throw new InvalidOperationException(
                "OpenAI returned no structured result.");
        }


        return text;
    }


    // =========================================================
    // FFmpeg check
    // =========================================================

    private async Task EnsureFfmpegAvailableAsync(
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo =
            new()
            {
                FileName =
                    _ffmpegPath,

                RedirectStandardOutput =
                    true,

                RedirectStandardError =
                    true,

                UseShellExecute =
                    false,

                CreateNoWindow =
                    true
            };


        startInfo.ArgumentList.Add(
            "-version");


        using Process process =
            new()
            {
                StartInfo =
                    startInfo
            };


        try
        {
            process.Start();


            await process.WaitForExitAsync(
                cancellationToken);


            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException();
            }
        }
        catch
        {
            throw new InvalidOperationException(
                "FFmpeg was not found. " +
                "Install it using: " +
                "winget install -e --id Gyan.FFmpeg " +
                "then restart Visual Studio.");
        }
    }


    // =========================================================
    // HTTP
    // =========================================================

    private HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string uri)
    {
        HttpRequestMessage request =
            new(
                method,
                uri);


        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _apiKey);


        return request;
    }


    private static void ThrowIfFailed(
        HttpResponseMessage response,
        string body,
        string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }


        throw new InvalidOperationException(
            $"{operation} failed. " +
            $"Status: {(int)response.StatusCode}. " +
            $"Response: {body}");
    }


    // =========================================================
    // Responses output
    // =========================================================

    private static string? ExtractResponseText(
        JsonElement root)
    {
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
            new();


        foreach (
            JsonElement item
            in output.EnumerateArray())
        {
            if (!item.TryGetProperty(
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
                if (contentItem.TryGetProperty(
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


    // =========================================================
    // Prompts
    // =========================================================

    private static string GetArabicTurnPrompt()
    {
        return
            """
            هذه جملة قصيرة مقتطعة من محادثة طبية
            باللهجة المصرية بين طبيب ومريض.

            انسخ فقط الكلام المسموع كما تم نطقه.

            لا تلخص.
            لا تعيد الصياغة.
            لا تحول اللهجة المصرية إلى الفصحى.
            لا تضف كلمات.
            لا تحذف كلمات.
            لا تكمل الجملة من عندك.

            انتبه لنهاية الجملة ولا تحذف آخر كلمة.

            انتبه بدقة للكلمات المتشابهة صوتيًا مثل:
            شديدة / جديدة
            عالي / عادي

            اختر ما تسمعه في الصوت فقط.

            حافظ على المصطلحات الطبية الإنجليزية
            وأسماء الأدوية والتحاليل كما تم نطقها.
            """;
    }


    private static string GetEnglishTurnPrompt()
    {
        return
            """
            This is a short audio turn from a medical
            consultation.

            Transcribe exactly what is spoken.

            Do not summarize.
            Do not paraphrase.
            Do not add words.
            Do not remove words.
            Do not omit the final word.

            Preserve medication names, medical terminology,
            numbers, diagnoses, tests, doses and negation.
            """;
    }


    // =========================================================
    // Raw transcript
    // =========================================================

    private static string BuildRawTranscript(
        List<AudioTurn> turns)
    {
        return string.Join(
            Environment.NewLine,
            turns
                .OrderBy(
                    x => x.Start)
                .Select(
                    x => x.OriginalText));
    }


    // =========================================================
    // Utils
    // =========================================================

    private static string NormalizeLanguage(
        string? language)
    {
        return language?
            .Trim()
            .ToLowerInvariant()
            switch
        {
            "en" =>
                "en",

            "ar" =>
                "ar",

            // المشروع عربي في الأساس.
            _ =>
                "ar"
        };
    }


    private static string FormatSeconds(
        double seconds)
    {
        TimeSpan value =
            TimeSpan.FromSeconds(
                seconds);


        return
            $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    }


    private static string JoinText(
        string first,
        string second)
    {
        if (string.IsNullOrWhiteSpace(
                first))
        {
            return second.Trim();
        }


        if (string.IsNullOrWhiteSpace(
                second))
        {
            return first.Trim();
        }


        return
            $"{first.Trim()} {second.Trim()}";
    }


    private static string GetSafeExtension(
        string fileName)
    {
        string extension =
            Path.GetExtension(
                    fileName)
                .ToLowerInvariant();


        return extension switch
        {
            ".webm" => ".webm",
            ".mp3" => ".mp3",
            ".wav" => ".wav",
            ".m4a" => ".m4a",
            ".mp4" => ".mp4",
            ".mpeg" => ".mpeg",
            ".mpga" => ".mpga",
            ".ogg" => ".ogg",
            ".flac" => ".flac",

            _ => ".webm"
        };
    }


    private static string GetContentType(
        string fileName,
        string? browserContentType)
    {
        string extension =
            Path.GetExtension(
                    fileName)
                .ToLowerInvariant();


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

            ".mpeg" =>
                "audio/mpeg",

            ".mpga" =>
                "audio/mpeg",

            ".ogg" =>
                "audio/ogg",

            ".flac" =>
                "audio/flac",

            _ =>
                CleanContentType(
                    browserContentType)
        };
    }


    private static string CleanContentType(
        string? contentType)
    {
        if (string.IsNullOrWhiteSpace(
                contentType))
        {
            return
                "application/octet-stream";
        }


        return contentType
            .Split(';')[0]
            .Trim();
    }
}