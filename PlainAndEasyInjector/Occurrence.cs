namespace PlainAndEasyInjector;

/// Which matches a literal edit (SetInt, SetString, ...) changes.
/// Matches are numbered in code order starting at 1: the method itself first, then its iterator/async
/// state machine, then its lambdas and local functions.
///   patcher.SetInt<Calculator>("Limits", 1, 5);                            // every 1
///   patcher.SetInt<Calculator>("Limits", 1, 5, Occurrence.First);          // only the first 1
///   patcher.SetInt<Calculator>("Limits", 1, 5, Occurrence.Last);           // only the last 1
///   patcher.SetInt<Calculator>("Limits", 1, 5, Occurrence.Specific, 2, 4); // only the 2nd and the 4th 1
public enum Occurrence
{
    /// Every match.
    All,
    /// Only the first match.
    First,
    /// Only the last match.
    Last,
    /// Only the matches whose numbers you pass after it (1 = first match, 2 = second match, ...).
    Specific
}
