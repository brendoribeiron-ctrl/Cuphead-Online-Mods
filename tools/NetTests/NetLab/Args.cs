using System.Globalization;
using System.Net;

namespace NetLab;

// Leitor simples de "--nome valor" e "--flag"
public sealed class Args
{
	private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

	public Args(IEnumerable<string> args)
	{
		string key = null;
		foreach (string a in args)
		{
			if (a.StartsWith("--"))
			{
				if (key != null)
				{
					values[key] = "true";
				}
				key = a.Substring(2);
			}
			else if (key != null)
			{
				values[key] = a;
				key = null;
			}
			else
			{
				throw new ArgumentException("argumento solto: " + a);
			}
		}
		if (key != null)
		{
			values[key] = "true";
		}
	}

	public bool Has(string name) => values.ContainsKey(name);

	public string Str(string name, string def) => values.TryGetValue(name, out var v) ? v : def;

	public double Num(string name, double def) => values.TryGetValue(name, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;

	public int Int(string name, int def) => (int)Num(name, def);

	public bool Flag(string name) => values.TryGetValue(name, out var v) && v != "false";

	public static IPEndPoint ParseEndPoint(string text, int defaultPort)
	{
		string host = text;
		int port = defaultPort;
		int colon = text.LastIndexOf(':');
		if (colon > 0)
		{
			host = text.Substring(0, colon);
			port = int.Parse(text.Substring(colon + 1), CultureInfo.InvariantCulture);
		}
		if (!IPAddress.TryParse(host, out var ip))
		{
			ip = Dns.GetHostAddresses(host).First(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
		}
		return new IPEndPoint(ip, port);
	}
}

public static class Log
{
	private static readonly object sync = new();

	public static void Info(string tag, string text)
	{
		lock (sync)
		{
			Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + tag + "] " + text);
		}
	}
}
