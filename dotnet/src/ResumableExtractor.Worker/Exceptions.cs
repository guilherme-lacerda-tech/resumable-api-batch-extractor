namespace ResumableExtractor.Worker;

public sealed class ApiContractException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class RecoverableApiException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class OutputIntegrityException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class ExtractionInterruptedException(string message, ExtractionStats? stats = null)
    : Exception(message)
{
    public ExtractionStats? Stats { get; } = stats;
}
