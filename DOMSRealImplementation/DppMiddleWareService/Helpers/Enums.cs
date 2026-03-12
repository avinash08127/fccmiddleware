namespace DPPMiddleware.Helpers
{
    public class Enums
    {
        public enum FpMainStateEnum : byte
        {
            Unconfigured = 0x00,
            Closed = 0x01,
            Idle = 0x02,
            Error = 0x03,
            Calling = 0x04,
            PreAuthorized = 0x05,
            Starting = 0x06,
            StartingPaused = 0x07,
            StartingTerminated = 0x08,
            Fuelling = 0x09,
            FuellingPaused = 0x0A,
            FuellingTerminated = 0x0B,
            Unavailable = 0x0C,
            UnavailableAndCalling = 0x0D
        }


    }
}
