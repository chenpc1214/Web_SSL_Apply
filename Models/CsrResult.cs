namespace SslWebConsole.Models;

public sealed class CsrResult
{
    public required string CommonName { get; init; }
    public bool Success { get; init; }
    /// <summary>true=資料夾已存在而中止（需要使用者按「刪除並重新開始」）。</summary>
    public bool AlreadyExists { get; init; }
    public string Message { get; init; } = "";
    public string? WorkDir { get; init; }
    public string? KeyFile { get; init; }
    public string? CsrFile { get; init; }
}
