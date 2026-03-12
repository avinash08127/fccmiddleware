namespace VirtualLab.Domain.Enums;

public enum PreAuthSessionStatus
{
    Pending = 0,
    Authorized = 1,
    Completed = 2,
    Cancelled = 3,
    Expired = 4,
    Failed = 5,
    Dispensing = 6,
}
