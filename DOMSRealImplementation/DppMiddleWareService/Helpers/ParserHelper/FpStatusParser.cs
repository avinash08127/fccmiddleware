namespace DPPMiddleware.Helpers.ParserHelper
{
    public static class FpStatusParser
    {
        public static FpStatusResponse Parse(string[] tokens)
        {
            var resp = new FpStatusResponse();
            int offset = 0;

            resp.FpId = tokens[offset++];
            resp.SmId = tokens[offset++];
            resp.FpMainState = new FpMainStateDto
            {
                Value = tokens[offset] + "H",
                Name = FpMainStateHelper.ToFriendlyString(tokens[offset])
            };
            offset++;
            resp.FpSubStates = tokens[offset++];
            resp.FpLockId = tokens[offset++];
            resp.FcGradeId = tokens[offset++];

            int noSuppl = Convert.ToInt32(tokens[offset++], 16); 
            for (int i = 0; i < noSuppl; i++)
            {
                string parId = tokens[offset++];
                int parLen = Convert.ToInt32(tokens[offset++], 16);

                string[] parDataTokens = tokens.Skip(offset).Take(parLen).ToArray();
                offset += parLen;

                ApplySupplParameter(resp.Supplemental, parId, parDataTokens);
            }

            return resp;
        }

        private static void ApplySupplParameter(SupplementalStatus supp, string parId, string[] dataTokens)
        {
            switch (parId)
            {
                case "01": supp.FpSubStates2 = string.Join(" ", dataTokens); break;
                case "02":
                    supp.FpAvailableSms = new FpAvailableSmsDto
                    {
                        SmsId = dataTokens.ElementAtOrDefault(0),
                        SmId = dataTokens.ElementAtOrDefault(1)
                    };
                    break;
                case "03":
                    supp.FpAvailableGrades = new FpAvailableGradesDto
                    {
                        Count = dataTokens.ElementAtOrDefault(0),
                        GradeIds = dataTokens.Skip(1).ToList()
                    };
                    break;
                case "04": supp.FpGradeOptionNo = string.Join(" ", dataTokens); break;
                case "05": supp.FuellingDataVol_e = string.Join(" ", dataTokens); break;
                case "06": supp.FuellingDataMon_e = string.Join(" ", dataTokens); break;
                case "07": supp.AttendantAccountId = string.Join(" ", dataTokens); break;
                case "08": supp.FpBlockingStatus = string.Join(" ", dataTokens); break;
                case "09":
                    var id = dataTokens.ElementAtOrDefault(0);
                    var asciiHex = dataTokens.ElementAtOrDefault(1);
                    supp.NozzleId = new NozzleIdDto
                    {
                        Id = id,
                        AsciiCode = asciiHex,
                        AsciiChar = !string.IsNullOrEmpty(asciiHex)
                                        ? System.Text.Encoding.ASCII.GetString(new byte[] { Convert.ToByte(asciiHex, 16) })
                                        : null
                    };
                    break;
                case "10": supp.FpOperationModeNo = string.Join(" ", dataTokens); break;
                case "11": supp.PgId = string.Join(" ", dataTokens); break;
                case "12": supp.NozzleTagReaderId = string.Join(" ", dataTokens); break;
                case "13": supp.FpSubStates3 = string.Join(" ", dataTokens); break;
                case "14": supp.FpAlarmStatus = string.Join(" ", dataTokens); break;
                case "15":
                    supp.MinPresetValues.Add(new MinPresetValue
                    {
                        FcGradeId = dataTokens.ElementAtOrDefault(0) ?? "",
                        MinMoneyPreset_e = string.Join(" ", dataTokens.Skip(1).Take(4)),
                        MinVolPreset_e = string.Join(" ", dataTokens.Skip(5).Take(4))
                    }); break;
                case "16": supp.FpSubStates4 = string.Join(" ", dataTokens); break;
                default: break;
            }
        }
    }
}
