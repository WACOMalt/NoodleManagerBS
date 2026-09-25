using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;


namespace NoodleManagerX.Models
{
    // Preview playback runs yt-dlp and pipes its audio into mpv.
    //
    // The original implementation used YoutubeExplode to extract a stream URL and
    // NAudio (MediaFoundationReader + WaveOutEvent) to play it. Both are unusable
    // here: the pinned YoutubeExplode fails with "Failed to extract the cipher
    // manifest" whenever YouTube rotates its player signature scheme, and the
    // NAudio types are Windows-only P/Invokes into mfplat.dll and winmm.dll.
    //
    // Handing a resolved URL to mpv does not work either - YouTube serves those
    // URLs only to a client sending the matching headers, so ffmpeg's own fetch
    // comes back 403 Forbidden. Letting yt-dlp do all the HTTP and giving mpv a
    // plain stream on stdin sidesteps that entirely, and yt-dlp tracks YouTube's
    // changes so this path does not rot the way a pinned extractor does.
    static class PlaybackHandler
    {
        public static MapItem currentlyPlaying;

        private const string downloader = "yt-dlp";
        private const string playerExecutable = "mpv";

        // Guards the fields below, which Play writes from a worker thread while
        // SetVolume and Stop read them from the UI thread.
        private static readonly object gate = new object();
        private static Process player;
        private static Process fetcher;
        private static string ipcPath;

        public static void Play(MapItem item)
        {
            Task.Run(async () =>
            {
                if (currentlyPlaying == item)
                {
                    Stop();
                    return;
                }

                string url = item.youtube_url;
                if (String.IsNullOrEmpty(url)) return;

                if (currentlyPlaying != null) Stop();
                currentlyPlaying = item;
                item.playing = true;
                MapItem playing = currentlyPlaying;

                Process ytdl = null;
                Process mpv = null;
                string socketPath = null;

                try
                {
                    socketPath = Path.Combine(Path.GetTempPath(), "nmx-mpv-" + Guid.NewGuid().ToString("N") + ".sock");

                    ProcessStartInfo fetch = new ProcessStartInfo(downloader)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    fetch.ArgumentList.Add("--no-warnings");
                    fetch.ArgumentList.Add("--no-playlist");
                    fetch.ArgumentList.Add("--quiet");
                    // YouTube's signature challenge needs a JavaScript runtime.
                    // Without one yt-dlp silently falls back to formats that are
                    // refused when fetched.
                    string runtime = FindJsRuntime();
                    if (runtime != null)
                    {
                        fetch.ArgumentList.Add("--js-runtimes");
                        fetch.ArgumentList.Add(runtime);
                    }
                    // YouTube's default client for this format is ANDROID_VR,
                    // whose media URLs answer 403 to yt-dlp's and ffmpeg's own
                    // fetches even though the extraction itself succeeds. mweb
                    // serves a stream that downloads normally.
                    fetch.ArgumentList.Add("--extractor-args");
                    fetch.ArgumentList.Add("youtube:player_client=mweb");
                    fetch.ArgumentList.Add("-f");
                    fetch.ArgumentList.Add("bestaudio/best");
                    fetch.ArgumentList.Add("-o");
                    fetch.ArgumentList.Add("-");
                    fetch.ArgumentList.Add("--");
                    fetch.ArgumentList.Add(url);

                    ProcessStartInfo play = new ProcessStartInfo(playerExecutable)
                    {
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    play.ArgumentList.Add("--no-video");
                    play.ArgumentList.Add("--input-terminal=no");
                    play.ArgumentList.Add("--no-config");
                    play.ArgumentList.Add("--idle=no");
                    play.ArgumentList.Add("--msg-level=all=error");
                    play.ArgumentList.Add("--volume=" + ClampVolume(MainViewModel.s_instance.previewVolume));
                    play.ArgumentList.Add("--input-ipc-server=" + socketPath);
                    play.ArgumentList.Add("-");

                    ytdl = Process.Start(fetch);
                    mpv = Process.Start(play);

                    lock (gate)
                    {
                        player = mpv;
                        fetcher = ytdl;
                        ipcPath = socketPath;
                    }

                    // Drain every pipe we are not otherwise reading, or a full
                    // buffer would deadlock the child.
                    Task<string> fetchError = ytdl.StandardError.ReadToEndAsync();
                    Task<string> playOutput = mpv.StandardOutput.ReadToEndAsync();
                    Task<string> playError = mpv.StandardError.ReadToEndAsync();

                    Task pump = PumpAsync(ytdl, mpv);

                    await mpv.WaitForExitAsync();
                    await pump;
                    await ytdl.WaitForExitAsync();

                    await playOutput;
                    string error = (await playError).Trim();
                    if (String.IsNullOrEmpty(error)) error = (await fetchError).Trim();

                    // 4 is mpv's exit code when playback was ended by a command,
                    // which is what Stop() does.
                    if (mpv.ExitCode != 0 && mpv.ExitCode != 4 && !String.IsNullOrEmpty(error))
                    {
                        MainViewModel.Log("Preview failed: " + error);
                    }

                    StopPlaying(playing);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Process.Start throws this when the executable is not on PATH.
                    MainViewModel.Log("Audio preview needs " + downloader + " and " + playerExecutable
                        + " on PATH. Install them to enable previews.");
                    StopPlaying(playing);
                }
                catch (Exception e)
                {
                    MainViewModel.Log(MethodBase.GetCurrentMethod(), e);
                    StopPlaying(playing);
                }
                finally
                {
                    KillQuietly(ytdl);

                    lock (gate)
                    {
                        if (player == mpv)
                        {
                            player = null;
                            fetcher = null;
                            ipcPath = null;
                        }
                    }

                    try
                    {
                        if (socketPath != null && File.Exists(socketPath)) File.Delete(socketPath);
                    }
                    catch { }

                    if (ytdl != null) ytdl.Dispose();
                    if (mpv != null) mpv.Dispose();
                }
            });
        }

        // Copies the downloaded audio into the player. Both ends can disappear
        // mid-track when the user stops playback, which is not an error.
        private static async Task PumpAsync(Process from, Process to)
        {
            try
            {
                await from.StandardOutput.BaseStream.CopyToAsync(to.StandardInput.BaseStream);
                await to.StandardInput.BaseStream.FlushAsync();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                try { to.StandardInput.Close(); } catch { }
            }
        }

        private static string FindJsRuntime()
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (path == null) return null;

            foreach (string runtime in new[] { "deno", "node", "bun" })
            {
                foreach (string dir in path.Split(Path.PathSeparator))
                {
                    if (dir.Length == 0) continue;
                    try
                    {
                        if (File.Exists(Path.Combine(dir, runtime))) return runtime;
                    }
                    catch { }
                }
            }
            return null;
        }

