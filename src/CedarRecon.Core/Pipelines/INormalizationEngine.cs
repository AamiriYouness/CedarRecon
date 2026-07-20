using CedarRecon.Core.Common;
using CedarRecon.Core.Entities;

namespace CedarRecon.Core.Pipelines;

/// <summary>
/// Normalization engine: transforms RawTransaction → Transaction.
/// All methods must be idempotent: normalize(normalize(x)) == normalize(x).
/// </summary>
public interface INormalizationEngine
{
    Result<Transaction> Normalize(RawTransaction raw);

    string NormalizeReference(string raw);
    decimal NormalizeAmount(string raw);
    DateTimeOffset NormalizeDate(string raw);
    string NormalizeDescription(string raw);
    string NormalizeIban(string raw);
    string NormalizeCurrency(string raw);
}
