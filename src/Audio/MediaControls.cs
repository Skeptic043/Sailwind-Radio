using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SailwindRadio.Audio
{
    // Reads and controls one application's Windows media session, the one media keys and the volume popup use,
    // through Windows.Media.Control. Its own thread polls every WinRT operation instead of registering
    // handlers, so Windows never calls into Mono and the audio worker never waits for an application.
    internal sealed class MediaControls
    {
        internal enum Command { None, PlayPause, Next, Previous, Play, Pause }
        private const int CommandMilliseconds = 50, RefreshMilliseconds = 1000, OperationMilliseconds = 2000, ShutdownPauseMilliseconds = 250;
        private const int AsyncCompleted = 1, AsyncStarted = 0;
        private static readonly Guid StaticsId = new Guid("2050c4ee-11a0-57de-aed7-c97c70338245");
        private static readonly Guid AsyncInfoId = new Guid("00000036-0000-0000-C000-000000000046");
        private readonly string program;
        private readonly string[] identities;
        private readonly Thread worker;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private volatile bool stopping;
        private int pending;
        // Kept apart from pending, so the worker's loop never takes it before it can be delivered.
        private volatile Command final;
        private volatile bool finalWasSounding;
        private volatile Track current;
        // Set once the worker has looked for the application, and false again only if its loop stops.
        private volatile bool located, reachable;

        // The application's current track, or null when it has no matching media session.
        internal Track Current { get { return current; } }

        internal sealed class Track
        {
            internal readonly string Title, Artist, Album;
            internal Track(string title, string artist, string album) { Title = title; Artist = artist; Album = album; }
        }

        [DllImport("combase.dll", ExactSpelling = true)] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out IntPtr value);
        [DllImport("combase.dll", ExactSpelling = true)] private static extern int WindowsDeleteString(IntPtr value);
        [DllImport("combase.dll", ExactSpelling = true)] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr value, out int length);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryMethod(IntPtr self, ref Guid iid, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PointerMethod(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IntMethod(IntPtr self, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ItemMethod(IntPtr self, int index, out IntPtr value);

        // Vtable slots after IInspectable's 0 to 5.
        private const int SlotRequest = 6, SlotSessions = 7, SlotGetAt = 6, SlotSize = 7, SlotStatus = 7, SlotResults = 8;
        private const int SlotIdentity = 6, SlotProperties = 7, SlotPlaybackInfo = 9, SlotPlay = 10, SlotPause = 11, SlotNext = 16, SlotPrevious = 17, SlotPlayPause = 20;
        private const int SlotPlaybackStatus = 7, StatusPlaying = 4;
        private const int SlotTitle = 6, SlotArtist = 9, SlotAlbum = 10;

        internal MediaControls(AudioApplication application)
        {
            program = application.Program;
            identities = Identities(program);
            worker = new Thread(Run) { IsBackground = true, Name = "Sailwind Radio media controls" };
            worker.Start();
        }

        // False when the application has neither a media session nor a window to send it to. Before the first
        // look the command waits, since the application usually turns up a moment later.
        internal bool Send(Command command)
        {
            if (located && !reachable) return false;
            int seen, merged;
            do
            {
                seen = Volatile.Read(ref pending);
                merged = (int)Merge((Command)seen, command);
            }
            while (Interlocked.CompareExchange(ref pending, merged, seen) != seen);
            try { wake.Set(); }
            catch (ObjectDisposedException) { }
            return true;
        }

        // A second play/pause before the first is delivered undoes it, so quick presses add up as pressed.
        // Any other command replaces what was waiting.
        internal static Command Merge(Command waiting, Command next) =>
            waiting == Command.PlayPause && next == Command.PlayPause ? Command.None : next;

        // Last is sent before the worker ends, such as the pause for a radio that switched away or closed.
        // For Pause, wasSounding tells the worker whether capture heard audio recently, so silent or already-paused
        // apps are not sent a stray window command that Windows could bounce to another media player.
        internal void Stop(Command last, bool wasSounding = true)
        {
            finalWasSounding = wasSounding;
            final = last;
            stopping = true;
            try { wake.Set(); }
            catch (ObjectDisposedException) { }
        }

        // Pure decision rule for shutdown pauses:
        // - With a Windows media session whose playback state is known, pause only if it is actually Playing.
        // - Otherwise (no session or unknown state), pause only if the app was actually making sound.
        internal static bool ShouldSendShutdownPause(bool hasSession, bool statusKnown, bool sessionPlaying, bool wasSounding)
        {
            if (hasSession && statusKnown) return sessionPlaying;
            return wasSounding;
        }

        internal bool Join(int milliseconds) => worker.Join(milliseconds);

        // Names Windows may know the application by. Win32 programs usually report their file name, and Firefox
        // reports a hash of its install folder, which it records under TaskBarIDs.
        private static string[] Identities(string program)
        {
            var names = new List<string> { Path.GetFileNameWithoutExtension(program) };
            string folder = Path.GetDirectoryName(program);
            foreach (IntPtr root in new[] { CurrentUser, LocalMachine })
            {
                var id = new System.Text.StringBuilder(64);
                int size = id.Capacity * 2, type;
                if (RegGetValueW(root, "Software\\Mozilla\\Firefox\\TaskBarIDs", folder, StringValues, out type, id, ref size) == 0 && id.Length > 0)
                    names.Add(id.ToString());
            }
            return names.ToArray();
        }

        private static readonly IntPtr CurrentUser = new IntPtr(unchecked((int)0x80000001)), LocalMachine = new IntPtr(unchecked((int)0x80000002));
        private const int StringValues = 0x2;
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegGetValueW(IntPtr key, string subKey, string value, int flags, out int type, System.Text.StringBuilder data, ref int size);

        private bool Matches(string identity)
        {
            foreach (string name in identities)
                if (identity.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void Run()
        {
            IntPtr manager = IntPtr.Zero, session = IntPtr.Zero;
            try
            {
                using (new ComCalls.Apartment())
                {
                    // Without Windows media sessions the application's window still takes the buttons.
                    try { manager = Await(RequestManager()); }
                    catch (Exception) { manager = IntPtr.Zero; }
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    long nextRefresh = 0;
                    IntPtr window = IntPtr.Zero;
                    while (!stopping)
                    {
                        // Deliver queued pause/play commands immediately if we already have a target,
                        // before doing any 1-second session refresh or metadata read.
                        if (session != IntPtr.Zero || window != IntPtr.Zero)
                            Deliver(session, window, (Command)Interlocked.Exchange(ref pending, 0));
                        bool refresh = clock.ElapsedMilliseconds >= nextRefresh;
                        if (refresh)
                        {
                            nextRefresh = clock.ElapsedMilliseconds + RefreshMilliseconds;
                            Locate(manager, ref session, ref window);
                            reachable = session != IntPtr.Zero || window != IntPtr.Zero;
                            located = true;
                        }
                        // After the lookup, so a press made before the first one reaches the application. Before the
                        // track is read, so a slow read never holds a press back.
                        Deliver(session, window, (Command)Interlocked.Exchange(ref pending, 0));
                        // Without a media session, the application's window title names the track.
                        if (refresh)
                            current = session != IntPtr.Zero ? Read(session) :
                                window != IntPtr.Zero ? new Track(AppWindows.Title(window), "", "") : null;
                        if (!stopping) wake.WaitOne(CommandMilliseconds);
                    }
                    // On shutdown, deliver the final command cleanly before releasing COM and restoring desktop volume.
                    if (final == Command.Pause) DeliverShutdownPause(manager, ref session, ref window, finalWasSounding);
                    else if (final != Command.None)
                    {
                        if (session == IntPtr.Zero && window == IntPtr.Zero) Locate(manager, ref session, ref window);
                        Deliver(session, window, final);
                    }
                }
            }
            // Track names and buttons are extras. Without them the application still plays.
            catch (Exception) { current = null; reachable = false; located = true; }
            finally
            {
                ComCalls.Release(ref session);
                ComCalls.Release(ref manager);
                wake.Close();
            }
        }

        // The application's media session, or without one, such as Spotify with its media controls setting off, its
        // window, which takes the buttons instead.
        private void Locate(IntPtr manager, ref IntPtr session, ref IntPtr window)
        {
            ComCalls.Release(ref session);
            session = manager == IntPtr.Zero ? IntPtr.Zero : Find(manager);
            window = session == IntPtr.Zero ? AppWindows.Find(program) : IntPtr.Zero;
        }

        private static void Deliver(IntPtr session, IntPtr window, Command command)
        {
            if (command == Command.None) return;
            if (session != IntPtr.Zero) Fire(session, command);
            else if (window != IntPtr.Zero) AppWindows.Send(window, command);
        }

        private static IntPtr RequestManager()
        {
            IntPtr name, statics;
            const string type = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";
            Check(WindowsCreateString(type, type.Length, out name));
            try
            {
                Guid id = StaticsId;
                Check(RoGetActivationFactory(name, ref id, out statics));
            }
            finally { WindowsDeleteString(name); }
            try
            {
                IntPtr operation;
                Check(ComCalls.Method<PointerMethod>(statics, SlotRequest)(statics, out operation));
                return operation;
            }
            finally { ComCalls.Release(ref statics); }
        }

        // The first session the application owns, or zero.
        private IntPtr Find(IntPtr manager)
        {
            IntPtr sessions;
            if (ComCalls.Method<PointerMethod>(manager, SlotSessions)(manager, out sessions) < 0) return IntPtr.Zero;
            try
            {
                int count;
                if (ComCalls.Method<IntMethod>(sessions, SlotSize)(sessions, out count) < 0) return IntPtr.Zero;
                for (int index = 0; index < count; index++)
                {
                    IntPtr session;
                    if (ComCalls.Method<ItemMethod>(sessions, SlotGetAt)(sessions, index, out session) < 0) continue;
                    if (Matches(Text(session, SlotIdentity))) return session;
                    ComCalls.Release(ref session);
                }
                return IntPtr.Zero;
            }
            finally { ComCalls.Release(ref sessions); }
        }

        private Track Read(IntPtr session)
        {
            IntPtr properties = IntPtr.Zero;
            try
            {
                IntPtr operation;
                if (ComCalls.Method<PointerMethod>(session, SlotProperties)(session, out operation) < 0) return new Track("", "", "");
                properties = Await(operation);
                return new Track(Text(properties, SlotTitle), Text(properties, SlotArtist), Text(properties, SlotAlbum));
            }
            catch (IOException) { return new Track("", "", ""); }
            finally { ComCalls.Release(ref properties); }
        }

        private void DeliverShutdownPause(IntPtr manager, ref IntPtr session, ref IntPtr window, bool wasSounding)
        {
            if (session == IntPtr.Zero) Locate(manager, ref session, ref window);
            if (session != IntPtr.Zero)
            {
                bool statusKnown = TryIsPlaying(session, out bool playing);
                if (!statusKnown)
                {
                    // Cached session may have gone stale between 1-second refreshes; look it up once more.
                    Locate(manager, ref session, ref window);
                    if (session != IntPtr.Zero) statusKnown = TryIsPlaying(session, out playing);
                }
                if (session != IntPtr.Zero)
                {
                    if (!ShouldSendShutdownPause(true, statusKnown, playing, wasSounding)) return;
                    if (!FireAndWait(session, Command.Pause, ShutdownPauseMilliseconds))
                    {
                        Locate(manager, ref session, ref window);
                        if (session != IntPtr.Zero && (!TryIsPlaying(session, out playing) || playing))
                            FireAndWait(session, Command.Pause, ShutdownPauseMilliseconds);
                    }
                    return;
                }
            }
            if (window != IntPtr.Zero && ShouldSendShutdownPause(false, false, false, wasSounding))
                AppWindows.Send(window, Command.Pause);
        }

        private static bool TryIsPlaying(IntPtr session, out bool playing)
        {
            playing = false;
            IntPtr info = IntPtr.Zero;
            try
            {
                if (ComCalls.Method<PointerMethod>(session, SlotPlaybackInfo)(session, out info) < 0 || info == IntPtr.Zero)
                    return false;
                int status;
                if (ComCalls.Method<IntMethod>(info, SlotPlaybackStatus)(info, out status) < 0)
                    return false;
                playing = status == StatusPlaying;
                return true;
            }
            finally { ComCalls.Release(ref info); }
        }

        // Starts a media command and waits briefly for the WinRT async operation to finish so shutdown
        // does not tear down COM or restore desktop volume before the application receives Pause.
        private static bool FireAndWait(IntPtr session, Command command, int timeoutMilliseconds)
        {
            int slot = command == Command.Next ? SlotNext : command == Command.Previous ? SlotPrevious :
                command == Command.Play ? SlotPlay : command == Command.Pause ? SlotPause : SlotPlayPause;
            IntPtr operation = IntPtr.Zero, info = IntPtr.Zero;
            try
            {
                if (ComCalls.Method<PointerMethod>(session, slot)(session, out operation) < 0 || operation == IntPtr.Zero)
                    return false;
                Guid id = AsyncInfoId;
                if (ComCalls.Method<QueryMethod>(operation, 0)(operation, ref id, out info) < 0 || info == IntPtr.Zero)
                    return true;
                int status = AsyncStarted;
                for (int waited = 0; waited < timeoutMilliseconds; waited += 5)
                {
                    if (ComCalls.Method<IntMethod>(info, SlotStatus)(info, out status) < 0 || status != AsyncStarted)
                        break;
                    Thread.Sleep(5);
                }
                // Once SlotPause accepted the call, do not send a duplicate Pause even if the app took >250 ms.
                return true;
            }
            finally
            {
                ComCalls.Release(ref info);
                ComCalls.Release(ref operation);
            }
        }

        // The command runs on in the application. Its result is not needed, so the operation is released at once.
        private static void Fire(IntPtr session, Command command)
        {
            int slot = command == Command.Next ? SlotNext : command == Command.Previous ? SlotPrevious :
                command == Command.Play ? SlotPlay : command == Command.Pause ? SlotPause : SlotPlayPause;
            IntPtr operation;
            if (ComCalls.Method<PointerMethod>(session, slot)(session, out operation) >= 0) ComCalls.Release(ref operation);
        }

        // Polls an IAsyncOperation until it completes, then returns its result and releases it.
        private IntPtr Await(IntPtr operation)
        {
            IntPtr info = IntPtr.Zero;
            try
            {
                Guid id = AsyncInfoId;
                Check(ComCalls.Method<QueryMethod>(operation, 0)(operation, ref id, out info));
                int status = AsyncStarted;
                for (int waited = 0; !stopping && waited < OperationMilliseconds; waited += 5)
                {
                    Check(ComCalls.Method<IntMethod>(info, SlotStatus)(info, out status));
                    if (status != AsyncStarted) break;
                    Thread.Sleep(5);
                }
                if (status != AsyncCompleted) throw new IOException("Windows media request did not complete");
                IntPtr result;
                Check(ComCalls.Method<PointerMethod>(operation, SlotResults)(operation, out result));
                return result;
            }
            finally
            {
                ComCalls.Release(ref info);
                ComCalls.Release(ref operation);
            }
        }

        private static string Text(IntPtr self, int slot)
        {
            IntPtr value;
            if (ComCalls.Method<PointerMethod>(self, slot)(self, out value) < 0 || value == IntPtr.Zero) return "";
            try
            {
                int length;
                IntPtr buffer = WindowsGetStringRawBuffer(value, out length);
                return buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(buffer, length);
            }
            finally { WindowsDeleteString(value); }
        }

        private static void Check(int result)
        {
            if (result < 0) throw new IOException("Windows media controls are unavailable (error 0x" + result.ToString("X8") + ")");
        }
    }

    // An application's main window, for applications without a Windows media session. Media commands go to it as
    // WM_APPCOMMAND, the message media keys send, so only that application receives them.
    internal static class AppWindows
    {
        private const int AppCommand = 0x319, PlayPause = 14, Next = 11, Previous = 12, Play = 46, Pause = 47;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string type, string title);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);
        [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, System.Text.StringBuilder text, int size);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        // The first visible, titled top-level window of a process running program, or zero.
        internal static IntPtr Find(string program)
        {
            var tree = new ProcessTree();
            for (IntPtr window = FindWindowExW(IntPtr.Zero, IntPtr.Zero, null, null); window != IntPtr.Zero;
                window = FindWindowExW(IntPtr.Zero, window, null, null))
            {
                int processId;
                if (!IsWindowVisible(window) || GetWindowTextLengthW(window) == 0) continue;
                GetWindowThreadProcessId(window, out processId);
                if (String.Equals(tree.Program(processId), program, StringComparison.OrdinalIgnoreCase)) return window;
            }
            return IntPtr.Zero;
        }

        internal static string Title(IntPtr window)
        {
            var text = new System.Text.StringBuilder(256);
            GetWindowTextW(window, text, text.Capacity);
            return text.ToString();
        }

        internal static void Send(IntPtr window, MediaControls.Command command)
        {
            int code = command == MediaControls.Command.Next ? Next : command == MediaControls.Command.Previous ? Previous :
                command == MediaControls.Command.Play ? Play : command == MediaControls.Command.Pause ? Pause : PlayPause;
            PostMessageW(window, AppCommand, window, new IntPtr(code << 16));
        }
    }
}
