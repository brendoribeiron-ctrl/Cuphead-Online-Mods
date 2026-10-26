using System;
using System.Net;
using System.Text;

// MOD ONLINE: codigo de sala. Guarda o IPv4 e a porta do host em 10 letras
// (ex.: K7Q2M-9XA3B), para o amigo nao precisar digitar IP.
public static class RoomCode
{
	// Alfabeto Crockford: sem I, L, O e U para nao confundir na hora de digitar
	private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

	// So embaralha, para o codigo nao parecer um IP. Nao e seguranca.
	private const ulong Mask = 0x5A3C96E17B2DUL;

	public const int Length = 10;

	public static string Encode(IPAddress ip, int port)
	{
		byte[] bytes = ip.GetAddressBytes();
		if (bytes.Length != 4)
		{
			throw new ArgumentException("so IPv4");
		}
		ulong value = ((ulong)bytes[0] << 40) | ((ulong)bytes[1] << 32) | ((ulong)bytes[2] << 24) | ((ulong)bytes[3] << 16) | (ushort)port;
		value ^= Mask;
		// 48 bits de dados + 2 bits de conferencia = 50 bits = 10 letras
		ulong full = (value << 2) | Checksum(value);
		char[] chars = new char[Length];
		for (int i = Length - 1; i >= 0; i--)
		{
			chars[i] = Alphabet[(int)(full & 31)];
			full >>= 5;
		}
		string code = new string(chars);
		return code.Substring(0, 5) + "-" + code.Substring(5);
	}

	public static bool TryDecode(string text, out IPAddress ip, out int port)
	{
		ip = null;
		port = 0;
		if (text == null)
		{
			return false;
		}
		StringBuilder sb = new StringBuilder();
		foreach (char raw in text.ToUpperInvariant())
		{
			char c = raw;
			if (c == '-' || c == ' ' || c == '\t')
			{
				continue;
			}
			if (c == 'O')
			{
				c = '0';
			}
			else if (c == 'I' || c == 'L')
			{
				c = '1';
			}
			sb.Append(c);
		}
		if (sb.Length != Length)
		{
			return false;
		}
		ulong full = 0;
		for (int i = 0; i < Length; i++)
		{
			int digit = Alphabet.IndexOf(sb[i]);
			if (digit < 0)
			{
				return false;
			}
			full = (full << 5) | (uint)digit;
		}
		ulong value = full >> 2;
		if ((full & 3) != Checksum(value))
		{
			return false;
		}
		value ^= Mask;
		port = (int)(value & 0xFFFF);
		ip = new IPAddress(new byte[4]
		{
			(byte)(value >> 40),
			(byte)(value >> 32),
			(byte)(value >> 24),
			(byte)(value >> 16)
		});
		return port > 0;
	}

	// Aceita codigo de sala, "ip", "ip:porta" ou nome de host
	public static bool TryParseAddress(string text, int defaultPort, out string host, out int port)
	{
		host = null;
		port = defaultPort;
		if (text == null)
		{
			return false;
		}
		text = text.Trim();
		if (text.Length == 0)
		{
			return false;
		}
		if (TryDecode(text, out var ip, out var codePort))
		{
			host = ip.ToString();
			port = codePort;
			return true;
		}
		if (text.IndexOf('.') < 0 && !text.Equals("localhost", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		int colon = text.LastIndexOf(':');
		if (colon > 0)
		{
			if (!int.TryParse(text.Substring(colon + 1), out port) || port <= 0 || port > 65535)
			{
				return false;
			}
			text = text.Substring(0, colon);
		}
		host = text;
		return true;
	}

	private static ulong Checksum(ulong value)
	{
		ulong sum = 0;
		while (value != 0)
		{
			sum += value & 0xFF;
			value >>= 8;
		}
		return sum & 3;
	}
}
