namespace CodexProvisioning;

/// <summary>Bounds and operations shared by local enrollment and the registry boundary.</summary>
public static class WorkerEnrollmentProtocol
{
    public const int AcknowledgementVersion = 1;
    public static bool ValidOperation(string operation) => operation is "enroll" or "rotate" or "recover" or "associate";
    public static bool ValidToken(string? token) => token is { Length: >= 32 and <= 4096 } &&
        !token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
}

public sealed record WorkerEnrollmentAcknowledgement(int ContractVersion, string WorkerId);
