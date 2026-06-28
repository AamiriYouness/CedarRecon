using CedarRecon.Domain.Enums;

namespace CedarRecon.Domain.Entities;

/// <summary>
/// Represents a source file to be ingested. Stream is consumed exactly once
/// by the ingester and disposed by the pipeline after use.
/// </summary>
public sealed record SourceFile(
    string Name,
    FileFormat Format,
    Stream Content,
    long FileSizeBytes = 0)
    : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
    }
}
