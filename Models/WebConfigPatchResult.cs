namespace SslWebConsole.Models;

public enum WebConfigOutcome { Patched, AlreadyPatched, Restored, Skipped, Failed }

public sealed record WebConfigPatchResult(string CommonName, WebConfigOutcome Outcome, string Message);
