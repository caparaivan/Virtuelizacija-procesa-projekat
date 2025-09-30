using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Common;

namespace SolarClient
{
    public class CsvReader
    {
        private const double Sentinel = 32767.0;

        public IEnumerable<PvSample> ReadSamples(string path, int limitN, string rejectsPath)
        {
            using (var reader = new StreamReader(path))
            using (var rejWriter = new StreamWriter(rejectsPath))
            {
                rejWriter.WriteLine("RowIndex,Reason,Raw");
                string header = reader.ReadLine();

                if (header != null)
                {
                    header = header.TrimStart(',');
                }
                if (header == null || !header.StartsWith("DAY,HOUR,ACPWRT", StringComparison.OrdinalIgnoreCase))
                    yield break;

                int rowIndex = 0;
                while (!reader.EndOfStream && rowIndex < limitN)
                {
                    string line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (line.StartsWith(","))
                        line = line.TrimStart(',');

                    var parts = line.Split(',');

                    PvSample sample = null;

                    try
                    {
                        string yearDoyStr = parts[1];  // 2023335
                        int yearDoy = int.Parse(yearDoyStr, CultureInfo.InvariantCulture);
                        int year = yearDoy / 1000;  // 2023
                        int doy = yearDoy % 1000;   // 335
                        if (doy < 1 || doy > 366)
                        {
                            throw new FormatException("Invalid DOY");
                        }
                        DateTime dt = new DateTime(year, 1, 1).AddDays(doy - 1);
                        string fullDay = dt.ToString("yyyy-M-d", CultureInfo.InvariantCulture);  // 2023-12-1

                        string fullHour = parts[2];  // 00:05:00

                        sample = new PvSample
                        {
                            RowIndex = rowIndex + 1,
                            Day = fullDay,          // 2023-12-1
                            Hour = fullHour,        // 00:05:00
                            AcPwrt = ToNullable(parts[3]),
                            DcVolt = ToNullable(parts[4]),
                            Temper = ToNullable(parts[6]),
                            Vl1to2 = ToNullable(parts[7]),
                            Vl2to3 = ToNullable(parts[8]),
                            Vl3to1 = ToNullable(parts[9]),
                            AcCur1 = ToNullable(parts[10]),
                            AcVlt1 = ToNullable(parts[13])
                        };
                    }
                    catch (Exception ex)
                    {
                        rejWriter.WriteLine($"{rowIndex + 1},\"{ex.Message}\",\"{line}\"");
                    }

                    if (sample != null)
                    {
                        yield return sample;
                        rowIndex++;
                    }
                }
            }
        }

        private double? ToNullable(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return null;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            {
                if (Math.Abs(val - Sentinel) < 1e-9)
                    return null;
                return val;
            }
            return null;
        }
    }
}