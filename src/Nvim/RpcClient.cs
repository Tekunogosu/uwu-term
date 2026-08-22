using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;

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
        private Socket _socket;
        private NamedPipeClientStream _pipe;
        private bool _joined;
        private Stream _toChild;
        private Stream _fromChild;
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

        internal bool Running =>
            (_child != null && !_child.HasExited)
            || (_socket != null && _socket.Connected)
            || (_pipe != null && _pipe.IsConnected);

        /// <summary>True when this is a conversation with an editor somebody else started, so
        /// ending it means hanging up rather than shutting the editor down.</summary>
        internal bool Attached => _joined;

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
        internal bool Start(string program, string arguments, string workingDirectory = null,
                            IDictionary<string, string> environment = null)
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

            // Which config and which plugins. Set rather than inherited because what the game
            // inherits is the sandbox's, and that is not where the mod's own runtime files are.
            if (environment != null)
                foreach (KeyValuePair<string, string> variable in environment)
                    info.EnvironmentVariables[variable.Key] = variable.Value;

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
            _fromChild = _child.StandardOutput.BaseStream;

            StartReading();
            return true;
        }

        /// <summary>
        /// Join an editor that is already running, instead of starting one.
        ///
        /// The protocol does not care what it travels over - the same msgpack goes down a
        /// socket that goes down a pipe - so an editor outside the game is reached the same
        /// way an embedded one is. Which is the point: a neovim on the machine has the
        /// machine's git, its compilers and its language servers, none of which exist in the
        /// container the game runs in.
        ///
        /// The address is either a path, which is a unix socket, or host:port.
        /// </summary>
        internal bool Connect(string address)
        {
            Stream stream;

            try
            {
                if (SplitPort(address, out string host, out int port))
                {
                    _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                    };
                    _socket.Connect(host, port);
                    stream = new NetworkStream(_socket, ownsSocket: false);
                }
                else if (NvimInstall.IsWindows)
                {
                    // "Named pipe" is neovim's word for both, but they are not the same object:
                    // on Windows --listen opens a real named pipe, which is not a socket and
                    // cannot be reached through one.
                    _pipe = new NamedPipeClientStream(".", PipeName(address),
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    _pipe.Connect(2000);
                    stream = _pipe;
                }
                else
                {
                    _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    _socket.Connect(new UnixEndPoint(address));
                    stream = new NetworkStream(_socket, ownsSocket: false);
                }
            }
            catch (Exception e)
            {
                _socket = null;
                _pipe = null;
                Ended?.Invoke($"could not reach {address} - {e.Message}");
                return false;
            }

            _joined = true;
            _toChild = stream;
            _fromChild = stream;

            lock (Live) Live.Add(this);

            StartReading();
            return true;
        }

        /// <summary>The pipe's own name, which is what NamedPipeClientStream wants - it puts
        /// the \\.\pipe\ back itself, and chokes on being given it twice.</summary>
        private static string PipeName(string address)
        {
            int last = address.LastIndexOf('\\');
            return last >= 0 && last < address.Length - 1 ? address.Substring(last + 1) : address;
        }

        /// <summary>host:port, or not. A path is anything with a separator in it or no number
        /// after the last colon, which is every socket path and no address.</summary>
        private static bool SplitPort(string address, out string host, out int port)
        {
            host = null;
            port = 0;

            if (string.IsNullOrEmpty(address) || address.IndexOf('/') >= 0) return false;

            int colon = address.LastIndexOf(':');
            if (colon <= 0 || colon == address.Length - 1) return false;

            if (!int.TryParse(address.Substring(colon + 1), out port) || port <= 0 || port > 65535)
                return false;

            host = address.Substring(0, colon);
            return true;
        }

        private void StartReading()
        {
            _reader = new System.Threading.Thread(Read) { IsBackground = true, Name = "UwUTerm.Nvim" };
            _reader.Start();
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
            Stream stream = _fromChild;
            if (stream == null) return;

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

            Stop(Attached ? "the editor closed the connection" : "the editor exited");
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
            // editor had not finished writing with it. An editor reached over a socket is
            // somebody else's, so hanging up is all that happens to it.
            try { _toChild?.Close(); }
            catch (Exception) { }

            try
            {
                if (_child != null && !_child.WaitForExit(1000) && !_child.HasExited) _child.Kill();
            }
            catch (Exception) { }

            try { _child?.Dispose(); }
            catch (Exception) { }

            try { _socket?.Close(); }
            catch (Exception) { }

            try { _pipe?.Dispose(); }
            catch (Exception) { }

            _child = null;
            _socket = null;
            _pipe = null;
            _toChild = null;
            _fromChild = null;
        }
    }
}
