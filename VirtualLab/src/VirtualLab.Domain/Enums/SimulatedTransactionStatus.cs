namespace VirtualLab.Domain.Enums;

public enum SimulatedTransactionStatus
{
    Created = 0,
    ReadyForDelivery = 1,
    Delivered = 2,
    Acknowledged = 3,
    Failed = 4,
}
