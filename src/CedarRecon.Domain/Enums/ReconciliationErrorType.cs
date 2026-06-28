namespace CedarRecon.Domain.Enums;

public enum ReconciliationErrorType
{
    // File level
    FileNotFound,
    FileCorrupted,
    UnsupportedFormat,
    FileTooLarge,

    // Parsing
    InvalidHeader,
    MalformedRow,
    InvalidAmount,
    InvalidDate,

    // Pipeline
    NormalizationFailed,
    MatchingTimeout,

    // Source / Transport
    FtpConnectionFailed,
    SftpConnectionFailed,
    QueueConnectionLost,
    BlobStorageUnavailable
}
