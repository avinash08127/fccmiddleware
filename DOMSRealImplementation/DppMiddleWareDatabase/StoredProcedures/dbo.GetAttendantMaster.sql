USE [pumaenergy]
GO
/****** Object:  StoredProcedure [dbo].[GetAttendantMaster]    Script Date: 06/03/2026 09:46:08 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
ALTER proc [dbo].[GetAttendantMaster]
as
begin

select * from AttendantMaster
end
