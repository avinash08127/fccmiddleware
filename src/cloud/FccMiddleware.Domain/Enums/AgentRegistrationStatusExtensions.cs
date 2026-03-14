namespace FccMiddleware.Domain.Enums;

public static class AgentRegistrationStatusExtensions
{
    public static bool IsSuspended(this AgentRegistrationStatus status) =>
        status is AgentRegistrationStatus.PENDING_APPROVAL or AgentRegistrationStatus.QUARANTINED;

    public static string ToDeviceAccessErrorCode(this AgentRegistrationStatus status) =>
        status switch
        {
            AgentRegistrationStatus.PENDING_APPROVAL => "DEVICE_PENDING_APPROVAL",
            AgentRegistrationStatus.QUARANTINED => "DEVICE_QUARANTINED",
            _ => "DEVICE_DECOMMISSIONED"
        };

    public static string ToDeviceAccessErrorMessage(this AgentRegistrationStatus status) =>
        status switch
        {
            AgentRegistrationStatus.PENDING_APPROVAL =>
                "This device is pending operator approval before it can receive configuration or commands.",
            AgentRegistrationStatus.QUARANTINED =>
                "This device has been quarantined by a registration policy and must be reviewed by an operator.",
            _ => "This device has been decommissioned."
        };
}
