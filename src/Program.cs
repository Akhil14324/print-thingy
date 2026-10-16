using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Management;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// PrintStation: tiny HTTP server that lets phones on the office Wi-Fi print to a USB printer
// attached to this PC. Targets .NET Framework 4 (ships with Windows 10/11), C# 5 syntax only.
namespace PrintStation
{
    class Job
    {
        public int Id;
        public string Name;
        public string Status; // queued, converting, printing, done, error
        public string Message;
        public string Time;
        public string SrcPath;
        public int Copies;
        public string Pages;
        public bool Color;
    }

    static class Program
    {
        static string BaseDir;
        static Dictionary<string, object> Cfg;
        static string PrinterName;
        static string Pin;
        static bool DryRun;
        static long MaxBytes;
        static readonly object JobLock = new object();
        static readonly List<Job> Jobs = new List<Job>();
        static readonly BlockingCollection<Job> Queue = new BlockingCollection<Job>();
        static int NextId = 1;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static string Tmp;

        static readonly HashSet<string> Direct = new HashSet<string> { ".pdf", ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };
        static readonly HashSet<string> Office = new HashSet<string> { ".doc", ".docx", ".rtf", ".txt", ".odt", ".xls", ".xlsx", ".ppt", ".pptx", ".ods", ".odp" };
        static readonly HashSet<string> WordOnly = new HashSet<string> { ".doc", ".docx", ".rtf", ".txt", ".odt" };

