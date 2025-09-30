using Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Virtuelizacija.Analytics;
using System.Configuration;
using System.ServiceModel;

namespace Virtuelizacija.Services
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class SolarService : ISolarService, IDisposable
    {
        private const double Sentinel = 32767.0;
        private const double Epsilon = 1e-6;

        private static string Quote(string t) => "\"" + t.Replace("\"", "\"\"") + "\"";

        private static string F(double? v) => v.HasValue ? v.Value.ToString("G17", CultureInfo.InvariantCulture) : "";

        private static double? ToNullable(double? v) => (v.HasValue && Math.Abs(v.Value - Sentinel) < 1e-9) ? null : v;

        // pragovi iz app.config
        private double OverTempThreshold = 75.0;
        private double VoltageImbalancePct = 10.0;
        private int PowerFlatlineWindow = 5;
        private double PowerSpikeThreshold = 1.5;
        private double DcSagDelta = 50.0;
        private double LowEfficiencyRatio = 0.6;

        // eventi
        public event EventHandler OnTransferStarted;
        public event EventHandler<int> OnSampleReceived;
        public event EventHandler OnTransferCompleted;
        public event EventHandler<WarningEventArgs> OnWarningRaised;

        // sesije
        private PvMeta _meta;
        private string _rootDir;
        private string _sessionDir;
        private string _sessionCsvPath;
        private string _rejectsCsvPath;
        private StreamWriter _okWriter;
        private StreamWriter _rejWriter;
        private int _received;
        private int _lastRowIndex = -1;

        // za  analitiku
        private double? _prevDcVolt;
        private double? _prevAcPwrt;
        private Queue<double?> _powerHistory = new Queue<double?>();
        private readonly List<string> _warnings = new List<string>();

        public ServerAck StartSession(PvMeta meta)
        {
            if (meta == null) return Fail("Meta is null");

            _meta = meta;
            LoadThresholdsFromConfig();

            var date = meta.SessionDateUtc?.Date ?? DateTime.UtcNow.Date;
            _rootDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            _sessionDir = Path.Combine(_rootDir, meta.PlantId, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(_sessionDir);

            _sessionCsvPath = Path.Combine(_sessionDir, "session.csv");
            _rejectsCsvPath = Path.Combine(_sessionDir, "rejects.csv");

            _okWriter = new StreamWriter(new FileStream(_sessionCsvPath, FileMode.Create, FileAccess.Write, FileShare.Read));
            _rejWriter = new StreamWriter(new FileStream(_rejectsCsvPath, FileMode.Create, FileAccess.Write, FileShare.Read));

            // headers
            _okWriter.WriteLine("RowIndex,Day,Hour,AcPwrt,DcVolt,Temper,Vl1to2,Vl2to3,Vl3to1,AcCur1,AcVlt1");
            _rejWriter.WriteLine("RowIndex,Reason,Raw");

            _okWriter.Flush();
            _rejWriter.Flush();

            _received = 0;
            _lastRowIndex = -1;
            _prevDcVolt = null;
            _prevAcPwrt = null;
            _powerHistory.Clear();
            _warnings.Clear();

            OnTransferStarted?.Invoke(this, EventArgs.Empty);

            return Ok("Session started");
        }

        public ServerAck PushSample(PvSample sample)
        {
            if (_meta == null) return Fail("Session not started");
            if (sample == null) return Fail("Sample is null");

            // monotonost i limit
            if (sample.RowIndex <= _lastRowIndex)
            {
                Reject(sample, $"RowIndex not monotonic (last={_lastRowIndex})");
                return Ack();
            }
            if (_received >= _meta.RowLimitN)
            {
                Reject(sample, $"RowLimitN reached ({_meta.RowLimitN})");
                return Ack();
            }

            // sentinel za null
            NormalizeSentinels(sample);

            var reason = Validate(sample);
            if (reason != null)
            {
                Reject(sample, reason);
                return Ack();
            }

            CheckDcAnalytics(sample);
            CheckPowerAnalytics(sample);
            CheckVoltageImbalance(sample);
            CheckEfficiencyAndTemp(sample);

            WriteOk(sample);

            _lastRowIndex = sample.RowIndex;
            _received++;

            OnSampleReceived?.Invoke(this, _received);

            // progres (Zadatak 7)
            Console.WriteLine($"Prenos u toku... Primljeno {_received} redova ({Math.Round(100.0 * _received / _meta.RowLimitN, 2)}%)");

            return Ack();
        }

        public ServerAck EndSession()
        {
            if (_meta == null) return Fail("Session not started");

            Dispose();

            OnTransferCompleted?.Invoke(this, EventArgs.Empty);

            return new ServerAck
            {
                Success = true,
                Message = "Session completed",
                ReceivedCount = _received,
                PercentOfLimit = _meta.RowLimitN > 0 ? Math.Round(100.0 * _received / _meta.RowLimitN, 2) : 100.0
            };
        }

        public List<string> GetWarnings()
        {
            return new List<string>(_warnings);
        }

        private void NormalizeSentinels(PvSample s)
        {
            s.AcPwrt = ToNullable(s.AcPwrt);
            s.DcVolt = ToNullable(s.DcVolt);
            s.Temper = ToNullable(s.Temper);
            s.Vl1to2 = ToNullable(s.Vl1to2);
            s.Vl2to3 = ToNullable(s.Vl2to3);
            s.Vl3to1 = ToNullable(s.Vl3to1);
            s.AcCur1 = ToNullable(s.AcCur1);
            s.AcVlt1 = ToNullable(s.AcVlt1);
        }

        private string Validate(PvSample s)
        {
            if (string.IsNullOrEmpty(s.Day) || !DateTime.TryParseExact(s.Day, "yyyy-M-d", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return "Invalid Day format (expected yyyy-M-d)";

            if (string.IsNullOrEmpty(s.Hour) || !TimeSpan.TryParseExact(s.Hour, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out _))
                return "Invalid Hour format (expected HH:mm:ss)";

            if (s.AcPwrt.HasValue && s.AcPwrt.Value < 0) return "AcPwrt < 0";
            if (s.DcVolt.HasValue && s.DcVolt.Value < 0) return "DcVolt < 0";
            if (s.Temper.HasValue && s.Temper.Value < -50) return "Temper too low";

            if (s.AcVlt1.HasValue && s.AcVlt1.Value <= 0) return "AcVlt1 <= 0";
            if (s.AcCur1.HasValue && s.AcCur1.Value < 0) return "AcCur1 < 0";
            if (s.Vl1to2.HasValue && s.Vl1to2.Value <= 0) return "Vl1to2 <= 0";
            if (s.Vl2to3.HasValue && s.Vl2to3.Value <= 0) return "Vl2to3 <= 0";
            if (s.Vl3to1.HasValue && s.Vl3to1.Value <= 0) return "Vl3to1 <= 0";

            return null;
        }

        private void CheckDcAnalytics(PvSample s)
        {
            // warnings za DCVOLT
            if (s.DcVolt.HasValue && _prevDcVolt.HasValue)
            {
                var delta = s.DcVolt.Value - _prevDcVolt.Value;
                if (Math.Abs(delta) > DcSagDelta)
                {
                    RaiseWarning(WarningType.DCSagWarning, $"DCVOLT sudden change Δ={delta:F2} > {DcSagDelta}", s.RowIndex);
                }
            }

            if (!s.DcVolt.HasValue || (s.DcVolt.HasValue && s.DcVolt.Value == 0))
            {
                if (s.AcPwrt.HasValue && s.AcPwrt.Value > 0)
                {
                    RaiseWarning(WarningType.DcFaultWarning, "DCVOLT missing or zero while ACPWRT > 0", s.RowIndex);
                }
            }

            if (s.DcVolt.HasValue) _prevDcVolt = s.DcVolt.Value;
        }

        private void CheckPowerAnalytics(PvSample s)
        {
            if (!s.AcPwrt.HasValue) return;

            if (_prevAcPwrt.HasValue && _prevAcPwrt.Value > 0)
            {
                var delta = Math.Abs(s.AcPwrt.Value - _prevAcPwrt.Value);
                if (delta > _prevAcPwrt.Value * PowerSpikeThreshold)
                {
                    RaiseWarning(WarningType.PowerSpikeWarning, $"Power spike Δ={delta:F2} > {_prevAcPwrt.Value * PowerSpikeThreshold:F2}", s.RowIndex);
                }
            }

            _powerHistory.Enqueue(s.AcPwrt.Value);
            if (_powerHistory.Count > PowerFlatlineWindow)
            {
                _powerHistory.Dequeue();
            }

            if (_powerHistory.Count == PowerFlatlineWindow && _powerHistory.All(p => Math.Abs(p.Value - _powerHistory.First().Value) < Epsilon))
            {
                RaiseWarning(WarningType.PowerFlatlineWarning, $"Power flatline over {PowerFlatlineWindow} samples", s.RowIndex);
            }

            _prevAcPwrt = s.AcPwrt.Value;
        }

        private void CheckVoltageImbalance(PvSample s)
        {
            if (!s.Vl1to2.HasValue || !s.Vl2to3.HasValue || !s.Vl3to1.HasValue) return;

            var voltages = new[] { s.Vl1to2.Value, s.Vl2to3.Value, s.Vl3to1.Value };
            var avg = voltages.Average();
            var maxDiff = voltages.Max() - voltages.Min();
            var imbalancePct = (maxDiff / avg) * 100;

            if (imbalancePct > VoltageImbalancePct)
            {
                RaiseWarning(WarningType.VoltageImbalanceWarning, $"Voltage imbalance {imbalancePct:F2}% > {VoltageImbalancePct}%", s.RowIndex);
            }
        }

        private void CheckEfficiencyAndTemp(PvSample s)
        {
            if (s.AcPwrt.HasValue && s.AcVlt1.HasValue && s.AcCur1.HasValue && s.AcVlt1.Value > 0)
            {
                var approx = s.AcVlt1.Value * s.AcCur1.Value;
                if (approx > 0)
                {
                    var ratio = s.AcPwrt.Value / approx;
                    if (ratio < LowEfficiencyRatio)
                    {
                        RaiseWarning(WarningType.LowEfficiencyWarning, $"Low efficiency ratio={ratio:F2} < {LowEfficiencyRatio}", s.RowIndex);
                    }
                }
            }

            if (s.Temper.HasValue && s.Temper.Value > OverTempThreshold)
            {
                RaiseWarning(WarningType.OverTempWarning, $"Over temperature {s.Temper.Value:F1} > {OverTempThreshold}", s.RowIndex);
            }
        }

        private void RaiseWarning(WarningType type, string message, int rowIndex)
        {
            var args = new WarningEventArgs(type, message, rowIndex);
            OnWarningRaised?.Invoke(this, args);
            _warnings.Add($"[{type}] {message} (Row {rowIndex})");
        }

        private void WriteOk(PvSample s)
        {
            _okWriter.WriteLine(string.Join(",",
                s.RowIndex,
                s.Day,
                s.Hour,
                F(s.AcPwrt),
                F(s.DcVolt),
                F(s.Temper),
                F(s.Vl1to2),
                F(s.Vl2to3),
                F(s.Vl3to1),
                F(s.AcCur1),
                F(s.AcVlt1)
            ));
            _okWriter.Flush();
        }

        private void Reject(PvSample s, string reason)
        {
            var raw = $"Day={s.Day};Hour={s.Hour};AcPwrt={s.AcPwrt};DcVolt={s.DcVolt};Temper={s.Temper};" +
                      $"Vl1to2={s.Vl1to2};Vl2to3={s.Vl2to3};Vl3to1={s.Vl3to1};AcCur1={s.AcCur1};AcVlt1={s.AcVlt1}";
            _rejWriter.WriteLine($"{s.RowIndex},{Quote(reason)},{Quote(raw)}");
            _rejWriter.Flush();
        }

        private ServerAck Ack()
        {
            return new ServerAck
            {
                Success = true,
                Message = "OK",
                ReceivedCount = _received,
                PercentOfLimit = _meta.RowLimitN > 0 ? Math.Round(100.0 * _received / _meta.RowLimitN, 2) : 100.0
            };
        }

        private ServerAck Ok(string msg) => new ServerAck { Success = true, Message = msg, ReceivedCount = _received, PercentOfLimit = _meta?.RowLimitN > 0 ? Math.Round(100.0 * _received / _meta.RowLimitN, 2) : 0 };

        private ServerAck Fail(string msg) => new ServerAck { Success = false, Message = msg, ReceivedCount = _received, PercentOfLimit = 0 };

        private void LoadThresholdsFromConfig()
        {
            try
            {
                var cfg = ConfigurationManager.AppSettings;
                if (double.TryParse(cfg["OverTempThreshold"], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) OverTempThreshold = t;
                if (double.TryParse(cfg["VoltageImbalancePct"], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) VoltageImbalancePct = v;
                if (int.TryParse(cfg["PowerFlatlineWindow"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)) PowerFlatlineWindow = w;
                if (double.TryParse(cfg["PowerSpikeThreshold"], NumberStyles.Float, CultureInfo.InvariantCulture, out var ps)) PowerSpikeThreshold = ps;
                if (double.TryParse(cfg["DcSagDelta"], NumberStyles.Float, CultureInfo.InvariantCulture, out var sd)) DcSagDelta = sd;
                if (double.TryParse(cfg["LowEfficiencyRatio"], NumberStyles.Float, CultureInfo.InvariantCulture, out var lr)) LowEfficiencyRatio = lr;
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            _okWriter?.Flush();
            _rejWriter?.Flush();

            _okWriter?.Dispose();
            _rejWriter?.Dispose();

            _okWriter = null;
            _rejWriter = null;

            _powerHistory.Clear();
            _warnings.Clear();
        }

    }
}
