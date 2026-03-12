namespace DPPMiddleware.Models
{
    public class Payment
    {
        public class DispenserInstallData 
        { 
            public int DispenserId { get; set; } 
            public string? Model { get; set; } 
        }
       
        public class EptInfo 
        { 
            public string? TerminalId { get; set; } 
            public string? Version { get; set; } 
        }

        public class EptBnaReport 
        { 
            public string? TerminalId { get; set; } 
            public int NotesAccepted { get; set; }
        }
       
        public class ChangeFcPriceSet 
        { 
            public int FcId { get; set; }
            public decimal NewPrice { get; set; } 
        }

    }
}