        static void Log(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(BaseDir, "printstation.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        static T Get<T>(string key, T def)
        {
            object v;
            if (Cfg != null && Cfg.TryGetValue(key, out v) && v != null)
            {
                try { return (T)Convert.ChangeType(v, typeof(T)); } catch { }
            }
            return def;
        }

        static void Main(string[] args)
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            string cfgPath = Path.Combine(BaseDir, "config.json");
            Cfg = new Dictionary<string, object>();
            try { Cfg = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(cfgPath)); }
            catch (Exception ex) { Log("config.json problem: " + ex.Message + " (using defaults)"); }

            PrinterName = Get("printer", "");
            if (string.IsNullOrEmpty(PrinterName)) PrinterName = new PrinterSettings().PrinterName;
            else
            {
                // allow a partial name such as "M126" to match "HP LaserJet Pro MFP M126a"
                string match = null;
                foreach (string p in PrinterSettings.InstalledPrinters)
                {
                    if (string.Equals(p, PrinterName, StringComparison.OrdinalIgnoreCase)) { match = p; break; }
                    if (match == null && p.IndexOf(PrinterName, StringComparison.OrdinalIgnoreCase) >= 0) match = p;
                }
                if (match != null) PrinterName = match;
            }
            Pin = Get("pin", "");
            DryRun = Get("dryRun", false);
            MaxBytes = (long)Get("maxMb", 50) * 1024 * 1024;
            int port = Get("port", 8080);

            Tmp = Path.Combine(Path.GetTempPath(), "PrintStation");
            Directory.CreateDirectory(Tmp);
            foreach (string f in Directory.GetFiles(Tmp)) { try { File.Delete(f); } catch { } }

            Thread worker = new Thread(Worker);
            worker.SetApartmentState(ApartmentState.STA); // Word COM needs STA
            worker.IsBackground = true;
            worker.Start();

            HttpListener l = new HttpListener();
            try
            {
                l.Prefixes.Add("http://+:" + port + "/");
                l.Start();
            }
            catch (Exception ex)
            {
                Log("Cannot listen on all interfaces (" + ex.Message + "). Run install.bat as administrator. Falling back to localhost only.");
                l = new HttpListener();
                l.Prefixes.Add("http://localhost:" + port + "/");
                l.Start();
            }
            Log("Started. Printer='" + PrinterName + "' port=" + port + " dryRun=" + DryRun);

            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = l.GetContext(); } catch { break; }
                ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
            }
        }

        // ---------- HTTP ----------

        static void Handle(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath;
                string method = ctx.Request.HttpMethod;
                if (method == "GET" && (path == "/" || path == "/index.html"))
                {
                    ServeFile(ctx, "index.html", "text/html; charset=utf-8");
                }
                else if (method == "GET" && path == "/api/info")
                {
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["title"] = Get("title", "Office Printer");
                    d["printer"] = PrinterName;
                    d["status"] = PrinterStatus();
                    d["pinRequired"] = Pin.Length > 0;
                    d["maxMb"] = MaxBytes / 1024 / 1024;
                    d["peers"] = Cfg.ContainsKey("peers") ? Cfg["peers"] : new object[0];
                    SendJson(ctx, 200, d);
                }
                else if (method == "GET" && path == "/api/jobs")
                {
                    if (!Authed(ctx)) { SendJson(ctx, 401, Err("Wrong PIN")); return; }
                    List<object> list = new List<object>();
                    lock (JobLock)
                    {
                        for (int i = Jobs.Count - 1; i >= 0 && list.Count < 15; i--)
                        {
                            Job j = Jobs[i];
                            Dictionary<string, object> d = new Dictionary<string, object>();
                            d["id"] = j.Id; d["name"] = j.Name; d["status"] = j.Status; d["message"] = j.Message; d["time"] = j.Time;
                            list.Add(d);
                        }
                    }
                    SendJson(ctx, 200, list);
                }
                else if (method == "POST" && path == "/api/print")
                {
                    if (!Authed(ctx)) { SendJson(ctx, 401, Err("Wrong PIN")); return; }
                    HandlePrint(ctx);
                }
                else
                {
                    SendJson(ctx, 404, Err("Not found"));
                }
            }
            catch (Exception ex)
            {
                Log("Request error: " + ex);
                try { SendJson(ctx, 500, Err("Server error")); } catch { }
            }
        }

        static bool Authed(HttpListenerContext ctx)
        {
            if (Pin.Length == 0) return true;
            return ctx.Request.Headers["X-Pin"] == Pin;
        }

        static Dictionary<string, object> Err(string m)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["error"] = m;
            return d;
        }

        static void SendJson(HttpListenerContext ctx, int code, object o)
        {
            SendBytes(ctx, code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json.Serialize(o)));
        }

        static void SendBytes(HttpListenerContext ctx, int code, string type, byte[] b)
        {
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = type;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b, 0, b.Length);
            ctx.Response.OutputStream.Close();
        }

        static void ServeFile(HttpListenerContext ctx, string name, string type)
        {
            string p = Path.Combine(BaseDir, "web", name);
            if (!File.Exists(p)) { SendJson(ctx, 404, Err("web/" + name + " missing")); return; }
            SendBytes(ctx, 200, type, File.ReadAllBytes(p));
        }

        // ---------- Upload ----------

        static void HandlePrint(HttpListenerContext ctx)
        {
            string ct = ctx.Request.ContentType ?? "";
            Match bm = Regex.Match(ct, "boundary=\"?([^\";]+)\"?");
            if (!ct.StartsWith("multipart/form-data") || !bm.Success) { SendJson(ctx, 400, Err("Bad upload")); return; }
            if (ctx.Request.ContentLength64 > MaxBytes) { SendJson(ctx, 413, Err("File too large (max " + (MaxBytes / 1024 / 1024) + " MB)")); return; }

            MemoryStream ms = new MemoryStream();
            byte[] buf = new byte[81920];
            int n;
            while ((n = ctx.Request.InputStream.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxBytes) { SendJson(ctx, 413, Err("File too large")); return; }
            }
            byte[] body = ms.ToArray();

            Dictionary<string, string> fields = new Dictionary<string, string>();
            string fileName = null;
            byte[] fileData = null;
            ParseMultipart(body, Encoding.ASCII.GetBytes("--" + bm.Groups[1].Value), fields, out fileName, out fileData);

            if (fileData == null || fileData.Length == 0 || string.IsNullOrEmpty(fileName)) { SendJson(ctx, 400, Err("No file received")); return; }
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (!Direct.Contains(ext) && !Office.Contains(ext)) { SendJson(ctx, 400, Err("File type " + ext + " is not supported. Use PDF, Word, Excel, PowerPoint or a photo.")); return; }

            int copies = 1;
            string cs; if (fields.TryGetValue("copies", out cs)) int.TryParse(cs, out copies);
            if (copies < 1) copies = 1; if (copies > 99) copies = 99;
            string pages = "";
            fields.TryGetValue("pages", out pages);
            pages = (pages ?? "").Trim();
            if (pages.Length > 0 && !Regex.IsMatch(pages, @"^[0-9][0-9,\- ]*$")) { SendJson(ctx, 400, Err("Pages must look like 1-3,5")); return; }
            string col; bool color = !(fields.TryGetValue("color", out col) && col == "bw");

            string saved = Path.Combine(Tmp, Guid.NewGuid().ToString("N") + ext);
            File.WriteAllBytes(saved, fileData);

            Job job = new Job();
            job.Name = Path.GetFileName(fileName);
            job.Status = "queued"; job.Message = "";
            job.Time = DateTime.Now.ToString("HH:mm:ss");
            job.SrcPath = saved; job.Copies = copies; job.Pages = pages.Replace(" ", ""); job.Color = color;
            lock (JobLock)
            {
                job.Id = NextId++;
                Jobs.Add(job);
                if (Jobs.Count > 100) Jobs.RemoveAt(0);
            }
            Queue.Add(job);
            Log("Queued #" + job.Id + " " + job.Name + " from " + ctx.Request.RemoteEndPoint);
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["id"] = job.Id;
            SendJson(ctx, 200, r);
        }

        static int IndexOf(byte[] hay, byte[] needle, int start)
        {
            for (int i = start; i <= hay.Length - needle.Length; i++)
            {
                int k = 0;
                while (k < needle.Length && hay[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }

        static void ParseMultipart(byte[] body, byte[] boundary, Dictionary<string, string> fields, out string fileName, out byte[] fileData)
        {
            fileName = null; fileData = null;
            byte[] headerEnd = new byte[] { 13, 10, 13, 10 };
            int pos = IndexOf(body, boundary, 0);
            while (pos >= 0)
            {
                int partStart = pos + boundary.Length;
                if (partStart + 1 < body.Length && body[partStart] == '-' && body[partStart + 1] == '-') break;
                partStart += 2; // CRLF after boundary
                int next = IndexOf(body, boundary, partStart);
                if (next < 0) break;
                int hEnd = IndexOf(body, headerEnd, partStart);
                if (hEnd >= 0 && hEnd < next)
                {
                    string headers = Encoding.UTF8.GetString(body, partStart, hEnd - partStart);
                    int dataStart = hEnd + 4;
                    int dataLen = next - 2 - dataStart; // strip CRLF before next boundary
                    if (dataLen < 0) dataLen = 0;
                    Match nm = Regex.Match(headers, "name=\"([^\"]*)\"");
                    Match fm = Regex.Match(headers, "filename=\"([^\"]*)\"");
                    if (fm.Success)
                    {
                        if (fileData == null)
                        {
                            fileName = fm.Groups[1].Value;
                            fileData = new byte[dataLen];
                            Buffer.BlockCopy(body, dataStart, fileData, 0, dataLen);
                        }
                    }
                    else if (nm.Success)
                    {
                        fields[nm.Groups[1].Value] = Encoding.UTF8.GetString(body, dataStart, dataLen);
                    }
                }
                pos = next;
            }
        }

        // ---------- Printer status ----------

        static string PrinterStatus()
        {
            try
            {
                string q = "SELECT WorkOffline, PrinterStatus FROM Win32_Printer WHERE Name='" + PrinterName.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(q))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        if (o["WorkOffline"] != null && (bool)o["WorkOffline"]) return "Offline - check the printer is on and connected";
                        return "Ready";
                    }
                }
                return "Printer '" + PrinterName + "' not found on this PC";
            }
            catch { return "Unknown"; }
        }

        // ---------- Worker: convert + print ----------

        static void Worker()
        {
            foreach (Job job in Queue.GetConsumingEnumerable())
            {
                string pdf = null;
                try
                {
                    string ext = Path.GetExtension(job.SrcPath).ToLowerInvariant();
                    string toPrint = job.SrcPath;
                    if (Office.Contains(ext))
                    {
                        Set(job, "converting", "");
                        pdf = ConvertToPdf(job.SrcPath, ext);
                        toPrint = pdf;
                    }
                    Set(job, "printing", "");
                    if (DryRun) { Thread.Sleep(1500); Log("DRYRUN would print " + toPrint); }
                    else PrintFile(job, toPrint);
                    Set(job, "done", "");
                    Log("Printed #" + job.Id + " " + job.Name);
                }
                catch (Exception ex)
                {
                    Set(job, "error", ex.Message);
                    Log("Failed #" + job.Id + " " + job.Name + ": " + ex);
                }
                finally
                {
                    try { File.Delete(job.SrcPath); } catch { }
                    if (pdf != null) { try { File.Delete(pdf); } catch { } }
                }
            }
        }

        static void Set(Job j, string status, string msg)
        {
            lock (JobLock) { j.Status = status; j.Message = msg; }
        }

        static void PrintFile(Job job, string file)
        {
            string exe = Path.Combine(BaseDir, "SumatraPDF.exe");
            if (!File.Exists(exe)) throw new Exception("SumatraPDF.exe is missing next to PrintStation.exe");
            List<string> s = new List<string>();
            if (job.Copies > 1) s.Add(job.Copies + "x");
            s.Add(job.Color ? "color" : "monochrome");
            if (job.Pages.Length > 0) s.Add(job.Pages);
            s.Add("fit");
            ProcessStartInfo psi = new ProcessStartInfo(exe,
                "-print-to \"" + PrinterName + "\" -print-settings \"" + string.Join(",", s.ToArray()) + "\" -silent -exit-when-done \"" + file + "\"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false;
            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } throw new Exception("Printing timed out"); }
                if (p.ExitCode != 0) throw new Exception("Print tool failed (code " + p.ExitCode + "). Is the printer on?");
            }
        }

        static string ConvertToPdf(string src, string ext)
        {
            string outPdf = Path.Combine(Tmp, Guid.NewGuid().ToString("N") + ".pdf");
            string wordErr = null;
            if (WordOnly.Contains(ext))
            {
                try { if (ConvertWithOffice(src, outPdf)) return outPdf; }
                catch (Exception ex) { wordErr = ex.Message; Log("Word convert failed: " + ex.Message); }
            }
            else
            {
                try { if (ConvertWithOffice(src, outPdf)) return outPdf; }
                catch (Exception ex) { wordErr = ex.Message; Log("Office convert failed: " + ex.Message); }
            }
            string lo = FindLibreOffice();
            if (lo != null)
            {
                string dir = Path.Combine(Tmp, "lo" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                ProcessStartInfo psi = new ProcessStartInfo(lo, "--headless --convert-to pdf --outdir \"" + dir + "\" \"" + src + "\"");
                psi.CreateNoWindow = true; psi.UseShellExecute = false;
                using (Process p = Process.Start(psi)) { if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } } }
                string[] res = Directory.GetFiles(dir, "*.pdf");
                if (res.Length > 0) { File.Move(res[0], outPdf); try { Directory.Delete(dir, true); } catch { } return outPdf; }
                try { Directory.Delete(dir, true); } catch { }
            }
            throw new Exception("Could not convert this file. Install Microsoft Office or LibreOffice on this PC, or send a PDF." + (wordErr != null ? " (" + wordErr + ")" : ""));
        }

        static string FindLibreOffice()
        {
            string[] c = {
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe" };
            foreach (string p in c) if (File.Exists(p)) return p;
            return null;
        }

        // Uses Word / Excel / PowerPoint through COM if installed. Returns false when the app is not installed.
        static bool ConvertWithOffice(string src, string outPdf)
        {
            string ext = Path.GetExtension(src).ToLowerInvariant();
            if (WordOnly.Contains(ext))
            {
                Type t = Type.GetTypeFromProgID("Word.Application");
                if (t == null) return false;
                dynamic app = Activator.CreateInstance(t);
                try
                {
                    app.Visible = false; app.DisplayAlerts = 0;
                    dynamic doc = app.Documents.Open(src, false, true);
                    doc.ExportAsFixedFormat(outPdf, 17); // wdExportFormatPDF
                    doc.Close(false);
                }
                finally { try { app.Quit(false); } catch { } }
                return File.Exists(outPdf);
            }
            if (ext == ".xls" || ext == ".xlsx")
            {
                Type t = Type.GetTypeFromProgID("Excel.Application");
                if (t == null) return false;
                dynamic app = Activator.CreateInstance(t);
                try
                {
                    app.Visible = false; app.DisplayAlerts = false;
                    dynamic wb = app.Workbooks.Open(src, 0, true);
                    wb.ExportAsFixedFormat(0, outPdf); // xlTypePDF
                    wb.Close(false);
                }
                finally { try { app.Quit(); } catch { } }
                return File.Exists(outPdf);
            }
            if (ext == ".ppt" || ext == ".pptx")
            {
                Type t = Type.GetTypeFromProgID("PowerPoint.Application");
                if (t == null) return false;
                dynamic app = Activator.CreateInstance(t);
                try
                {
                    dynamic pres = app.Presentations.Open(src, -1, 0, 0); // ReadOnly, Untitled, WithWindow=false
                    pres.SaveAs(outPdf, 32); // ppSaveAsPDF
                    pres.Close();
                }
                finally { try { app.Quit(); } catch { } }
                return File.Exists(outPdf);
            }
            return false;
        }
    }
}
