namespace SslWebConsole.Models;

public sealed record PfxResult(string Domain, bool Success, string Message, string? PfxFile = null, string? FriendlyName = null);
