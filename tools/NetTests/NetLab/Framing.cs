using System.Net.Sockets;

namespace NetLab;

// Mensagens do NetSession: [tipo:byte][len:int32 little-endian][payload]
public sealed class FrameConn : IDisposable
{
	public const int MaxPayload = 8 << 20;

	private readonly Stream stream;

	private readonly BinaryReader reader;

	private readonly object writeLock = new();

	public TcpClient Tcp { get; }

	public long BytesIn;

	public long BytesOut;

	public FrameConn(TcpClient tcp)
	{
		Tcp = tcp;
		tcp.NoDelay = true;
		stream = tcp.GetStream();
		reader = new BinaryReader(stream);
	}

	public FrameConn(Stream s)
	{
		stream = s;
		reader = new BinaryReader(s);
	}

	public void Send(byte type, byte[] payload)
	{
		payload ??= Array.Empty<byte>();
		byte[] buf = new byte[5 + payload.Length];
		buf[0] = type;
		BitConverter.TryWriteBytes(buf.AsSpan(1), payload.Length);
		payload.CopyTo(buf, 5);
		lock (writeLock)
		{
			stream.Write(buf, 0, buf.Length);
			stream.Flush();
			BytesOut += buf.Length;
		}
	}

	// Bloqueia ate chegar uma mensagem; lanca EndOfStreamException/IOException quando cai
	public (byte type, byte[] payload) Receive()
	{
		byte type = reader.ReadByte();
		int len = reader.ReadInt32();
		if (len < 0 || len > MaxPayload)
		{
			throw new InvalidDataException($"tamanho invalido {len} (tipo 0x{type:X2})");
		}
		byte[] p = reader.ReadBytes(len);
		if (p.Length != len)
		{
			throw new EndOfStreamException("conexao fechou no meio da mensagem");
		}
		BytesIn += 5 + len;
		return (type, p);
	}

	public void Dispose()
	{
		try
		{
			Tcp?.Close();
		}
		catch
		{
		}
		stream.Dispose();
	}
}

public static class Framing
{
	// Stream que entrega no maximo N bytes por Read (simula TCP picotado)
	private sealed class TrickleStream : Stream
	{
		private readonly MemoryStream inner;

		private readonly Random rng = new(7);

		public TrickleStream(byte[] data) => inner = new MemoryStream(data);

		public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, rng.Next(1, 4)));

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => inner.Length;

		public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

		public override void Flush() { }

		public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();

		public override void SetLength(long v) => throw new NotSupportedException();

		public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
	}

	public static void SelfTest(Action<string, bool, string> check)
	{
		var ms = new MemoryStream();
		var w = new FrameConn(ms);
		var rng = new Random(3);
		var sent = new List<(byte, byte[])>();
		for (int i = 0; i < 300; i++)
		{
			byte[] p = new byte[i % 50 == 0 ? rng.Next(0, 300000) : rng.Next(0, 40)];
			rng.NextBytes(p);
			byte t = (byte)rng.Next(0, 64);
			sent.Add((t, p));
			w.Send(t, p);
		}
		var r = new FrameConn(new TrickleStream(ms.ToArray()));
		bool ok = true;
		foreach (var (t, p) in sent)
		{
			var (rt, rp) = r.Receive();
			ok &= rt == t && rp.AsSpan().SequenceEqual(p);
		}
		check("framing [tipo][len][payload] com leituras picotadas", ok, $"{sent.Count} mensagens, ate 300 KB, lidas de 1 a 3 bytes por vez");

		bool threw = false;
		try
		{
			new FrameConn(new MemoryStream(new byte[] { 0x20, 0xFF, 0xFF, 0xFF, 0x7F })).Receive();
		}
		catch (InvalidDataException)
		{
			threw = true;
		}
		check("framing recusa tamanho absurdo", threw, "len=2147483647 deve dar erro, sem alocar 2 GB");

		threw = false;
		try
		{
			new FrameConn(new MemoryStream(new byte[] { 0x20, 10, 0, 0, 0, 1, 2 })).Receive();
		}
		catch (EndOfStreamException)
		{
			threw = true;
		}
		check("framing detecta mensagem cortada", threw, "payload de 10 com so 2 bytes");
	}
}
