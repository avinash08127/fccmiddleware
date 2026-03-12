using static DPPMiddleware.Helpers.Enums;

namespace DPPMiddleware.Helpers
{
    public class FpMainStateHelper
    {
        public static string ToFriendlyString(string hexWithOptionalH)
        {
            string hex = hexWithOptionalH.Replace("H", "", StringComparison.OrdinalIgnoreCase);
            byte value = Convert.ToByte(hex, 16);

            return ((FpMainStateEnum)value) switch
            {
                FpMainStateEnum.Unconfigured => "Unconfigured",
                FpMainStateEnum.Closed => "Closed",
                FpMainStateEnum.Idle => "Idle",
                FpMainStateEnum.Error => "Error",
                FpMainStateEnum.Calling => "Calling",
                FpMainStateEnum.PreAuthorized => "PreAuthorized",
                FpMainStateEnum.Starting => "Starting",
                FpMainStateEnum.StartingPaused => "Starting_paused",
                FpMainStateEnum.StartingTerminated => "Starting_terminated",
                FpMainStateEnum.Fuelling => "Fuelling",
                FpMainStateEnum.FuellingPaused => "Fuelling_paused",
                FpMainStateEnum.FuellingTerminated => "Fuelling_terminated",
                FpMainStateEnum.Unavailable => "Unavailable",
                FpMainStateEnum.UnavailableAndCalling => "Unavailable_and_calling",
                _ => "Unknown"
            };
        }
    }
}
