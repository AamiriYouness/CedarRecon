namespace CedarRecon.Domain.Enums;

public enum MatchStrategy
{
    Exact,      // reference + amount + date all match → confidence 1.0
    Fuzzy,      // reference matches + tolerance applied → confidence 0.7–0.99
    Aggregate   // split or consolidated detection → confidence 0.5–0.69
}
