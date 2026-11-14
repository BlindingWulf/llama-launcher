using System.Text.Json;

namespace LlamaLauncher;

// Share only the authenticated model API. The server can remain on loopback (also with PolyStrata).
public static class ModelTailnet
{
    internal static bool OwnRoute(JsonElement root, int port)
        => root.TryGetProperty("TCP", out var tcp) && tcp.TryGetProperty(port.ToString(), out var entry)
            && entry.TryGetProperty("TCPForward", out var target) && target.GetString() == $"127.0.0.1:{port}";

    static async Task<JsonDocument> Routes(string exe)
    {
        var (code, output, error) = await Ollama.Tool(exe, "serve status --json");
        if (code != 0) throw new InvalidOperationException("Cannot read Tailscale sharing: " + error.Trim());
        return JsonDocument.Parse(output);
    }
    static async Task Change(string exe, string args)
    {
        var (code, output, error) = await Ollama.Tool(exe, args);
        if (code != 0) throw new InvalidOperationException("Cannot update Tailscale sharing: " + (error + " " + output).Trim());
    }
    public static async Task Share(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("Set a server API key before sharing the model API.");
        string exe = Ollama.Tailscale() ?? throw new InvalidOperationException("Install Tailscale and sign in before sharing.");
        var address = await Ollama.TailnetAddress();
        if (address.Problem != null) throw new InvalidOperationException(address.Problem);
        using var routes = await Routes(exe);
        int port = settings.Port;
        if (!OwnRoute(routes.RootElement, port))
        {
            if (routes.RootElement.TryGetProperty("TCP", out var tcp) && tcp.TryGetProperty(port.ToString(), out _))
                throw new InvalidOperationException($"Tailscale port {port} already shares another service. Choose a different model port.");
            await Change(exe, $"serve --bg --tcp={port} tcp://127.0.0.1:{port}");
        }
        if (settings.ModelTailscalePort > 0 && settings.ModelTailscalePort != port)
            await Stop(settings.ModelTailscalePort);
        settings.ModelTailscalePort = port;
    }
    public static async Task Stop(int port)
    {
        string exe = Ollama.Tailscale() ?? throw new InvalidOperationException("Tailscale is not installed.");
        using var routes = await Routes(exe);
        // Never remove an unrelated HTTPS or TCP service on this port.
        if (OwnRoute(routes.RootElement, port))
            await Change(exe, $"serve --tcp={port} off");
    }
}
