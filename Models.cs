namespace BugFixer;

public sealed class BugDetails
{
    public int? Id { get; set; }
    public string? Title { get; set; }
    public string? State { get; set; }
    public string? AssignedTo { get; set; }
    public string? WorkItemType { get; set; }
    public DateTimeOffset? CreatedDate { get; set; }
}

public sealed class FixResult
{
    public string? Verdict { get; set; }          // FIXED | NOT_REPRODUCIBLE | DATA_ISSUE | OBSOLETE | NEEDS_HUMAN
    public string? Environment { get; set; }      // DATA_ISSUE only: QA | DEV
    public string? Summary { get; set; }
    public string? Evidence { get; set; }
    public string? RootCause { get; set; }
    public List<string>? Changes { get; set; }
    public List<string>? Tests { get; set; }
    public string? Risk { get; set; }
    public string? PrTitle { get; set; }
    public string? PrDescription { get; set; }
}

public sealed class AdoWriteResult
{
    public bool? Ok { get; set; }
    public string? Error { get; set; }
}

public sealed class PublishResult
{
    public int? PrId { get; set; }
    public string? PrUrl { get; set; }
    public string? Error { get; set; }
}

public static class Outcome
{
    public const string PrCreated = "PR_CREATED";
    public const string NotReproducible = "NOT_REPRODUCIBLE";
    public const string NeedsHuman = "NEEDS_HUMAN";
    public const string DataIssue = "DATA_ISSUE";
    public const string Obsolete = "OBSOLETE";
    public const string Failed = "FAILED";
    public const string Skipped = "SKIPPED";
}
