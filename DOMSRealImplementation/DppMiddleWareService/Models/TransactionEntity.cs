namespace DPPMiddleware.Models
{
    public class TransactionEntity
    {
        public int Id { get; set; }
        public string? TransactionId { get; set; } = string.Empty;
        public string? ReferenceId { get; set; } = string.Empty;  
        public string? HexMessage { get; set; } = string.Empty;
        public string? ParsedJson { get; set; } = string.Empty;
        public string? Port { get; set; } = string.Empty;
        public bool IsSynced { get; set; }
        public int RetryCount { get; set; }
        public DateTime? LastAttempt { get; set; }
        public string? OdooAckId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public EventDetailEntity? EventDetails { get; set; }
    }

    public class EventDetailEntity
    {
        public int Id { get; set; }

        public string? TransactionId { get; set; } = string.Empty;
        public string? ReferenceId { get; set; } = string.Empty;

        public string? Port { get; set; } = string.Empty;
        public string? Classification { get; set; } = string.Empty;
        public string? SubCode { get; set; }

        public int? FpId { get; set; }
        public string? State { get; set; }
        public decimal? Vol { get; set; }
        public decimal? Money { get; set; }
        public string? PaymentType { get; set; }
        public decimal? TotalVol { get; set; }
        public decimal? TotalMoney { get; set; }
        public int? NozzleId { get; set; }

        public string? TerminalId { get; set; }
        public string? Version { get; set; }
        public int? NotesAccepted { get; set; }

        public int? DispenserId { get; set; }
        public string? Model { get; set; }
        public int? FcId { get; set; }
        public decimal? NewPrice { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }


    }

    public class SocketViewModel
    {
        public FpStatusResponse? FpStatusResponse { set; get; }
 

    }

    public class FpEntity
    {
        public FpStatusResponse FpStatusResponse { get; set; }
    }
}
