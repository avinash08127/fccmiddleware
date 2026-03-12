using DPPMiddleware.Models;

namespace DPPMiddleware.Helpers
{
    public class Helper
    {
        public string ParseFpState(byte stateCode)
        {
            return stateCode switch
            {
                0x00 => "Idle",
                0x01 => "Authorized",
                0x02 => "Fuelling",
                0x03 => "Finished",
                0x04 => "Error",
                _ => "Unknown"
            };
        }

        public string ParsePaymentType(byte value)
        {
            return value switch
            {
                0x00 => "Cash",
                0x01 => "Card",
                0x02 => "MobilePay",
                _ => "Unknown"
            };
        }

       

    }
}
