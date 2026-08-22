using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UwUTerm.Nvim
{
    /// <summary>
    /// A filesystem path as a socket address.
    ///
    /// net472 has no endpoint for AF_UNIX - it arrived in .NET Core - but the socket layer
    /// underneath does, and an EndPoint is only asked to lay itself out as the sockaddr the
    /// kernel expects. That is two bytes of family followed by a null-terminated path, so
    /// this is the whole of it.
    ///
    /// Worth having over a loopback port: neovim's rpc will run any lua it is handed, and a
    /// port is reachable by everything else on the machine while a socket is reachable by
    /// whoever can open the file.
    /// </summary>
    internal sealed class UnixEndPoint : EndPoint
    {
        // sun_path in sockaddr_un, which is 108 bytes on Linux and not negotiable.
        internal const int MaximumPath = 107;

        private readonly string _path;

        internal UnixEndPoint(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("a socket needs a path");
            _path = path;
        }

        public override AddressFamily AddressFamily => AddressFamily.Unix;

        public override SocketAddress Serialize()
        {
            byte[] path = Encoding.UTF8.GetBytes(_path);
            var address = new SocketAddress(AddressFamily.Unix, 2 + path.Length + 1);

            for (int i = 0; i < path.Length; i++) address[2 + i] = path[i];
            address[2 + path.Length] = 0;

            return address;
        }

        public override EndPoint Create(SocketAddress socketAddress)
        {
            if (socketAddress == null || socketAddress.Size <= 2) return new UnixEndPoint("");

            int end = 2;
            while (end < socketAddress.Size && socketAddress[end] != 0) end++;

            var path = new byte[end - 2];
            for (int i = 0; i < path.Length; i++) path[i] = socketAddress[2 + i];

            return new UnixEndPoint(Encoding.UTF8.GetString(path));
        }

        public override string ToString() => _path;

        public override bool Equals(object other) =>
            other is UnixEndPoint endpoint && endpoint._path == _path;

        public override int GetHashCode() => _path.GetHashCode();
    }
}
