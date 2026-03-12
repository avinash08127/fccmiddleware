namespace DPPMiddleware.ForecourtTcpWorker
{
    public class DppMessageClassifier
    {
        public static string Classify(string[] payload)
        {

            if (payload == null || payload.Length < 4)
                return "Unknown";

            if (IsKeepAlive(payload))
                return "KeepAlive";

            string extc = payload[1];
            string subCode = payload[2];

            switch (extc)
            {
                case "95": return ClassifyFpStatus(subCode);
                case "96": return ClassifyFpSupTransBufStatus(subCode);
                case "06": return "FpAuthorize";
                case "07": return "FpFuellingData";
                case "08": return "FpTransactionCompleted";
                case "09": return "FpTotals";
                default: return "Unknown";
            }
        }

        private static bool IsKeepAlive(string[] tokens)
        {
            return tokens.Length == 3 && tokens[0] == "02" && tokens[2] == "03";
        }

        private static string ClassifySupervised(byte subCode)
        {
            return subCode switch
            {
                0x95 => "FpStatus",
                0x06 => "FpAuthorize",
                0x07 => "FpFuellingData",
                0x08 => "FpTransactionCompleted",
                0x09 => "FpTotals",
                _ => "UnknownSupervised"
            };
        }

        private static string ClassifyUnsupervised(byte subCode)
        {
            return subCode switch
            {
                0x10 => "FpUnsupervisedTransaction",
                0x11 => "FpUnsupervisedFuellingData",
                _ => "UnknownUnsupervised"
            };
        }

        private static string ClassifyPeripherals(byte subCode)
        {
            return subCode switch
            {
                0x20 => "DispenserInstallData",
                0x21 => "EptInfo",
                0x22 => "EptBnaReport",
                0x23 => "ChangeFcPriceSet",
                _ => "UnknownPeripheral"
            };
        }
        private static string ClassifyFpStatus(string subCode)
        {
            return subCode switch
            {
                "00" => "FpStatus_0",
                "01" => "FpStatus_1",
                "02" => "FpStatus_2",
                "03" => "FpStatus_3",
                _ => "FpStatus_Unknown"
            };
        }

        private static string ClassifyFpSupTransBufStatus(string subCode)
        {
            return subCode switch
            {
                "00" => "FpSupTransBufStatus_0",
                "01" => "FpSupTransBufStatus_1",                
                "03" => "FpSupTransBufStatus_3",
                _ => "FpSupTransBufStatus_Unknown"
            };
        }

    }
}
