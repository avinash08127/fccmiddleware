using DPPMiddleware.Models;

namespace DPPMiddleware.Interface
{
    public interface IParserService
    {
        DppMessage ParseSupervised(string[] payload, string classification, bool solicited);
        DppMessage ParseUnsupervised(byte[] payload, string classification, bool solicited);
        DppMessage ParsePeripherals(byte[] payload, string classification);
    }
}
