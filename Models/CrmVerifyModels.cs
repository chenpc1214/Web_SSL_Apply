namespace SslWebConsole.Models;

public sealed record CrmDetail(string Ip, string FtpUser, string FtpPass, string Address, string City, string Locality, bool Parsed);

public sealed record CrmFieldDiff(string Field, string CurrentValue, string CrmValue);

public enum CrmVerifyOutcome { Ok, Diff, NotFound, ParseFailed, Error, LoginRequired }

public sealed record CrmVerifyResult(
    string CaseNo,
    string CommonName,
    CrmVerifyOutcome Outcome,
    List<CrmFieldDiff> Diffs,
    string? Note = null,
    string? DetailUrl = null);
