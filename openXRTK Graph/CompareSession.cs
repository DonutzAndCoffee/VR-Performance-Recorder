namespace openXRTK_Graph;

/// <summary>
/// A loaded session used in the comparison window (A, B, C).
/// </summary>
public sealed record CompareSession(string Key, string Label, IList<CsvDataPoint> Data, SessionMetadata? Meta);
