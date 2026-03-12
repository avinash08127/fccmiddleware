using DPPMiddleware.Interface;
using DPPMiddleware.Models;

namespace DPPMiddleware.ForecourtTcpWorker
{
    public class DppHexParser
    {
        private readonly IParserService _parserService;

        public DppHexParser(IParserService parserService)
        {
            _parserService = parserService;
        }


        public  DppMessage Parse(byte[] payload, int port)
       {
            var hex = BitConverter.ToString(payload).Replace("-", " ");
            var payloads = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var classification = DppMessageClassifier.Classify(payloads);

            switch (port)
            {
                case 5001: 
                    return _parserService.ParseSupervised(payloads, classification, true);

                case 5002: 
                    return _parserService.ParseSupervised(payloads, classification, true);

                case 5003: 
                    return new DppMessage
                    {
                        Name = "FallbackConsole",
                        SubCode = "00H",
                        Solicited = true,
                        Data = new { RawHex = payloads }
                    };

                case 5004: 
                    return _parserService.ParseUnsupervised(payload, classification, true);

                case 5005: 
                    return _parserService.ParseUnsupervised(payload, classification, false);

                case 5006:
                    return _parserService.ParsePeripherals(payload, classification);

                default:
                    return new DppMessage
                    {
                        Name = "Unknown",
                        SubCode = "00H",
                        Data = new { RawHex = hex }
                    };
            }
        }
    }
}
