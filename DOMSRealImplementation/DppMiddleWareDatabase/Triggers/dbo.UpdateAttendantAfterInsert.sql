USE [pumaenergy]
GO
/****** Object:  StoredProcedure [dbo].[sp_UpsertGradePrice_Bulk]    Script Date: 27/02/2026 16:30:17 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO


CREATE TRIGGER UpdateAttendantAfterInsert ON PumpTransactions AFTER INSERT AS BEGIN    
-- Declare a variable to store the FpId     
DECLARE @FpId INT;     
-- Select the FpId from the inserted row 
SELECT @FpId = PumpId FROM inserted;    
-- Increment the TransactionCount column by 1 for the corresponding FpId in the Attendant table    
UPDATE AttendantMaster     SET LimiLeftCount = ISNULL(LimiLeftCount, 0) + 1     WHERE FpId = @FpId;      
-- Optional: Add additional logic or error handling if needed
END;
Go;
