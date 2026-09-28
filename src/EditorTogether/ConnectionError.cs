using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EditorTogether
{
    internal static class ConnectionError
    {
        public static string Describe(Exception exception, bool createRoom, string room)
        {
            string roomName = string.IsNullOrWhiteSpace(room) ? "room" : "Room " + room.Trim();
            string details = Flatten(exception);

            if (ContainsHttpStatus(details, 404))
                return roomName + " not found";
            if (ContainsHttpStatus(details, 409))
                return roomName + " already exists";
            if (ContainsHttpStatus(details, 426))
                return "Protocol mismatch - update Editor Together/server";
            if (ContainsHttpStatus(details, 401) || ContainsHttpStatus(details, 403))
                return "Server refused access";
            if (ContainsHttpStatus(details, 400))
                return "Server rejected the connection";
            if (ContainsHttpStatus(details, 502) || ContainsHttpStatus(details, 503) || ContainsHttpStatus(details, 504))
                return "Server unavailable";

            if (HasException<UriFormatException>(exception))
                return "Invalid server URL";
            if (IsSecureConnectionFailure(exception, details))
                return "Secure connection failed";
            if (IsNetworkFailure(exception, details))
                return "Server unreachable";

            string shortMessage = ShortMessage(exception);
            string prefix = createRoom ? "Create room failed" : "Join room failed";
            return string.IsNullOrEmpty(shortMessage) ? prefix : prefix + ": " + shortMessage;
        }

        private static bool ContainsHttpStatus(string text, int status)
        {
            string code = status.ToString();
            return text.IndexOf("status code '" + code + "'", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("status code " + code, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("http " + code, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("(" + code + ")", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsNetworkFailure(Exception exception, string details)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is SocketException) return true;
                if (current is WebException web)
                {
                    switch (web.Status)
                    {
                        case WebExceptionStatus.ConnectFailure:
                        case WebExceptionStatus.NameResolutionFailure:
                        case WebExceptionStatus.ProxyNameResolutionFailure:
                        case WebExceptionStatus.Timeout:
                        case WebExceptionStatus.ConnectionClosed:
                            return true;
                    }
                }
            }

            string lower = (details ?? string.Empty).ToLowerInvariant();
            return lower.Contains("unable to connect") ||
                   lower.Contains("could not connect") ||
                   lower.Contains("connection refused") ||
                   lower.Contains("actively refused") ||
                   lower.Contains("could not resolve") ||
                   lower.Contains("name resolution") ||
                   lower.Contains("no such host") ||
                   lower.Contains("network is unreachable") ||
                   lower.Contains("connection timed out") ||
                   lower.Contains("connect timed out");
        }

        private static bool IsSecureConnectionFailure(Exception exception, string details)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is WebException web &&
                    (web.Status == WebExceptionStatus.TrustFailure || web.Status == WebExceptionStatus.SecureChannelFailure))
                    return true;
            }

            string lower = (details ?? string.Empty).ToLowerInvariant();
            return lower.Contains("certificate") || lower.Contains("tls") || lower.Contains("ssl");
        }

        private static bool HasException<T>(Exception exception) where T : Exception
        {
            for (Exception current = exception; current != null; current = current.InnerException)
                if (current is T) return true;
            return false;
        }

        private static string Flatten(Exception exception)
        {
            var builder = new StringBuilder();
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (builder.Length > 0) builder.Append(" | ");
                builder.Append(current.GetType().Name).Append(": ").Append(current.Message);
            }
            return builder.ToString();
        }

        private static string ShortMessage(Exception exception)
        {
            string value = exception?.Message ?? string.Empty;
            value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (value.Length > 120) value = value.Substring(0, 117) + "...";
            return value;
        }
    }
}
