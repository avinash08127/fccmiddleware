using System.Data;

namespace DPPMiddleware.Helpers
{
    public class TableHelper
    {
        public static DataTable BuildFpAvailableSmsTable(dynamic supp)
        {
            var table = new DataTable();
            table.Columns.Add("SmsId", typeof(string));
            table.Columns.Add("SmId", typeof(string));

            if (supp?.FpAvailableSms != null)
            {
                var sms = supp.FpAvailableSms;
                table.Rows.Add(sms.SmsId, sms.SmId);
            }
            return table;
        }

        public static DataTable BuildFpAvailableGradesTable(dynamic supp)
        {
            var table = new DataTable();
            table.Columns.Add("Count", typeof(string));
            table.Columns.Add("GradeId", typeof(string));

            if (supp?.FpAvailableGrades != null)
            {
                var grades = supp.FpAvailableGrades;
                foreach (var grade in grades.GradeIds)
                {
                    table.Rows.Add(grades.Count, grade);
                }
            }
            return table;
        }

        public static DataTable BuildFpNozzleIdTable(dynamic supp)
        {
            var table = new DataTable();
            table.Columns.Add("NozzleId", typeof(string));
            table.Columns.Add("AsciiCode", typeof(string));
            table.Columns.Add("AsciiChar", typeof(string));

            if (supp?.NozzleId != null)
            {
                var nozzle = supp.NozzleId;
                table.Rows.Add(nozzle.Id, nozzle.AsciiCode, nozzle.AsciiChar);
            }
            return table;
        }

        public static DataTable BuildMinPresetValuesTable(dynamic supp)
        {
            var table = new DataTable();
            table.Columns.Add("FcGradeId", typeof(string));
            table.Columns.Add("MinMoneyPreset_e", typeof(string));
            table.Columns.Add("MinVolPreset_e", typeof(string));

            if (supp?.MinPresetValues != null)
            {
                foreach (var preset in supp.MinPresetValues)
                {
                    table.Rows.Add(preset.FcGradeId, preset.MinMoneyPreset_e, preset.MinVolPreset_e);
                }
            }
            return table;
        }
    }
}
