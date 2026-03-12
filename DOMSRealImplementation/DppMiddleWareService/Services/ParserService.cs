using DPPMiddleware.Models;
using static DPPMiddleware.Models.CommonDTOs;
using static DPPMiddleware.Models.Payment;
using static DPPMiddleware.Models.FpUnsupervisedTransaction;
using DPPMiddleware.Interface;
using DPPMiddleware.Helpers;
using static DPPMiddleware.Helpers.Helper;
using DPPMiddleware.Helpers.ParserHelper;


namespace DPPMiddleware.Services
{
    public class ParserService : IParserService
    {
        private readonly Helper _helper;

        public ParserService(Helper helper)
        {
            _helper = helper;
        }
        public DppMessage ParseSupervised(string[] payload, string classification, bool solicited)
        {
            string subCodeHex = payload.Length > 3 ? payload[3] + "H" : "00H";

            switch (classification)
            {
                case "FpStatus_0":
                    return new DppMessage
                    {
                        Name = "FpStatus_resp",
                        CallType= "FpStatus_0",
                        EXTC = payload[1],
                        SubCode = "00H",
                        Solicited = solicited,
                        Data = new
                        {
                            FpId = payload[4],
                            SmId = payload[5],
                            FpMainState = new
                            {
                                Value = payload[6] + "H",
                                Name = FpMainStateHelper.ToFriendlyString(payload[6])
                            },
                            FpSubStates = payload[7],  
                            FpLockId = payload[8]
                        }
                    };

                case "FpStatus_1":
                    return new DppMessage
                    {
                        Name = "FpStatus_resp",
                        CallType = "FpStatus_1",
                        EXTC = payload[1],
                        SubCode = "01H",
                        Solicited = solicited,
                        Data = new
                        {
                            FpId = payload[4],
                            SmId = payload[5],
                            FpMainState = new FpMainStateDto
                            {
                                Value = payload[6] + "H",
                                Name = FpMainStateHelper.ToFriendlyString(payload[6])
                            },
                            FpSubStates = payload[7],
                            FpLockId = payload[8],
                            FcGradeId = payload[9]
                        }
                    };

                case "FpStatus_2":
                    return new DppMessage
                    {
                        Name = "FpStatus_resp",
                        CallType = "FpStatus_2",
                        EXTC = payload[1],
                        SubCode = "02H",
                        Solicited = solicited,
                        Data = new
                        {
                            FpId = payload[4],
                            SmId = payload[5],
                            FpMainState = new FpMainStateDto
                            {
                                Value = payload[6] + "H",
                                Name = FpMainStateHelper.ToFriendlyString(payload[6])
                            },
                            FpSubStates = payload[7],
                            FpLockId = payload[8],
                            FpDescriptor = string.Join(" ", payload.Skip(9)) 
                        }
                    };

                case "FpStatus_3":
                    var status3Tokens = payload.Skip(3).ToArray();
                    var status3 = FpStatusParser.Parse(status3Tokens);
                    return new DppMessage
                    {
                        Name = "FpStatus_resp",
                        CallType = "FpStatus_3",
                        EXTC = payload[1],
                        SubCode = "03H",
                        Solicited = solicited,
                        Data = status3 
                    };

                case "FpSupTransBufStatus_1":
                    var transTokens = payload.Skip(3).ToArray();
                    var transResp = FpSupTransBufStatusParser.Parse(transTokens);
                    return new DppMessage
                    {
                        Name = "FpSupTransBufStatus_resp",
                        CallType = "FpSupTransBufStatus_1",
                        EXTC = payload[1],
                        SubCode = payload[3] + "H",
                        Solicited = solicited,
                        Data = transResp
                    };

                case "FpFuellingData":
                    return new DppMessage
                    {
                        Name = "FpFuellingData_resp",
                        SubCode = subCodeHex,
                        Solicited = solicited,
                        Data = new FpFuellingData
                        {
                            FpId = Convert.ToByte(payload[4], 16),
                            Vol = BitConverter.ToInt32(payload.Skip(5).Take(4).Select(x => Convert.ToByte(x, 16)).ToArray(), 0) / 100m,
                            Money = BitConverter.ToInt32(payload.Skip(9).Take(4).Select(x => Convert.ToByte(x, 16)).ToArray(), 0) / 100m
                        }
                    };

                case "FpTransactionCompleted":
                    return new DppMessage
                    {
                        Name = "FpTransactionCompleted_resp",
                        SubCode = subCodeHex,
                        Solicited = solicited,
                        Data = new FpTransactionCompleted
                        {
                            FpId = Convert.ToByte(payload[4], 16),
                            Vol = BitConverter.ToInt32(payload.Skip(5).Take(4).Select(x => Convert.ToByte(x, 16)).ToArray(), 0) / 100m,
                            Money = BitConverter.ToInt32(payload.Skip(9).Take(4).Select(x => Convert.ToByte(x, 16)).ToArray(), 0) / 100m,
                            PaymentType = _helper.ParsePaymentType(Convert.ToByte(payload[13], 16))
                        }
                    };

                case "FpTotals":
                    return new DppMessage
                    {
                        Name = "FpTotals_resp",
                        SubCode = subCodeHex,
                        Solicited = solicited,
                        Data = new FpTotals
                        {
                            FpId = Convert.ToByte(payload[4], 16),
                            TotalVol = BitConverter.ToInt32(payload.Skip(5).Take(4).Select(x => Convert.ToByte(x, 16)).ToArray(), 0) / 100m,
                            TotalMoney = BitConverter.ToInt32(payload.Skip(9).Take(4).Select(x => Convert.ToByte(x, 16)).ToArray(), 0) / 100m
                        }
                    };

                default:
                    return new DppMessage
                    {
                        Name = "UnknownSupervised",
                        SubCode = subCodeHex,
                        Solicited = solicited,
                        Data = new { Raw = string.Join(" ", payload) }
                    };
            }
        }




        public DppMessage ParseUnsupervised(byte[] payload, string classification, bool solicited)
        {
            switch (classification)
            {
                case "FpUnsupervisedTransaction":
                    return new DppMessage { Name = "FpUnsupervisedTransaction_resp", Solicited = solicited, Data = new FpUnsupervisedTransaction { FpId = payload[2], Vol = 8.5m, Money = 15.0m } };

                default:
                    return new DppMessage { Name = "UnknownUnsupervised", Solicited = solicited, Data = new { Raw = BitConverter.ToString(payload) } };
            }
        }

        public DppMessage ParsePeripherals(byte[] payload, string classification)
        {
            switch (classification)
            {
                case "DispenserInstallData":
                    return new DppMessage { Name = "DispenserInstallData_resp", Solicited = false, Data = new DispenserInstallData { DispenserId = payload[2], Model = "DOMS5000" } };

                case "EptInfo":
                    return new DppMessage { Name = "EptInfo_resp", Data = new EptInfo { TerminalId = "T001", Version = "1.0.0" } };

                case "EptBnaReport":
                    return new DppMessage { Name = "EptBnaReport_resp", Data = new EptBnaReport { TerminalId = "T001", NotesAccepted = 10 } };

                case "ChangeFcPriceSet":
                    return new DppMessage { Name = "ChangeFcPriceSet_resp", Data = new ChangeFcPriceSet { FcId = payload[2], NewPrice = 1.50m } };

                default:
                    return new DppMessage { Name = "UnknownPeripheral", Data = new { Raw = BitConverter.ToString(payload) } };
            }
        }


       
    }
}