        public static void SetVolume(int volume)
        {
            string socketPath;
            lock (gate) { socketPath = ipcPath; }
            if (socketPath == null) return;

            // Best effort: mpv creates the socket asynchronously, so a change made
            // in the first moments of a track can arrive before it exists. The next
            // track picks the value up through --volume regardless.
            try
            {
                using (Socket socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
                {
                    socket.Connect(new UnixDomainSocketEndPoint(socketPath));
                    string command = "{\"command\":[\"set_property\",\"volume\"," + ClampVolume(volume) + "]}\n";
                    socket.Send(Encoding.UTF8.GetBytes(command));
                }
            }
            catch { }
        }

        public static void Stop()
        {
            Process mpv;
            Process ytdl;
            lock (gate)
            {
                mpv = player;
                ytdl = fetcher;
            }

            try
            {
                KillQuietly(ytdl);
                KillQuietly(mpv);
            }
            catch (Exception e)
            {
                MainViewModel.Log(MethodBase.GetCurrentMethod(), e);
            }
            finally
            {
                StopPlaying(currentlyPlaying);
            }
        }

        private static void KillQuietly(Process process)
        {
            try
            {
                if (process != null && !process.HasExited) process.Kill(true);
            }
            catch { }
        }

        private static int ClampVolume(int volume)
        {
            return Math.Clamp(volume, 0, 100);
        }

        private static void StopPlaying(MapItem playing)
        {
            if (playing != null) playing.playing = false;
            if (currentlyPlaying == playing)
            {
                currentlyPlaying = null;
            }
        }
    }
}
