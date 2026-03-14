namespace FccMiddleware.Contracts.AgentControl;

/// <summary>
/// Frozen audit event names for the agent-control/bootstrap-token/FCM workstream.
/// Keep values stable because portal filters, analytics, and compliance queries depend on them.
/// </summary>
public static class AgentControlAuditEventTypes
{
    public const string BootstrapTokenUsed = "BOOTSTRAP_TOKEN_USED";
    public const string AgentCommandCreated = "AGENT_COMMAND_CREATED";
    public const string AgentCommandAcked = "AGENT_COMMAND_ACKED";
    public const string AgentCommandFailed = "AGENT_COMMAND_FAILED";
    public const string AgentCommandExpired = "AGENT_COMMAND_EXPIRED";
    public const string AgentCommandCancelled = "AGENT_COMMAND_CANCELLED";
    public const string AgentPushHintSent = "AGENT_PUSH_HINT_SENT";
    public const string AgentPushHintFailed = "AGENT_PUSH_HINT_FAILED";
    public const string AgentInstallationUpdated = "AGENT_INSTALLATION_UPDATED";
    public const string SuspiciousRegistrationHeld = "SUSPICIOUS_REGISTRATION_HELD";
    public const string SuspiciousRegistrationApproved = "SUSPICIOUS_REGISTRATION_APPROVED";
    public const string SuspiciousRegistrationRejected = "SUSPICIOUS_REGISTRATION_REJECTED";
}
