using System.Net;
using System.Text;
using System.Text.Json;

namespace Baka;

/// Part of --ui-test: real file uploads (a photo and a video picked through the page's file input, and a
/// large drag-and-drop style FormData post) to a local server, and the engine's 4K video decoding support.
sealed partial class BrowserForm
{
    async Task RunMediaChecksAsync(Action<bool, string> step, Action<string> note, Func<string, int, Task> go,
        Func<string, Task<string>> eval, Func<int, Task> wait, string reportDir)
    {
        var port = 47000 + Random.Shared.Next(1000);
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        var received = new List<(string Path, long Bytes)>();
        try { listener.Start(); }
        catch (Exception ex) { step(false, "local upload server could not start: " + ex.Message); return; }
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { break; }
                try
                {
                    long total = 0;
                    if (ctx.Request.HasEntityBody)
                    {
                        var buffer = new byte[1 << 16]; int n;
                        while ((n = await ctx.Request.InputStream.ReadAsync(buffer)) > 0) total += n;
                    }
                    lock (received) received.Add((ctx.Request.Url!.AbsolutePath, total));
                    var body = ctx.Request.Url!.AbsolutePath == "/"
                        ? "<!doctype html><title>upload</title><form id=f method=post action=/form enctype=multipart/form-data><input id=photo type=file name=photo accept=image/*><input id=video type=file name=video accept=video/*></form>"
                        : $"received {total}";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = "text/html";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { try { ctx.Response.Abort(); } catch { } }
            }
        });

        try
        {
            // test files: a real JPEG-sized photo and a 40 MB "video"
            var photo = Path.Combine(reportDir, "upload-photo.jpg");
            var video = Path.Combine(reportDir, "upload-video.mp4");
            File.WriteAllBytes(photo, RandomBytes(3 * 1024 * 1024));
            File.WriteAllBytes(video, RandomBytes(40 * 1024 * 1024));

            await go(prefix, 1200);
            if (Current?.View.CoreWebView2 is not { } core) { step(false, "upload page did not open"); return; }
            // Put the files into the page's <input type=file> exactly as the file picker does.
            await core.CallDevToolsProtocolMethodAsync("DOM.enable", "{}");
            var doc = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{}"));
            var rootId = doc.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
            foreach (var (selector, file) in new[] { ("#photo", photo), ("#video", video) })
            {
                var q = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.querySelector",
                    JsonSerializer.Serialize(new { nodeId = rootId, selector })));
                var nodeId = q.RootElement.GetProperty("nodeId").GetInt32();
                await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles",
                    JsonSerializer.Serialize(new { files = new[] { file }, nodeId }));
            }
            var sizes = await eval("JSON.stringify([document.getElementById('photo').files[0]?.size||0,document.getElementById('video').files[0]?.size||0])");
            note("file inputs hold: " + sizes);
            // Form submit (classic upload) and fetch with FormData (what most sites use).
            var fetchResult = await EvalAwaitAsync(core, @"(async()=>{const fd=new FormData();fd.append('photo',document.getElementById('photo').files[0]);fd.append('video',document.getElementById('video').files[0]);
                try{const r=await fetch('/fetch',{method:'POST',body:fd});return await r.text()}catch(e){return 'error '+e}})()");
            // Chunked XHR upload of Blob slices with progress events (Google Photos / YouTube / Drive style).
            var xhrResult = await EvalAwaitAsync(core, @"(async()=>{const f=document.getElementById('video').files[0];const size=8*1024*1024;let sent=0,progress=0;
                for(let o=0;o<f.size;o+=size){await new Promise((ok,bad)=>{const x=new XMLHttpRequest();x.open('PUT','/xhr');x.upload.onprogress=()=>progress++;
                x.onload=()=>{sent+=Math.min(size,f.size-o);ok()};x.onerror=()=>bad('xhr error');x.send(f.slice(o,o+size))})}return sent+' bytes, '+progress+' progress events'})()");
            note("chunked XHR upload: " + xhrResult);
            await wait(4000);
            await eval("document.getElementById('f').submit()");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline) { lock (received) if (received.Any(r => r.Path == "/form")) break; await Task.Delay(300); }
            long fetchBytes, formBytes, xhrBytes;
            lock (received)
            {
                xhrBytes = received.Where(r => r.Path == "/xhr").Sum(r => r.Bytes);
                fetchBytes = received.Where(r => r.Path == "/fetch").Select(r => r.Bytes).DefaultIfEmpty(0).Max();
                formBytes = received.Where(r => r.Path == "/form").Select(r => r.Bytes).DefaultIfEmpty(0).Max();
            }
            var expected = new FileInfo(photo).Length + new FileInfo(video).Length;
            note($"fetch upload: server got {fetchBytes:N0} bytes (files {expected:N0}); page said {fetchResult}");
            note($"form upload: server got {formBytes:N0} bytes");
            step(fetchBytes >= expected, "a photo and a 40 MB video upload with fetch/FormData (how most sites upload)");
            step(formBytes >= expected, "the same files upload with a normal form");
            step(xhrBytes == new FileInfo(video).Length, $"the video uploads in 8 MB XHR chunks with progress (server got {xhrBytes:N0} bytes)");
            try { File.Delete(photo); File.Delete(video); } catch { }
        }
        catch (Exception ex) { step(false, "upload test: " + ex.Message); }
        finally { try { listener.Stop(); } catch { } }
        await serving.ContinueWith(_ => { });

        // 4K: what the engine can decode, and whether it can do it smoothly.
        var media = Current?.View.CoreWebView2 is { } mcore ? await EvalAwaitAsync(mcore, @"(async()=>{const t=[['VP9 4K60','video/webm; codecs=""vp09.00.51.08""'],['AV1 4K60','video/mp4; codecs=""av01.0.13M.08""'],['H.264 4K','video/mp4; codecs=""avc1.640033""'],['HEVC 4K','video/mp4; codecs=""hvc1.1.6.L153.B0""']];
            const out=[];for(const [n,c] of t){let i={supported:false,smooth:false,powerEfficient:false};
            try{i=await navigator.mediaCapabilities.decodingInfo({type:'media-source',video:{contentType:c,width:3840,height:2160,framerate:60,bitrate:40000000}})}catch(e){}
            out.push(n+': '+(MediaSource.isTypeSupported(c)?'yes':'no')+' smooth='+i.smooth+' hw='+i.powerEfficient)}return out.join(' | ')})()") : "no page";
        note("4K decoding: " + media);
        step(media.Contains("VP9 4K60: yes", StringComparison.Ordinal) && media.Contains("AV1 4K60: yes", StringComparison.Ordinal),
            "4K VP9 and AV1 video (YouTube's 4K formats) are supported");

        await RunBackgroundVideoCheckAsync(step, note, wait, reportDir);
    }

    /// The new-tab background video, the way Settings > Appearance > "Choose image or video…" stores it:
    /// a real video is recorded in the page (canvas + MediaRecorder), saved as the background, and the
    /// new-tab page must actually play it (time moving, no media error).
    async Task RunBackgroundVideoCheckAsync(Action<bool, string> step, Action<string> note, Func<int, Task> wait, string reportDir)
    {
        var oldBackground = Config.Current.HomeBackgroundFile;
        foreach (var (mime, ext) in new[] { ("video/webm;codecs=vp9", ".webm"), ("video/mp4;codecs=avc1.42E01E", ".mp4") })
        {
            var source = Path.Combine(reportDir, "background-test" + ext);
            try
            {
                var maker = await NewTabAsync(null, html: "<!doctype html><title>make video</title><canvas id=c width=640 height=360></canvas>");
                await wait(1200);
                if (maker?.View.CoreWebView2 is not { } mcore) { step(false, "background video: no tab"); continue; }
                var b64 = await EvalAwaitAsync(mcore, "(async()=>{const m='" + mime + "';if(!MediaRecorder.isTypeSupported(m))return 'unsupported';" +
                    "const c=document.getElementById('c'),x=c.getContext('2d');const s=c.captureStream(30);const r=new MediaRecorder(s,{mimeType:m});const parts=[];r.ondataavailable=e=>parts.push(e.data);" +
                    "let f=0;const t=setInterval(()=>{x.fillStyle='hsl('+(f*7%360)+',70%,45%)';x.fillRect(0,0,640,360);x.fillStyle='#fff';x.font='60px sans-serif';x.fillText('BAKA '+f++,40,200)},33);" +
                    "r.start(250);await new Promise(ok=>setTimeout(ok,3000));r.stop();await new Promise(ok=>r.onstop=ok);clearInterval(t);" +
                    "const buf=new Uint8Array(await new Blob(parts,{type:m}).arrayBuffer());let s2='';for(let i=0;i<buf.length;i+=32768)s2+=String.fromCharCode.apply(null,buf.subarray(i,i+32768));return btoa(s2)})()");
                CloseTab(_open.IndexOf(maker));
                if (b64 == "unsupported" || b64.StartsWith("error", StringComparison.Ordinal)) { note($"background video {ext}: the engine cannot record {mime} ({b64}); skipped"); continue; }
                File.WriteAllBytes(source, Convert.FromBase64String(b64));
                note($"background video {ext}: test file {new FileInfo(source).Length:N0} bytes");
                // exactly what ChooseBackground does
                var name = "home-background" + ext;
                SettingsForm.ReplaceManagedFile(source, Path.Combine(App.HomeBackgroundDirectory, name));
                Config.Current.HomeBackgroundFile = name;
                var home = await NewTabAsync(null);
                await wait(4000);
                var state = home?.View.CoreWebView2 is { } hcore ? await EvalAwaitAsync(hcore,
                    "(async()=>{const v=document.querySelector('.wallpaper video');if(!v)return 'no video element';await new Promise(ok=>setTimeout(ok,1500));" +
                    "return JSON.stringify({src:v.currentSrc,ready:v.readyState,net:v.networkState,paused:v.paused,time:v.currentTime,error:v.error?v.error.code+' '+v.error.message:null})})()") : "no tab";
                note($"background video {ext}: {state}");
                double time = 0;
                try { using var d = JsonDocument.Parse(state); time = d.RootElement.GetProperty("time").GetDouble(); } catch { }
                step(time > 0.3, $"the new-tab background video plays ({ext})");
                if (home is not null) CloseTab(_open.IndexOf(home));
            }
            catch (Exception ex) { step(false, $"background video {ext}: {ex.Message}"); }
            finally { try { File.Delete(source); } catch { } }
        }
        Config.Current.HomeBackgroundFile = oldBackground;
    }

    /// ExecuteScriptAsync does not wait for promises (it returns "{}"), so async checks go through CDP.
    static async Task<string> EvalAwaitAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, string expression)
    {
        try
        {
            var json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true }));
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
            return json;
        }
        catch (Exception ex) { return "error " + ex.Message; }
    }

    static byte[] RandomBytes(int n) { var b = new byte[n]; Random.Shared.NextBytes(b); return b; }
}
