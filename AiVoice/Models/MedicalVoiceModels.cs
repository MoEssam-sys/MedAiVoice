namespace AiVoice.Models;


// ============================================================
// Diarization
// ============================================================

public class DiarizedTranscript
{
    public string Text { get; set; } = string.Empty;

    public double Duration { get; set; }

    public List<DiarizedSegment> Segments { get; set; }
        = new();
}


public class DiarizedSegment
{
    public string Id { get; set; } = string.Empty;

    // A / B
    public string SpeakerLabel { get; set; } = string.Empty;

    public double Start { get; set; }

    public double End { get; set; }

    // نستخدمه للمقارنة فقط
    // وليس OriginalText النهائي.
    public string Text { get; set; } = string.Empty;
}


// ============================================================
// Audio Turn
//
// بعد دمج Segments المتتالية لنفس Speaker.
// ============================================================

public class AudioTurn
{
    public string Id { get; set; } = string.Empty;

    public string SpeakerLabel { get; set; } = string.Empty;

    public double Start { get; set; }

    public double End { get; set; }

    // كلام الـDiarizer.
    // فقط كـfallback/debugging.
    public string DiarizedText { get; set; } = string.Empty;

    // النص الحقيقي اللي رجع مباشرة
    // من gpt-transcribe لهذا الجزء من الصوت.
    public string OriginalText { get; set; } = string.Empty;
}


// ============================================================
// Final Result
// ============================================================

public class MedicalVoiceResult
{
    public string RawTranscript { get; set; } = string.Empty;

    public string DoctorSpeakerLabel { get; set; } = string.Empty;

    public string PatientSpeakerLabel { get; set; } = string.Empty;

    public double SpeakerMappingConfidence { get; set; }

    public List<ConversationMessage> Conversation { get; set; }
        = new();

    public List<DiagnosisResult> Diagnoses { get; set; }
        = new();

    public string Summary { get; set; } = string.Empty;
}


// ============================================================
// Conversation
// ============================================================

public class ConversationMessage
{
    public string SegmentId { get; set; } = string.Empty;

    public string SpeakerLabel { get; set; } = string.Empty;

    public string SpeakerRole { get; set; } = string.Empty;

    // IMPORTANT:
    //
    // ده جاي من gpt-transcribe مباشرة.
    // Luna لن تعدله.
    public string OriginalText { get; set; } = string.Empty;

    public string EnglishText { get; set; } = string.Empty;

    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; }
}


// ============================================================
// Diagnosis
// ============================================================

public class DiagnosisResult
{
    public string DiagnosisName { get; set; } = string.Empty;

    public string Icd10Code { get; set; } = string.Empty;

    public string Icd10Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public string Evidence { get; set; } = string.Empty;
}


// ============================================================
// Luna
// ============================================================

public class ConversationLanguageResult
{
    public string DoctorSpeakerLabel { get; set; } = string.Empty;

    public string PatientSpeakerLabel { get; set; } = string.Empty;

    public double SpeakerMappingConfidence { get; set; }

    public List<TurnTranslation> Translations { get; set; }
        = new();

    public bool RequiresDiagnosisReview { get; set; }

    // IDs للـTurns اللي فيها كلام متعلق بالتشخيص.
    public List<string> DiagnosisEvidenceTurnIds { get; set; }
        = new();

    public string Summary { get; set; } = string.Empty;
}


public class TurnTranslation
{
    public string TurnId { get; set; } = string.Empty;

    // English فقط.
    // العربي غير موجود هنا عمدًا.
    public string EnglishText { get; set; } = string.Empty;
}


// ============================================================
// Terra
// ============================================================

public class DiagnosisReviewResult
{
    public List<DiagnosisResult> Diagnoses { get; set; }
        = new();
}