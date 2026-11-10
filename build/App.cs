// BatteryCheck.exe - native Windows dashboard for the battery health check.
//
// The analysis itself is BatteryCheck.ps1 (embedded as a resource, extracted to %LOCALAPPDATA%\BatteryCheck
// and run hidden). Every test is stored as JSON + HTML in a "Reports" folder next to the exe; this app is
// the UI on top of that folder: run tests, import reports, browse history, edit ticket details, export CSV.
//
//   BatteryCheck.exe                 open the dashboard
//   BatteryCheck.exe --cli [args]    run one test in a console window (args go to BatteryCheck.ps1)
//
// Compiled with the C# 5 compiler that ships with .NET Framework 4.x (see Build.ps1) - keep to C# 5 syntax.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Battery Check")]
[assembly: AssemblyDescription("Battery health check and dashboard")]
[assembly: AssemblyProduct("Battery Check")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

namespace BatteryCheck
{
    // =========================================================================================
    // Entry point
    // =========================================================================================
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                Runner.AppDir = Extract();
                Runner.ReportsDir = GetReportsDir();

                // Diagnostics: run a basic test through exactly the dashboard's code path and write the outcome to a file
                if (args.Length > 1 && args[0] == "--selftest")
                {
                    // optional third argument: cancel after that many milliseconds (tests the Cancel button's path)
                    string result;
                    List<string> steps = new List<string>();
                    System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource();
                    if (args.Length > 2) cts.CancelAfter(int.Parse(args[2]));
                    try { result = "OK " + Runner.RunAsync(new List<string> { "-Days", "14" }, new Dictionary<string, object>(), st => { lock (steps) steps.Add(st); }, cts.Token).Result; }
                    catch (Exception ex) { Exception inner = ex.InnerException ?? ex; result = (inner is OperationCanceledException ? "CANCELLED " : "FAILED ") + inner.Message; }
                    lock (steps) result += "\r\nSteps=" + string.Join(" | ", steps.ToArray());
                    File.WriteAllText(args[1], result + "\r\nIs64BitProcess=" + Environment.Is64BitProcess + "\r\nAdmin=" + Runner.IsAdmin() + "\r\nPowerShell=" + Runner.PowerShellPath() + "\r\nReports=" + Runner.ReportsDir);
                    return 0;
                }

                if (args.Length > 0 && (args[0] == "--cli" || args[0] == "/cli"))
                {
                    StringBuilder cmd = new StringBuilder("-NoProfile -ExecutionPolicy Bypass -File ");
                    cmd.Append(Runner.Q(Runner.Script)).Append(" -OutputDir ").Append(Runner.Q(Runner.ReportsDir)).Append(" -PauseAtEnd");
                    for (int i = 1; i < args.Length; i++) cmd.Append(' ').Append(Runner.Q(args[i]));
                    ProcessStartInfo psi = new ProcessStartInfo(Runner.PowerShellPath(), cmd.ToString());
                    psi.UseShellExecute = true;   // its own console window
                    Process p = Process.Start(psi);
                    p.WaitForExit();
                    return p.ExitCode;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Theme.Init();
                Settings.Load();
                MainForm form = new MainForm();
                if (args.Length > 1 && args[0] == "--show") form.Shown += (s, e) => form.OpenId(args[1], args.Length > 2 ? int.Parse(args[2]) : 0);
                Application.Run(form);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Battery Check could not start:\n\n" + ex.Message, "Battery Check", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // Extract the embedded script into a folder named after its content hash, so a new build never runs a stale copy.
        static string Extract()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            byte[] data;
            using (Stream s = asm.GetManifestResourceStream("BatteryCheck.ps1"))
            {
                if (s == null) throw new InvalidOperationException("The embedded BatteryCheck.ps1 is missing.");
                MemoryStream ms = new MemoryStream();
                s.CopyTo(ms);
                data = ms.ToArray();
            }
            string hash;
            using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryCheck", "app");
            string dir = Path.Combine(root, hash);
            string target = Path.Combine(dir, "BatteryCheck.ps1");
            Directory.CreateDirectory(dir);
            if (!File.Exists(target) || new FileInfo(target).Length != data.Length) File.WriteAllBytes(target, data);
            try
            {
                foreach (string old in Directory.GetDirectories(root))
                    if (!string.Equals(Path.GetFileName(old), hash, StringComparison.OrdinalIgnoreCase)) Directory.Delete(old, true);
            }
            catch { }
            return dir;
        }

        // Reports next to the exe (portable, e.g. a USB stick); Documents when that folder is read-only.
        static string GetReportsDir()
        {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string dir = Path.Combine(exeDir, "Reports");
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BatteryCheck Reports");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }

    // =========================================================================================
    // Running BatteryCheck.ps1
    // =========================================================================================
    static class Runner
    {
        public static string AppDir, ReportsDir;
        public static string Script { get { return Path.Combine(AppDir, "BatteryCheck.ps1"); } }

        public static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static string PowerShellPath()
        {
            // 64-bit PowerShell even if this process ever runs as 32-bit (32-bit powercfg cannot create reports)
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sys = !Environment.Is64BitProcess && Environment.Is64BitOperatingSystem ? "Sysnative" : "System32";
            string p = Path.Combine(win, sys, "WindowsPowerShell\\v1.0\\powershell.exe");
            return File.Exists(p) ? p : "powershell.exe";
        }

        public static string Q(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) return a;
            return "\"" + a.TrimEnd('\\').Replace("\"", "\\\"") + "\"";
        }

        // Runs a test and returns the id of the new result. progress receives a short description of the
        // current step; cancelling stops PowerShell and powercfg and removes the partial raw files.
        public static Task<string> RunAsync(List<string> args, Dictionary<string, object> meta,
            Action<string> progress = null, System.Threading.CancellationToken cancel = default(System.Threading.CancellationToken))
        {
            return Task.Run(() => Run(args, meta, progress, cancel));
        }

        static string StepText(string line)
        {
            if (line.Contains("powercfg /batteryreport") || line.StartsWith("Step: battery report")) return "Collecting the battery report…";
            if (line.StartsWith("Extending powercfg /sleepstudy")) return (line.Contains("wake-ups") ? "Earlier wake-ups found: " : "Few recent standby sessions: ") + "looking back 7 days. This can take up to a minute or longer.";
            if (line.Contains("powercfg /sleepstudy")) return "Collecting the sleep study (last 3 days)…";
            if (line.Contains("Analysing sleep study")) return "Analysing the sleep study…";
            if (line.StartsWith("Step: analysing battery report")) return "Analysing the battery report…";
            if (line.StartsWith("Step: reading system information")) return "Reading system information…";
            if (line.StartsWith("Step: writing report")) return "Writing the report…";
            return null;
        }

        static void KillTree(int pid)
        {
            try
            {
                ProcessStartInfo k = new ProcessStartInfo("taskkill.exe", "/T /F /PID " + pid) { UseShellExecute = false, CreateNoWindow = true };
                using (Process p = Process.Start(k)) p.WaitForExit(10000);
            }
            catch { }
        }

        // Upper limit for a whole test. Each powercfg call has its own limit inside the script; this one
        // catches anything else that hangs (e.g. security software holding back PowerShell).
        const int MaxTestMinutes = 15;
        // Testing only: BATTERYCHECK_TEST_TIMEOUT_SEC shortens the limit so the timeout path can be checked
        static double MaxTestSeconds
        {
            get { int v; return int.TryParse(Environment.GetEnvironmentVariable("BATTERYCHECK_TEST_TIMEOUT_SEC"), out v) && v > 0 ? v : MaxTestMinutes * 60; }
        }
        // After this long without any output from PowerShell, tell the technician something is holding it back
        const int SilentWarningSeconds = 30;

        public static string LogsDir { get { return Path.Combine(ReportsDir, "logs"); } }

        static string Run(List<string> args, Dictionary<string, object> meta, Action<string> progress, System.Threading.CancellationToken cancel)
        {
            string metaPath = Path.Combine(Path.GetTempPath(), "batterycheck-meta-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(metaPath, J.Write(meta), new UTF8Encoding(false));
            HashSet<string> before = new HashSet<string>(Directory.GetFiles(ReportsDir, "BatteryCheck-*.json"), StringComparer.OrdinalIgnoreCase);
            HashSet<string> dirsBefore = new HashSet<string>(Directory.GetDirectories(ReportsDir, "raw-*"), StringComparer.OrdinalIgnoreCase);
            StringBuilder output = new StringBuilder();

            // One log per run, so a test that fails or hangs on a customer device can be diagnosed afterwards
            Directory.CreateDirectory(LogsDir);
            string logPath = Path.Combine(LogsDir, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            Stopwatch clock = Stopwatch.StartNew();
            object logLock = new object();
            Action<string> log = line =>
            {
                lock (logLock) try { File.AppendAllText(logPath, string.Format("{0,7:0.0}s  {1}\r\n", clock.Elapsed.TotalSeconds, line), Encoding.UTF8); } catch { }
            };
            try
            {
                foreach (string old in Directory.GetFiles(LogsDir, "run-*.log"))
                    if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-60)) File.Delete(old);
            }
            catch { }

            string lastStep = "Starting PowerShell";
            bool gotOutput = false, warnedSilent = false;
            try
            {
                StringBuilder cmd = new StringBuilder("-NoProfile -ExecutionPolicy Bypass -File ");
                cmd.Append(Q(Script)).Append(" -Unattended -OutputDir ").Append(Q(ReportsDir)).Append(" -MetaFile ").Append(Q(metaPath));
                foreach (string a in args) cmd.Append(' ').Append(Q(a));
                log("BatteryCheck " + Assembly.GetExecutingAssembly().GetName().Version + " on " + Environment.MachineName + ", Windows " + Environment.OSVersion.Version + ", admin=" + IsAdmin());
                log("command: " + PowerShellPath() + " " + cmd);

                ProcessStartInfo psi = new ProcessStartInfo(PowerShellPath(), cmd.ToString());
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (System.Threading.ManualResetEvent outDone = new System.Threading.ManualResetEvent(false))
                using (System.Threading.ManualResetEvent errDone = new System.Threading.ManualResetEvent(false))
                using (Process p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) { outDone.Set(); return; }
                        gotOutput = true;
                        lock (output) output.AppendLine(e.Data);
                        log(e.Data);
                        string step = StepText(e.Data);
                        if (step != null) { lastStep = step.TrimEnd('…', '.'); if (progress != null) progress(step); }
                    };
                    p.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null) { errDone.Set(); return; }
                        lock (output) output.AppendLine(e.Data);
                        log("stderr: " + e.Data);
                    };
                    p.Start();
                    log("PowerShell started, pid " + p.Id);
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    string stopReason = null;
                    while (!p.WaitForExit(250))
                    {
                        if (!gotOutput && !warnedSilent && clock.Elapsed.TotalSeconds > SilentWarningSeconds)
                        {
                            warnedSilent = true;
                            log("no output from PowerShell after " + SilentWarningSeconds + " s");
                            if (progress != null) progress("PowerShell has not responded for " + SilentWarningSeconds + " seconds. Security software on this laptop may be checking or blocking it. You can wait or click Cancel.");
                        }
                        if (cancel.IsCancellationRequested) { stopReason = "cancel"; break; }
                        if (clock.Elapsed.TotalSeconds > MaxTestSeconds) { stopReason = "timeout"; break; }
                    }

                    if (stopReason != null)
                    {
                        log(stopReason == "cancel" ? "cancelled by the user (last step: " + lastStep + ")" : "time limit of " + MaxTestSeconds + " s reached (last step: " + lastStep + ")");
                        KillTree(p.Id);
                        p.WaitForExit(5000);
                        // The result file is written last: if it is complete, the test actually finished - keep it
                        string done = Directory.GetFiles(ReportsDir, "BatteryCheck-*.json").FirstOrDefault(f => !before.Contains(f));
                        if (done != null)
                        {
                            try { J.Parse(File.ReadAllText(done, Encoding.UTF8)); log("result was already complete: " + Path.GetFileName(done)); return Path.GetFileNameWithoutExtension(done); }
                            catch
                            {
                                try { File.Delete(done); File.Delete(Path.ChangeExtension(done, ".html")); } catch { }
                            }
                        }
                        else
                        {
                            string html = Directory.GetFiles(ReportsDir, "BatteryCheck-*.html").FirstOrDefault(f => !File.Exists(Path.ChangeExtension(f, ".json")));
                            if (html != null) try { File.Delete(html); } catch { }
                        }
                        foreach (string d in Directory.GetDirectories(ReportsDir, "raw-*"))
                            if (!dirsBefore.Contains(d)) try { Directory.Delete(d, true); } catch { }
                        if (stopReason == "cancel") throw new OperationCanceledException();
                        string limit = MaxTestSeconds >= 120 ? Math.Round(MaxTestSeconds / 60) + " minutes" : MaxTestSeconds + " seconds";
                        string busy = lastStep.Length > 0 && !lastStep.StartsWith("PowerShell") ? char.ToLowerInvariant(lastStep[0]) + lastStep.Substring(1) : lastStep;
                        throw new Exception("The test did not finish within " + limit + " and was stopped. It was busy with: " + busy + "." +
                            (gotOutput ? "" : "\n\nPowerShell never responded, which usually means security software on this laptop is blocking it.") +
                            "\n\nDetails were saved in:\n" + logPath);
                    }

                    // Wait briefly for the last output lines; never wait forever (a stray child process could keep the pipe open)
                    outDone.WaitOne(5000);
                    errDone.WaitOne(2000);
                    log("PowerShell exited with code " + p.ExitCode);
                }
            }
            finally { try { File.Delete(metaPath); } catch { } }

            string created = Directory.GetFiles(ReportsDir, "BatteryCheck-*.json")
                .Where(f => !before.Contains(f)).OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
            if (created == null)
            {
                string err = ExtractError(output.ToString());
                log("FAILED: " + err);
                throw new Exception(err + "\n\nDetails were saved in:\n" + logPath);
            }
            log("OK: " + Path.GetFileName(created));
            return Path.GetFileNameWithoutExtension(created);
        }

        static string ExtractError(string output)
        {
            string[] lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            string err = lines.FirstOrDefault(l => l.StartsWith("ERROR:"));
            if (err != null) return err.Substring(6).Trim();
            err = lines.FirstOrDefault(l => l.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("No battery", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0);
            return err ?? "The test did not produce a result.";
        }
    }

    // =========================================================================================
    // JSON access (results are written by PowerShell's ConvertTo-Json)
    // =========================================================================================
    static class J
    {
        static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        public static Dictionary<string, object> Parse(string s) { return S.Deserialize<Dictionary<string, object>>(s); }
        public static string Write(object o) { return S.Serialize(o); }

        public static object Get(object o, string path)
        {
            foreach (string part in path.Split('.'))
            {
                IDictionary<string, object> d = o as IDictionary<string, object>;
                if (d == null || !d.TryGetValue(part, out o)) return null;
            }
            return o;
        }
        public static string Str(object o, string path)
        {
            object v = Get(o, path);
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        public static double? Num(object o, string path)
        {
            object v = Get(o, path);
            if (v == null) return null;
            if (v is string)
            {
                double d;
                return double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null;
            }
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
        }
        public static List<object> List(object o, string path)
        {
            object v = path == null ? o : Get(o, path);
            List<object> r = new List<object>();
            if (v == null) return r;
            if (v is IDictionary<string, object>) { r.Add(v); return r; }
            IEnumerable e = v as IEnumerable;
            if (e != null && !(v is string)) foreach (object x in e) r.Add(x);
            return r;
        }
    }

    // =========================================================================================
    // Stored tests
    // =========================================================================================
    class TestRecord
    {
        public string Id, JsonPath;
        public Dictionary<string, object> Data;
        public DateTime Created;

        public string Verdict { get { return J.Str(Data, "Verdict"); } }
        public string ColorName
        {
            get
            {
                string c = J.Str(Data, "Color");
                if (c != "") return c;
                string v = Verdict;
                if (v == "REPLACE BATTERY" || v == "REPLACE + FIX SOFTWARE") return "red";
                if (v == "REPLACEMENT JUSTIFIED" || v == "FIX SOFTWARE FIRST") return "orange";
                if (v == "NO FAULT FOUND" || v == "BATTERY OK") return "green";
                if (v.StartsWith("DO NOT")) return "blue";
                return "grey";
            }
        }
        // replace | software | ok | other
        public string Category
        {
            get
            {
                string c = ColorName;
                if (Verdict.StartsWith("FIX SOFTWARE") || c == "blue") return "software";
                if (c == "red" || c == "orange") return "replace";
                if (c == "green") return "ok";
                return "other";
            }
        }
        public string ShortVerdict
        {
            get
            {
                switch (Verdict)
                {
                    case "REPLACE BATTERY": return "Replace battery";
                    case "REPLACE + FIX SOFTWARE": return "Replace + fix software";
                    case "REPLACEMENT JUSTIFIED": return "Replacement justified";
                    case "FIX SOFTWARE FIRST": return "Fix software first";
                    case "NO FAULT FOUND": return "No fault found";
                    case "BATTERY OK": return "Battery OK";
                    case "INCONCLUSIVE": return "Inconclusive";
                    case "CALIBRATE BATTERY, THEN RETEST": return "Calibrate & retest";
                }
                return Verdict.StartsWith("DO NOT") ? "Software / config" : Verdict;
            }
        }
        public string Computer { get { return J.Str(Data, "System.Computer"); } }
        public string Manufacturer { get { return J.Str(Data, "System.Manufacturer"); } }
        public string Model { get { return J.Str(Data, "System.Model"); } }
        public string Device { get { return (Model == "" ? "—" : Model) + (Computer != "" ? "  ·  " + Computer : ""); } }
        public string DeviceSerial { get { return J.Str(Data, "System.ServiceTag"); } }

        // True when both tests come from the same laptop
        public bool SameDevice(TestRecord o)
        {
            if (DeviceSerial != "" && o.DeviceSerial != "") return string.Equals(DeviceSerial, o.DeviceSerial, StringComparison.OrdinalIgnoreCase);
            // Imported reports have no device serial: fall back to name + model, or a battery serial that looks unique
            if (Model != o.Model) return false;
            if (Computer != "" && Computer == o.Computer) return true;
            return UniqueBatterySerial(BatterySerial) && BatterySerial == o.BatterySerial;
        }
        static bool UniqueBatterySerial(string s)
        {
            // "SurfaceBattery 0", "Battery 1234" etc. are placeholders, not serials
            string tail = s.Contains(" ") ? s.Substring(s.LastIndexOf(' ') + 1) : s;
            return tail.Trim('0').Length >= 4;
        }

        public string BatterySerial
        {
            get { return string.Join(", ", J.List(Data, "Battery.Packs").Select(p => (J.Str(p, "Id") + " " + J.Str(p, "Serial")).Trim()).ToArray()); }
        }
        public double? Health { get { double? h = J.Num(Data, "Battery.HealthPct"); return h > 0 ? h : null; } }
        public double? Cycles { get { double? c = J.Num(Data, "Battery.Cycles"); return c > 0 ? c : null; } }
        public double? ActiveW { get { return J.Num(Data, "Consumption.ActiveAvgW"); } }
        public double? StandbyPctH { get { return J.Num(Data, "Consumption.StandbyPctPerHour"); } }
        public string Ticket { get { return J.Str(Data, "Meta.Ticket"); } }
        public string Customer { get { return J.Str(Data, "Meta.Customer"); } }
        public string Technician { get { return J.Str(Data, "Meta.Technician"); } }
        public string Notes { get { return J.Str(Data, "Meta.Notes"); } }
        public string Status { get { string s = J.Str(Data, "Meta.Status"); return s == "" ? "Open" : s; } }
        public string HtmlPath
        {
            get
            {
                string h = J.Str(Data, "Files.Html");
                return Path.Combine(Path.GetDirectoryName(JsonPath), h != "" ? h : Id + ".html");
            }
        }
        public string RawDirName { get { return J.Str(Data, "Files.RawDir"); } }

        public void SetMeta(string key, string value)
        {
            Dictionary<string, object> meta = J.Get(Data, "Meta") as Dictionary<string, object>;
            if (meta == null) { meta = new Dictionary<string, object>(); Data["Meta"] = meta; }
            meta[key] = value;
        }
    }

    // Basic mode (default, for dealers) shows the verdict and a few figures; Expert shows everything.
    // Remembered in Reports\settings.json, so it travels with the tool on a USB stick.
    static class Settings
    {
        static string FilePath { get { return Path.Combine(Runner.ReportsDir, "settings.json"); } }
        public static bool Expert;
        public static void Load()
        {
            try { if (File.Exists(FilePath)) Expert = J.Str(J.Parse(File.ReadAllText(FilePath)), "Mode") == "Expert"; } catch { }
        }
        public static void Save()
        {
            try { File.WriteAllText(FilePath, J.Write(new Dictionary<string, object> { { "Mode", Expert ? "Expert" : "Basic" } })); } catch { }
        }
    }

    static class Store
    {
        public static List<TestRecord> LoadAll()
        {
            List<TestRecord> list = new List<TestRecord>();
            foreach (string f in Directory.GetFiles(Runner.ReportsDir, "BatteryCheck-*.json"))
            {
                try
                {
                    TestRecord r = new TestRecord();
                    r.JsonPath = f;
                    r.Id = Path.GetFileNameWithoutExtension(f);
                    r.Data = J.Parse(File.ReadAllText(f, Encoding.UTF8));
                    DateTime dt;
                    r.Created = DateTime.TryParse(J.Str(r.Data, "CreatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt) ? dt : File.GetLastWriteTime(f);
                    list.Add(r);
                }
                catch { /* unreadable file - skip */ }
            }
            return list.OrderByDescending(r => r.Created).ToList();
        }

        public static void Save(TestRecord r)
        {
            File.WriteAllText(r.JsonPath, J.Write(r.Data), new UTF8Encoding(false));
        }

        public static void Delete(TestRecord r, List<TestRecord> all)
        {
            string raw = r.RawDirName;
            File.Delete(r.JsonPath);
            if (File.Exists(r.HtmlPath)) File.Delete(r.HtmlPath);
            bool safeName = raw != "" && System.Text.RegularExpressions.Regex.IsMatch(raw, @"^(raw|import)-[\w\-]+$");
            bool shared = all.Any(x => x != r && x.RawDirName == raw);
            string rawPath = Path.Combine(Runner.ReportsDir, raw);
            if (safeName && !shared && Directory.Exists(rawPath)) Directory.Delete(rawPath, true);
        }

        public static void ExportCsv(string path, List<TestRecord> all)
        {
            Func<object, string> csv = v =>
            {
                string s = v == null ? "" : (v is double ? ((double)v).ToString("0.##", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture));
                return s.IndexOfAny(new char[] { '"', ';', ',', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
            };
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Date;Ticket;Customer;Technician;Status;Jabbla serial (device name);Manufacturer;Model;Surface serial;Battery;Mode;Verdict;Battery level;Power level;Health %;Cycles;Use W;Standby %/h;Shortfall from wear %");
            foreach (TestRecord r in all)
            {
                object[] row = { r.Created.ToString("yyyy-MM-dd HH:mm"), r.Ticket, r.Customer, r.Technician, r.Status, r.Computer, r.Manufacturer, r.Model, r.DeviceSerial, r.BatterySerial,
                    J.Str(r.Data, "Mode"), r.Verdict, J.Str(r.Data, "BatteryLevel"), J.Str(r.Data, "PowerLevel"), r.Health, r.Cycles, r.ActiveW, r.StandbyPctH, J.Num(r.Data, "Runtime.WearSharePct") };
                sb.AppendLine(string.Join(";", row.Select(csv).ToArray()));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
    }

    // =========================================================================================
    // Look & feel
    // =========================================================================================
    static class Theme
    {
        // Jabbla house style (jabbla.com): cream page, warm beige panels, deep teal for headings and actions,
        // a warm gold accent, square corners. Status colours are kept distinct but warmed to sit on cream.
        public static readonly Color Bg = Color.FromArgb(253, 246, 238), Card = Color.FromArgb(255, 253, 250), Text = Color.FromArgb(0, 51, 51),
            Body = Color.FromArgb(51, 51, 51), Muted = Color.FromArgb(108, 108, 108), Line = Color.FromArgb(234, 224, 208), Soft = Color.FromArgb(247, 239, 227),
            Accent = Color.FromArgb(0, 51, 51), AccentHover = Color.FromArgb(20, 82, 82), AccentDown = Color.FromArgb(0, 34, 34),
            Gold = Color.FromArgb(233, 177, 98), Panel = Color.FromArgb(238, 229, 214),
            Red = Color.FromArgb(181, 58, 58), Orange = Color.FromArgb(201, 124, 28),
            Blue = Color.FromArgb(38, 104, 128), Green = Color.FromArgb(44, 122, 87), Grey = Color.FromArgb(122, 114, 104),
            Lime = Color.FromArgb(122, 148, 44), Select = Color.FromArgb(238, 229, 214);
        public static float Scale = 1f;
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        static FontFamily headFamily;
        static System.Drawing.Text.PrivateFontCollection privateFonts;
        [DllImport("gdi32.dll")] static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, out uint pcFonts);

        public static void Init()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            // Heading font: Renner* (Jabbla's typeface, SIL Open Font License, embedded), then Century Gothic, then Segoe UI
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Renner.ttf"))
                {
                    if (s != null)
                    {
                        byte[] data = new byte[s.Length]; s.Read(data, 0, data.Length);
                        IntPtr mem = Marshal.AllocCoTaskMem(data.Length);   // kept for the life of the program
                        Marshal.Copy(data, 0, mem, data.Length);
                        privateFonts = new System.Drawing.Text.PrivateFontCollection();
                        privateFonts.AddMemoryFont(mem, data.Length);
                        uint n; AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, out n);   // lets GDI text drawing use it too
                        if (privateFonts.Families.Length > 0) headFamily = privateFonts.Families[0];
                    }
                }
            }
            catch { headFamily = null; }
            if (headFamily == null)
                foreach (string name in new[] { "Century Gothic", "Segoe UI Semibold" })
                    try { FontFamily ff = new FontFamily(name); headFamily = ff; break; } catch { }
        }
        // Headings in the Jabbla typeface
        public static Font Head(float size)
        {
            string key = size + "|head";
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = headFamily != null ? new Font(headFamily, size) : Semi(size); fonts[key] = f; }
            return f;
        }
        public static int Px(int v) { return (int)Math.Round(v * Scale); }
        public static Font F(float size, FontStyle style = FontStyle.Regular)
        {
            string key = size + "|" + style;
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font("Segoe UI", size, style); fonts[key] = f; }
            return f;
        }
        public static Font Semi(float size)
        {
            string key = size + "|semi";
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                try { f = new Font("Segoe UI Semibold", size); } catch { f = new Font("Segoe UI", size, FontStyle.Bold); }
                fonts[key] = f;
            }
            return f;
        }
        public static Color ByName(string c)
        {
            switch (c) { case "red": return Red; case "orange": return Orange; case "blue": return Blue; case "green": return Green; case "lime": return Lime; default: return Grey; }
        }
        public static Color Level(string l)
        {
            switch (l)
            {
                case "OK": case "NORMAL": return Green;
                case "FAIR": return Lime;
                case "ELEVATED": case "WORN": return Orange;
                case "HIGH": case "REPLACE": return Red;
                default: return Grey;
            }
        }
        public static Color Health(double h) { return h >= 80 ? Green : h >= 75 ? Lime : h >= 65 ? Orange : Red; }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // A colored "pill" with white text, used in grids and headers
        public static void DrawPill(Graphics g, string text, Font font, Color back, Rectangle bounds, bool center)
        {
            SmoothingMode before = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Size ts = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
            int w = Math.Min(bounds.Width, ts.Width + Px(16)), h = ts.Height + Px(4);
            int x = center ? bounds.X + (bounds.Width - w) / 2 : bounds.X;
            Rectangle r = new Rectangle(x, bounds.Y + (bounds.Height - h) / 2, w, h);
            using (GraphicsPath p = Round(r, Px(3))) using (SolidBrush b = new SolidBrush(back)) g.FillPath(b, p);
            TextRenderer.DrawText(g, text, font, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            g.SmoothingMode = before;
        }

        public static string Hours(double? h)
        {
            if (h == null || h <= 0 || double.IsNaN(h.Value) || double.IsInfinity(h.Value)) return "—";
            TimeSpan ts = TimeSpan.FromHours(h.Value);
            if (ts.TotalHours >= 48) return ts.TotalDays.ToString("0.0") + " days";
            return ((int)ts.TotalHours) + "h " + ts.Minutes.ToString("00") + "m";
        }
        public static string N(double? v, int dec, string suffix = "")
        {
            return v == null ? "—" : v.Value.ToString("N" + dec) + suffix;
        }
    }

    // ---------- small UI building blocks ----------
    class Card : Panel
    {
        public Color Stripe = Color.Empty;
        public Card()
        {
            BackColor = Theme.Card;
            Padding = new Padding(Theme.Px(16), Theme.Px(14), Theme.Px(16), Theme.Px(14));
            Margin = new Padding(Theme.Px(6));
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            if (Stripe != Color.Empty) using (SolidBrush b = new SolidBrush(Stripe)) e.Graphics.FillRectangle(b, 0, 0, Theme.Px(5), Height);
        }
    }

    class Tile : Card
    {
        string title, value, sub; Color bar, valueColor;
        public Tile(string title, string value, string sub, Color bar)
        {
            this.title = title; this.value = value; this.sub = sub; this.bar = bar; valueColor = Theme.Text;
            Dock = DockStyle.Fill;
        }
        public void Set(string v, string s, Color? vc = null) { value = v; sub = s; if (vc.HasValue) valueColor = vc.Value; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            int x = Theme.Px(14), y = Theme.Px(10), w = Width - 2 * x;
            TextRenderer.DrawText(g, title, Theme.F(8.5f), new Rectangle(x, y, w, Theme.Px(18)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, value, Theme.Head(18f), new Rectangle(x, y + Theme.Px(17), w, Theme.Px(34)), valueColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, sub ?? "", Theme.F(8f), new Rectangle(x, y + Theme.Px(52), w, Theme.Px(16)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (bar != Color.Empty) using (SolidBrush b = new SolidBrush(bar)) g.FillRectangle(b, x, Height - Theme.Px(10), w, Theme.Px(3));
        }
    }

    class PillLabel : Control
    {
        Color back;
        public PillLabel(string text, Color back)
        {
            Text = text; this.back = back; Font = Theme.Semi(8f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size s = TextRenderer.MeasureText(text, Font);
            Size = new Size(s.Width + Theme.Px(12), s.Height + Theme.Px(4));
            Margin = new Padding(0, Theme.Px(2), Theme.Px(14), 0);
        }
        protected override void OnPaint(PaintEventArgs e) { Theme.DrawPill(e.Graphics, Text, Font, back, ClientRectangle, false); }
    }

    // ---------- updates from GitHub Releases ----------
    // A release is tagged vX.Y.Z, has BatteryCheck.exe attached and "SHA256: <hash>" in its notes.
    // The new exe is only used when its hash matches; the swap happens after this program has closed.
    static class Updater
    {
        public const string Repo = "SmilePineapple/battery-check";
        public static Version Current { get { Version v = Assembly.GetExecutingAssembly().GetName().Version; return new Version(v.Major, v.Minor, v.Build); } }

        public class Release { public Version Version; public string Tag, Url, Sha256, Notes; }

        static WebClient Client()
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            WebClient w = new WebClient();
            w.Headers[HttpRequestHeader.UserAgent] = "BatteryCheck/" + Current;
            w.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return w;
        }

        // Returns the latest release if it is newer than this version, otherwise null.
        public static Release CheckNewer()
        {
            string json;
            using (WebClient w = Client()) json = w.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
            Dictionary<string, object> d = J.Parse(json);
            string tag = Convert.ToString(d["tag_name"]);
            Version v;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out v)) return null;
            v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            if (v <= Current) return null;
            string notes = d.ContainsKey("body") ? Convert.ToString(d["body"]) : "";
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(notes, @"SHA256:\s*([0-9a-fA-F]{64})");
            string url = null;
            foreach (object a in (IEnumerable)d["assets"])
            {
                Dictionary<string, object> ad = (Dictionary<string, object>)a;
                if (string.Equals(Convert.ToString(ad["name"]), "BatteryCheck.exe", StringComparison.OrdinalIgnoreCase)) url = Convert.ToString(ad["browser_download_url"]);
            }
            if (url == null || !m.Success) return null;   // incomplete release: ignore it
            int cut = notes.IndexOf("SHA256:", StringComparison.Ordinal);
            return new Release { Version = v, Tag = tag, Url = url, Sha256 = m.Groups[1].Value.ToLowerInvariant(), Notes = (cut >= 0 ? notes.Substring(0, cut) : notes).Trim() };
        }

        // Downloads and checks the new exe, then starts a small script that swaps it in once this program has exited.
        public static void Apply(Release r)
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            string dir = Path.GetDirectoryName(exe);
            string tmp = exe + ".download";
            using (WebClient w = Client()) { w.Headers.Remove(HttpRequestHeader.Accept); w.DownloadFile(r.Url, tmp); }
            byte[] bytes = File.ReadAllBytes(tmp);
            string hash;
            using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (bytes.Length < 20000 || bytes[0] != 'M' || bytes[1] != 'Z' || hash != r.Sha256)
            {
                try { File.Delete(tmp); } catch { }
                throw new Exception("The downloaded file did not pass the safety check (checksum mismatch), so nothing was changed.");
            }
            string script = Path.Combine(Path.GetTempPath(), "batterycheck-update-" + Process.GetCurrentProcess().Id + ".cmd");
            int pid = Process.GetCurrentProcess().Id;
            File.WriteAllText(script,
                "@echo off\r\n" +
                ":wait\r\n" +
                "tasklist /fi \"PID eq " + pid + "\" | find \"" + pid + "\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                "move /y \"" + tmp + "\" \"" + exe + "\" >nul\r\n" +
                "if errorlevel 1 del \"" + tmp + "\"\r\n" +
                "start \"\" /d \"" + dir + "\" \"" + exe + "\"\r\n" +
                "del \"%~f0\"\r\n", Encoding.ASCII);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
    }

    static class UI
    {
        public static Label Label(string text, Font font = null, Color? color = null)
        {
            Label l = new Label();
            l.Text = text; l.AutoSize = true; l.Font = font ?? Theme.F(9.5f); l.ForeColor = color ?? Theme.Text;
            l.Margin = new Padding(0, Theme.Px(2), 0, Theme.Px(2));
            l.UseMnemonic = false;
            return l;
        }
        public static Label FieldLabel(string text)
        {
            Label l = Label(text, Theme.F(8.5f), Theme.Muted);
            l.Margin = new Padding(0, Theme.Px(10), 0, Theme.Px(3));
            return l;
        }
        public static Button Button(string text, bool primary = false, bool danger = false)
        {
            Button b = new Button();
            b.Text = text; b.FlatStyle = FlatStyle.Flat; b.Cursor = Cursors.Hand; b.UseMnemonic = false;
            b.Font = primary ? Theme.Semi(9.5f) : Theme.F(9.5f);
            b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(Theme.Px(10), Theme.Px(3), Theme.Px(10), Theme.Px(3));
            b.MinimumSize = new Size(0, Theme.Px(34));
            b.BackColor = primary ? Theme.Accent : Theme.Card;
            b.ForeColor = primary ? Color.White : danger ? Theme.Red : Theme.Text;
            if (primary) b.Text = text.ToUpperInvariant();   // Jabbla buttons are uppercase
            b.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Line;
            b.FlatAppearance.MouseOverBackColor = primary ? Theme.AccentHover : Theme.Soft;
            b.FlatAppearance.MouseDownBackColor = primary ? Theme.AccentDown : Theme.Line;
            b.Margin = new Padding(Theme.Px(6), 0, 0, 0);
            return b;
        }
        public static TextBox Text(string value = "", bool multiline = false)
        {
            TextBox t = new TextBox();
            t.Text = value; t.BorderStyle = BorderStyle.FixedSingle; t.Font = Theme.F(9.5f); t.Dock = DockStyle.Fill;
            t.Margin = new Padding(0);
            if (multiline) { t.Multiline = true; t.Height = Theme.Px(80); t.ScrollBars = ScrollBars.Vertical; t.AcceptsReturn = true; }
            return t;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        public static void Cue(TextBox t, string cue)
        {
            t.HandleCreated += (s, e) => SendMessage(t.Handle, 0x1501, (IntPtr)1, cue);
        }

        // Single-column auto-sizing stack
        public static TableLayoutPanel Stack()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.ColumnCount = 1; t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.AutoSize = true; t.AutoSizeMode = AutoSizeMode.GrowAndShrink; t.Dock = DockStyle.Top;
            t.Margin = new Padding(0); t.Padding = new Padding(0);
            return t;
        }
        public static void Add(TableLayoutPanel t, Control c, bool fill = true)
        {
            t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (fill) c.Dock = DockStyle.Fill;
            t.Controls.Add(c, 0, t.RowCount - 1);
        }
        public static TableLayoutPanel Columns(params float[] percents)
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.ColumnCount = percents.Length; t.RowCount = 1;
            foreach (float p in percents) t.ColumnStyles.Add(p > 0 ? new ColumnStyle(SizeType.Percent, p) : new ColumnStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.AutoSize = true; t.Dock = DockStyle.Top; t.Margin = new Padding(0); t.Padding = new Padding(0);
            return t;
        }

        public static DataGridView Grid(bool selectable = true)
        {
            DataGridView g = new DataGridView();
            g.Dock = DockStyle.Fill;
            g.BorderStyle = BorderStyle.None;
            g.BackgroundColor = Theme.Card;
            g.GridColor = Theme.Card;   // separators are drawn by BottomLine
            g.CellBorderStyle = DataGridViewCellBorderStyle.None;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = Theme.Card;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
            g.ColumnHeadersDefaultCellStyle.Font = Theme.Semi(8.5f);
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Card;
            g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(Theme.Px(4), 0, Theme.Px(4), 0);
            g.ColumnHeadersHeight = Theme.Px(34);
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.DefaultCellStyle.BackColor = Theme.Card;
            g.DefaultCellStyle.ForeColor = Theme.Text;
            g.DefaultCellStyle.Font = Theme.F(9.5f);
            g.DefaultCellStyle.SelectionBackColor = Theme.Select;
            g.DefaultCellStyle.SelectionForeColor = Theme.Text;
            g.DefaultCellStyle.Padding = new Padding(Theme.Px(4), 0, Theme.Px(4), 0);
            g.RowTemplate.Height = Theme.Px(32);
            g.RowHeadersVisible = false;
            g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.AllowUserToResizeRows = false;
            g.ReadOnly = true; g.MultiSelect = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;   // columns share the width by FillWeight
            g.SortCompare += (s, e) =>
            {
                object a = e.CellValue1, b = e.CellValue2;
                if (a == null && b == null) e.SortResult = 0;
                else if (a == null) e.SortResult = -1;
                else if (b == null) e.SortResult = 1;
                else if (a is IComparable && a.GetType() == b.GetType()) e.SortResult = ((IComparable)a).CompareTo(b);
                else e.SortResult = string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCultureIgnoreCase);
                e.Handled = true;
            };
            if (!selectable)
            {
                g.DefaultCellStyle.SelectionBackColor = Theme.Card; g.DefaultCellStyle.SelectionForeColor = Theme.Text;
                g.TabStop = false;
                g.SelectionChanged += (s, e) => { if (g.CurrentCell != null) g.CurrentCell = null; };
                g.VisibleChanged += (s, e) => { if (g.Visible) g.CurrentCell = null; };
            }
            // Paint cells without the built-in borders (they show vertical lines), then one horizontal separator
            g.CellPainting += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                FillCell(e);
                e.Paint(e.CellBounds, DataGridViewPaintParts.ContentForeground);
                BottomLine(e);
                e.Handled = true;
            };
            typeof(DataGridView).InvokeMember("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty, null, g, new object[] { true });
            return g;
        }
        public static void FillCell(DataGridViewCellPaintingEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.None;
            bool sel = (e.State & DataGridViewElementStates.Selected) != 0;
            using (SolidBrush b = new SolidBrush(sel ? e.CellStyle.SelectionBackColor : e.CellStyle.BackColor)) e.Graphics.FillRectangle(b, e.CellBounds);
        }
        public static void BottomLine(DataGridViewCellPaintingEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.None;
            using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
        }
        public static DataGridViewColumn Col(DataGridView g, string name, string header, int width, string format = null, bool right = false, bool fill = false)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.Name = name; c.HeaderText = header;
            c.FillWeight = width * (fill ? 1.6f : 1f);
            c.MinimumWidth = Theme.Px((int)(width * 0.85));
            c.SortMode = DataGridViewColumnSortMode.Automatic;
            if (format != null) c.DefaultCellStyle.Format = format;
            if (right) { c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight; }
            g.Columns.Add(c);
            return c;
        }

        // Line chart of health % with the 80% / 60% thresholds
        public static void DrawHealthChart(Graphics g, Rectangle r, List<double> values, string leftLabel, string rightLabel, int highlight)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (values.Count < 2) return;
            int axis = Theme.Px(34), bottom = leftLabel != null ? Theme.Px(18) : 0;
            Rectangle plot = new Rectangle(r.X + axis, r.Y + Theme.Px(6), r.Width - axis - Theme.Px(6), r.Height - Theme.Px(12) - bottom);
            // top: 100%, or higher when the gauge reported more (glitches up to ~120% happen)
            double lo = Math.Floor((Math.Min(values.Min(), 65) - 5) / 10) * 10, hi = Math.Max(100, Math.Ceiling(values.Max() / 10) * 10);
            Func<double, float> Y = v => (float)(plot.Bottom - plot.Height * (v - lo) / (hi - lo));
            Func<int, float> X = i => plot.X + plot.Width * i / (float)(values.Count - 1);
            Font f = Theme.F(7.5f);
            foreach (double t in new double[] { lo, 80, 65, 100, hi }.Distinct())
            {
                if (t < lo) continue;
                Color c = t == 80 ? Theme.Green : t == 65 ? Theme.Red : Theme.Line;
                using (Pen p = new Pen(c, 1f)) { if (t == 80 || t == 65) p.DashPattern = new float[] { 4, 3 }; g.DrawLine(p, plot.X, Y(t), plot.Right, Y(t)); }
                TextRenderer.DrawText(g, t + "%", f, new Rectangle(r.X, (int)Y(t) - Theme.Px(8), axis - Theme.Px(4), Theme.Px(16)), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            PointF[] pts = values.Select((v, i) => new PointF(X(i), Y(v))).ToArray();
            using (Pen p = new Pen(Theme.Accent, Theme.Px(2))) g.DrawLines(p, pts);
            if (values.Count <= 40)
                for (int i = 0; i < pts.Length; i++)
                {
                    float rad = i == highlight ? Theme.Px(5) : Theme.Px(3);
                    using (SolidBrush b = new SolidBrush(Theme.Health(values[i]))) g.FillEllipse(b, pts[i].X - rad, pts[i].Y - rad, rad * 2, rad * 2);
                    using (Pen p = new Pen(Theme.Card, 1.5f)) g.DrawEllipse(p, pts[i].X - rad, pts[i].Y - rad, rad * 2, rad * 2);
                }
            if (leftLabel != null)
            {
                TextRenderer.DrawText(g, leftLabel, f, new Rectangle(plot.X, plot.Bottom + Theme.Px(4), plot.Width / 2, Theme.Px(14)), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, rightLabel, f, new Rectangle(plot.X + plot.Width / 2, plot.Bottom + Theme.Px(4), plot.Width / 2, Theme.Px(14)), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }
        }
    }

    // =========================================================================================
    // Progress dialog while a test runs
    // =========================================================================================
    class RunDialog : Form
    {
        Func<Action<string>, System.Threading.CancellationToken, Task<string>> work;
        Label time, step;
        Button cancelButton;
        Timer timer;
        DateTime start;
        System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource();
        public string ResultId, Error;
        public bool Cancelled;

        public RunDialog(string title, string sub, Func<Action<string>, System.Threading.CancellationToken, Task<string>> work)
        {
            this.work = work;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            BackColor = Theme.Card; Size = new Size(Theme.Px(460), Theme.Px(220)); Font = Theme.F(9.5f);
            Padding = new Padding(Theme.Px(24));
            TableLayoutPanel s = UI.Stack(); s.Dock = DockStyle.Fill;
            UI.Add(s, UI.Label(title, Theme.Head(13f)));
            step = UI.Label(sub, Theme.F(9f), Theme.Muted);
            step.MaximumSize = new Size(Theme.Px(410), 0);
            UI.Add(s, step);
            ProgressBar pb = new ProgressBar(); pb.Style = ProgressBarStyle.Marquee; pb.MarqueeAnimationSpeed = 25; pb.Height = Theme.Px(8); pb.Margin = new Padding(0, Theme.Px(14), 0, Theme.Px(8));
            UI.Add(s, pb);
            TableLayoutPanel bottom = UI.Columns(100, 0);
            time = UI.Label("0 s", Theme.F(9f), Theme.Muted); time.Anchor = AnchorStyles.Left;
            cancelButton = UI.Button("Cancel");
            cancelButton.Click += (o, e) => StopTest();
            bottom.Controls.Add(time, 0, 0); bottom.Controls.Add(cancelButton, 1, 0);
            UI.Add(s, bottom);
            Controls.Add(s);
            KeyPreview = true;
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) StopTest(); };
            timer = new Timer(); timer.Interval = 500;
            timer.Tick += (o, e) => time.Text = ((int)(DateTime.Now - start).TotalSeconds) + " s";
        }
        void StopTest()
        {
            if (cts.IsCancellationRequested) return;
            cts.Cancel();
            cancelButton.Enabled = false; cancelButton.Text = "Stopping…";
            step.Text = "Stopping the test…";
        }
        void SetStep(string text)
        {
            if (IsDisposed || cts.IsCancellationRequested) return;
            try { BeginInvoke((MethodInvoker)(() => { if (!cts.IsCancellationRequested) step.Text = text; })); } catch { }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            start = DateTime.Now; timer.Start();
            try { ResultId = await work(SetStep, cts.Token); DialogResult = DialogResult.OK; }
            catch (OperationCanceledException) { Cancelled = true; DialogResult = DialogResult.Cancel; }
            catch (Exception ex)
            {
                Exception inner = (ex is AggregateException && ex.InnerException != null) ? ex.InnerException : ex;
                if (inner is OperationCanceledException) { Cancelled = true; DialogResult = DialogResult.Cancel; }
                else { Error = inner.Message; DialogResult = DialogResult.Abort; }
            }
            finally { timer.Stop(); }
        }
    }

    // =========================================================================================
    // Main window
    // =========================================================================================
    class MainForm : Form
    {
        static readonly string[] Statuses = { "Open", "Battery replaced", "Software/config fixed", "No action needed", "Returned to customer" };

        List<TestRecord> tests = new List<TestRecord>();
        Panel body;
        Control listView, detailView;
        Label adminLabel;

        // list view controls
        Tile tTotal, tReplace, tSoftware, tOk, tAvg;
        DataGridView grid;
        TextBox search;
        ComboBox filter;
        RadioButton tabLocal, tabImport;
        Panel localPanel, importPanel;
        NumericUpDown days;
        TextBox fTicket, fTech, fCustomer, fNotes, fBatteryXml, fSleepXml;
        Button runButton;
        Label tipLabel;

        public MainForm()
        {
            Text = "Battery Check";
            Font = Theme.F(9.5f);
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Theme.Px(1360), Theme.Px(860));
            MinimumSize = new Size(Theme.Px(1050), Theme.Px(660));
            WindowState = FormWindowState.Maximized;
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

            body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Px(14), Theme.Px(10), Theme.Px(14), Theme.Px(14)) };
            Controls.Add(body);
            Controls.Add(BuildHeader());

            listView = BuildListView();
            body.Controls.Add(listView);
            Reload();
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && detailView != null) ShowList(); };
        }

        // ---------- header ----------
        Control BuildHeader()
        {
            Panel h = new Panel { Dock = DockStyle.Top, Height = Theme.Px(64), BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 1) };
            h.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, h.Height - 1, h.Width, h.Height - 1);
                // battery glyph
                Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                int x = Theme.Px(22), y = Theme.Px(24);
                using (Pen p = new Pen(Theme.Text, Theme.Px(2)))
                using (GraphicsPath gp = Theme.Round(new RectangleF(x, y, Theme.Px(24), Theme.Px(16)), Theme.Px(3))) g.DrawPath(p, gp);
                using (SolidBrush b = new SolidBrush(Theme.Text)) g.FillRectangle(b, x + Theme.Px(25), y + Theme.Px(5), Theme.Px(3), Theme.Px(6));
                using (SolidBrush b = new SolidBrush(Theme.Green)) g.FillRectangle(b, x + Theme.Px(4), y + Theme.Px(4), Theme.Px(11), Theme.Px(8));
            };
            Label title = UI.Label("Battery Check", Theme.Head(17f), Theme.Text);
            title.Location = new Point(Theme.Px(56), Theme.Px(14));
            h.Controls.Add(title);
            adminLabel = UI.Label(Runner.IsAdmin() ? "Ready" : "Not running as administrator: the standby part of the test will ask for permission", Theme.F(8.5f), Theme.Muted);
            adminLabel.Location = new Point(title.Left + title.PreferredWidth + Theme.Px(18), Theme.Px(25));
            h.Controls.Add(adminLabel);

            FlowLayoutPanel right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, Theme.Px(12), Theme.Px(14), 0), WrapContents = false, BackColor = Theme.Bg };
            Button csv = UI.Button("Export CSV");
            csv.Click += (s, e) => ExportCsv();
            csv.Visible = Settings.Expert;
            Button folder = UI.Button("Reports folder");
            folder.Click += (s, e) => Process.Start("explorer.exe", Runner.Q(Runner.ReportsDir));
            // Basic | Expert switch
            TableLayoutPanel modeSwitch = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Width = Theme.Px(190), Height = Theme.Px(34), Margin = new Padding(Theme.Px(6), 0, Theme.Px(10), 0) };
            modeSwitch.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); modeSwitch.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            RadioButton basicBtn = TabButton("Basic", !Settings.Expert), expertBtn = TabButton("Expert", Settings.Expert);
            modeSwitch.Controls.Add(basicBtn, 0, 0); modeSwitch.Controls.Add(expertBtn, 1, 0);
            expertBtn.CheckedChanged += (s, e) =>
            {
                if (Settings.Expert == expertBtn.Checked) return;
                Settings.Expert = expertBtn.Checked; Settings.Save();
                csv.Visible = Settings.Expert;
                ApplyMode();
            };
            // Update button: checks quietly at startup and turns blue when a newer version is out
            Button upd = UI.Button("Check for updates");
            Updater.Release found = null;
            Action<Updater.Release> showFound = rel =>
            {
                found = rel;
                upd.Text = "UPDATE TO V" + rel.Version;
                upd.BackColor = Theme.Accent; upd.ForeColor = Color.White; upd.Font = Theme.Semi(9.5f);
                upd.FlatAppearance.BorderColor = Theme.Accent; upd.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
            };
            upd.Click += async (s, e) =>
            {
                upd.Enabled = false;
                try
                {
                    if (found == null)
                    {
                        upd.Text = "Checking…";
                        Updater.Release rel = await Task.Run(() => Updater.CheckNewer());
                        if (rel == null) { upd.Text = "Up to date (v" + Updater.Current + ")"; return; }
                        showFound(rel);
                    }
                    string notes = string.IsNullOrEmpty(found.Notes) ? "" : "\n\nWhat's new:\n" + found.Notes;
                    if (MessageBox.Show(this, "Version " + found.Version + " is available (you have " + Updater.Current + ")." + notes +
                        "\n\nBattery Check will close, update itself and open again. Your saved tests are kept.\n\nUpdate now?",
                        "Battery Check update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
                    upd.Text = "Downloading…";
                    Updater.Release r = found;
                    await Task.Run(() => Updater.Apply(r));
                    Application.Exit();
                }
                catch (Exception ex)
                {
                    upd.Text = found != null ? "UPDATE TO V" + found.Version : "Check for updates";
                    MessageBox.Show(this, "The update did not work:\n\n" + ex.Message + "\n\nCheck the internet connection and try again.", "Battery Check update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { upd.Enabled = true; }
            };
            Shown += async (s, e) =>
            {
                Updater.Release rel = await Task.Run(() => { try { return Updater.CheckNewer(); } catch { return null; } });
                if (rel != null && found == null && !upd.IsDisposed) showFound(rel);
            };
            right.Controls.Add(csv); right.Controls.Add(folder); right.Controls.Add(upd); right.Controls.Add(modeSwitch);
            h.Controls.Add(right);
            return h;
        }

        // ---------- list view ----------
        Control BuildListView()
        {
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Px(104)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            TableLayoutPanel stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = new Padding(0) };
            for (int i = 0; i < 5; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tTotal = new Tile("Tests run", "0", "", Theme.Line);
            tReplace = new Tile("Battery replacement", "0", "", Theme.Red);
            tSoftware = new Tile("Software / config", "0", "", Theme.Blue);
            tOk = new Tile("No fault / OK", "0", "", Theme.Green);
            tAvg = new Tile("Average health", "—", "across all tests", Theme.Lime);
            stats.Controls.Add(tTotal, 0, 0); stats.Controls.Add(tReplace, 1, 0); stats.Controls.Add(tSoftware, 2, 0); stats.Controls.Add(tOk, 3, 0); stats.Controls.Add(tAvg, 4, 0);
            root.Controls.Add(stats, 0, 0);

            TableLayoutPanel main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Px(345)));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.Controls.Add(BuildNewTestCard(), 0, 0);
            main.Controls.Add(BuildHistoryCard(), 1, 0);
            root.Controls.Add(main, 0, 1);
            return root;
        }

        Control BuildNewTestCard()
        {
            Card card = new Card { Dock = DockStyle.Fill, AutoScroll = true };
            TableLayoutPanel s = UI.Stack();
            UI.Add(s, UI.Label("New test", Theme.Head(14f)));

            TableLayoutPanel tabs = UI.Columns(50, 50);
            tabs.Margin = new Padding(0, Theme.Px(8), 0, Theme.Px(4));
            tabLocal = TabButton("This PC", true); tabImport = TabButton("Import", false);
            tabs.Controls.Add(tabLocal, 0, 0); tabs.Controls.Add(tabImport, 1, 0);
            UI.Add(s, tabs);

            // this PC (one test: battery report + sleep study)
            TableLayoutPanel local = UI.Stack();
            Label what = UI.Label("Checks battery wear, the power the laptop really uses, standby drain and what wakes it up. Takes about 20 seconds.", Theme.F(9f), Theme.Muted);
            what.Margin = new Padding(0, Theme.Px(6), 0, Theme.Px(2));
            UI.Add(local, what);
            Label daysLabel = UI.FieldLabel("Days of history to analyse");
            UI.Add(local, daysLabel);
            days = new NumericUpDown { Minimum = 1, Maximum = 28, Value = 14, BorderStyle = BorderStyle.FixedSingle, Font = Theme.F(9.5f), Width = Theme.Px(90) };
            UI.Add(local, days, false);
            daysLabel.Visible = days.Visible = Settings.Expert;
            localPanel = local;

            // import
            TableLayoutPanel imp = UI.Stack();
            Label help = UI.Label("Analyse reports made on another laptop. On that laptop, run:\npowercfg /batteryreport /xml\npowercfg /sleepstudy /xml   (as admin, optional)", Theme.F(8.5f), Theme.Muted);
            UI.Add(imp, help);
            UI.Add(imp, UI.FieldLabel("Battery report XML (required)"));
            fBatteryXml = FileRow(imp, "Battery report");
            UI.Add(imp, UI.FieldLabel("Sleep study XML (optional)"));
            fSleepXml = FileRow(imp, "Sleep study");
            importPanel = imp; imp.Visible = false;

            UI.Add(s, local); UI.Add(s, imp);

            // ticket fields (shared)
            TableLayoutPanel two = UI.Columns(50, 50);
            TableLayoutPanel c1 = UI.Stack(); c1.Margin = new Padding(0, 0, Theme.Px(6), 0);
            TableLayoutPanel c2 = UI.Stack(); c2.Margin = new Padding(Theme.Px(6), 0, 0, 0);
            UI.Add(c1, UI.FieldLabel("Ticket / RMA no.")); fTicket = UI.Text(); UI.Cue(fTicket, "e.g. R-24017"); UI.Add(c1, fTicket);
            UI.Add(c2, UI.FieldLabel("Technician")); fTech = UI.Text(); UI.Add(c2, fTech);
            two.Controls.Add(c1, 0, 0); two.Controls.Add(c2, 1, 0);
            UI.Add(s, two);
            UI.Add(s, UI.FieldLabel("Customer")); fCustomer = UI.Text(); UI.Add(s, fCustomer);
            Label custLabel = (Label)s.Controls[s.Controls.Count - 2];
            Label notesLabel = UI.FieldLabel("Complaint / notes");
            UI.Add(s, notesLabel); fNotes = UI.Text("", true); UI.Cue(fNotes, "e.g. Battery empty after 2 hours"); UI.Add(s, fNotes);
            // Basic mode: no import, no ticket fields
            tabs.Visible = two.Visible = custLabel.Visible = fCustomer.Visible = notesLabel.Visible = fNotes.Visible = Settings.Expert;

            runButton = UI.Button(Settings.Expert ? "Run test on this PC" : "Test this laptop", true);
            runButton.Font = Theme.Semi(10.5f);
            runButton.AutoSize = false; runButton.Height = Theme.Px(42); runButton.Margin = new Padding(0, Theme.Px(16), 0, 0);
            runButton.Click += async (o, e) => await RunClicked();
            UI.Add(s, runButton);
            tipLabel = UI.Label("Tip: run it at intake, before charging. It needs recent use on battery to judge power consumption.", Theme.F(8.5f), Theme.Muted);
            tipLabel.Margin = new Padding(0, Theme.Px(8), 0, 0);
            UI.Add(s, tipLabel);
            card.Resize += (o, e) => { int w = card.ClientSize.Width - card.Padding.Horizontal - Theme.Px(4); tipLabel.MaximumSize = new Size(w, 0); help.MaximumSize = new Size(w, 0); };

            tabLocal.CheckedChanged += (o, e) => SwitchTab();
            card.Controls.Add(s);

            if (!HasBattery())
            {
                tabImport.Checked = true;
                tabLocal.Enabled = false;
                if (!Settings.Expert) { runButton.Enabled = false; tipLabel.Text = "This PC has no battery. Switch to Expert mode to analyse reports from another laptop."; tipLabel.Visible = true; }
            }
            return card;
        }

        RadioButton TabButton(string text, bool on)
        {
            RadioButton r = new RadioButton { Text = text, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter, Checked = on, Dock = DockStyle.Fill, Height = Theme.Px(34), Cursor = Cursors.Hand, Margin = new Padding(0) };
            r.FlatAppearance.BorderColor = Theme.Line;
            r.FlatAppearance.CheckedBackColor = Theme.Accent;   // selected: Jabbla teal with white text
            r.FlatAppearance.MouseOverBackColor = Theme.Soft;
            r.BackColor = Theme.Card;
            Action style = () => { r.Font = r.Checked ? Theme.Semi(9.5f) : Theme.F(9.5f); r.ForeColor = r.Checked ? Color.White : Theme.Text; r.FlatAppearance.MouseOverBackColor = r.Checked ? Theme.AccentHover : Theme.Soft; };
            r.CheckedChanged += (s, e) => style();
            style();
            return r;
        }
        RadioButton ModeRadio(string text)
        {
            RadioButton r = new RadioButton { Text = text, AutoSize = false, Height = Theme.Px(44), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, Theme.Px(4), 0, 0), Cursor = Cursors.Hand };
            return r;
        }
        TextBox FileRow(TableLayoutPanel parent, string what)
        {
            TableLayoutPanel row = UI.Columns(100, 0);
            TextBox t = UI.Text(); t.ReadOnly = true; t.BackColor = Theme.Card; UI.Cue(t, "No file chosen");
            Button b = UI.Button("Browse…");
            b.Click += (s, e) =>
            {
                using (OpenFileDialog d = new OpenFileDialog { Filter = "powercfg XML (*.xml)|*.xml|All files|*.*", Title = "Choose the " + what.ToLower() + " XML" })
                    if (d.ShowDialog(this) == DialogResult.OK) t.Text = d.FileName;
            };
            row.Controls.Add(t, 0, 0); row.Controls.Add(b, 1, 0);
            UI.Add(parent, row);
            return t;
        }
        void SwitchTab()
        {
            bool local = tabLocal.Checked;
            localPanel.Visible = local; importPanel.Visible = !local;
            runButton.Text = (local ? (Settings.Expert ? "Run test on this PC" : "Test this laptop") : "Analyse reports").ToUpperInvariant();
            tipLabel.Visible = local;
        }
        static bool HasBattery()
        {
            try { return SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery; } catch { return true; }
        }

        Control BuildHistoryCard()
        {
            Card card = new Card { Dock = DockStyle.Fill };
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            TableLayoutPanel top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, Theme.Px(10)) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Px(290)));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Px(190)));
            Label title = UI.Label("Test history", Theme.Head(14f)); title.Anchor = AnchorStyles.Left;
            search = UI.Text(); UI.Cue(search, "Search ticket, customer, device, serial…"); search.Anchor = AnchorStyles.Left | AnchorStyles.Right; search.Dock = DockStyle.None; search.Margin = new Padding(0, Theme.Px(3), Theme.Px(8), 0);
            search.TextChanged += (s, e) => FillGrid();
            filter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.F(9.5f), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, Theme.Px(3), 0, 0) };
            filter.Items.AddRange(new object[] { "All tests", "Replace battery", "Software / config", "No fault / OK" });
            if (Settings.Expert) filter.Items.Add("Open tickets");
            filter.SelectedIndex = 0;
            filter.SelectedIndexChanged += (s, e) => FillGrid();
            top.Controls.Add(title, 0, 0); top.Controls.Add(search, 1, 0); top.Controls.Add(filter, 2, 0);
            t.Controls.Add(top, 0, 0);

            grid = UI.Grid();
            UI.Col(grid, "date", "Date", 135, "dd-MM-yyyy HH:mm");
            UI.Col(grid, "ticket", "Ticket", 65);
            UI.Col(grid, "customer", "Customer", 85);
            UI.Col(grid, "device", "Device", 90, null, false, true);
            UI.Col(grid, "serial", "Jabbla serial", 110);
            UI.Col(grid, "health", "Health", 92, "0.0'%'", true);
            UI.Col(grid, "cycles", "Cycles", 52, "0", true);
            UI.Col(grid, "use", "Use W", 52, "0.0", true);
            UI.Col(grid, "standby", "Standby", 72, "0.0' %/h'", true);
            UI.Col(grid, "verdict", "Verdict", 150);
            UI.Col(grid, "status", "Status", 120);
            grid.Sort(grid.Columns["date"], System.ComponentModel.ListSortDirection.Descending);
            if (!Settings.Expert)
                foreach (string c in new[] { "ticket", "customer", "use", "standby", "status" }) grid.Columns[c].Visible = false;
            grid.CellPainting += GridCellPainting;
            grid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && grid.CurrentRow != null) { e.Handled = true; ShowDetail((TestRecord)grid.CurrentRow.Tag); } };
            grid.CellClick += (s, e) => { if (e.RowIndex >= 0) ShowDetail((TestRecord)grid.Rows[e.RowIndex].Tag); };
            grid.Cursor = Cursors.Hand;
            t.Controls.Add(grid, 0, 1);

            Label empty = UI.Label("No tests yet. Run your first test on the left — every result is saved here.", Theme.F(10f), Theme.Muted);
            empty.AutoSize = false; empty.TextAlign = ContentAlignment.MiddleCenter; empty.Dock = DockStyle.Fill; empty.Visible = false; empty.Name = "empty";
            grid.Controls.Add(empty);

            card.Controls.Add(t);
            return card;
        }

        void GridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            string col = grid.Columns[e.ColumnIndex].Name;
            if (col != "verdict" && col != "health" && col != "status") return;
            TestRecord r = (TestRecord)grid.Rows[e.RowIndex].Tag;
            UI.FillCell(e);
            Rectangle b = Rectangle.Inflate(e.CellBounds, -Theme.Px(6), -Theme.Px(6));
            if (col == "verdict") Theme.DrawPill(e.Graphics, r.ShortVerdict, Theme.Semi(7.5f), Theme.ByName(r.ColorName), b, false);
            else if (col == "status")
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Size ts = TextRenderer.MeasureText(r.Status, Theme.F(8.5f));
                // (BottomLine below resets the smoothing mode)
                Rectangle pr = new Rectangle(b.X, b.Y + (b.Height - ts.Height - Theme.Px(2)) / 2, Math.Min(b.Width, ts.Width + Theme.Px(8)), ts.Height + Theme.Px(2));
                using (GraphicsPath p = Theme.Round(pr, pr.Height / 2f)) { using (SolidBrush br = new SolidBrush(Theme.Soft)) e.Graphics.FillPath(br, p); using (Pen pen = new Pen(Theme.Line)) e.Graphics.DrawPath(pen, p); }
                TextRenderer.DrawText(e.Graphics, r.Status, Theme.F(8.5f), pr, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else
            {
                double? h = r.Health;
                int barW = Theme.Px(40), barH = Theme.Px(6);
                Rectangle bar = new Rectangle(b.Right - barW, b.Y + (b.Height - barH) / 2, barW, barH);
                using (SolidBrush br = new SolidBrush(Theme.Soft)) e.Graphics.FillRectangle(br, bar);
                if (h.HasValue) using (SolidBrush br = new SolidBrush(Theme.Health(h.Value))) e.Graphics.FillRectangle(br, bar.X, bar.Y, (int)(barW * Math.Max(0, Math.Min(100, h.Value)) / 100), barH);
                TextRenderer.DrawText(e.Graphics, h.HasValue ? h.Value.ToString("0.0") + "%" : "—", e.CellStyle.Font, new Rectangle(b.X - Theme.Px(6), b.Y, b.Width - barW, b.Height), Theme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            UI.BottomLine(e);
            e.Handled = true;
        }

        void Reload()
        {
            try { tests = Store.LoadAll(); }
            catch (Exception ex) { MessageBox.Show(this, "Could not read the Reports folder:\n" + ex.Message, "Battery Check", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            int n = tests.Count;
            Func<string, int> cnt = c => tests.Count(t => t.Category == c);
            Func<int, string> pct = v => n > 0 ? Math.Round(100.0 * v / n) + "% of tests" : "";
            tTotal.Set(n.ToString(), n > 0 ? "since " + tests.Last().Created.ToString("dd MMM yyyy") : "none yet");
            tReplace.Set(cnt("replace").ToString(), pct(cnt("replace")));
            tSoftware.Set(cnt("software").ToString(), n > 0 ? pct(cnt("software")) + " — battery not the cause" : "");
            tOk.Set(cnt("ok").ToString(), pct(cnt("ok")));
            List<double> hs = tests.Where(t => t.Health.HasValue).Select(t => t.Health.Value).ToList();
            tAvg.Set(hs.Count > 0 ? hs.Average().ToString("0.0") + "%" : "—", "across all tests");
            FillGrid();
        }

        void FillGrid()
        {
            string q = search.Text.Trim().ToLowerInvariant();
            string qNoSpace = q.Replace(" ", "");
            int f = filter.SelectedIndex;
            DataGridViewColumn sortCol = grid.SortedColumn;
            SortOrder order = grid.SortOrder;
            grid.Rows.Clear();
            foreach (TestRecord r in tests)
            {
                if (f == 1 && r.Category != "replace") continue;
                if (f == 2 && r.Category != "software") continue;
                if (f == 3 && r.Category != "ok") continue;
                if (f == 4 && r.Status != "Open") continue;
                if (q != "")
                {
                    string hay = string.Join(" ", new string[] { r.Ticket, r.Customer, r.Technician, r.Computer, r.Model, r.Manufacturer, r.BatterySerial, J.Str(r.Data, "System.ServiceTag"), r.Verdict, r.Status }).ToLowerInvariant();
                    if (!hay.Contains(q) && !hay.Replace(" ", "").Contains(qNoSpace)) continue;
                }
                int i = grid.Rows.Add(r.Created, r.Ticket == "" ? "—" : r.Ticket, r.Customer == "" ? "—" : r.Customer, r.Model == "" ? "—" : r.Model, r.Computer == "" ? "—" : r.Computer, r.Health, r.Cycles, r.ActiveW, r.StandbyPctH, r.Verdict, r.Status);
                grid.Rows[i].Tag = r;
                grid.Rows[i].Cells["device"].ToolTipText = r.Device;
                if (r.DeviceSerial != "") grid.Rows[i].Cells["serial"].ToolTipText = "Surface serial: " + r.DeviceSerial;
                grid.Rows[i].Cells["verdict"].ToolTipText = r.Verdict;
            }
            if (sortCol != null && order != SortOrder.None) grid.Sort(sortCol, order == SortOrder.Ascending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending);
            grid.ClearSelection();
            grid.CurrentCell = null;
            Control empty = grid.Controls["empty"];
            if (empty != null)
            {
                empty.Text = tests.Count == 0 ? "No tests yet. Run your first test on the left — every result is saved here." : "No tests match this search.";
                empty.Visible = grid.Rows.Count == 0;
            }
        }

        Dictionary<string, object> ReadMeta()
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["Ticket"] = fTicket.Text.Trim(); m["Customer"] = fCustomer.Text.Trim(); m["Technician"] = fTech.Text.Trim(); m["Notes"] = fNotes.Text.Trim();
            return m;
        }

        async Task RunClicked()
        {
            Dictionary<string, object> meta = ReadMeta();
            List<string> args = new List<string>();
            string title, sub, importDir = null;
            if (tabLocal.Checked)
            {
                bool adv = true;   // one test: battery report + sleep study
                args.Add("-Days"); args.Add(((int)days.Value).ToString());
                if (adv) args.Add("-Advanced");
                title = adv ? "Running advanced test…" : "Running basic test…";
                sub = adv ? "Collecting the battery report and sleep study." : "Collecting the battery report.";
                if (adv && !Runner.IsAdmin()) sub += " Accept the Windows permission prompt.";
            }
            else
            {
                if (!File.Exists(fBatteryXml.Text)) { MessageBox.Show(this, "Choose the battery report XML first.", "Battery Check", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                string err = CheckXml(fBatteryXml.Text, "BatteryReport", "battery report");
                if (err == null && fSleepXml.Text != "") err = CheckXml(fSleepXml.Text, "SleepStudy", "sleep study");
                if (err != null) { MessageBox.Show(this, err, "Battery Check", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                // keep a copy of the imported files with the result
                string dir = Path.Combine(Runner.ReportsDir, "import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(dir);
                importDir = dir;
                string bx = Path.Combine(dir, "batteryreport.xml");
                File.Copy(fBatteryXml.Text, bx, true);
                args.Add("-BatteryReportXml"); args.Add(bx);
                if (fSleepXml.Text != "")
                {
                    string sx = Path.Combine(dir, "sleepstudy.xml");
                    File.Copy(fSleepXml.Text, sx, true);
                    args.Add("-SleepStudyXml"); args.Add(sx);
                }
                title = "Analysing reports…";
                sub = fSleepXml.Text != "" ? "Battery report + sleep study" : "Battery report";
            }

            using (RunDialog d = new RunDialog(title, sub, (progress, cancel) => Runner.RunAsync(args, meta, progress, cancel)))
            {
                d.ShowDialog(this);
                if (d.ResultId == null)
                {
                    if (importDir != null) try { Directory.Delete(importDir, true); } catch { }
                    if (!d.Cancelled) MessageBox.Show(this, "The test failed:\n\n" + d.Error, "Battery Check", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                fTicket.Text = ""; fCustomer.Text = ""; fNotes.Text = ""; fBatteryXml.Text = ""; fSleepXml.Text = "";
                Reload();
                TestRecord r = tests.FirstOrDefault(t => t.Id == d.ResultId);
                if (r != null) ShowDetail(r);
            }
            await Task.FromResult(0);
        }

        static string CheckXml(string path, string root, string what)
        {
            try
            {
                using (StreamReader sr = new StreamReader(path))
                {
                    char[] buf = new char[4096];
                    int n = sr.Read(buf, 0, buf.Length);
                    string head = new string(buf, 0, n);
                    if (head.IndexOf("<" + root, StringComparison.Ordinal) < 0) return Path.GetFileName(path) + " is not a powercfg " + what + " XML file. Create it with the /xml option.";
                }
                return null;
            }
            catch (Exception ex) { return "Cannot read " + Path.GetFileName(path) + ": " + ex.Message; }
        }

        void ExportCsv()
        {
            using (SaveFileDialog d = new SaveFileDialog { Filter = "CSV (Excel)|*.csv", FileName = "battery-tests-" + DateTime.Now.ToString("yyyyMMdd") + ".csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { Store.ExportCsv(d.FileName, tests); Process.Start(new ProcessStartInfo(d.FileName) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, "Export failed: " + ex.Message, "Battery Check", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        // ---------- navigation ----------
        void ShowList()
        {
            if (detailView != null) { body.Controls.Remove(detailView); detailView.Dispose(); detailView = null; }
            listView.Visible = true;
            grid.Focus();
        }

        void ShowDetail(TestRecord r)
        {
            SuspendLayout();
            if (detailView != null) { body.Controls.Remove(detailView); detailView.Dispose(); }
            detailView = new DetailView(this, r, tests);
            body.Controls.Add(detailView);
            listView.Visible = false;
            ResumeLayout();
        }

        // Rebuild the screens for the chosen mode, keeping the open test (if any) open
        void ApplyMode()
        {
            SuspendLayout();
            string openId = detailView != null ? ((DetailView)detailView).RecordId : null;
            if (detailView != null) { body.Controls.Remove(detailView); detailView.Dispose(); detailView = null; }
            body.Controls.Remove(listView); listView.Dispose();
            listView = BuildListView();
            body.Controls.Add(listView);
            Reload();
            if (openId != null) { TestRecord r = tests.FirstOrDefault(t => t.Id == openId); if (r != null) ShowDetail(r); }
            ResumeLayout();
        }

        // called by DetailView
        public void Back() { ShowList(); }
        public void Saved() { Reload(); }
        public void Open(TestRecord r) { ShowDetail(tests.FirstOrDefault(t => t.Id == r.Id) ?? r); }
        public void Deleted() { Reload(); ShowList(); }
        public static string[] StatusList { get { return Statuses; } }
        public void OpenId(string id, int tab)
        {
            TestRecord r = tests.FirstOrDefault(t => t.Id == id);
            if (r == null) return;
            ShowDetail(r);
            ((DetailView)detailView).SelectTab(tab);
        }
    }

    // =========================================================================================
    // Detail view of one test
    // =========================================================================================
    class DetailView : TableLayoutPanel
    {
        MainForm main;
        TestRecord r;
        List<TestRecord> all;
        TabControl tabs;
        public void SelectTab(int i) { if (tabs != null && i >= 0 && i < tabs.TabCount) tabs.SelectedIndex = i; }
        public string RecordId { get { return r.Id; } }

        public DetailView(MainForm main, TestRecord r, List<TestRecord> all)
        {
            this.main = main; this.r = r; this.all = all;
            Dock = DockStyle.Fill; ColumnCount = 1; RowCount = 4; Margin = new Padding(0);
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Px(150)));
            RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Px(104)));
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(BuildToolbar(), 0, 0);
            // Applied after the current layout pass; a row height changed during layout is ignored
            Action<int> setHeaderRow = h =>
            {
                if (Math.Abs(RowStyles[1].Height - h) <= 0.5f) return;
                MethodInvoker apply = () => { RowStyles[1].Height = h; PerformLayout(); };
                if (IsHandleCreated) BeginInvoke(apply); else apply();
            };
            Controls.Add(BuildHeader(setHeaderRow), 0, 1);
            Controls.Add(BuildKpis(), 0, 2);

            TableLayoutPanel bodyT = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            bodyT.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bodyT.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Px(360)));
            bodyT.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            if (Settings.Expert)
            {
                bodyT.Controls.Add(BuildTabs(), 0, 0);
                bodyT.Controls.Add(BuildTicketCard(), 1, 0);
            }
            else
            {
                // Basic: up to four short points in plain words, full width
                bodyT.ColumnStyles[1].Width = 0;
                Card c = new Card { Dock = DockStyle.Fill, Padding = new Padding(Theme.Px(22), Theme.Px(16), Theme.Px(22), Theme.Px(16)) };
                List<object> points = J.List(r.Data, "Basic.Points");
                if (points.Count == 0) { Control f = BuildFindings(); f.Dock = DockStyle.Fill; c.Padding = new Padding(Theme.Px(8)); c.Controls.Add(f); }   // tests from older versions
                else
                {
                    TableLayoutPanel s = UI.Stack();
                    UI.Add(s, UI.Label("What we found", Theme.Head(14f)));
                    TableLayoutPanel g = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, Theme.Px(10), 0, 0) };
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Px(110)));
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    foreach (object p in points)
                    {
                        string lvl = J.Str(p, "Level");
                        string tag = lvl == "HIGH" ? "PROBLEM" : lvl == "ELEVATED" ? "ATTENTION" : "OK";
                        PillLabel pill = new PillLabel(tag, Theme.Level(lvl)); pill.Margin = new Padding(0, Theme.Px(9), 0, Theme.Px(9));
                        Label txt = UI.Label(J.Str(p, "Text"), Theme.F(11.5f)); txt.Margin = new Padding(0, Theme.Px(6), 0, Theme.Px(6));
                        txt.MaximumSize = new Size(Theme.Px(1100), 0);
                        g.Controls.Add(pill); g.Controls.Add(txt);
                    }
                    UI.Add(s, g, false);
                    Label more = UI.Label("Switch to Expert (top right) for all details, figures and the cause of each point.", Theme.F(9f), Theme.Muted);
                    more.Margin = new Padding(0, Theme.Px(14), 0, 0);
                    UI.Add(s, more);
                    c.Controls.Add(s);
                }
                bodyT.Controls.Add(c, 0, 0);
            }
            Controls.Add(bodyT, 0, 3);
        }

        Control BuildToolbar()
        {
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(Theme.Px(6), 0, Theme.Px(6), Theme.Px(6)) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Button back = UI.Button("←  All tests"); back.Margin = new Padding(0);
            back.Click += (s, e) => main.Back();
            t.Controls.Add(back, 0, 0);
            FlowLayoutPanel right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            Button report = UI.Button("Open / print full report");
            report.Click += (s, e) => { if (File.Exists(r.HtmlPath)) Process.Start(new ProcessStartInfo(r.HtmlPath) { UseShellExecute = true }); else Msg("The HTML report for this test is missing."); };
            Button files = UI.Button("Raw files");
            files.Click += (s, e) =>
            {
                string raw = Path.Combine(Runner.ReportsDir, r.RawDirName);
                if (r.RawDirName != "" && Directory.Exists(raw)) Process.Start("explorer.exe", Runner.Q(raw));
                else Process.Start("explorer.exe", "/select," + Runner.Q(r.JsonPath));
            };
            Button del = UI.Button("Delete", false, true);
            del.Click += (s, e) =>
            {
                if (MessageBox.Show(main, "Delete this test and its saved reports?\nThis cannot be undone.", "Delete test", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                try { Store.Delete(r, all); main.Deleted(); }
                catch (Exception ex) { Msg("Delete failed: " + ex.Message); }
            };
            files.Visible = del.Visible = Settings.Expert;
            right.Controls.Add(report); right.Controls.Add(files); right.Controls.Add(del);
            t.Controls.Add(right, 1, 0);
            return t;
        }

        Control BuildHeader(Action<int> setRowHeight)
        {
            Card c = new Card { Dock = DockStyle.Fill, Stripe = Theme.ByName(r.ColorName) };
            c.Padding = new Padding(Theme.Px(22), Theme.Px(12), Theme.Px(18), Theme.Px(14));
            TableLayoutPanel s = UI.Stack();
            string sysLine = string.Join("  ·  ", new string[] { (r.Manufacturer + " " + r.Model).Trim(), r.Computer, r.Created.ToString("dd MMM yyyy HH:mm"), J.Str(r.Data, "Mode") }.Where(x => x != "").ToArray());
            List<string> metaBits = new List<string>();
            if (r.Ticket != "") metaBits.Add("Ticket " + r.Ticket);
            if (r.Customer != "") metaBits.Add("Customer " + r.Customer);
            if (r.Technician != "") metaBits.Add("Technician " + r.Technician);
            if (metaBits.Count > 0) sysLine += "  ·  " + string.Join("  ·  ", metaBits.ToArray());
            UI.Add(s, UI.Label(sysLine, Theme.F(8.5f), Theme.Muted));
            UI.Add(s, UI.Label(r.Verdict, Theme.Head(21f), Theme.ByName(r.ColorName)));
            string advice = J.Str(r.Data, "Basic.Advice");
            Label summary = !Settings.Expert && advice != "" ? UI.Label(advice, Theme.F(12.5f), Theme.Text) : UI.Label(J.Str(r.Data, "Summary"), Theme.F(10f));
            UI.Add(s, summary);
            FlowLayoutPanel pills = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, Theme.Px(8), 0, 0) };
            pills.Controls.Add(UI.Label("Battery", Theme.F(9f), Theme.Muted));
            pills.Controls.Add(new PillLabel(J.Str(r.Data, "BatteryLevel"), Theme.Level(J.Str(r.Data, "BatteryLevel"))));
            pills.Controls.Add(UI.Label("Power consumption", Theme.F(9f), Theme.Muted));
            pills.Controls.Add(new PillLabel(J.Str(r.Data, "PowerLevel"), Theme.Level(J.Str(r.Data, "PowerLevel"))));
            if (Settings.Expert || advice == "") UI.Add(s, pills, false);   // Basic: the tiles already say this
            c.Controls.Add(s);
            FitHeight(c, s, setRowHeight, summary);
            return c;
        }

        // Size a card to its stacked content for the current width (wrapping labels get a max width first)
        static void FitHeight(Card c, TableLayoutPanel content, Action<int> setRowHeight, params Label[] wrap)
        {
            bool busy = false;
            EventHandler fit = (o, e) =>
            {
                if (busy || c.ClientSize.Width <= 0) return;
                busy = true;
                int w = c.ClientSize.Width - c.Padding.Horizontal;
                foreach (Label l in wrap) l.MaximumSize = new Size(Math.Max(100, w - Theme.Px(6)), 0);
                int h = c.Padding.Vertical;
                foreach (Control ch in content.Controls)
                {
                    Label l = ch as Label;
                    int ph = l != null
                        ? TextRenderer.MeasureText(l.Text, l.Font, new Size(Math.Max(100, w - Theme.Px(6)), 0), TextFormatFlags.WordBreak).Height
                        : ch.PreferredSize.Height;
                    h += ph + ch.Margin.Vertical;
                }
                setRowHeight(h + c.Margin.Vertical);
                busy = false;
            };
            c.Resize += fit;
            c.HandleCreated += fit;
        }

        Control BuildKpis()
        {
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = new Padding(0) };
            for (int i = 0; i < 6; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            double? health = r.Health, cycles = r.Cycles;
            double? full = J.Num(r.Data, "Battery.FullChargemWh"), design = J.Num(r.Data, "Battery.DesignmWh");
            double? w = r.ActiveW, sb = r.StandbyPctH;
            double? wearShare = J.Num(r.Data, "Runtime.WearSharePct"), consShare = J.Num(r.Data, "Runtime.ConsumptionSharePct");
            List<object> packs = J.List(r.Data, "Battery.Packs");
            string healthSub = Theme.N(full, 0) + " / " + Theme.N(design, 0) + " mWh";
            if (packs.Count > 1)
            {
                int k = 0;
                healthSub = string.Join(" · ", packs.Select(p => "battery " + (++k) + ": " + Theme.N(J.Num(p, "HealthPct"), 0, "%")).ToArray());
            }
            if (!Settings.Expert)
            {
                // Basic: four plain figures
                t.ColumnCount = 4; t.ColumnStyles.Clear();
                for (int i = 0; i < 4; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                Func<string, string> word = l => l == "OK" || l == "NORMAL" ? "normal" : l == "FAIR" ? "normal ageing" : l == "ELEVATED" || l == "WORN" ? "above normal" : l == "HIGH" || l == "REPLACE" ? "too high" : "";
                string bl = J.Str(r.Data, "BatteryLevel");
                string healthWord = bl == "OK" ? "good" : bl == "FAIR" ? "normal ageing" : bl == "WORN" ? "worn" : bl == "REPLACE" ? "worn out" : "not reliable";
                t.Controls.Add(new Tile("Battery health", Theme.N(health.HasValue ? Math.Min(100, health.Value) : (double?)null, 0, "%"), healthWord, health.HasValue && bl != "UNKNOWN" ? Theme.Health(health.Value) : Theme.Line), 0, 0);
                double? lifeNow = J.Num(r.Data, "Runtime.NowAtObserved"), lifeNew = J.Num(r.Data, "Runtime.NewAtObserved");
                t.Controls.Add(new Tile("Battery life now", Theme.Hours(lifeNow), lifeNew.HasValue ? "with a new battery: " + Theme.Hours(lifeNew) : "", Color.Empty), 1, 0);
                string al = J.Str(r.Data, "Consumption.ActiveLevel"), sl = J.Str(r.Data, "Consumption.StandbyLevel");
                t.Controls.Add(new Tile("Power use", Theme.N(w, 0, " W"), word(al), Theme.Level(al)), 2, 0);
                t.Controls.Add(new Tile("Lost per day in standby", sb.HasValue ? Math.Min(100, Math.Round(sb.Value * 24)) + "%" : "—", word(sl), Theme.Level(sl)), 3, 0);
                return t;
            }
            t.Controls.Add(new Tile(packs.Count > 1 ? "Battery health (" + packs.Count + " batteries)" : "Battery health", Theme.N(health, 1, "%"), healthSub, health.HasValue ? Theme.Health(health.Value) : Theme.Line), 0, 0);
            t.Controls.Add(new Tile("Cycle count", Theme.N(cycles, 0), "", Color.Empty), 1, 0);
            t.Controls.Add(new Tile("Use on battery", Theme.N(w, 1, " W"), Theme.Hours(J.Num(r.Data, "Consumption.ActiveHoursMeasured")) + " measured", Theme.Level(J.Str(r.Data, "Consumption.ActiveLevel"))), 2, 0);
            t.Controls.Add(new Tile("Standby drain", Theme.N(sb, 2, " %/h"), Theme.Hours(J.Num(r.Data, "Consumption.StandbyHoursMeasured")) + " measured", Theme.Level(J.Str(r.Data, "Consumption.StandbyLevel"))), 3, 0);
            double? nowH = J.Num(r.Data, "Runtime.NowAtObserved"), newH = J.Num(r.Data, "Runtime.NewAtObserved");
            string gainTxt = "";
            if (nowH.HasValue && newH.HasValue)
            {
                double gainMin = (newH.Value - nowH.Value) * 60;
                gainTxt = "new battery: " + Theme.Hours(newH) + (gainMin >= 1 ? " (+" + Math.Round(gainMin) + " min)" : " (no gain)");
                double? heavy = J.Num(r.Data, "Runtime.NowHeavy");
                if (heavy.HasValue) gainTxt = "heavier use: " + Theme.Hours(heavy) + " · " + gainTxt;
            }
            t.Controls.Add(new Tile("Runtime now", Theme.Hours(nowH), gainTxt, Color.Empty), 4, 0);
            t.Controls.Add(new Tile("Shortfall from wear", wearShare.HasValue ? Math.Round(wearShare.Value) + "%" : "—", consShare.HasValue ? Math.Round(consShare.Value) + "% from consumption" : "", Color.Empty), 5, 0);
            return t;
        }

        // ---------- tabs ----------
        Control BuildTabs()
        {
            Card c = new Card { Dock = DockStyle.Fill, Padding = new Padding(Theme.Px(8)) };
            tabs = new TabControl { Dock = DockStyle.Fill, Font = Theme.F(9.5f), Padding = new Point(Theme.Px(14), Theme.Px(5)) };
            tabs.TabPages.Add(Page("Findings", BuildFindings()));
            tabs.TabPages.Add(Page("Runtime & capacity", new OverviewPanel(r)));
            tabs.TabPages.Add(Page("Battery sessions", BuildSessions()));
            if (J.Get(r.Data, "SleepStudy") != null) tabs.TabPages.Add(Page("Sleep study", BuildSleepStudy()));
            tabs.TabPages.Add(Page("Battery & system", BuildSystem()));
            tabs.TabPages.Add(Page("Device history", BuildDeviceHistory()));
            c.Controls.Add(tabs);
            return c;
        }
        static TabPage Page(string title, Control content)
        {
            TabPage p = new TabPage(title) { BackColor = Theme.Card, Padding = new Padding(Theme.Px(6)) };
            content.Dock = DockStyle.Fill;
            p.Controls.Add(content);
            return p;
        }

        static int LevelOrder(string l)
        {
            switch (l) { case "HIGH": return 0; case "ELEVATED": return 1; case "FAIR": return 2; case "OK": return 3; default: return 4; }
        }

        Control BuildFindings()
        {
            DataGridView g = UI.Grid(false);
            g.Cursor = Cursors.Default;
            UI.Col(g, "level", "", 95).FillWeight = 60;
            UI.Col(g, "area", "Area", 95).FillWeight = 70;
            DataGridViewColumn text = UI.Col(g, "text", "Finding", 300, null, false, true);
            text.FillWeight = 600;
            text.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            g.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            g.DefaultCellStyle.Padding = new Padding(Theme.Px(4), Theme.Px(7), Theme.Px(4), Theme.Px(7));
            foreach (DataGridViewColumn col in g.Columns) col.SortMode = DataGridViewColumnSortMode.NotSortable;
            foreach (object f in J.List(r.Data, "Findings").OrderBy(x => LevelOrder(J.Str(x, "Level"))))
            {
                int i = g.Rows.Add(J.Str(f, "Level"), J.Str(f, "Area"), J.Str(f, "Text"));
                g.Rows[i].Cells["area"].Style.ForeColor = Theme.Muted;
            }
            g.CellPainting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
                UI.FillCell(e);
                string l = Convert.ToString(e.Value);
                Rectangle b = new Rectangle(e.CellBounds.X + Theme.Px(6), e.CellBounds.Y + Theme.Px(6), e.CellBounds.Width - Theme.Px(12), Theme.Px(22));
                Theme.DrawPill(e.Graphics, l, Theme.Semi(7.5f), l == "INFO" ? Theme.Grey : Theme.Level(l), b, false);
                UI.BottomLine(e);
                e.Handled = true;
            };
            return g;
        }

        Control BuildSessions()
        {
            List<object> rows = J.List(r.Data, "Consumption.Sessions");
            if (rows.Count == 0) return Note("Session details are not stored in this (older) result. Open the full report to see them.");
            DataGridView g = UI.Grid(false); g.Cursor = Cursors.Default;
            UI.Col(g, "start", "Start", 160, "dd MMM yyyy  HH:mm");
            UI.Col(g, "type", "State", 150);
            UI.Col(g, "dur", "Duration", 100, null, true);
            UI.Col(g, "mwh", "Used", 100, "#,0' mWh'", true);
            UI.Col(g, "w", "Avg power", 100, "0.0' W'", true);
            UI.Col(g, "pct", "% per hour", 100, "0.0", true, true);
            foreach (object x in rows)
            {
                DateTime dt; object start = DateTime.TryParse(J.Str(x, "Start"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt) ? (object)dt : J.Str(x, "Start");
                g.Rows.Add(start, J.Str(x, "Type"), Theme.Hours(J.Num(x, "Hours")), J.Num(x, "mWh"), J.Num(x, "W"), J.Num(x, "PctPerHour"));
            }
            return WithNote(g, "Sessions on battery of 5 minutes or longer, from the battery report.");
        }

        Control BuildSleepStudy()
        {
            // Sessions first and full width: the per-session pattern is the key to explaining standby drain
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 44)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 28)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 28));

            DataGridView s = UI.Grid(false); s.Cursor = Cursors.Default;
            UI.Col(s, "start", "Start", 140, "dd MMM  HH:mm");
            UI.Col(s, "dur", "Duration", 80, null, true);
            UI.Col(s, "pct", "%/hour", 70, "0.00", true);
            UI.Col(s, "drips", "HW sleep", 75, "0'%'", true);
            UI.Col(s, "sw", "Win sleep", 75, "0'%'", true);
            UI.Col(s, "pattern", "Pattern", 150);
            UI.Col(s, "top", "Top blocker", 200, null, false, true);
            foreach (object x in J.List(r.Data, "SleepStudy.Sessions"))
            {
                DateTime dt; object start = DateTime.TryParse(J.Str(x, "Start"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt) ? (object)dt : J.Str(x, "Start");
                string pat = J.Str(x, "Pattern");
                string patText = pat == "Asleep" ? "Slept properly" : pat == "HwOnly" ? "Windows asleep, hardware not" : pat == "Awake" ? "Kept awake" : "";
                string blocker = J.Str(x, "SocBlocker") != "" ? J.Str(x, "SocBlocker") + "; " + J.Str(x, "TopBlocker") : J.Str(x, "TopBlocker");
                int i = s.Rows.Add(start, Theme.Hours(J.Num(x, "Hours")), J.Num(x, "PctPerHour"), J.Num(x, "HwDripsPct"), J.Num(x, "SwDripsPct"), patText, blocker);
                if (pat == "HwOnly" || pat == "Awake") s.Rows[i].Cells["pattern"].Style.ForeColor = Theme.Orange;
                double? d = J.Num(x, "HwDripsPct");
                if (d.HasValue && d.Value < 80) { s.Rows[i].Cells["drips"].Style.ForeColor = Theme.Red; s.Rows[i].Cells["drips"].Style.Font = Theme.Semi(9.5f); }
            }
            Control sessionsSection = Section("Standby sessions on battery", s, "HW sleep: share of time the hardware spent in its deepest sleep state (healthy is above 80%). Win sleep: the same for Windows. Windows asleep with the hardware awake points to a driver, firmware or connected device.");
            t.Controls.Add(sessionsSection, 0, 0);
            t.SetColumnSpan(sessionsSection, 2);

            DataGridView w = UI.Grid(false); w.Cursor = Cursors.Default;
            UI.Col(w, "name", "App / service", 200, null, false, true);
            UI.Col(w, "via", "Via", 120);
            UI.Col(w, "min", "Active", 80, "0.0' min'", true);
            foreach (object x in J.List(r.Data, "SleepStudy.WakeApps")) w.Rows.Add(J.Str(x, "Name"), J.Str(x, "Via"), J.Num(x, "Minutes"));
            DataGridView wk = UI.Grid(false); wk.Cursor = Cursors.Default;
            UI.Col(wk, "reason", "Reason", 220, null, false, true);
            UI.Col(wk, "kind", "Kind", 120);
            UI.Col(wk, "n", "Times", 60, "0", true);
            foreach (object x in J.List(r.Data, "SleepStudy.Wakes"))
            {
                string kind = J.Str(x, "Kind");
                int i = wk.Rows.Add(J.Str(x, "Reason"), kind == "User" ? "By the user" : kind == "Charger" ? "Charger" : kind == "Ended" ? "Standby ended" : kind == "Drain" ? "Drained too fast" : "By itself", J.Num(x, "Count"));
                if (kind == "Automatic" || (kind == "Drain" && J.Num(x, "AwakeCount") > 0)) { wk.Rows[i].Cells["kind"].Style.ForeColor = Theme.Orange; wk.Rows[i].Cells["kind"].Style.Font = Theme.Semi(9.5f); }
            }
            List<object> armed = J.List(r.Data, "WakeConfig.ArmedDevices");
            t.Controls.Add(Section("Why standby ended (on battery)", wk, armed.Count > 0 ? "Allowed to wake the laptop: " + string.Join("; ", armed.Select(dev => Convert.ToString(dev)).ToArray()) : "'By itself' = woken by maintenance, a timer or the network. 'Drained too fast' = never really slept."), 1, 1);

            DataGridView b = UI.Grid(false); b.Cursor = Cursors.Default;
            UI.Col(b, "name", "Component", 220, null, false, true);
            UI.Col(b, "type", "Type", 90);
            UI.Col(b, "pct", "% of standby", 100, "0'%'", true);
            foreach (object x in J.List(r.Data, "SleepStudy.Blockers")) b.Rows.Add(J.Str(x, "Name"), J.Str(x, "Type"), J.Num(x, "PctOfSleep"));
            t.Controls.Add(Section("Top blockers in standby", b, "Devices and drivers that keep the laptop awake."), 0, 1);

            DataGridView a = UI.Grid(false); a.Cursor = Cursors.Default;
            UI.Col(a, "name", "App / process", 200, null, false, true);
            UI.Col(a, "main", "Mainly", 80);
            UI.Col(a, "share", "Share", 70, "0'%'", true);
            UI.Col(a, "w", "Avg W", 70, "0.00", true);
            foreach (object x in J.List(r.Data, "SleepStudy.Apps")) a.Rows.Add(J.Str(x, "Name"), J.Str(x, "Main"), J.Num(x, "SharePct"), J.Num(x, "AvgW"));
            t.Controls.Add(Section("Software activity during standby", w, null), 0, 2);
            t.Controls.Add(Section("Energy per app while in use on battery", a, "Windows' own estimates. Shares are of the energy assigned to an app."), 1, 2);
            return t;
        }

        Control BuildSystem()
        {
            DataGridView g = UI.Grid(false); g.Cursor = Cursors.Default;
            g.ColumnHeadersVisible = false;
            UI.Col(g, "k", "", 190).DefaultCellStyle.ForeColor = Theme.Muted;
            UI.Col(g, "v", "", 300, null, false, true);
            foreach (DataGridViewColumn col in g.Columns) col.SortMode = DataGridViewColumnSortMode.NotSortable;
            int n = 1;
            foreach (object p in J.List(r.Data, "Battery.Packs"))
            {
                string pre = J.List(r.Data, "Battery.Packs").Count > 1 ? "Battery " + n++ + " – " : "Battery – ";
                g.Rows.Add(pre + "name", J.Str(p, "Id"));
                g.Rows.Add(pre + "manufacturer", J.Str(p, "Manufacturer"));
                g.Rows.Add(pre + "chemistry", J.Str(p, "Chemistry"));
                g.Rows.Add(pre + "serial", J.Str(p, "Serial"));
                g.Rows.Add(pre + "design capacity", Theme.N(J.Num(p, "Design"), 0, " mWh"));
                g.Rows.Add(pre + "full charge capacity", Theme.N(J.Num(p, "Full"), 0, " mWh"));
                g.Rows.Add(pre + "health", Theme.N(J.Num(p, "HealthPct"), 1, "%"));
                g.Rows.Add(pre + "cycles", Theme.N(J.Num(p, "Cycles"), 0));
            }
            IDictionary<string, object> sys = J.Get(r.Data, "System") as IDictionary<string, object>;
            if (sys != null) foreach (KeyValuePair<string, object> kv in sys) g.Rows.Add(kv.Key, Convert.ToString(kv.Value));
            return g;
        }

        static Control Section(string title, Control content, string note)
        {
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(Theme.Px(4)) };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (title != null)
            {
                Label l = UI.Label(title, Theme.Semi(9.5f)); l.Margin = new Padding(0, Theme.Px(4), 0, Theme.Px(4));
                t.Controls.Add(l, 0, 0);
            }
            content.Dock = DockStyle.Fill;
            t.Controls.Add(content, 0, 1);
            if (note != null) { Label n = UI.Label(note, Theme.F(8f), Theme.Muted); t.Controls.Add(n, 0, 2); }
            return t;
        }
        static Control WithNote(Control content, string note) { return Section(null, content, note); }
        static Control Note(string text)
        {
            Label l = UI.Label(text, Theme.F(9.5f), Theme.Muted);
            l.AutoSize = false; l.TextAlign = ContentAlignment.MiddleCenter;
            return l;
        }

        // ---------- side: ticket ----------
        Control BuildTicketCard()
        {
            Card c = new Card { Dock = DockStyle.Fill };
            TableLayoutPanel s = UI.Stack();
            UI.Add(s, UI.Label("Ticket", Theme.Head(13f)));
            UI.Add(s, UI.FieldLabel("Status"));
            ComboBox status = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.F(9.5f) };
            status.Items.AddRange(MainForm.StatusList);
            if (!status.Items.Contains(r.Status)) status.Items.Add(r.Status);
            status.SelectedItem = r.Status;
            UI.Add(s, status);
            TableLayoutPanel two = UI.Columns(50, 50);
            TableLayoutPanel c1 = UI.Stack(); c1.Margin = new Padding(0, 0, Theme.Px(6), 0);
            TableLayoutPanel c2 = UI.Stack(); c2.Margin = new Padding(Theme.Px(6), 0, 0, 0);
            UI.Add(c1, UI.FieldLabel("Ticket / RMA no.")); TextBox ticket = UI.Text(r.Ticket); UI.Add(c1, ticket);
            UI.Add(c2, UI.FieldLabel("Technician")); TextBox tech = UI.Text(r.Technician); UI.Add(c2, tech);
            two.Controls.Add(c1, 0, 0); two.Controls.Add(c2, 1, 0);
            UI.Add(s, two);
            UI.Add(s, UI.FieldLabel("Customer")); TextBox cust = UI.Text(r.Customer); UI.Add(s, cust);
            UI.Add(s, UI.FieldLabel("Notes")); TextBox notes = UI.Text(r.Notes, true); notes.Height = Theme.Px(64); UI.Add(s, notes);

            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, Theme.Px(12), 0, 0) };
            Button save = UI.Button("Save", true); save.Margin = new Padding(0);
            Label saved = UI.Label("", Theme.F(9f), Theme.Green); saved.Margin = new Padding(Theme.Px(10), Theme.Px(8), 0, 0);
            Timer clear = new Timer { Interval = 2500 };
            clear.Tick += (o, e) => { saved.Text = ""; clear.Stop(); };
            save.Click += (o, e) =>
            {
                try
                {
                    r.SetMeta("Status", Convert.ToString(status.SelectedItem));
                    r.SetMeta("Ticket", ticket.Text.Trim()); r.SetMeta("Technician", tech.Text.Trim());
                    r.SetMeta("Customer", cust.Text.Trim()); r.SetMeta("Notes", notes.Text.Trim());
                    Store.Save(r);
                    main.Saved();
                    saved.Text = "Saved"; clear.Start();
                }
                catch (Exception ex) { Msg("Save failed: " + ex.Message); }
            };
            row.Controls.Add(save); row.Controls.Add(saved);
            UI.Add(s, row, false);
            Label hint = UI.Label("The printed report shows the ticket details from when the test was run.", Theme.F(8f), Theme.Muted);
            hint.Margin = new Padding(0, Theme.Px(8), 0, 0);
            UI.Add(s, hint);
            c.Resize += (o, e) => hint.MaximumSize = new Size(Math.Max(50, c.ClientSize.Width - c.Padding.Horizontal), 0);
            c.AutoScroll = true;
            c.Controls.Add(s);
            return c;
        }

        // ---------- side: device history ----------
        Control BuildDeviceHistory()
        {
            Panel c = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
            List<TestRecord> same = all.Where(x => x == r || r.SameDevice(x)).OrderBy(x => x.Created).ToList();
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, same.Count > 1 ? Theme.Px(170) : 0));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.Controls.Add(UI.Label(same.Count > 1 ? "Health across " + same.Count + " tests of this device (click a row to open it)" : "History of this device", Theme.Semi(9.5f)), 0, 0);
            if (same.Count <= 1)
            {
                Label l = UI.Label("This is the only test of this device so far. Test it again later to see how its health changes.", Theme.F(9f), Theme.Muted);
                t.Controls.Add(l, 0, 2);
            }
            else
            {
                List<TestRecord> withH = same.Where(x => x.Health.HasValue).ToList();
                Panel spark = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, Theme.Px(6), 0, Theme.Px(6)) };
                typeof(Panel).InvokeMember("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty, null, spark, new object[] { true });
                spark.Paint += (o, e) => UI.DrawHealthChart(e.Graphics, spark.ClientRectangle, withH.Select(x => x.Health.Value).ToList(), null, null, withH.IndexOf(r));
                spark.Resize += (o, e) => spark.Invalidate();
                t.Controls.Add(spark, 0, 1);
                DataGridView g = UI.Grid(false);
                g.Cursor = Cursors.Hand;
                UI.Col(g, "date", "Date", 140, "dd MMM yyyy HH:mm");
                UI.Col(g, "h", "Health", 70, "0.0'%'", true);
                UI.Col(g, "c", "Cycles", 70, "0", true);
                UI.Col(g, "v", "Verdict", 220, null, false, true);
                UI.Col(g, "t", "Ticket", 90);
                foreach (DataGridViewColumn col in g.Columns) col.SortMode = DataGridViewColumnSortMode.NotSortable;
                foreach (TestRecord x in same.AsEnumerable().Reverse())
                {
                    int i = g.Rows.Add(x.Created, x.Health, x.Cycles, x.ShortVerdict, x.Ticket);
                    g.Rows[i].Tag = x;
                    if (x.Id == r.Id) foreach (DataGridViewCell cell in g.Rows[i].Cells) { cell.Style.BackColor = Theme.Select; cell.Style.Font = Theme.Semi(9.5f); }
                }
                g.CellClick += (o, e) => { if (e.RowIndex >= 0 && ((TestRecord)g.Rows[e.RowIndex].Tag).Id != r.Id) main.Open((TestRecord)g.Rows[e.RowIndex].Tag); };
                t.Controls.Add(g, 0, 2);
            }
            c.Controls.Add(t);
            return c;
        }

        void Msg(string text) { MessageBox.Show(main, text, "Battery Check", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // Runtime comparison, wear/consumption split and capacity history, painted
    class OverviewPanel : Panel
    {
        TestRecord r;
        public OverviewPanel(TestRecord r)
        {
            this.r = r; BackColor = Theme.Card; DoubleBuffered = true; ResizeRedraw = true; AutoScroll = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int x = Theme.Px(10), y = Theme.Px(10), w = ClientSize.Width - 2 * x;
            Font h = Theme.Semi(10.5f), f = Theme.F(9.5f), small = Theme.F(8.5f);
            TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak, R = TextFormatFlags.Right | TextFormatFlags.NoPadding;

            object rt = J.Get(r.Data, "Runtime");
            TextRenderer.DrawText(g, "Runtime: what would a new battery change?", h, new Rectangle(x, y, w, Theme.Px(22)), Theme.Text, L);
            y += Theme.Px(30);
            if (rt == null)
            {
                TextRenderer.DrawText(g, "Not enough use on battery was recorded to measure consumption, so runtime cannot be projected.", f, new Rectangle(x, y, w, Theme.Px(40)), Theme.Muted, L);
                y += Theme.Px(40);
            }
            else
            {
                int c1 = x + (int)(w * 0.55), c2 = x + (int)(w * 0.8), cw = (int)(w * 0.2);
                TextRenderer.DrawText(g, "Current battery", small, new Rectangle(c1 - cw, y, cw, Theme.Px(18)), Theme.Muted, R);
                TextRenderer.DrawText(g, "New battery", small, new Rectangle(c2 - cw + Theme.Px(10), y, cw, Theme.Px(18)), Theme.Muted, R);
                y += Theme.Px(22);
                string obs = "At measured consumption (" + Theme.N(J.Num(r.Data, "Consumption.ActiveAvgW"), 1, " W") + ")";
                string refW = "At normal consumption (" + Theme.N(J.Num(rt, "ReferenceW"), 0, " W") + ")";
                foreach (string[] row in new string[][] {
                    new string[] { obs, Theme.Hours(J.Num(rt, "NowAtObserved")), Theme.Hours(J.Num(rt, "NewAtObserved")) },
                    new string[] { refW, Theme.Hours(J.Num(rt, "NowAtReference")), Theme.Hours(J.Num(rt, "NewAtReference")) } })
                {
                    using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, x, y - Theme.Px(4), x + w, y - Theme.Px(4));
                    TextRenderer.DrawText(g, row[0], f, new Rectangle(x, y, (int)(w * 0.5), Theme.Px(22)), Theme.Text, L);
                    TextRenderer.DrawText(g, row[1], Theme.Semi(9.5f), new Rectangle(c1 - cw, y, cw, Theme.Px(22)), Theme.Text, R);
                    TextRenderer.DrawText(g, row[2], Theme.Semi(9.5f), new Rectangle(c2 - cw + Theme.Px(10), y, cw, Theme.Px(22)), Theme.Text, R);
                    y += Theme.Px(30);
                }
                // What the difference means, in plain words (a near-zero gain is a result, not an error)
                double? nowH = J.Num(rt, "NowAtObserved"), newH = J.Num(rt, "NewAtObserved");
                double? design = J.Num(r.Data, "Battery.DesignmWh"), full = J.Num(r.Data, "Battery.FullChargemWh");
                if (nowH.HasValue && newH.HasValue && design > 0 && full > 0)
                {
                    double gainMin = (newH.Value - nowH.Value) * 60, gainPct = 100 * (design.Value / full.Value - 1);
                    string lvl = J.Str(r.Data, "BatteryLevel");
                    string meaning = gainMin < 1 ? "A new battery would not make it last longer: the battery is not the reason for the short runtime."
                        : (lvl == "WORN" || lvl == "REPLACE") ? "A new battery would add " + Math.Round(gainMin) + " minutes: a clear improvement."
                        : gainPct < 10 ? "A new battery would add only " + Math.Round(gainMin) + " minutes: the battery is not the reason for the short runtime."
                        : lvl == "FAIR" ? "A new battery would add " + Math.Round(gainMin) + " minutes: a moderate improvement. Normal ageing alone does not justify a replacement."
                        : "A new battery would add " + Math.Round(gainMin) + " minutes: a modest improvement. The battery is still in good condition.";
                    Size ms = TextRenderer.MeasureText(g, meaning, Theme.Semi(9.5f), new Size(w, 0), L);
                    TextRenderer.DrawText(g, meaning, Theme.Semi(9.5f), new Rectangle(x, y, w, ms.Height), gainPct < 10 ? Theme.Blue : Theme.Text, L);
                    y += ms.Height + Theme.Px(10);
                }
                double? ws = J.Num(rt, "WearSharePct");
                y += Theme.Px(6);
                if (ws.HasValue)
                {
                    TextRenderer.DrawText(g, "Compared with a new battery at normal consumption, the runtime shortfall is caused by:", f, new Rectangle(x, y, w, Theme.Px(20)), Theme.Text, L);
                    y += Theme.Px(26);
                    int wear = (int)Math.Round(ws.Value), barH = Theme.Px(26);
                    int ww = (int)(w * wear / 100.0);
                    using (GraphicsPath p = Theme.Round(new Rectangle(x, y, w, barH), Theme.Px(5))) { g.SetClip(p); }
                    using (SolidBrush b = new SolidBrush(Theme.Red)) g.FillRectangle(b, x, y, ww, barH);
                    using (SolidBrush b = new SolidBrush(Theme.Blue)) g.FillRectangle(b, x + ww, y, w - ww, barH);
                    g.ResetClip();
                    if (wear > 8) TextRenderer.DrawText(g, "Battery wear " + wear + "%", Theme.Semi(8.5f), new Rectangle(x, y, ww, barH), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    if (wear < 92) TextRenderer.DrawText(g, "Power consumption " + (100 - wear) + "%", Theme.Semi(8.5f), new Rectangle(x + ww, y, w - ww, barH), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    y += barH + Theme.Px(8);
                    string expl = "Runtime = capacity ÷ power, so the shortfall splits into the capacity lost and the extra power drawn. 'Normal' is " + Theme.N(J.Num(rt, "ReferenceW"), 0, " W") + " (light office use). For heavier laptops, raise it in the script's settings.";
                    Size es = TextRenderer.MeasureText(g, expl, small, new Size(w, 0), L);
                    TextRenderer.DrawText(g, expl, small, new Rectangle(x, y, w, es.Height), Theme.Muted, L);
                    y += es.Height + Theme.Px(10);
                }
                else
                {
                    TextRenderer.DrawText(g, "The measured runtime is at or above that of a new battery at normal consumption, so there is no shortfall to explain.", f, new Rectangle(x, y, w, Theme.Px(40)), Theme.Muted, L);
                    y += Theme.Px(40);
                }
            }

            y += Theme.Px(14);
            TextRenderer.DrawText(g, "Capacity history", h, new Rectangle(x, y, w, Theme.Px(22)), Theme.Text, L);
            y += Theme.Px(28);
            List<object> hist = J.List(r.Data, "Battery.History");
            if (hist.Count >= 2)
            {
                List<double> vals = hist.Select(o => J.Num(o, "HealthPct") ?? 0).ToList();
                int ch = Math.Max(Theme.Px(150), ClientSize.Height - y - Theme.Px(30));
                UI.DrawHealthChart(g, new Rectangle(x, y, w, ch), vals, J.Str(hist[0], "Date"), J.Str(hist[hist.Count - 1], "Date"), -1);
                y += ch + Theme.Px(4);
                TextRenderer.DrawText(g, "Dashed lines: 80% (good) and 65% (replace).", small, new Rectangle(x, y, w, Theme.Px(18)), Theme.Muted, L);
            }
            else
            {
                TextRenderer.DrawText(g, J.Get(r.Data, "Battery.History") == null ? "Capacity history is not stored in this (older) result. Open the full report to see it." : "Windows has not recorded enough capacity history yet.", f, new Rectangle(x, y, w, Theme.Px(40)), Theme.Muted, L);
            }
        }
    }
}
