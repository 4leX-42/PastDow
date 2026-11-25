using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PastDow
{
    /// <summary>
    /// Pages yt-dlp cannot read (players behind a login such as Blackboard Collaborate recordings, or sites
    /// without an extractor) open in a small browser window. It watches the page's network traffic for video
    /// files and streams (MP4, WebM, HLS .m3u8, DASH .mpd), the way browser download helpers do, and hands the
    /// chosen one to yt-dlp with the page as referer and the window's cookies for that host. The window keeps
    /// its own profile (%LOCALAPPDATA%\PastDow\browser), so a login there is remembered for next time.
    /// DRM-protected streams (Netflix and the like) cannot be downloaded this way.
    /// </summary>
    sealed class Capture : Form
    {
        /// <summary>Hosts that always go straight to the capture window: yt-dlp has no extractor for them.</summary>
        static readonly Regex Direct = new Regex(@"^https?://([a-z0-9-]+\.)*bbcollab\.com/", RegexOptions.IgnoreCase);
        public static bool Wants(string url) => Direct.IsMatch(url ?? "");

        static Capture open;
        public static bool Busy => open != null && !open.IsDisposed;

        sealed class Found
        {
            public CoreWebView2 Core;
            public string Url, Key, Kind;
            public long Size;
            public int Order;
            public string Label => Kind + (Size > 0 ? "  ·  " + (Size / 1048576.0).ToString(Size < 10485760 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + " MB" : "");
        }

        readonly Job job;
        readonly Action<Job> done;
        readonly WebView2 web = new WebView2();
        readonly Label status = new Label();
        readonly Label pick = new Label();
        readonly Button go = new Button();
        readonly List<Found> found = new List<Found>();
        readonly Dictionary<CoreWebView2, List<BrowserSubtitle>> subtitles = new Dictionary<CoreWebView2, List<BrowserSubtitle>>();
        readonly List<Form> popups = new List<Form>();
        CoreWebView2Environment environment;
        int chosen = -1, seen;
        bool handed, handing, userPicked;

        static readonly Regex MediaExt = new Regex(@"\.(mp4|m4v|webm|mov|mkv|m3u8|mpd|mp3|m4a)$", RegexOptions.IgnoreCase);
        static readonly Regex SegmentExt = new Regex(@"\.(ts|m4s|aac|key|init)$", RegexOptions.IgnoreCase);
        static readonly Regex Total = new Regex(@"/(\d+)\s*$");

        /// <summary>Opens the window for this job. done runs on the UI thread with the job filled in (or canceled).</summary>
        public static void Open(Job j, IWin32Window owner, Action<Job> done)
        {
            open = new Capture(j, done);
            // on the same monitor as PastDow, not always the primary one
            var wa = Screen.FromHandle(owner.Handle).WorkingArea;
            open.StartPosition = FormStartPosition.Manual;
            open.Location = new Point(wa.X + Math.Max(0, (wa.Width - open.Width) / 2), wa.Y + Math.Max(0, (wa.Height - open.Height) / 2));
            if (open.Width > wa.Width || open.Height > wa.Height) open.Size = new Size(Math.Min(open.Width, wa.Width), Math.Min(open.Height, wa.Height));
            open.Show(owner);
        }

        Capture(Job j, Action<Job> done)
        {
            job = j; this.done = done;
            Text = "PastDow · find the video";
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Theme.S(1100), Theme.S(720));
            MinimumSize = new Size(Theme.S(640), Theme.S(420));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var bar = new Panel { Dock = DockStyle.Top, Height = Theme.S(52), BackColor = Color.FromArgb(10, 10, 10), Padding = new Padding(Theme.S(16), 0, Theme.S(12), 0) };
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = Theme.Sub;
            status.Font = Theme.Sans(10f);
            status.UseMnemonic = false;
            status.Text = "Sign in if the page asks, then press play · PastDow catches the video as it loads";

            pick.Dock = DockStyle.Right;
            pick.AutoSize = false;
            pick.Width = Theme.S(260);
            pick.TextAlign = ContentAlignment.MiddleRight;
            pick.ForeColor = Theme.Faint;
            pick.Font = Theme.Sans(9.5f);
            pick.UseMnemonic = false;
            pick.Cursor = Cursors.Hand;
            pick.Click += (s, e) => { if (found.Count > 1) { chosen = (chosen + 1) % found.Count; userPicked = true; Paint_(); } };

            go.Text = "DOWNLOAD";
            go.Font = Theme.Display(10f);
            go.FlatStyle = FlatStyle.Flat;
            go.FlatAppearance.BorderColor = Color.FromArgb(230, 195, 74);
            go.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 34, 14);
            go.ForeColor = Color.FromArgb(230, 195, 74);
            go.BackColor = Color.Black;
            go.Enabled = false;
            go.Click += async (s, e) => await Hand();

            var goWrap = new Panel { Dock = DockStyle.Right, Width = Theme.S(150), Padding = new Padding(0, Theme.S(10), 0, Theme.S(10)) };
            go.Dock = DockStyle.Fill;
            goWrap.Controls.Add(go);

            bar.Controls.Add(status);
            bar.Controls.Add(pick);
            bar.Controls.Add(goWrap);

            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.White;
            Controls.Add(web);
            Controls.Add(bar);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Home, "browser"));
                await web.EnsureCoreWebView2Async(environment);
            }
            catch (Exception ex)
            {
                status.Text = "Could not start the browser: " + ex.Message;
                return;
            }
            var core = web.CoreWebView2;
            Hook(core, this);
            core.Navigate(job.Page ?? job.Url);
        }

        void Hook(CoreWebView2 core, Form window)
        {
            subtitles[core] = new List<BrowserSubtitle>();
            core.NavigationStarting += (s, a) => Forget(core);
            core.WebResourceResponseReceived += (s, a) => OnResponse(core, a);
            core.DocumentTitleChanged += (s, a) => { if (!string.IsNullOrWhiteSpace(core.DocumentTitle)) window.Text = "PastDow · " + core.DocumentTitle; };
            core.NewWindowRequested += Popup;
            core.WindowCloseRequested += (s, a) => { if (window != this) window.Close(); };
        }

        async void Popup(object sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            using (args.GetDeferral())
            {
                args.Handled = true;
                var popup = new Form { Text = "PastDow · browser", Size = Size, StartPosition = FormStartPosition.CenterParent, BackColor = Color.Black, Icon = Icon };
                var view = new WebView2 { Dock = DockStyle.Fill };
                var download = new Button { Text = "DOWNLOAD FOUND VIDEO", Dock = DockStyle.Top, Height = Theme.S(40), BackColor = Color.Black, ForeColor = Color.FromArgb(230, 195, 74), FlatStyle = FlatStyle.Flat };
                download.Click += async (s, e) =>
                {
                    if (view.CoreWebView2 != null && (chosen < 0 || chosen >= found.Count || found[chosen].Core != view.CoreWebView2))
                        chosen = found.FindIndex(f => f.Core == view.CoreWebView2);
                    await Hand();
                };
                popup.Controls.Add(view); popup.Controls.Add(download);
                popups.Add(popup);
                popup.FormClosed += (s, e) =>
                {
                    popups.Remove(popup);
                    if (view.CoreWebView2 != null) { Forget(view.CoreWebView2); subtitles.Remove(view.CoreWebView2); }
                    view.Dispose();
                };
                try
                {
                    popup.Show(this);
                    await view.EnsureCoreWebView2Async(environment);
                    args.NewWindow = view.CoreWebView2;
                    Hook(view.CoreWebView2, popup);
                }
                catch (Exception ex) { popup.Close(); status.Text = "Could not open the player window: " + ex.Message; }
            }
        }

        void Forget(CoreWebView2 core)
        {
            found.RemoveAll(f => f.Core == core);
            if (subtitles.TryGetValue(core, out var list)) list.Clear();
            chosen = -1; userPicked = false;
            Rank(); Paint_();
        }

        void OnResponse(object sender, CoreWebView2WebResourceResponseReceivedEventArgs a)
        {
            try
            {
                string url = a.Request.Uri;
                if (!BrowserMedia.Http(url)) return;
                if (a.Response.StatusCode < 200 || a.Response.StatusCode >= 300) return;
                var core = (CoreWebView2)sender;
                var h = a.Response.Headers;
                string type = (h.Contains("Content-Type") ? h.GetHeader("Content-Type") : "").ToLowerInvariant();
                string path = new Uri(url).AbsolutePath;
                if (type.Contains("text/vtt") || type.Contains("application/x-subrip") || Regex.IsMatch(path, @"\.(vtt|srt)$", RegexOptions.IgnoreCase))
                {
                    var list = subtitles[core];
                    string subKey = url.Split('?')[0];
                    list.RemoveAll(s => s.url.Split('?')[0] == subKey);
                    list.Add(new BrowserSubtitle { url = url, lang = BrowserMedia.Language(url), ext = type.Contains("subrip") || path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ? "srt" : "vtt" });
                    if (list.Count > 100) list.RemoveAt(0);
                    Paint_();
                    return;
                }
                if (SegmentExt.IsMatch(path) || type.Contains("mp2t") || type.Contains("iso.segment")) return;

                string kind = null;
                if (type.Contains("mpegurl") || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) kind = "HLS stream";
                else if (type.Contains("dash+xml") || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)) kind = "DASH stream";
                else if (type.StartsWith("video/") || (MediaExt.IsMatch(path) && !type.StartsWith("text/"))) kind = Ext(path, type, "VIDEO");
                else if (type.StartsWith("audio/")) kind = Ext(path, type, "AUDIO");
                if (kind == null) return;

                long size = 0;
                if (h.Contains("Content-Range")) { var m = Total.Match(h.GetHeader("Content-Range")); if (m.Success) long.TryParse(m.Groups[1].Value, out size); }
                if (size == 0 && h.Contains("Content-Length")) long.TryParse(h.GetHeader("Content-Length"), out size);
                // previews, ads and tiny clips; stream manifests are always small, so they are never filtered by size
                if (!kind.Contains("stream") && size > 0 && size < 200 * 1024) return;

                string key = url.Split('?')[0];
                var f = found.FirstOrDefault(x => x.Key == key && x.Core == core);
                if (f == null) found.Add(f = new Found { Url = url, Key = key, Kind = kind, Order = seen++, Core = core });
                if (found.Count > 200) found.RemoveAt(0);
                f.Url = url;   // keep the freshest signed URL
                if (size > f.Size) f.Size = size;
                Rank();
                Paint_();
            }
            catch { }
        }

        static string Ext(string path, string type, string fallback)
        {
            string e = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            if (e.Length > 0 && e.Length <= 4) return e;
            int i = type.IndexOf('/');
            string t = i >= 0 ? type.Substring(i + 1).Split(';')[0].Trim().ToUpperInvariant() : "";
            return t.Length > 0 && t.Length <= 5 ? t : fallback;
        }

        /// <summary>Best first: a stream manifest (the first one seen is usually the master), then the biggest file.</summary>
        void Rank()
        {
            var keep = chosen >= 0 && chosen < found.Count ? found[chosen] : null;
            var sorted = found.OrderBy(x => x.Kind.Contains("stream") ? 0 : x.Kind == "AUDIO" || x.Kind == "MP3" || x.Kind == "M4A" ? 2 : 1)
                              .ThenByDescending(x => x.Kind.Contains("stream") ? 0 : x.Size)
                              .ThenBy(x => x.Order).ToList();
            found.Clear(); found.AddRange(sorted);
            chosen = keep != null && userPicked ? found.IndexOf(keep) : 0;
        }

        void Paint_()
        {
            if (found.Count == 0)
            {
                go.Enabled = false; pick.Text = "";
                status.Text = "Press play in the browser window to find the video";
                return;
            }
            if (chosen < 0 || chosen >= found.Count) chosen = 0;
            int subs = found[chosen].Core != null && subtitles.TryGetValue(found[chosen].Core, out var sl) ? sl.Count : 0;
            string withSubs = subs == 0 ? "" : subs == 1 ? " with 1 subtitle" : " with " + subs + " subtitles";
            status.Text = found.Count == 1 ? "Video found" + withSubs + " · press DOWNLOAD" : found.Count + " videos found" + withSubs + " · press DOWNLOAD, or click the right label to pick another";
            status.ForeColor = Theme.Text;
            pick.Text = found[chosen].Label + (found.Count > 1 ? "  (" + (chosen + 1) + "/" + found.Count + ")" : "");
            go.Enabled = true;
        }

        async Task Hand()
        {
            if (handed || handing || chosen < 0 || chosen >= found.Count) return;
            var f = found[chosen];
            var core = f.Core;
            string source, title, agent;
            try { source = core.Source; title = core.DocumentTitle; agent = core.Settings.UserAgent; }
            catch { status.Text = "The player window was closed, reopen it and play the video again"; return; }
            handing = true;
            go.Enabled = false;
            job.Page = source;
            job.Url = f.Url;
            job.Referer = source;
            job.UserAgent = agent;
            job.MediaKind = f.Kind;
            job.Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            try
            {
                var cookies = await core.CookieManager.GetCookiesAsync(f.Url);
                job.Cookie = cookies.Count == 0 ? null : string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value));
            }
            catch { job.Cookie = null; }
            job.Subtitles = new List<BrowserSubtitle>();
            foreach (var sub in subtitles[core].ToArray())
            {
                string cookie = null;
                try { cookie = string.Join("; ", (await core.CookieManager.GetCookiesAsync(sub.url)).Select(c => c.Name + "=" + c.Value)); } catch { }
                job.Subtitles.Add(new BrowserSubtitle { url = sub.url, lang = sub.lang, ext = sub.ext, cookie = cookie });
            }
            if (IsDisposed) return;
            handed = true;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            open = null;
            foreach (var popup in popups.ToArray()) popup.Close();
            if (!handed) { job.CancelRequested = true; job.Error = "Closed before a video was picked"; }
            try { web.Dispose(); } catch { }
            done(job);
        }
    }
}
