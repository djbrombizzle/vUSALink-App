using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace VusaLinkApp
{
    // The version of vUSALink App and vUSALink-Lite (both ship together). Bump it with each release tag (v0.3.3 ...).
    static class AppVersion
    {
        public const string Current = "0.3.7";
    }

    class UpdateInfo
    {
        public Version Version;
        public string Tag, ZipUrl, ZipName, PageUrl;
    }

    // One-click update from GitHub Releases. CI publishes vUSALink-App-vX.Y.Z.zip and vUSALink-Lite-vX.Y.Z.zip for
    // every version tag. Check() asks GitHub for the latest release; Install() downloads the zip and swaps the files
    // in place; Relaunch() starts the new exe and the caller exits.
    //
    // Swapping a running program: Windows won't overwrite an exe or DLL that's in use, but it does allow renaming it.
    // So each file is renamed to *.old and the new one copied in; the next start deletes the *.old files. If any
    // step fails, the files already swapped are put back, so the old version keeps working.
    // Settings and learned CIDs live in %LOCALAPPDATA%\vUSALinkApp and are not touched.
    static class Updater
    {
        const string Repo = "djbrombizzle/vUSALink-App";
        const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
        const string AfterUpdateArg = "--after-update";

        // This process was started by an update (see Relaunch).
        public static bool JustUpdated { get; private set; }

        // The version this exe is. VUSALINK_FAKE_VERSION pretends to be an older one (to test updating).
        public static Version Current
        {
            get
            {
                Version v;
                var fake = Environment.GetEnvironmentVariable("VUSALINK_FAKE_VERSION");
                return !string.IsNullOrEmpty(fake) && Version.TryParse(fake, out v) ? v : Version.Parse(AppVersion.Current);
            }
        }

        // Latest release with a zip whose name starts with assetPrefix ("vUSALink-App-" or "vUSALink-Lite-"), if it's
        // newer than this exe. Null when up to date or when GitHub can't be reached (checked off the UI thread).
        public static UpdateInfo Check(string assetPrefix)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(LatestApi);
                req.UserAgent = "vUSALinkApp-updater";
                req.Accept = "application/vnd.github+json";
                req.Timeout = 15000;
                string body;
                using (var resp = req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = rd.ReadToEnd();
                var rel = Json.Parse(body);
                var tag = Json.Str(rel, "tag_name") ?? "";
                Version v;
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out v))
                {
                    Log.Info("Update check: latest release tag '" + tag + "' isn't a version");
                    return null;
                }
                if (v <= Current)
                {
                    Log.Info("Update check: up to date (" + Current + ", latest " + tag + ")");
                    return null;
                }
                var assets = Json.Arr(rel, "assets");
                if (assets != null)
                    foreach (var a in assets)
                    {
                        var name = Json.Str(a, "name") ?? "";
                        if (name.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Info("Update check: " + tag + " available (this is " + Current + ")");
                            return new UpdateInfo
                            {
                                Version = v, Tag = tag, ZipName = name,
                                ZipUrl = Json.Str(a, "browser_download_url"), PageUrl = Json.Str(rel, "html_url")
                            };
                        }
                    }
                Log.Info("Update check: " + tag + " has no " + assetPrefix + "*.zip");
            }
            catch (Exception ex) { Log.Info("Update check failed: " + ex.Message); }
            return null;
        }

        // Downloads the release zip and swaps the files next to this exe. Returns null when done, else why not (the
        // old version is left as it was). Run off the UI thread.
        public static string Install(UpdateInfo u, string exeName)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            var work = Path.Combine(Path.GetTempPath(), "vUSALink-update-" + u.Tag);
            var moved = new List<string>();     // targets renamed to .old
            var added = new List<string>();     // targets that didn't exist before
            try
            {
                // Can we write here at all? (e.g. under Program Files without admin)
                var probe = Path.Combine(dir, ".update-probe");
                try { File.WriteAllText(probe, ""); File.Delete(probe); }
                catch { return "can't write to " + dir.TrimEnd('\\') + " (move the app to a folder you own, or update by hand)"; }

                if (Directory.Exists(work)) Directory.Delete(work, true);
                Directory.CreateDirectory(work);
                var zip = Path.Combine(work, u.ZipName);
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "vUSALinkApp-updater";
                    wc.DownloadFile(u.ZipUrl, zip);
                }
                var src = Path.Combine(work, "files");
                ZipFile.ExtractToDirectory(zip, src);
                // The zip is flat; if it ever has one top folder, use that.
                if (!File.Exists(Path.Combine(src, exeName)))
                {
                    var sub = Directory.GetDirectories(src).FirstOrDefault(d => File.Exists(Path.Combine(d, exeName)));
                    if (sub == null) return "the download has no " + exeName;
                    src = sub;
                }
                Log.Info("Update: downloaded " + u.ZipName + ", installing into " + dir);

                foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    var rel = file.Substring(src.Length).TrimStart('\\');
                    var target = Path.Combine(dir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (File.Exists(target))
                    {
                        var old = target + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(target, old);
                        moved.Add(target);
                    }
                    else added.Add(target);
                    File.Copy(file, target);
                }
                Log.Info("Update: " + (moved.Count + added.Count) + " file(s) replaced, now " + u.Tag);
                try { Directory.Delete(work, true); } catch { }
                return null;
            }
            catch (Exception ex)
            {
                Log.Info("Update failed, putting the old files back: " + ex.Message);
                foreach (var t in added) try { File.Delete(t); } catch { }
                foreach (var t in moved)
                    try { if (File.Exists(t)) File.Delete(t); File.Move(t + ".old", t); }
                    catch (Exception rex) { Log.Info("Update rollback: " + t + ": " + rex.Message); }
                return ex.Message;
            }
        }

        // Starts the (new) exe, which waits for this process to exit before it starts up.
        public static bool Relaunch(string exePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exePath, AfterUpdateArg + " " + Process.GetCurrentProcess().Id)
                {
                    UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath)
                });
                return true;
            }
            catch (Exception ex) { Log.Info("Update: restart failed: " + ex.Message); return false; }
        }

        // Call first thing in Main. After an update, waits (up to 20 s) for the old process to exit, so it has let
        // go of CRC and the single-instance locks. Deletes *.old files left by an update. Returns the other args.
        public static string[] AfterStart(string[] args)
        {
            var rest = new List<string>(args ?? new string[0]);
            int i = rest.IndexOf(AfterUpdateArg);
            if (i >= 0)
            {
                int pid;
                if (i + 1 < rest.Count && int.TryParse(rest[i + 1], out pid))
                {
                    try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(20000); } catch { }
                    rest.RemoveAt(i + 1);
                }
                rest.RemoveAt(i);
                JustUpdated = true;
                Log.Info("Started after update: version " + AppVersion.Current);
            }
            try
            {
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var old in Directory.GetFiles(dir, "*.old", SearchOption.TopDirectoryOnly))
                {
                    for (int n = 0; n < 10; n++)
                    {
                        try { File.Delete(old); break; } catch { Thread.Sleep(300); }
                    }
                }
            }
            catch { }
            return rest.ToArray();
        }
    }
}
