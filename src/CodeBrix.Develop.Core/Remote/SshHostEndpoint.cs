//
// SshHostEndpoint.cs
//
// Copyright (c) 2026 Jeremy Ellis and contributors
// SPDX-License-Identifier: MIT
//

using System.Globalization;

namespace CodeBrix.Develop.Core.Remote;

/// <summary>
/// The parsed form of an SSH "Hostname/address" field: a host plus an
/// optional ":port" suffix, defaulting to the standard SSH port 22.
/// Understands "host", "host:2222", a bare IPv6 address ("::1" — with two or
/// more colons every colon belongs to the address, never to a port), and the
/// bracketed IPv6-with-port form ("[::1]:2222").
/// </summary>
public sealed class SshHostEndpoint
{
    /// <summary>The standard SSH port, used when the input names none.</summary>
    public const int DefaultPort = 22;

    SshHostEndpoint(string host, int port)
    {
        Host = host;
        Port = port;
    }

    /// <summary>The host name or address, without brackets or port.</summary>
    public string Host { get; }

    /// <summary>The port; <see cref="DefaultPort"/> unless the input carried a ":port" suffix.</summary>
    public int Port { get; }

    /// <summary>
    /// Parses a user-typed host field. Returns null when the input is blank,
    /// the host part is empty or contains whitespace, or a ":port" suffix is
    /// not a number between 1 and 65535.
    /// </summary>
    public static SshHostEndpoint TryParse(string input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        string host;
        string portText = null;

        if (text[0] == '[')
        {
            // Bracketed IPv6: "[address]" or "[address]:port".
            var close = text.IndexOf(']');
            if (close < 0)
                return null;
            host = text.Substring(1, close - 1);
            var rest = text.Substring(close + 1);
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                    return null;
                portText = rest.Substring(1);
            }
        }
        else
        {
            var firstColon = text.IndexOf(':');
            if (firstColon < 0 || text.IndexOf(':', firstColon + 1) >= 0)
            {
                // No colon, or several: a plain host or a bare IPv6 address.
                host = text;
            }
            else
            {
                host = text.Substring(0, firstColon);
                portText = text.Substring(firstColon + 1);
            }
        }

        if (host.Length == 0)
            return null;
        foreach (var c in host)
        {
            if (char.IsWhiteSpace(c))
                return null;
        }

        var port = DefaultPort;
        if (portText != null
            && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535))
            return null;

        return new SshHostEndpoint(host, port);
    }
}
