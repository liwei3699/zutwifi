namespace ZutWifi.Diagnostics;

public sealed record TransactionRecord(string Stage, string Method, string Url, int? Status,
    string? Location, string? Summary, string? Failure);
