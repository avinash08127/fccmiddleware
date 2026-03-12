using System.Text.Json.Serialization;

public class FpStatusResponse
{
    public string? FpId { get; set; }
    public string? SmId { get; set; }
    public FpMainStateDto? FpMainState { get; set; }
    public string? FpSubStates { get; set; }
    public string? FpLockId { get; set; }
    public string? FcGradeId { get; set; }
    public SupplementalStatus Supplemental { get; set; } = new();
}
public class FpMainStateDto
{
    public string? Value { get; set; }   
    public string? Name { get; set; }   
}

public class SupplementalStatus
{
    public string? FpSubStates2 { get; set; }
    public FpAvailableSmsDto? FpAvailableSms { get; set; }
    public FpAvailableGradesDto? FpAvailableGrades { get; set; }
    public string? FpGradeOptionNo { get; set; }
    public string? FuellingDataVol_e { get; set; }
    public string? FuellingDataMon_e { get; set; }
    public string? AttendantAccountId { get; set; }
    public string? FpBlockingStatus { get; set; }
    public NozzleIdDto? NozzleId { get; set; }
    public string? FpOperationModeNo { get; set; }
    public string? PgId { get; set; }
    public string? NozzleTagReaderId { get; set; }
    public string? FpSubStates3 { get; set; }
    public string? FpAlarmStatus { get; set; }
    public List<MinPresetValue> MinPresetValues { get; set; } = new();
    public string? FpSubStates4 { get; set; }
    public string? ReferenceId { get; set; }
}
public class NozzleIdDto
{
    public string? Id { get; set; }             
    public string? AsciiCode { get; set; }     
    public string? AsciiChar { get; set; }     
}

public class FpAvailableGradesDto
{
    public string? Count { get; set; }          
    public List<string>? GradeIds { get; set; }  
}
public class FpAvailableSmsDto
{
    public string? SmsId { get; set; }   
    public string? SmId { get; set; }      
}
public class MinPresetValue
{
    public string? FcGradeId { get; set; }
    public string? MinMoneyPreset_e { get; set; }
    public string? MinVolPreset_e { get; set; }
}




public class FpSatus
{
    public int FpId { get; set; }
    public string FpStatus { get; set; }
    public int NozzleId { get; set; }
    public string Volume { get; set; }
    public string Money { get; set; }

    public string AttendantId { get; set; }
    public int FpGradeOptionNo { get; set; }
    public bool IsOnline { get; set; }


}

public class FuelPumpStatusDto
{
    public int pump_number { get; set; }
    public int nozzle_number { get; set; }
    public string status { get; set; }
    public decimal reading { get; set; }
    public decimal volume { get; set; } 
    public decimal litre { get; set; }
    public decimal amount { get; set; } 
    public string attendant { get; set; }

    public int count { get; set; }
    public int FpGradeOptionNo { get; set; }
    public decimal? unit_price { get; set; }
    public bool isOnline { get; set; }

}

public class AttendantLimit
{
    public string AttendantId { get; set; } = string.Empty;
    public string Limit { get; set; }
}


public class FpLimitDto
{
    public int FpId { get; set; }
    public int MaxLimit { get; set; }
    public int CurrentCount { get; set; }
    public string? Status { get; set; }
    public bool IsAllowed { get; set; }

}




