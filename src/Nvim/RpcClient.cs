using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace UwUTerm.Nvim
{
    /// <summary>
    /// A msgpack-rpc conversation with an embedded neovim.
    ///
    /// "nvim --embed" speaks msgpack-rpc over its stdin and stdout: requests are
    /// [0, id, method, args], their answers [1, id, error, result], and anything the editor
    /// says on its own [2, method, args]. Redraws arrive as notifications, which is what makes
    /// this a UI protocol rather than a terminal - there is no escape sequence anywhere in it.
    ///
    /// Reading happens on its own thread because the stream blocks, and delivery happens on
    /// the caller's, because everything that reacts to a redraw touches Unity objects and
    /// those may only be touched from the main thread. Between the two sits a queue.
    /// </summary>
    internal sealed class RpcClient : IDisposable
    {
        private const int RequestKind = 0;
        private const int ResponseKind = 1;
        private const int NotificationKind = 2;

        private Process _child;
        private Stream _toChild;
        private System.Threading.Thread _reader;
        private volatile bool _stopping;

        private int _nextId;

        private readonly Dictionary<int, Action<object, object>> _waiting =
            new Dictionary<int, Action<object, object>>();

        // Filled by the reading thread, drained by the main one.
        private readonly Queue<Notification> _arrived = new Queue<Notification>();

        internal struct Notification
        {
            internal string Method;
            internal object[] Arguments;
        }

        /// <summary>Raised on the reading thread when the conversation ends - the editor
        /// exiting, or the pipe breaking.</summary>
        internal event Action<string> Ended;

        internal bool Running => _child != null && !_child.HasExited;

        // Every client that has started a child, so none is left running if the game goes away
        // without taking its editors down first.
        private static readonly List<RpcClient> Live = new List<RpcClient>();

        static RpcClient()
        {
            // A crash or a hard quit skips everything else. This is the last thing that runs.
            AppDomain.CurrentDomain.ProcessExit += (sender, e) => KillAll();
            AppDomain.CurrentDomain.DomainUnload += (sender, e) => KillAll();
        }

        /// <summary>Stop every editor still running. Safe to call more than once.</summary>
        internal static void KillAll()
        {
            RpcClient[] clients;
            lock (Live) clients = Live.ToArray();

            foreach (RpcClient client in clients) client.Dispose();
        }

        /// <summary>Start neovim with no UI of its own and no shell around it.</summary>
        internal bool Start(string program, string arguments, string workingDirectory = null)
        {
            var info = new ProcessStartInfo(program, arguments)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            // Where the editor thinks it is. Without this it inherits the game's own directory
            // and every :e, :w and file browser starts in the middle of the install.
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                info.WorkingDirectory = workingDirectory;

            try
            {
                _child = Process.Start(info);
                lock (Live) Live.Add(this);
            }
            catch (Exception e)
            {
                Ended?.Invoke($"could not start {program}: {e.Message}");
                return false;
            }

            _toChild = _child.StandardInput.BaseStream;

            _reader = new System.Threading.Thread(Read) { IsBackground = true, Name = "UwUTerm.Nvim" };
            _reader.Start();
            return true;
        }

        // ---- talking ---------------------------------------------------------------------

        /// <summary>Ask for something and hear back. The answer arrives on the reading thread,
        /// so a handler that touches the scene has to hop across itself.</summary>
        internal void Request(string method, object[] arguments, Action<object, object> answered = null)
        {
            int id = System.Threading.Interlocked.Increment(ref _nextId);
            if (answered != null) lock (_waiting) _waiting[id] = answered;

            Send(new object[] { (long)RequestKind, (long)id, method, arguments ?? new object[0] });
        }

        /// <summary>Say something that expects no answer.</summary>
        internal void Notify(string method, object[] arguments)
        {
            Send(new object[] { (long)NotificationKind, method, arguments ?? new object[0] });
        }

        private void Send(object[] message)
        {
            if (_toChild == null) return;

            try
            {
                using (var buffer = new MemoryStream(256))
                {
                    MsgPack.Write(buffer, message);
                    byte[] bytes = buffer.ToArray();

                    lock (_toChild)
                    {
                        _toChild.Write(bytes, 0, bytes.Length);
                        _toChild.Flush();
                    }
                }
            }
            catch (Exception e)
            {
                Stop("write failed: " + e.Message);
            }
        }

        // ---- listening -------------------------------------------------------------------

        /// <summary>Hand over everything that has arrived since the last call. Called from the
        /// main thread, which is the only place a redraw may be acted on.</summary>
        internal int Drain(List<Notification> into)
        {
            lock (_arrived)
            {
                while (_arrived.Count > 0) into.Add(_arrived.Dequeue());
                return into.Count;
            }
        }

        private void Read()
        {
            Stream stream = _child.StandardOutput.BaseStream;

            var buffer = new byte[64 * 1024];
            int end = 0;

            try
            {
                while (!_stopping)
                {
                    if (end == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);

                    int read = stream.Read(buffer, end, buffer.Length - end);
                    if (read <= 0) break;
                    end += read;

                    // A read returns whatever has arrived, which may be half a message or
                    // several - so decode until one comes up short, then keep the remainder.
                    int at = 0;
                    while (MsgPack.TryRead(buffer, end, ref at, out object message)) Dispatch(message);

                    if (at > 0)
                    {
                        Array.Copy(buffer, at, buffer, 0, end - at);
                        end -= at;
                    }
                }
            }
            catch (Exception e)
            {
                if (!_stopping) Stop("read failed: " + e.Message);
                return;
            }

            Stop("the editor exited");
        }

        private void Dispatch(object message)
        {
            if (!(message is object[] parts) || parts.Length < 3) return;

            switch ((int)Convert.ToInt64(parts[0]))
            {
                case ResponseKind when parts.Length >= 4:
                {
                    int id = (int)Convert.ToInt64(parts[1]);
                    Action<object, object> answered = null;

                    lock (_waiting)
                        if (_waiting.TryGetValue(id, out answered)) _waiting.Remove(id);

                    answered?.Invoke(parts[2], parts[3]);
                    return;
                }

                case NotificationKind:
                {
                    var notification = new Notification
                    {
                        Method = parts[1] as string ?? "",
                        Arguments = parts[2] as object[] ?? new object[0],
                    };

                    lock (_arrived) _arrived.Enqueue(notification);
                    return;
                }
            }
        }

        // ---- ending ----------------------------------------------------------------------

        private void Stop(string why)
        {
            if (_stopping) return;
            _stopping = true;

            Ended?.Invoke(why);
        }

        public void Dispose()
        {
            _stopping = true;

            lock (Live) Live.Remove(this);

            // Closing the pipe is how an embedded neovim is asked to leave: it exits when its
            // channel does. Killing it outright would work too, but it would take whatever the
            // editor had not finished writing with it.
            try { _toChild?.Close(); }
            catch (Exception) { }

            try
            {
                if (_child != null && !_child.WaitForExit(1000) && !_child.HasExited) _child.Kill();
            }
            catch (Exception) { }

            try { _child?.Dispose(); }
            catch (Exception) { }

            _child = null;
            _toChild = null;
        }
    }
}
