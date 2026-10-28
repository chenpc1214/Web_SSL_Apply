namespace SslWebConsole.Models;

public enum UploadOutcome { Success, Skipped, Failed }

public sealed record ValidationUploadResult(string CommonName, UploadOutcome Outcome, string Message);
