using DPPMiddleware.Models;

namespace DPPMiddleware.Helpers.ParserHelper
{
    public static class FpSupTransBufStatusParser
    {
        public static FpSupTransBufStatusResponse Parse(string[] tokens)
        {
            var resp = new FpSupTransBufStatusResponse();
            int offset = 0;

            resp.FpId = tokens[offset++]; 
            int noTrans = Convert.ToInt32(tokens[offset++], 16); 

            for (int i = 0; i < noTrans; i++)
            {
                var t = new TransInSupBuffer
                {
                    TransSeqNo = string.Join("", tokens.Skip(offset).Take(2)), 
                    SmId = tokens[offset + 2],
                    TransLockId = tokens[offset + 3]
                };
                offset += 4;

                string maskHex = tokens[offset++];
                int maskVal = Convert.ToInt32(maskHex, 16);

                t.TransInfoMask = DecodeTransInfoMask(maskHex, maskVal);

                if (HasMoneyDue(maskVal))
                {
                    t.MoneyDue =Convert.ToDecimal(string.Join("", tokens.Skip(offset).Take(3))); 
                    t.MoneyDue = Convert.ToDecimal(string.Join("", tokens.Skip(offset).Take(3))); 
                    offset += 3;
                }

                if (HasVolIncluded(maskVal))
                {
                    t.Vol = Convert.ToDecimal(string.Join("", tokens.Skip(offset).Take(3)));
                    offset += 3;

                    if (offset < tokens.Length && tokens[offset].Length == 2)
                    {
                        t.FcGradeId = tokens[offset++];
                    }
                }

                resp.TransInSupBuffer.Add(t);
            }

            return resp;
        }

  
        private static TransInfoMaskDto DecodeTransInfoMask(string hex, int value)
        {
            var dto = new TransInfoMaskDto
            {
                Value = hex + "H",
                Bits = new Dictionary<string, int>()
            };

            dto.Bits.Add("StoredTrans", (value & 0x01) != 0 ? 1 : 0);
            dto.Bits.Add("ErrorTrans", (value & 0x02) != 0 ? 1 : 0);
            dto.Bits.Add("TransGreaterThanMinLimit", (value & 0x04) != 0 ? 1 : 0);
            dto.Bits.Add("PrepayModeUsed", (value & 0x08) != 0 ? 1 : 0);
            dto.Bits.Add("VolOrVolAndGradeIdIncluded", (value & 0x10) != 0 ? 1 : 0);
            dto.Bits.Add("FinalizeNotAllowed", (value & 0x20) != 0 ? 1 : 0);
            dto.Bits.Add("MoneyDueIsNegative", (value & 0x40) != 0 ? 1 : 0);
            dto.Bits.Add("MoneyDueIncluded", (value & 0x80) != 0 ? 1 : 0);

            return dto;
        }

        private static bool HasMoneyDue(int mask) => (mask & 0x80) != 0;
        private static bool HasVolIncluded(int mask) => (mask & 0x10) != 0;
    }

}
