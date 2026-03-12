namespace DPPMiddleware.Models
{
    public class CommonDTOs
    {
        public class FpStatus
        {
            public int FpId { get; set; } 
            public string? State { get; set; } 
        }

        public class FpFuellingData 
        {
            public int FpId { get; set; } 
            public decimal Vol { get; set; } 
            public decimal Money { get; set; } 
        }
        
        public class FpTransactionCompleted 
        { 
            public int FpId { get; set; } 
            public decimal Vol { get; set; } 
            public decimal Money { get; set; } 
            public string? PaymentType { get; set; } 
        }
        public class FpTotals 
        { 
            public int FpId { get; set; } 
            public decimal TotalVol { get; set; } 
            public decimal TotalMoney { get; set; } 
        }


    }
}
