namespace SslWebConsole.Models;

public enum CertPollStatus { Complete, Pending, Failed, Error }

public sealed record CertPollOutcome(string CommonName, CertPollStatus Status, string Detail);
