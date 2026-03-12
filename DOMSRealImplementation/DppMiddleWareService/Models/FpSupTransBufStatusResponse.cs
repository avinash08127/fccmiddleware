namespace DPPMiddleware.Models
{
    public class FpSupTransBufStatusResponse
    {
        public string? FpId { get; set; } = "";
        public List<TransInSupBuffer> TransInSupBuffer { get; set; } = new();
    }

    public class TransInSupBuffer
    {
        public string? TransSeqNo { get; set; } = "";
        public string? SmId { get; set; } = "";
        public string? TransLockId { get; set; } = "";
        public TransInfoMaskDto TransInfoMask { get; set; } = new();
        public decimal? MoneyDue { get; set; }
        public decimal? Vol { get; set; }
        public string? FcGradeId { get; set; }
        public decimal? UnitPrice { get; set; }
    }

    public class TransInfoMaskDto
    {
        public string? Value { get; set; } = "";
        public Dictionary<string, int> Bits { get; set; } = new();
    }

    public class FpSupDataDto
    {
        public string? FpId { get; set; }
        public string? TransSeqNo { get; set; }
        public decimal? Volume { get; set; }
        public decimal? MoneyDue { get; set; }
        public string? GradeId { get; set; }
        public decimal? UnitPrice { get; set; }
        public string? Money { get; set; }
    }

    public class TransactionDiscard
    {
        public string TransactionId { get; set; }
        public string Status { get; set; }
        public bool IsDiscard { get; set; }
    }
}
